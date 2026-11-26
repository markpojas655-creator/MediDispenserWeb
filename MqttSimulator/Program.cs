using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║   MediDispenser MQTT Device Simulator    ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine();

var broker = args.Length > 0 ? args[0] : "broker.hivemq.com";
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 1883;
var prefix = args.Length > 2 ? args[2] : "medidispenser";
var deviceId = args.Length > 3 ? args[3] : "disp01";

Console.WriteLine($"Broker:   {broker}:{port}");
Console.WriteLine($"Prefix:   {prefix}");
Console.WriteLine($"DeviceId: {deviceId}");
Console.WriteLine();

var client = new MqttFactory().CreateMqttClient();
var state = "idle";
var battery = 87;
var irDetected = false;
var lang = "en";
var scheduleVersion = 0;
var loadedPockets = new bool[8] { true, true, true, true, false, false, false, false };
var calibHome = 90.0;
var calibOffsets = new double[8];
var hatchClosed = 0.0;
var hatchOpen = 90.0;
var dispensedCompartment = 0;
var dispensedTime = DateTime.MinValue;
var random = new Random();

client.ApplicationMessageReceivedAsync += async e =>
{
    var topic = e.ApplicationMessage.Topic;
    var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
    var sub = topic.Replace($"{prefix}/{deviceId}/", "");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  ← Received [{sub}]: {payload}");
    Console.ResetColor();

    try
    {
        var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        switch (sub)
        {
            case "dispense":
                await HandleDispense(root);
                break;
            case "schedule/set":
                HandleScheduleSet(root);
                break;
            case "config/set":
                HandleConfigSet(root);
                break;
            case "test/run":
                await HandleTestRun(root);
                break;
            case "calib/cmd":
                await HandleCalibCmd(root);
                break;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Error processing message: {ex.Message}");
        Console.ResetColor();
    }
};

async Task Publish(string subtopic, object payload, bool retain = false)
{
    var json = JsonSerializer.Serialize(payload);
    var msg = new MqttApplicationMessageBuilder()
        .WithTopic($"{prefix}/{deviceId}/{subtopic}")
        .WithPayload(json)
        .WithRetainFlag(retain)
        .Build();
    await client.PublishAsync(msg);
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  → Published [{subtopic}]: {json}");
    Console.ResetColor();
}

async Task HandleDispense(JsonElement root)
{
    var compartment = root.GetProperty("compartment").GetInt32();
    var amount = root.GetProperty("amount").GetInt32();
    Console.WriteLine($"  ⚙️  Dispensing from compartment {compartment}, amount {amount}...");

    // Pre-check
    if (compartment >= 1 && compartment <= 8 && !loadedPockets[compartment - 1])
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  🚫 Compartment {compartment} is EMPTY!");
        Console.ResetColor();
        state = "idle";
        await Publish("alert", new { type = "empty", compartment, message = $"Compartment {compartment} is empty." });
        return;
    }

    state = "dispensing";

    // Simulate dispensing (1 second delay)
    await Task.Delay(1000);

    // 90% chance of successful landing
    var landed = random.NextDouble() < 0.9;
    if (!landed)
    {
        Console.WriteLine("  ⚠️  Piece didn't land! Retrying...");
        for (int retry = 1; retry <= 3; retry++)
        {
            Console.WriteLine($"  🔄 Retry {retry}/3: Wiggling carousel ±4°...");
            await Task.Delay(500);
            if (random.NextDouble() < 0.8) { landed = true; break; }
        }
    }

    if (!landed)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ❌ JAM! Piece did not land after 3 retries.");
        Console.ResetColor();
        state = "idle";
        await Publish("alert", new { type = "jam", compartment, message = $"Jam in compartment {compartment}. Piece did not land after 3 retries." });
        return;
    }

    irDetected = true;
    state = "waiting";
    dispensedCompartment = compartment;
    dispensedTime = DateTime.UtcNow;

    await Publish("dispensed", new { compartment, amount, status = "dispensed_in_tray" });
    Console.WriteLine("  ✅ Dispensed successfully. Waiting for patient to take it...");
}

void HandleScheduleSet(JsonElement root)
{
    if (root.TryGetProperty("version", out var v))
    {
        scheduleVersion = v.GetInt32();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  📅 Schedule updated to version {scheduleVersion}");
        Console.ResetColor();
    }
    if (root.TryGetProperty("items", out var items))
    {
        Console.WriteLine($"  📅 Received {items.GetArrayLength()} schedule items");
    }
}

void HandleConfigSet(JsonElement root)
{
    if (root.TryGetProperty("lang", out var l))
    {
        lang = l.GetString() ?? "en";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  🌐 Language set to: {lang}");
        Console.ResetColor();
    }
    if (root.TryGetProperty("volume", out var vol))
    {
        Console.WriteLine($"  🔊 Volume set to: {vol.GetInt32()}");
    }
}

