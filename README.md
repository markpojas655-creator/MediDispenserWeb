# MediDispenserWeb

Full-stack ASP.NET Core 8 + MQTTnet 4.3.7.1207 + SQLite (EF Core) + vanilla HTML/CSS/JS web application for managing one or more ESP32 automated medication dispensers over MQTT.

## Features

- **Multi-device management**: Register, edit, and delete dispensers; auto-detect unregistered devices publishing on the broker; switch active device from the header.
- **Real-time updates**: Server-Sent Events (`/api/sse`) push broker status, heartbeats, dispense events, test results, and calibration state without polling.
- **Scheduler & Adherence**: Per-device weekly dose schedule (compartments 1–8, time `HH:mm`, day chips, amount 1–3, label, enable/disable), automatic schedule sync to device (`.../schedule/set` retained), weekly calendar view, next-dose countdown, adherence chart, and CSV export.
- **System Flow**: Live state-machine diagram highlighting the current device state (`idle` → `precheck` → `dispensing` → `confirming` → `waiting` → `taken` / `returned` / `reminder` / `missed` / `empty` / `jam`).
- **System Tests**: Run full or individual hardware checks (`carousel`, `hatch`, `ir1`, `ir2`, `sound`, `leds`, `tft`, `rtc`) with live pass/fail checklist and persisted test history.
- **Voice Language Switch (EN / TL)**: Header toggle (`en` | `tl`) with retained `.../config/set` and per-line DFPlayer voice test buttons.
- **Carousel Calibration Wizard**: Jog controls (`0.5°`, `1°`, `5°`), home setting with 0–180° MG996R range validation, per-pocket offsets, MG90S hatch angles, test dispense verification, NVS save, and calibration history.
- **Alerts & Settings**: Acknowledge `missed`, `empty`, `jam`, and `reminder` alerts; edit MQTT broker host, port, prefix, volume, and reminder/missed timings in SQLite.

---

## How to Run in Visual Studio 2022 (.NET 8)

1. Open `MediDispenserWeb.sln` in **Visual Studio 2022** (with `.NET 8.0` SDK installed).
2. Set `MediDispenserWeb` as the startup project.
3. Press **F5** (or **Ctrl+F5**) to launch.
   - `AppDbContext` automatically creates `medidispenser.db` (SQLite) on first run via `EnsureCreated()`.
   - The app opens in your browser (default URLs in `Properties/launchSettings.json`: `https://localhost:54165` / `http://localhost:54166`).
4. *(Optional)* To test without hardware, run the `MqttSimulator` project:
   ```powershell
   dotnet run --project MqttSimulator -- broker.hivemq.com 1883 medidispenser disp01
   ```

---

## How to Change the MQTT Broker

- **Via Web UI (recommended)**: Open the **Settings** tab, update **Broker Host**, **Port**, and **Topic Prefix**, then click **Save & Reconnect**. Values are persisted in the SQLite `Settings` table.
- **Via `appsettings.json`** (initial defaults before Settings are saved in DB):
  ```json
  "Mqtt": {
    "Broker": "broker.hivemq.com",
    "Port": 1883,
    "Prefix": "medidispenser"
  }
  ```

---

## Full MQTT Contract (v2)

All topics are scoped per device under `{prefix}/{deviceId}/...` (default prefix: `medidispenser`).

### Device → Web App

| Topic | Payload Example | Notes |
|---|---|---|
| `{prefix}/{deviceId}/online` | `"online"` or `"offline"` | Retained; configure MQTT Last Will as `"offline"`. Device is also treated offline after 45 s without heartbeat. |
| `{prefix}/{deviceId}/heartbeat` | `{"ts":123,"ir_detected":true,"battery":87,"state":"idle","lang":"en","schedule_version":12}` | Sent every 15 s. Server auto-resyncs schedule if `schedule_version` is behind. |
| `{prefix}/{deviceId}/dispensed` | `{"compartment":3,"amount":1,"status":"dispensed_in_tray"}` | Published after piece lands on tray (or retry succeeds). |
| `{prefix}/{deviceId}/status` | `{"event":"pill_taken","compartment":3,"elapsed_sec":42,"message":"..."}` | `event`: `pill_taken` or `pill_returned`. |
| `{prefix}/{deviceId}/alert` | `{"type":"reminder","compartment":3,"message":"..."}` | `type`: `reminder`, `missed`, `empty`, or `jam`. |
| `{prefix}/{deviceId}/test/result` | `{"test":"hatch","ok":true,"detail":"..."}` | One message per check, followed by `{"test":"all","ok":true}` when running full test. |
| `{prefix}/{deviceId}/calib/state` | `{"home":90,"offsets":[0,0,1,0,0,0,0,0],"hatchClosed":0,"hatchOpen":90,"step":22.5}` | Published after calibration changes. |

