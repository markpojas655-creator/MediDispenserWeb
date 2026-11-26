using System.Text.Json;
using MediDispenserWeb.Data;
using MediDispenserWeb.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// EF Core SQLite
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite("Data Source=medidispenser.db"));

// Services
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<SseManager>();
builder.Services.AddSingleton<MqttService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttService>());

var app = builder.Build();

// Ensure DB exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ===================== SSE endpoint =====================
app.MapGet("/api/sse", async (HttpContext ctx, SseManager sse) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    var writer = new StreamWriter(ctx.Response.Body) { AutoFlush = true };
    var clientId = sse.AddClient(writer);
    await writer.WriteAsync($"event: connected\ndata: {{\"id\":\"{clientId}\"}}\n\n");
    await writer.FlushAsync();
    try
    {
        // Keep the connection alive until the client disconnects
        await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
    }
    catch (TaskCanceledException) { }
    finally
    {
        sse.RemoveClient(clientId);
    }
});

// ===================== Broker state =====================
app.MapGet("/api/state", (MqttService mqtt, DeviceRegistry reg) => new
{
    broker = mqtt.BrokerConnected,
    brokerHost = mqtt.Prefix,
    prefix = mqtt.Prefix
});

// ===================== Devices =====================
app.MapGet("/api/devices", async (AppDbContext db, DeviceRegistry reg) =>
{
    var devices = await db.Devices.OrderBy(d => d.Name).ToListAsync();
    return devices.Select(d =>
    {
        var state = reg.Get(d.DeviceId);
        return new
        {
            d.Id, d.DeviceId, d.Name, d.Location, d.PatientName, d.CreatedAt,
            online = state?.Online ?? false,
            battery = state?.Battery,
            lastHeartbeat = state?.LastHeartbeat,
            state = state?.State ?? "unknown",
            ir = state?.IrDetected,
            lang = state?.Lang,
            scheduleVersion = state?.ScheduleVersion,
            activeCompartment = state?.ActiveCompartment
        };
    });
});

app.MapGet("/api/devices/unregistered", (DeviceRegistry reg) => reg.GetUnregistered());

app.MapPost("/api/devices", async (DeviceCreateRequest req, AppDbContext db, DeviceRegistry reg) =>
{
    if (string.IsNullOrWhiteSpace(req.DeviceId) || !System.Text.RegularExpressions.Regex.IsMatch(req.DeviceId, @"^[a-zA-Z0-9\-]+$"))
        return Results.BadRequest("DeviceId must only contain letters, digits and dashes.");
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest("Name is required.");
    if (await db.Devices.AnyAsync(d => d.DeviceId == req.DeviceId))
        return Results.BadRequest("A device with this DeviceId already exists.");

    var device = new Device
    {
        DeviceId = req.DeviceId,
        Name = req.Name,
        Location = req.Location,
        PatientName = req.PatientName,
        CreatedAt = DateTime.UtcNow
    };
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    reg.RemoveUnregistered(req.DeviceId);
    return Results.Ok(device);
});