async Task HandleTestRun(JsonElement root)
{
    var test = root.GetProperty("test").GetString() ?? "all";
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  🧪 Running test: {test}");
    Console.ResetColor();

    if (test == "all" || test == "sound")
    {
        // Check if it's a voice line test
        if (root.TryGetProperty("line", out var line))
        {
            Console.WriteLine($"  🔊 Playing voice line {line.GetInt32()} in {lang}...");
            await Task.Delay(500);
            await Publish("test/result", new { test = "sound", ok = true, detail = $"Played line {line.GetInt32()} in {lang}" });
            return;
        }
    }

    var tests = test == "all"
        ? new[] { "carousel", "hatch", "ir1", "ir2", "sound", "leds", "tft", "rtc" }
        : new[] { test };

    foreach (var t in tests)
    {
        await Task.Delay(800);
        var ok = random.NextDouble() < 0.95; // 95% pass rate
        var detail = t switch
        {
            "carousel" => ok ? "All 8 positions reached correctly" : "Position 5 off by 2 degrees",
            "hatch" => ok ? "Open/close cycle OK" : "Hatch stuck at 45 degrees",
            "ir1" => ok ? "Tray IR sensor responsive" : "IR1 not responding",
            "ir2" => ok ? "Pocket IR sensor responsive" : "IR2 not responding",
            "sound" => ok ? "DFPlayer responded, test tone played" : "DFPlayer not responding",
            "leds" => ok ? "Green, yellow, red all lit successfully" : "Red LED not lighting",
            "tft" => ok ? "Test pattern displayed" : "TFT blank",
            "rtc" => ok ? $"RTC time: {DateTime.UtcNow:HH:mm:ss}, drift: {random.Next(0, 3)}s" : $"RTC drift: {random.Next(6, 30)}s (>5s)",
            _ => "Unknown test"
        };
        Console.WriteLine($"  🧪 {t}: {(ok ? "PASS" : "FAIL")} — {detail}");
        await Publish("test/result", new { test = t, ok, detail });
    }

    if (test == "all")
    {
        await Publish("test/result", new { test = "all", ok = true, detail = "All tests complete" });
    }
}

async Task HandleCalibCmd(JsonElement root)
{
    var cmd = root.GetProperty("cmd").GetString();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"  🔧 Calibration command: {cmd}");
    Console.ResetColor();

    switch (cmd)
    {
        case "jog":
            var deg = root.GetProperty("deg").GetDouble();
            calibHome += deg;
            Console.WriteLine($"  🔧 Jogged {deg}°, current position: {calibHome}°");
            break;
        case "goto":
            var pocket = root.GetProperty("pocket").GetInt32();
            var target = calibHome + (pocket - 1) * 22.5 + calibOffsets[pocket - 1];
            Console.WriteLine($"  🔧 Moving to pocket {pocket} at {target}°");
            break;
        case "home_set":
            Console.WriteLine($"  🔧 Home set at {calibHome}°");
            break;
        case "pocket_offset":
            var pkt = root.GetProperty("pocket").GetInt32();
            var off = root.TryGetProperty("deg", out var dv) ? dv.GetDouble() : 0;
            if (pkt >= 1 && pkt <= 8) calibOffsets[pkt - 1] = off;
            Console.WriteLine($"  🔧 Pocket {pkt} offset set to {off}°");
            break;
        case "hatch_set":
            if (root.TryGetProperty("closed", out var c)) hatchClosed = c.GetDouble();
            if (root.TryGetProperty("open", out var o)) hatchOpen = o.GetDouble();
            Console.WriteLine($"  🔧 Hatch angles: closed={hatchClosed}°, open={hatchOpen}°");
            if (root.TryGetProperty("test", out _))
            {
                Console.WriteLine("  🔧 Testing hatch open/close...");
                await Task.Delay(500);
            }
            break;
        case "save":
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  💾 Calibration saved to NVS!");
            Console.ResetColor();
            break;
    }

    // Publish current calibration state
    await Publish("calib/state", new
    {
        home = calibHome,
        offsets = calibOffsets,
        hatchClosed,
        hatchOpen,
        step = 22.5
    });
}

// Connect
var options = new MqttClientOptionsBuilder()
    .WithTcpServer(broker, port)
    .WithClientId($"MediDisp_Sim_{deviceId}_{Guid.NewGuid():N}"[..32])
    .WithCleanSession()
    .WithWillTopic($"{prefix}/{deviceId}/online")
    .WithWillPayload("offline")
    .WithWillRetain()
    .Build();

Console.Write("Connecting to broker...");
await client.ConnectAsync(options);
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine(" Connected!");
Console.ResetColor();

// Subscribe to device commands
var sub = new MqttFactory().CreateSubscribeOptionsBuilder()
    .WithTopicFilter($"{prefix}/{deviceId}/dispense")
    .WithTopicFilter($"{prefix}/{deviceId}/schedule/set")
    .WithTopicFilter($"{prefix}/{deviceId}/config/set")
    .WithTopicFilter($"{prefix}/{deviceId}/test/run")
    .WithTopicFilter($"{prefix}/{deviceId}/calib/cmd")
    .Build();
