using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MediDispenserWeb.Data;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Client;

namespace MediDispenserWeb.Services;

/// <summary>Tracks runtime state of each device (online, heartbeat data, etc.).</summary>
public class DeviceState
{
    public string DeviceId { get; set; } = "";
    public bool Online => LastHeartbeat.HasValue && (DateTime.UtcNow - LastHeartbeat.Value).TotalSeconds < 45;
    public DateTime? LastHeartbeat { get; set; }
    public bool? IrDetected { get; set; }
    public string State { get; set; } = "unknown";
    public int? ActiveCompartment { get; set; }
    public int? Battery { get; set; }
    public string? Lang { get; set; }
    public int? ScheduleVersion { get; set; }
    public CalibrationState? Calibration { get; set; }
}

public class CalibrationState
{
    public double Home { get; set; } = 90;
    public double[] Offsets { get; set; } = new double[8];
    public double HatchClosed { get; set; }
    public double HatchOpen { get; set; } = 90;
    public double Step { get; set; } = 22.5;
}

/// <summary>Keeps an in-memory registry of all known devices and their live state.</summary>
public class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _unregistered = new(StringComparer.OrdinalIgnoreCase);

    public DeviceState GetOrCreate(string deviceId)
    {
        return _devices.GetOrAdd(deviceId, id => new DeviceState { DeviceId = id });
    }

    public DeviceState? Get(string deviceId) => _devices.GetValueOrDefault(deviceId);

    public IReadOnlyCollection<DeviceState> GetAll() => _devices.Values.ToList();

    public void NoteUnregistered(string deviceId)
    {
        _unregistered[deviceId] = DateTime.UtcNow;
    }

    public void RemoveUnregistered(string deviceId)
    {
        _unregistered.TryRemove(deviceId, out _);
    }

    public IReadOnlyDictionary<string, DateTime> GetUnregistered() =>
        new Dictionary<string, DateTime>(_unregistered, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Central MQTT service: connects to the broker, routes messages per device, publishes commands.</summary>
public class MqttService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<MqttService> _log;
    private readonly IMqttClient _client;
    private readonly DeviceRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SseManager _sse;
    private string _broker;
    private int _port;
    private string _prefix;

    // Pending test results keyed by deviceId
    private readonly ConcurrentDictionary<string, List<JsonElement>> _pendingTests = new();

    public bool BrokerConnected => _client.IsConnected;
    public string Prefix => _prefix;

    public MqttService(IConfiguration cfg, ILogger<MqttService> log, DeviceRegistry registry,
        IServiceScopeFactory scopeFactory, SseManager sse)
    {
        _cfg = cfg;
        _log = log;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _sse = sse;
        _broker = cfg["Mqtt:Broker"] ?? "broker.hivemq.com";
        _port = int.TryParse(cfg["Mqtt:Port"], out var p) ? p : 1883;
        _prefix = cfg["Mqtt:Prefix"] ?? "medidispenser";
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessage;
        _client.DisconnectedAsync += _ =>
        {
            _log.LogWarning("MQTT disconnected");
            _sse.Broadcast("broker", JsonSerializer.Serialize(new { connected = false }));
            return Task.CompletedTask;
        };
    }

    public void UpdateBrokerSettings(string broker, int port, string prefix)
    {
        _broker = broker;
        _port = port;
        _prefix = prefix;
    }

    public async Task ReconnectAsync()
    {
        try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Load broker settings from DB on startup
        await LoadSettingsFromDb();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(_broker, _port)
                        .WithClientId("MediDispenserWeb_" + Guid.NewGuid().ToString("N")[..8])
                        .WithCleanSession()
                        .Build();

                    await _client.ConnectAsync(options, ct);
                    // Subscribe to all devices under the prefix
                    var sub = new MqttFactory().CreateSubscribeOptionsBuilder()
                        .WithTopicFilter($"{_prefix}/+/#")
                        .Build();
                    await _client.SubscribeAsync(sub, ct);
                    _log.LogInformation("MQTT connected to {Broker}:{Port}, subscribed to {Prefix}/+/#", _broker, _port, _prefix);

                    _sse.Broadcast("broker", JsonSerializer.Serialize(new { connected = true, broker = _broker, port = _port }));

                    // Re-sync schedules for all known devices
                    _ = Task.Run(() => ResyncAllSchedules(ct), ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("MQTT connect failed: {Msg}", ex.Message);
            }
            try { await Task.Delay(5000, ct); } catch (TaskCanceledException) { }
        }
        if (_client.IsConnected) await _client.DisconnectAsync();
    }

    private async Task LoadSettingsFromDb()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var broker = await db.Settings.FindAsync("mqtt:broker");
            var port = await db.Settings.FindAsync("mqtt:port");
            var prefix = await db.Settings.FindAsync("mqtt:prefix");
            if (broker != null) _broker = broker.Value;
            if (port != null && int.TryParse(port.Value, out var p)) _port = p;
            if (prefix != null) _prefix = prefix.Value;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not load settings from DB: {Msg}", ex.Message);
        }
    }

    private async Task ResyncAllSchedules(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var devices = await db.Devices.ToListAsync(ct);
            foreach (var d in devices)
            {
                await PublishScheduleAsync(d.DeviceId, db);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Re-sync schedules failed: {Msg}", ex.Message);
        }
    }

    public async Task PublishScheduleAsync(string deviceId, AppDbContext? db = null)
    {
        bool ownScope = db == null;
        IServiceScope? scope = null;
        if (ownScope)
        {
            scope = _scopeFactory.CreateScope();
            db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        }
        try
        {
            var items = await db!.ScheduleItems.Where(s => s.DeviceId == deviceId).ToListAsync();
            // compute version: max version + 1, or 1
            var maxVersion = items.Any() ? items.Max(s => s.Version) : 0;
            var version = maxVersion > 0 ? maxVersion : 1;

            var payload = new
            {
                version,
                items = items.Select(s => new
                {
                    id = s.Id,
                    compartment = s.Compartment,
                    time = s.Time,
                    days = DaysMaskToArray(s.DaysMask),
                    amount = s.Amount,
                    label = s.Label,
                    enabled = s.Enabled
                })
            };

            await PublishAsync($"{_prefix}/{deviceId}/schedule/set",
                JsonSerializer.Serialize(payload), retain: true);
        }
        finally
        {
            if (ownScope) scope?.Dispose();
        }
    }

    public static int[] DaysMaskToArray(int mask)
    {
        var days = new List<int>();
        for (int i = 0; i < 7; i++)
            if ((mask & (1 << i)) != 0) days.Add(i + 1);
        return days.ToArray();
    }

    public static int DaysArrayToMask(int[] days)
    {
        int mask = 0;
        foreach (var d in days)
            if (d >= 1 && d <= 7) mask |= (1 << (d - 1));
        return mask;
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("Not connected to the MQTT broker.");
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();
        await _client.PublishAsync(msg);
    }

    public async Task SendDispenseAsync(string deviceId, int compartment, int amount)
    {
        var payload = JsonSerializer.Serialize(new { compartment, amount });
        await PublishAsync($"{_prefix}/{deviceId}/dispense", payload);
        await LogEventAsync(deviceId, "command", compartment,
            $"Dispense requested: compartment {compartment}, amount {amount}", payload);
    }

    public async Task SendConfigAsync(string deviceId, string lang, int? volume = null)
    {
        var dict = new Dictionary<string, object> { ["lang"] = lang };
        if (volume.HasValue) dict["volume"] = volume.Value;
        var payload = JsonSerializer.Serialize(dict);
        await PublishAsync($"{_prefix}/{deviceId}/config/set", payload, retain: true);
    }

    public async Task SendTestRunAsync(string deviceId, string test)
    {
        var payload = JsonSerializer.Serialize(new { test });
        await PublishAsync($"{_prefix}/{deviceId}/test/run", payload);
        _pendingTests[deviceId] = new List<JsonElement>();
    }

    public async Task SendCalibrationCmd(string deviceId, JsonElement cmd)
    {
        await PublishAsync($"{_prefix}/{deviceId}/calib/cmd", cmd.GetRawText());
    }

    public List<JsonElement>? GetPendingTestResults(string deviceId) =>
        _pendingTests.GetValueOrDefault(deviceId);

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var raw = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        // Parse: {prefix}/{deviceId}/{subtopic...}
        if (!topic.StartsWith(_prefix + "/")) return Task.CompletedTask;
        var rest = topic[(_prefix.Length + 1)..];
        var slashIdx = rest.IndexOf('/');
        if (slashIdx < 0)
        {
            // Could be {prefix}/{deviceId} with no subtopic — skip
            return Task.CompletedTask;
        }

        var deviceId = rest[..slashIdx];
        var subtopic = rest[(slashIdx + 1)..];

        // Skip our own commands echoed back
        if (subtopic is "dispense" or "schedule/set" or "config/set" or "test/run" or "calib/cmd")
            return Task.CompletedTask;

        // Check if device is registered
        _ = Task.Run(() => HandleDeviceMessage(deviceId, subtopic, raw, topic));
        return Task.CompletedTask;
    }

    private async Task HandleDeviceMessage(string deviceId, string subtopic, string raw, string fullTopic)
    {
        // Check if registered
        bool isRegistered;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            isRegistered = await db.Devices.AnyAsync(d => d.DeviceId == deviceId);
        }

        if (!isRegistered)
        {
            _registry.NoteUnregistered(deviceId);
        }

        var state = _registry.GetOrCreate(deviceId);

        JsonElement root = default;
        bool isJson = false;
        try
        {
            root = JsonDocument.Parse(raw).RootElement.Clone();
            isJson = root.ValueKind == JsonValueKind.Object;
        }
        catch { }

        string? Str(string k) => isJson && root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string k) => isJson && root.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : null;
        bool? Bool(string k) => isJson && root.TryGetProperty(k, out var v) &&
            (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
        double? Dbl(string k) => isJson && root.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : null;

        var comp = Int("compartment");

        switch (subtopic)
        {
            case "online":
                // Retained online/offline
                var isOnline = raw.Trim('"').Equals("online", StringComparison.OrdinalIgnoreCase);
                if (isOnline) state.LastHeartbeat = DateTime.UtcNow;
                else state.State = "offline";
                _sse.Broadcast("device", JsonSerializer.Serialize(new
                {
                    deviceId,
                    online = isOnline,
                    @event = "online_status"
                }));
                // If coming online, re-sync schedule
                if (isOnline && isRegistered) _ = Task.Run(() => ResyncDeviceSchedule(deviceId));
                return;

            case "heartbeat":
                state.LastHeartbeat = DateTime.UtcNow;
                state.IrDetected = Bool("ir_detected");
                state.State = Str("state") ?? state.State;
                state.Battery = Int("battery");
                state.Lang = Str("lang");
                var reportedVersion = Int("schedule_version");
                state.ScheduleVersion = reportedVersion;

                _sse.Broadcast("heartbeat", JsonSerializer.Serialize(new
                {
                    deviceId,
                    battery = state.Battery,
                    ir = state.IrDetected,
                    state = state.State,
                    lang = state.Lang,
                    scheduleVersion = reportedVersion,
                    ts = DateTime.UtcNow
                }));

                // Check schedule version sync
                if (isRegistered && reportedVersion.HasValue)
                {
                    _ = Task.Run(() => CheckScheduleSync(deviceId, reportedVersion.Value));
                }
                return; // heartbeats are not logged as events

            case "dispensed":
                state.State = "waiting";
                state.ActiveCompartment = comp;
                await LogEventAsync(deviceId, "dispensed", comp,
                    $"Compartment {comp} dispensed to tray. Waiting for patient.", raw);
                break;

            case "status":
                var ev = Str("event");
                if (ev == "pill_taken")
                {
                    state.State = "idle";
                    state.ActiveCompartment = null;
                    var elapsed = Int("elapsed_sec");
                    await LogEventAsync(deviceId, "taken", comp,
                        Str("message") ?? $"Pill taken after {elapsed}s.", raw);
                }
                else if (ev == "pill_returned")
                {
                    await LogEventAsync(deviceId, "returned", comp,
                        Str("message") ?? "Pill put back.", raw);
                }
                else
                {
                    await LogEventAsync(deviceId, "status", comp,
                        Str("message") ?? raw, raw);
                }
                break;

            case "alert":
                var type = Str("type") ?? "alert";
                if (type == "missed") state.State = "missed";
                var kind = type switch
                {
                    "missed" => "missed",
                    "reminder" => "reminder",
                    "empty" => "empty",
                    "jam" => "jam",
                    _ => "alert"
                };
                await LogEventAsync(deviceId, kind, comp,
                    Str("message") ?? raw, raw);
                break;

            case "test/result":
                if (isJson)
                {
                    if (_pendingTests.TryGetValue(deviceId, out var results))
                    {
                        results.Add(root.Clone());
                    }
                    _sse.Broadcast("test", JsonSerializer.Serialize(new
                    {
                        deviceId,
                        result = root
                    }));

                    // If "all" test, save the run
                    if (Str("test") == "all")
                    {
                        await SaveTestRun(deviceId);
                    }
                }
                return;

            case "calib/state":
                if (isJson)
                {
                    state.Calibration = new CalibrationState
                    {
                        Home = Dbl("home") ?? 90,
                        HatchClosed = Dbl("hatchClosed") ?? 0,
                        HatchOpen = Dbl("hatchOpen") ?? 90,
                        Step = Dbl("step") ?? 22.5
                    };
                    if (root.TryGetProperty("offsets", out var offsets) && offsets.ValueKind == JsonValueKind.Array)
                    {
                        var arr = new double[8];
                        int idx = 0;
                        foreach (var o in offsets.EnumerateArray())
                        {
                            if (idx < 8 && o.TryGetDouble(out var d)) arr[idx] = d;
                            idx++;
                        }
                        state.Calibration.Offsets = arr;
                    }
                    _sse.Broadcast("calibration", JsonSerializer.Serialize(new
                    {
                        deviceId,
                        calibration = state.Calibration
                    }));

                    // Save to history
                    await SaveCalibrationHistory(deviceId, raw);
                }
                return;

            default:
                await LogEventAsync(deviceId, "info", comp, Str("message") ?? raw, raw);
                break;
        }

        // Broadcast the event to SSE clients
        _sse.Broadcast("event", JsonSerializer.Serialize(new
        {
            deviceId,
            state = state.State,
            activeCompartment = state.ActiveCompartment
        }));
    }

    private async Task CheckScheduleSync(string deviceId, int reportedVersion)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var items = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).ToListAsync();
            var currentVersion = items.Any() ? items.Max(s => s.Version) : 0;
            if (currentVersion > 0 && reportedVersion < currentVersion)
            {
                _log.LogInformation("Device {DeviceId} has schedule v{Reported}, server has v{Current}. Re-syncing.",
                    deviceId, reportedVersion, currentVersion);
                await PublishScheduleAsync(deviceId, db);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Schedule sync check failed for {DeviceId}: {Msg}", deviceId, ex.Message);
        }
    }

    private async Task ResyncDeviceSchedule(string deviceId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PublishScheduleAsync(deviceId, db);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Re-sync schedule for {DeviceId} failed: {Msg}", deviceId, ex.Message);
        }
    }

    private async Task LogEventAsync(string deviceId, string kind, int? compartment, string message, string raw)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EventLogs.Add(new EventLog
            {
                DeviceId = deviceId,
                Time = DateTime.UtcNow,
                Kind = kind,
                Compartment = compartment,
                Message = message,
                Raw = raw
            });
            await db.SaveChangesAsync();

            // Clean up events older than 90 days
            var cutoff = DateTime.UtcNow.AddDays(-90);
            var old = await db.EventLogs.Where(e => e.Time < cutoff).ToListAsync();
            if (old.Count > 0)
            {
                db.EventLogs.RemoveRange(old);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to log event: {Msg}", ex.Message);
        }

        _sse.Broadcast("event", JsonSerializer.Serialize(new
        {
            deviceId,
            kind,
            compartment,
            message,
            time = DateTime.UtcNow
        }));
    }

    private async Task SaveTestRun(string deviceId)
    {
        if (!_pendingTests.TryRemove(deviceId, out var results)) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TestRuns.Add(new TestRun
            {
                DeviceId = deviceId,
                Time = DateTime.UtcNow,
                ResultsJson = JsonSerializer.Serialize(results)
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to save test run: {Msg}", ex.Message);
        }
    }

    private async Task SaveCalibrationHistory(string deviceId, string valuesJson)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CalibrationHistories.Add(new CalibrationHistory
            {
                DeviceId = deviceId,
                Time = DateTime.UtcNow,
                ValuesJson = valuesJson
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to save calibration history: {Msg}", ex.Message);
        }
    }
}