app.MapPut("/api/devices/{id:int}", async (int id, DeviceCreateRequest req, AppDbContext db) =>
{
    var device = await db.Devices.FindAsync(id);
    if (device == null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest("Name is required.");
    device.Name = req.Name;
    device.Location = req.Location;
    device.PatientName = req.PatientName;
    await db.SaveChangesAsync();
    return Results.Ok(device);
});

app.MapDelete("/api/devices/{id:int}", async (int id, AppDbContext db) =>
{
    var device = await db.Devices.FindAsync(id);
    if (device == null) return Results.NotFound();
    // Also delete related data
    var schedules = await db.ScheduleItems.Where(s => s.DeviceId == device.DeviceId).ToListAsync();
    db.ScheduleItems.RemoveRange(schedules);
    db.Devices.Remove(device);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ===================== Dispense =====================
app.MapPost("/api/dispense", async (DispenseRequest req, MqttService mqtt) =>
{
    if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest("DeviceId is required.");
    if (req.Compartment < 1 || req.Compartment > 8) return Results.BadRequest("Compartment must be 1-8.");
    if (req.Amount < 1 || req.Amount > 3) return Results.BadRequest("Amount must be 1-3.");
    try
    {
        await mqtt.SendDispenseAsync(req.DeviceId, req.Compartment, req.Amount);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// ===================== Schedule =====================
app.MapGet("/api/schedule/{deviceId}", async (string deviceId, AppDbContext db) =>
{
    var items = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).OrderBy(s => s.Time).ToListAsync();
    return items.Select(s => new
    {
        s.Id, s.DeviceId, s.Compartment, s.Time, s.Amount, s.Label, s.Enabled, s.Version,
        days = MqttService.DaysMaskToArray(s.DaysMask)
    });
});

app.MapPost("/api/schedule/{deviceId}", async (string deviceId, ScheduleItemRequest req, AppDbContext db, MqttService mqtt) =>
{
    if (req.Compartment < 1 || req.Compartment > 8) return Results.BadRequest("Compartment must be 1-8.");
    if (req.Amount < 1 || req.Amount > 3) return Results.BadRequest("Amount must be 1-3.");
    if (!TimeOnly.TryParseExact(req.Time, "HH:mm", out _)) return Results.BadRequest("Time must be HH:mm.");
    if (req.Days == null || req.Days.Length == 0) return Results.BadRequest("At least one day required.");

    // Check for duplicate time
    var existing = await db.ScheduleItems.Where(s => s.DeviceId == deviceId && s.Time == req.Time && s.Enabled).ToListAsync();
    string? warning = existing.Count > 0 ? "Warning: another dose is already scheduled at this time." : null;

    // Compute next version
    var maxVer = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).MaxAsync(s => (int?)s.Version) ?? 0;
    var newVersion = maxVer + 1;

    var item = new ScheduleItem
    {
        DeviceId = deviceId,
        Compartment = req.Compartment,
        Time = req.Time,
        DaysMask = MqttService.DaysArrayToMask(req.Days),
        Amount = req.Amount,
        Label = req.Label ?? "",
        Enabled = req.Enabled ?? true,
        Version = newVersion
    };
    db.ScheduleItems.Add(item);

    // Update all items to the new version
    var allItems = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).ToListAsync();
    foreach (var i in allItems) i.Version = newVersion;
    await db.SaveChangesAsync();

    // Publish schedule to device
    try { await mqtt.PublishScheduleAsync(deviceId); } catch { }

    return Results.Ok(new { item = new { item.Id, item.Compartment, item.Time, item.Amount, item.Label, item.Enabled, days = req.Days, item.Version }, warning });
});

app.MapPut("/api/schedule/{deviceId}/{id:int}", async (string deviceId, int id, ScheduleItemRequest req, AppDbContext db, MqttService mqtt) =>
{
    var item = await db.ScheduleItems.FindAsync(id);
    if (item == null || item.DeviceId != deviceId) return Results.NotFound();
    if (req.Compartment < 1 || req.Compartment > 8) return Results.BadRequest("Compartment must be 1-8.");
    if (req.Amount < 1 || req.Amount > 3) return Results.BadRequest("Amount must be 1-3.");
    if (!TimeOnly.TryParseExact(req.Time, "HH:mm", out _)) return Results.BadRequest("Time must be HH:mm.");

    var maxVer = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).MaxAsync(s => (int?)s.Version) ?? 0;
    var newVersion = maxVer + 1;

    item.Compartment = req.Compartment;
    item.Time = req.Time;
    item.DaysMask = MqttService.DaysArrayToMask(req.Days ?? new[] { 1, 2, 3, 4, 5, 6, 7 });
    item.Amount = req.Amount;
    item.Label = req.Label ?? "";
    item.Enabled = req.Enabled ?? true;
    item.Version = newVersion;

    var allItems = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).ToListAsync();
    foreach (var i in allItems) i.Version = newVersion;
    await db.SaveChangesAsync();

    try { await mqtt.PublishScheduleAsync(deviceId); } catch { }
    return Results.Ok(new { updated = true, version = newVersion });
});

app.MapDelete("/api/schedule/{deviceId}/{id:int}", async (string deviceId, int id, AppDbContext db, MqttService mqtt) =>
{
    var item = await db.ScheduleItems.FindAsync(id);
    if (item == null || item.DeviceId != deviceId) return Results.NotFound();
    db.ScheduleItems.Remove(item);

    var maxVer = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).MaxAsync(s => (int?)s.Version) ?? 0;
    var newVersion = maxVer + 1;
    var allItems = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).ToListAsync();
    foreach (var i in allItems) i.Version = newVersion;
    await db.SaveChangesAsync();

    try { await mqtt.PublishScheduleAsync(deviceId); } catch { }
    return Results.Ok(new { deleted = true, version = newVersion });
});