await client.SubscribeAsync(sub);

// Publish online
await Publish("online", "online");

// Publish initial calibration state
await Publish("calib/state", new { home = calibHome, offsets = calibOffsets, hatchClosed, hatchOpen, step = 22.5 });

Console.WriteLine();
Console.WriteLine("Commands: [t]ake, [r]eturn, [m]iss, [l]oad <1-8>, [u]nload <1-8>, [b]attery <0-100>, [q]uit");
Console.WriteLine("Heartbeats are sent automatically every 15 seconds.");
Console.WriteLine();

// Heartbeat timer
var heartbeatTimer = new System.Timers.Timer(15000);
heartbeatTimer.Elapsed += async (s, e) =>
{
    if (!client.IsConnected) return;
    battery = Math.Max(0, battery - random.Next(0, 2)); // Slowly drain
    await Publish("heartbeat", new
    {
        ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ir_detected = irDetected,
        battery,
        state,
        lang,
        schedule_version = scheduleVersion
    });
};
heartbeatTimer.Start();

// Send initial heartbeat
await Publish("heartbeat", new
{
    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    ir_detected = irDetected,
    battery,
    state,
    lang,
    schedule_version = scheduleVersion
});

// Simulate patient behavior in background
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(1000);
        if (state == "waiting" && dispensedCompartment > 0)
        {
            var elapsed = (DateTime.UtcNow - dispensedTime).TotalSeconds;

            // 5-minute reminder
            if (elapsed > 10 && elapsed < 12) // simulated faster: 10s = 5min
            {
                await Publish("alert", new { type = "reminder", compartment = dispensedCompartment, message = "Please take your medicine." });
            }

            // 10-minute missed
            if (elapsed > 20) // simulated faster: 20s = 10min
            {
                state = "missed";
                await Publish("alert", new { type = "missed", compartment = dispensedCompartment, message = $"Dose from compartment {dispensedCompartment} was missed." });
                irDetected = false;
                dispensedCompartment = 0;
                state = "idle";
            }
        }
    }
});

// Interactive command loop
while (true)
{
    var line = Console.ReadLine()?.Trim().ToLower();
    if (string.IsNullOrEmpty(line)) continue;

    var parts = line.Split(' ');
    switch (parts[0])
    {
        case "t" or "take":
            if (state != "waiting") { Console.WriteLine("No pill to take (not in waiting state)."); break; }
            irDetected = false;
            state = "idle";
            var elapsed_sec = (int)(DateTime.UtcNow - dispensedTime).TotalSeconds;
            await Publish("status", new { @event = "pill_taken", compartment = dispensedCompartment, elapsed_sec, message = "Pill taken. Thank you!" });
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✅ Pill taken after {elapsed_sec}s.");
            Console.ResetColor();
            dispensedCompartment = 0;
            break;

        case "r" or "return":
            if (state != "idle" || !irDetected) { Console.WriteLine("No pill to return."); break; }
            irDetected = true;
            await Publish("status", new { @event = "pill_returned", compartment = dispensedCompartment, elapsed_sec = 0, message = "Pill put back in tray." });
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ↩️ Pill returned to tray.");
            Console.ResetColor();
            break;

        case "l" or "load":
            if (parts.Length > 1 && int.TryParse(parts[1], out var lp) && lp >= 1 && lp <= 8)
            {
                loadedPockets[lp - 1] = true;
                Console.WriteLine($"  📦 Pocket {lp} loaded.");
            }
            else Console.WriteLine("Usage: load <1-8>");
            break;

        case "u" or "unload":
            if (parts.Length > 1 && int.TryParse(parts[1], out var up) && up >= 1 && up <= 8)
            {
                loadedPockets[up - 1] = false;
                Console.WriteLine($"  📭 Pocket {up} unloaded.");
            }
            else Console.WriteLine("Usage: unload <1-8>");
            break;

        case "b" or "battery":
            if (parts.Length > 1 && int.TryParse(parts[1], out var bv) && bv >= 0 && bv <= 100)
            {
                battery = bv;
                Console.WriteLine($"  🔋 Battery set to {battery}%.");
            }
            else Console.WriteLine("Usage: battery <0-100>");
            break;

        case "q" or "quit" or "exit":
            Console.WriteLine("Shutting down...");
            heartbeatTimer.Stop();
            // Publish offline
            var offMsg = new MqttApplicationMessageBuilder()
                .WithTopic($"{prefix}/{deviceId}/online")
                .WithPayload("offline")
                .WithRetainFlag()
                .Build();
            await client.PublishAsync(offMsg);
            await client.DisconnectAsync();
            return;

        default:
            Console.WriteLine("Unknown command. Use: take, return, load <n>, unload <n>, battery <n>, quit");
            break;
    }
}