### Web App → Device

| Topic | Payload Example | Retained |
|---|---|---|
| `{prefix}/{deviceId}/dispense` | `{"compartment":3,"amount":1}` | No |
| `{prefix}/{deviceId}/schedule/set` | `{"version":12,"items":[{"id":1,"compartment":3,"time":"08:00","days":[1,2,3,4,5,6,7],"amount":1,"label":"Vitamin C","enabled":true}]}` | Yes |
| `{prefix}/{deviceId}/config/set` | `{"lang":"en","volume":18}` | Yes |
| `{prefix}/{deviceId}/test/run` | `{"test":"all"}` (or `carousel`, `hatch`, `ir1`, `ir2`, `sound`, `leds`, `tft`, `rtc`, or `{"test":"sound","line":1}`) | No |
| `{prefix}/{deviceId}/calib/cmd` | `{"cmd":"jog","deg":1.0}`, `{"cmd":"goto","pocket":3}`, `{"cmd":"home_set"}`, `{"cmd":"pocket_offset","pocket":3,"deg":-1.5}`, `{"cmd":"hatch_set","closed":0,"open":90}`, `{"cmd":"save"}` | No |

---

## DFPlayer Mini SD Card Layout

Create two folders on the FAT32 microSD card:
- `/01` = **English**
- `/02` = **Tagalog**

Use the exact same numbered filenames in both folders:

| File | English (`/01`) | Tagalog (`/02`) |
|---|---|---|
| `0001.mp3` | "Time to take your medicine." | "Oras na para uminom ng gamot." |
| `0002.mp3` | "Thank you." | "Salamat po." |
| `0003.mp3` | "Dose missed." | "Hindi nainom ang gamot." |
| `0004.mp3` | "Pill put back." | "Ibinalik ang gamot." |
| `0005.mp3` | "Empty or jam, please refill." | "Walang laman o may bara. Pakilagyan po ng gamot." |
| `0006.mp3` | Start-up chime | Start-up chime |

---

## ESP32 Firmware Changes Required

To match this v2 contract, update the ESP32 firmware with:
1. **Per-device MQTT topics & LWT**: Prefix all pub/sub topics with `{prefix}/{deviceId}/...` and configure the MQTT Last Will & Testament on `{prefix}/{deviceId}/online` with `"offline"` (retained). Publish `"online"` (retained) upon connect.
2. **Heartbeat fields**: Publish `{prefix}/{deviceId}/heartbeat` every 15 s including `ts`, `ir_detected`, `battery`, `state`, `lang`, and `schedule_version`.
3. **Retained Schedule & NVS Persistence**: Subscribe to `{prefix}/{deviceId}/schedule/set`. Store `version` and `items` in ESP32 NVS (`Preferences`) so scheduled dosing continues via the DS3231 RTC across power cuts or Wi-Fi outages.
4. **Retained Config & Language Folders**: Subscribe to `{prefix}/{deviceId}/config/set`, persist `lang` (`en`/`tl`) and `volume` (`0–30`) in NVS, and map `en` → DFPlayer folder `1` (`/01`) and `tl` → folder `2` (`/02`).
5. **Hardware Test Runner**: Subscribe to `{prefix}/{deviceId}/test/run`, execute the requested check (`all`, `carousel`, `hatch`, `ir1`, `ir2`, `sound`, `leds`, `tft`, `rtc`, or `sound` with `line`), and publish each result to `{prefix}/{deviceId}/test/result`.
6. **Calibration Commands & NVS**: Subscribe to `{prefix}/{deviceId}/calib/cmd` (`jog`, `goto`, `home_set`, `pocket_offset`, `hatch_set`, `save`), persist `home`, `offsets[8]`, `hatchClosed`, and `hatchOpen` in NVS on `save`, and publish `{prefix}/{deviceId}/calib/state`.