app.MapPost("/api/schedule/{deviceId}/toggle/{id:int}", async (string deviceId, int id, AppDbContext db, MqttService mqtt) =>
{
    var item = await db.ScheduleItems.FindAsync(id);
    if (item == null || item.DeviceId != deviceId) return Results.NotFound();
    item.Enabled = !item.Enabled;

    var maxVer = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).MaxAsync(s => (int?)s.Version) ?? 0;
    var newVersion = maxVer + 1;
    var allItems = await db.ScheduleItems.Where(s => s.DeviceId == deviceId).ToListAsync();
    foreach (var i in allItems) i.Version = newVersion;
    await db.SaveChangesAsync();

    try { await mqtt.PublishScheduleAsync(deviceId); } catch { }
    return Results.Ok(new { enabled = item.Enabled, version = newVersion });
});

app.MapPost("/api/schedule/{deviceId}/resend", async (string deviceId, MqttService mqtt) =>
{
    try
    {
        await mqtt.PublishScheduleAsync(deviceId);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// ===================== Events / History =====================
app.MapGet("/api/events/{deviceId}", async (string deviceId, AppDbContext db, int? limit) =>
{
    var max = Math.Min(limit ?? 100, 500);
    var events = await db.EventLogs
        .Where(e => e.DeviceId == deviceId)
        .OrderByDescending(e => e.Time)
        .Take(max)
        .ToListAsync();
    return events;
});

app.MapGet("/api/events", async (AppDbContext db, int? limit) =>
{
    var max = Math.Min(limit ?? 100, 500);
    var events = await db.EventLogs
        .OrderByDescending(e => e.Time)
        .Take(max)
        .ToListAsync();
    return events;
});

app.MapPost("/api/events/{id:int}/ack", async (int id, AppDbContext db) =>
{
    var ev = await db.EventLogs.FindAsync(id);
    if (ev == null) return Results.NotFound();
    ev.Acknowledged = true;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/adherence/{deviceId}", async (string deviceId, AppDbContext db, string? from, string? to) =>
{
    var fromDate = DateTime.TryParse(from, out var f) ? f : DateTime.UtcNow.AddDays(-30);
    var toDate = DateTime.TryParse(to, out var t) ? t : DateTime.UtcNow;
    var events = await db.EventLogs
        .Where(e => e.DeviceId == deviceId && e.Time >= fromDate && e.Time <= toDate &&
            (e.Kind == "taken" || e.Kind == "missed" || e.Kind == "dispensed"))
        .OrderBy(e => e.Time)
        .ToListAsync();

    // Group by date
    var grouped = events.GroupBy(e => e.Time.Date).Select(g => new
    {
        date = g.Key.ToString("yyyy-MM-dd"),
        dispensed = g.Count(e => e.Kind == "dispensed"),
        taken = g.Count(e => e.Kind == "taken"),
        missed = g.Count(e => e.Kind == "missed")
    });

    return new { events, summary = grouped };
});

app.MapGet("/api/adherence/{deviceId}/csv", async (string deviceId, AppDbContext db, string? from, string? to) =>
{
    var fromDate = DateTime.TryParse(from, out var f) ? f : DateTime.UtcNow.AddDays(-30);
    var toDate = DateTime.TryParse(to, out var t) ? t : DateTime.UtcNow;
    var events = await db.EventLogs
        .Where(e => e.DeviceId == deviceId && e.Time >= fromDate && e.Time <= toDate &&
            (e.Kind == "taken" || e.Kind == "missed" || e.Kind == "dispensed"))
        .OrderBy(e => e.Time)
        .ToListAsync();

    var csv = "Time,Kind,Compartment,Message\n" +
        string.Join("\n", events.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss},{e.Kind},{e.Compartment},{e.Message.Replace(",", ";")}"));

    return Results.Text(csv, "text/csv");
});

// ===================== Tests =====================
app.MapPost("/api/test/{deviceId}", async (string deviceId, TestRequest req, MqttService mqtt) =>
{
    var validTests = new HashSet<string> { "all", "carousel", "hatch", "ir1", "ir2", "sound", "leds", "tft", "rtc" };
    if (!validTests.Contains(req.Test)) return Results.BadRequest("Invalid test name.");
    try
    {
        await mqtt.SendTestRunAsync(deviceId, req.Test);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/test/{deviceId}/results", (string deviceId, MqttService mqtt) =>
{
    return mqtt.GetPendingTestResults(deviceId) ?? new List<JsonElement>();
});

app.MapGet("/api/test/{deviceId}/history", async (string deviceId, AppDbContext db) =>
{
    return await db.TestRuns.Where(t => t.DeviceId == deviceId).OrderByDescending(t => t.Time).Take(20).ToListAsync();
});

// ===================== Config =====================
app.MapPost("/api/config/{deviceId}", async (string deviceId, ConfigRequest req, MqttService mqtt) =>
{
    if (req.Lang != "en" && req.Lang != "tl") return Results.BadRequest("Language must be 'en' or 'tl'.");
    if (req.Volume.HasValue && (req.Volume < 0 || req.Volume > 30)) return Results.BadRequest("Volume must be 0-30.");
    try
    {
        await mqtt.SendConfigAsync(deviceId, req.Lang, req.Volume);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/api/config/{deviceId}/testvoice", async (string deviceId, VoiceTestRequest req, MqttService mqtt) =>
{
    if (req.Line < 1 || req.Line > 6) return Results.BadRequest("Line must be 1-6.");
    var payload = JsonSerializer.Serialize(new { test = "sound", line = req.Line });
    await mqtt.PublishAsync($"{mqtt.Prefix}/{deviceId}/test/run", payload);
    return Results.Ok(new { sent = true });
});

// ===================== Calibration =====================
app.MapGet("/api/calibration/{deviceId}", (string deviceId, DeviceRegistry reg) =>
{
    var state = reg.Get(deviceId);
    return state?.Calibration ?? new CalibrationState();
});

app.MapPost("/api/calibration/{deviceId}/cmd", async (string deviceId, JsonElement cmd, MqttService mqtt) =>
{
    try
    {
        await mqtt.SendCalibrationCmd(deviceId, cmd);
        return Results.Ok(new { sent = true });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/api/calibration/{deviceId}/history", async (string deviceId, AppDbContext db) =>
{
    return await db.CalibrationHistories.Where(c => c.DeviceId == deviceId)
        .OrderByDescending(c => c.Time).Take(20).ToListAsync();
});

// ===================== Settings =====================
app.MapGet("/api/settings", async (AppDbContext db, IConfiguration cfg) =>
{
    var settings = await db.Settings.ToListAsync();
    var dict = settings.ToDictionary(s => s.Key, s => s.Value);

    // Merge defaults from appsettings
    if (!dict.ContainsKey("mqtt:broker")) dict["mqtt:broker"] = cfg["Mqtt:Broker"] ?? "broker.hivemq.com";
    if (!dict.ContainsKey("mqtt:port")) dict["mqtt:port"] = cfg["Mqtt:Port"] ?? "1883";
    if (!dict.ContainsKey("mqtt:prefix")) dict["mqtt:prefix"] = cfg["Mqtt:Prefix"] ?? "medidispenser";
    if (!dict.ContainsKey("reminder_minutes")) dict["reminder_minutes"] = "5";
    if (!dict.ContainsKey("missed_minutes")) dict["missed_minutes"] = "10";
    if (!dict.ContainsKey("volume")) dict["volume"] = "18";

    return dict;
});

app.MapPost("/api/settings", async (Dictionary<string, string> req, AppDbContext db, MqttService mqtt) =>
{
    foreach (var (key, value) in req)
    {
        var setting = await db.Settings.FindAsync(key);
        if (setting != null)
            setting.Value = value;
        else
            db.Settings.Add(new Setting { Key = key, Value = value });
    }
    await db.SaveChangesAsync();

    // Update MQTT service if broker settings changed
    if (req.ContainsKey("mqtt:broker") || req.ContainsKey("mqtt:port") || req.ContainsKey("mqtt:prefix"))
    {
        var settings = await db.Settings.ToListAsync();
        var dict = settings.ToDictionary(s => s.Key, s => s.Value);
        mqtt.UpdateBrokerSettings(
            dict.GetValueOrDefault("mqtt:broker", "broker.hivemq.com"),
            int.TryParse(dict.GetValueOrDefault("mqtt:port", "1883"), out var p) ? p : 1883,
            dict.GetValueOrDefault("mqtt:prefix", "medidispenser")
        );
        await mqtt.ReconnectAsync();
    }

    return Results.Ok(new { saved = true });
});

app.Run();

// ===================== Request records =====================
record DeviceCreateRequest(string DeviceId, string Name, string? Location, string? PatientName);
record DispenseRequest(string DeviceId, int Compartment, int Amount);
record ScheduleItemRequest(int Compartment, string Time, int[] Days, int Amount, string? Label, bool? Enabled);
record TestRequest(string Test);
record ConfigRequest(string Lang, int? Volume);
record VoiceTestRequest(int Line);
