/*
 * MediDispenserESP32.ino — Compact ESP32 38-pin Firmware (Arduino IDE)
 * Matches MediDispenserWeb MQTT v2 contract.
 *
 * Required Arduino IDE Libraries (Library Manager):
 *   - PubSubClient (Nick O'Leary)
 *   - ArduinoJson (Benoit Blanchon, v7.x)
 *   - ESP32Servo (Kevin Harrington)
 *   - RTClib (Adafruit)
 *   - DFRobotDFPlayerMini (DFRobot)
 *   - Adafruit GFX Library + Adafruit ST7735 (or swap for your TFT)
 *
 * Board: ESP32 Dev Module | Partition Scheme: Default 4MB (or Minimal SPIFFS)
 */

#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <ESP32Servo.h>
#include <Wire.h>
#include <RTClib.h>
#include <DFRobotDFPlayerMini.h>
#include <Preferences.h>
#include <Adafruit_GFX.h>
#include <Adafruit_ST7735.h>

// ===================== USER CONFIG =====================
const char* WIFI_SSID   = "YOUR_WIFI_SSID";
const char* WIFI_PASS   = "YOUR_WIFI_PASSWORD";
const char* MQTT_BROKER = "broker.hivemq.com";
const int   MQTT_PORT   = 1883;
const char* MQTT_PREFIX = "medidispenser";
const char* DEVICE_ID   = "disp01";

// ===================== PINS (ESP32 38-pin) =====================
#define PIN_CAROUSEL   13   // MG996R servo
#define PIN_HATCH      14   // MG90S servo
#define PIN_IR1        34   // Pocket 1 IR sensor (pre-check, active LOW)
#define PIN_IR2        35   // Ramp/tray IR sensor (landing/taken/returned, active LOW)
#define PIN_BTN        32   // Manual / Ack button (INPUT_PULLUP)
#define PIN_BAT        36   // Battery ADC voltage divider
#define PIN_LED_G      25
#define PIN_LED_Y      26
#define PIN_LED_R      27
#define PIN_DF_RX      16   // Serial2 RX <- DFPlayer TX
#define PIN_DF_TX      17   // Serial2 TX -> DFPlayer RX
#define PIN_TFT_CS      5
#define PIN_TFT_DC      2
#define PIN_TFT_RST     4

// ===================== TIMING =====================
#define HB_MS          15000UL
#define REMIND_SEC     300UL   // 5 min
#define MISSED_SEC     600UL   // 10 min
#define STEP_DEG       22.5f

// ===================== STRUCTS =====================
struct SchedItem {
  uint8_t comp, amt, daysMask; // bit 0=Mon .. bit 6=Sun
  uint8_t hr, mn;
  bool enabled;
};

struct Calib {
  float home = 90.0f;
  float off[8] = {0};
  float hatchClosed = 0.0f;
  float hatchOpen = 90.0f;
};

// ===================== GLOBALS =====================
WiFiClient wifiClient;
PubSubClient mqtt(wifiClient);
Servo srvCarousel, srvHatch;
RTC_DS3231 rtc;
DFRobotDFPlayerMini dfp;
Preferences prefs;
Adafruit_ST7735 tft(PIN_TFT_CS, PIN_TFT_DC, PIN_TFT_RST);

SchedItem sched[16];
uint8_t schedCount = 0;
uint16_t schedVer = 0;
int lastSchedMin = -1;

Calib cal;
float curCarouselDeg = 90.0f;
bool loadedMem[8] = {true,true,true,true,true,true,true,true};

String lang = "en";
uint8_t vol = 18;
String state = "idle"; // idle, dispensing, waiting, missed
int activeComp = 0;
bool wasTaken = false;
unsigned long waitStartMs = 0;
bool reminded = false;
unsigned long lastHbMs = 0;

// ===================== HELPERS =====================
String top(const char* sub) { return String(MQTT_PREFIX) + "/" + DEVICE_ID + "/" + sub; }
bool irPocket() { return digitalRead(PIN_IR1) == LOW; }
bool irTray() { return digitalRead(PIN_IR2) == LOW; }

void setLeds(bool g, bool y, bool r) {
  digitalWrite(PIN_LED_G, g);
  digitalWrite(PIN_LED_Y, y);
  digitalWrite(PIN_LED_R, r);
}

void playVoice(uint8_t file) {
  uint8_t folder = (lang == "tl") ? 2 : 1;
  dfp.playFolder(folder, file);
}

void tftStatus(const char* l1, const char* l2 = "") {
  tft.fillScreen(ST77XX_BLACK);
  tft.setTextColor(ST77XX_WHITE);
  tft.setTextSize(1);
  tft.setCursor(4, 10); tft.print("MediDispenser "); tft.println(DEVICE_ID);
  tft.setTextSize(2);
  tft.setCursor(4, 36); tft.println(l1);
  tft.setTextSize(1);
  tft.setCursor(4, 70); tft.println(l2);
}

int readBattery() {
  int raw = analogRead(PIN_BAT);
  int pct = map(raw, 2100, 2950, 0, 100); // adjust to your resistor divider
  return constrain(pct, 0, 100);
}

void moveCarousel(float deg) {
  deg = constrain(deg, 0.0f, 180.0f);
  curCarouselDeg = deg;
  srvCarousel.write((int)roundf(deg));
  delay(450);
}

void gotoPocket(int p) {
  if (p < 1 || p > 8) return;
  moveCarousel(cal.home + (p - 1) * STEP_DEG + cal.off[p - 1]);
}

// ===================== NVS STORAGE =====================
void saveCalib() {
  prefs.begin("cal", false);
  prefs.putFloat("home", cal.home);
  prefs.putFloat("hc", cal.hatchClosed);
  prefs.putFloat("ho", cal.hatchOpen);
  prefs.putBytes("off", cal.off, sizeof(cal.off));
  prefs.end();
}

void loadNVS() {
  prefs.begin("cal", true);
  cal.home = prefs.getFloat("home", 90.0f);
  cal.hatchClosed = prefs.getFloat("hc", 0.0f);
  cal.hatchOpen = prefs.getFloat("ho", 90.0f);
  prefs.getBytes("off", cal.off, sizeof(cal.off));
  prefs.end();

  prefs.begin("cfg", true);
  lang = prefs.getString("lang", "en");
  vol = prefs.getUChar("vol", 18);
  prefs.end();

  prefs.begin("sch", true);
  schedVer = prefs.getUShort("ver", 0);
  schedCount = prefs.getUChar("cnt", 0);
  if (schedCount > 16) schedCount = 16;
  if (schedCount > 0) prefs.getBytes("items", sched, schedCount * sizeof(SchedItem));
  prefs.end();
}

// ===================== MQTT PUBLISHERS =====================
void pubJson(const char* sub, JsonDocument& doc, bool retain = false) {
  char buf[512];
  size_t n = serializeJson(doc, buf, sizeof(buf));
  mqtt.publish(top(sub).c_str(), (const uint8_t*)buf, n, retain);
}

void pubHeartbeat() {
  JsonDocument doc;
  doc["ts"] = rtc.now().unixtime();
  doc["ir_detected"] = irTray();
  doc["battery"] = readBattery();
  doc["state"] = state;
  doc["lang"] = lang;
  doc["schedule_version"] = schedVer;
  pubJson("heartbeat", doc);
}

void pubAlert(const char* type, int comp, const char* msg) {
  JsonDocument doc;
  doc["type"] = type;
  doc["compartment"] = comp;
  doc["message"] = msg;
  pubJson("alert", doc);
}

void pubCalibState() {
  JsonDocument doc;
  doc["home"] = cal.home;
  JsonArray a = doc["offsets"].to<JsonArray>();
  for (int i = 0; i < 8; i++) a.add(cal.off[i]);
  doc["hatchClosed"] = cal.hatchClosed;
  doc["hatchOpen"] = cal.hatchOpen;
  doc["step"] = STEP_DEG;
  pubJson("calib/state", doc);
}

void pubTestRes(const char* t, bool ok, const char* detail) {
  JsonDocument doc;
  doc["test"] = t;
  doc["ok"] = ok;
  doc["detail"] = detail;
  pubJson("test/result", doc);
}

// ===================== DISPENSE FLOW =====================
void finalizeMissed() {
  state = "missed";
  setLeds(false, true, true);
  playVoice(3); // Dose missed
  pubAlert("missed", activeComp, "Dose missed.");
  tftStatus("DOSE MISSED", ("Comp " + String(activeComp)).c_str());
  delay(1500);
  state = "idle";
  activeComp = 0;
  wasTaken = false;
  setLeds(false, false, false);
  tftStatus("IDLE");
}

void doDispense(int comp, int amt) {
  if (comp < 1 || comp > 8) return;
  if (state == "waiting") finalizeMissed(); // next dose arrived first

  // Pre-check (IR2 over pocket 1, or loaded memory)
  gotoPocket(comp);
  bool hasPill = (comp == 1) ? irPocket() : loadedMem[comp - 1];
  if (!hasPill) {
    setLeds(false, false, true);
    playVoice(5); // Empty or jam
    pubAlert("empty", comp, "Pocket empty.");
    tftStatus("EMPTY!", ("Comp " + String(comp)).c_str());
    state = "idle";
    return;
  }

  state = "dispensing";
  setLeds(true, false, false);
  tftStatus("DISPENSING", ("Comp " + String(comp)).c_str());

  bool landed = false;
  for (int drop = 0; drop < amt; drop++) {
    srvHatch.write((int)cal.hatchOpen);
    delay(600);
    srvHatch.write((int)cal.hatchClosed);
    delay(400);

    landed = irTray();
    for (int retry = 0; retry < 3 && !landed; retry++) {
      moveCarousel(curCarouselDeg + (retry % 2 == 0 ? 4.0f : -4.0f));
      srvHatch.write((int)cal.hatchOpen); delay(450);
      srvHatch.write((int)cal.hatchClosed); delay(350);
      landed = irTray();
    }
    if (!landed) break;
  }

  if (!landed) {
    setLeds(false, false, true);
    playVoice(5);
    pubAlert("jam", comp, "Empty or jam after 3 retries.");
    tftStatus("JAM / EMPTY", ("Comp " + String(comp)).c_str());
    state = "idle";
    return;
  }

  loadedMem[comp - 1] = false;
  playVoice(1); // Time to take your medicine
  state = "waiting";
  activeComp = comp;
  wasTaken = false;
  reminded = false;
  waitStartMs = millis();

  JsonDocument doc;
  doc["compartment"] = comp;
  doc["amount"] = amt;
  doc["status"] = "dispensed_in_tray";
  pubJson("dispensed", doc);
  tftStatus("TAKE MEDS", ("Comp " + String(comp)).c_str());
}

// ===================== HARDWARE TESTS =====================
void runTests(const char* which, int line = 0) {
  bool all = strcmp(which, "all") == 0;
  if (line > 0) {
    playVoice(line);
    pubTestRes("sound", true, "Played voice line");
    return;
  }
  if (all || strcmp(which, "carousel") == 0) {
    for (int p = 1; p <= 8; p++) gotoPocket(p);
    gotoPocket(1);
    pubTestRes("carousel", true, "8 pockets OK");
  }
  if (all || strcmp(which, "hatch") == 0) {
    srvHatch.write((int)cal.hatchOpen); delay(400);
    srvHatch.write((int)cal.hatchClosed); delay(300);
    pubTestRes("hatch", true, "Hatch open/close OK");
  }
  if (all || strcmp(which, "ir1") == 0)
    pubTestRes("ir1", true, irPocket() ? "Pocket1: loaded" : "Pocket1: empty");
  if (all || strcmp(which, "ir2") == 0)
    pubTestRes("ir2", true, irTray() ? "Ramp/tray: object" : "Ramp/tray: clear");
  if (all || strcmp(which, "sound") == 0) {
    playVoice(6);
    pubTestRes("sound", true, "Chime played");
  }
  if (all || strcmp(which, "leds") == 0) {
    setLeds(true,0,0); delay(250); setLeds(0,true,0); delay(250);
    setLeds(0,0,true); delay(250); setLeds(0,0,0);
    pubTestRes("leds", true, "G/Y/R cycled");
  }
  if (all || strcmp(which, "tft") == 0) {
    tft.fillScreen(ST77XX_GREEN); delay(200);
    tft.fillScreen(ST77XX_BLUE); delay(200);
    tftStatus("TFT OK");
    pubTestRes("tft", true, "Test pattern OK");
  }
  if (all || strcmp(which, "rtc") == 0) {
    DateTime now = rtc.now();
    char buf[32];
    snprintf(buf, sizeof(buf), "%02d:%02d:%02d", now.hour(), now.minute(), now.second());
    pubTestRes("rtc", !rtc.lostPower(), buf);
  }
  if (all) pubTestRes("all", true, "Full test complete");
}

// ===================== MQTT CALLBACK =====================
void onMqtt(char* topic, byte* payload, unsigned int len) {
  String t(topic);
  String prefix = top("");
  if (!t.startsWith(prefix)) return;
  String sub = t.substring(prefix.length());

  JsonDocument doc;
  if (deserializeJson(doc, payload, len)) return;

  if (sub == "dispense") {
    doDispense(doc["compartment"] | 1, doc["amount"] | 1);
  }
  else if (sub == "schedule/set") {
    schedVer = doc["version"] | 0;
    JsonArray arr = doc["items"].as<JsonArray>();
    schedCount = 0;
    for (JsonObject it : arr) {
      if (schedCount >= 16) break;
      SchedItem& s = sched[schedCount++];
      s.comp = it["compartment"] | 1;
      s.amt = it["amount"] | 1;
      s.enabled = it["enabled"] | true;
      const char* tm = it["time"] | "08:00";
      s.hr = atoi(tm);
      s.mn = atoi(tm + 3);
      s.daysMask = 0;
      for (JsonVariant v : it["days"].as<JsonArray>()) {
        int d = v.as<int>();
        if (d >= 1 && d <= 7) s.daysMask |= (1 << (d - 1));
      }
    }
    prefs.begin("sch", false);
    prefs.putUShort("ver", schedVer);
    prefs.putUChar("cnt", schedCount);
    prefs.putBytes("items", sched, schedCount * sizeof(SchedItem));
    prefs.end();
    pubHeartbeat();
  }
  else if (sub == "config/set") {
    if (doc["lang"].is<const char*>()) lang = doc["lang"].as<String>();
    if (doc["volume"].is<int>()) { vol = constrain((int)doc["volume"], 0, 30); dfp.volume(vol); }
    prefs.begin("cfg", false);
    prefs.putString("lang", lang);
    prefs.putUChar("vol", vol);
    prefs.end();
    pubHeartbeat();
  }
  else if (sub == "test/run") {
    runTests(doc["test"] | "all", doc["line"] | 0);
  }
  else if (sub == "calib/cmd") {
    const char* cmd = doc["cmd"] | "";
    if (strcmp(cmd, "jog") == 0) moveCarousel(curCarouselDeg + (float)(doc["deg"] | 0.0f));
    else if (strcmp(cmd, "goto") == 0) gotoPocket(doc["pocket"] | 1);
    else if (strcmp(cmd, "home_set") == 0) cal.home = curCarouselDeg;
    else if (strcmp(cmd, "pocket_offset") == 0) {
      int p = doc["pocket"] | 1;
      if (p >= 1 && p <= 8) {
        float expected = cal.home + (p - 1) * STEP_DEG;
        cal.off[p - 1] = curCarouselDeg - expected;
      }
    }
    else if (strcmp(cmd, "hatch_set") == 0) {
      cal.hatchClosed = doc["closed"] | cal.hatchClosed;
      cal.hatchOpen = doc["open"] | cal.hatchOpen;
      if (doc["test"] | false) {
        srvHatch.write((int)cal.hatchOpen); delay(500);
        srvHatch.write((int)cal.hatchClosed);
      }
    }
    else if (strcmp(cmd, "save") == 0) saveCalib();
    pubCalibState();
  }
}

void ensureMqtt() {
  if (mqtt.connected()) return;
  String cid = String("ESP32_") + DEVICE_ID;
  if (mqtt.connect(cid.c_str(), top("online").c_str(), 0, true, "offline")) {
    mqtt.publish(top("online").c_str(), "online", true);
    mqtt.subscribe(top("dispense").c_str());
    mqtt.subscribe(top("schedule/set").c_str());
    mqtt.subscribe(top("config/set").c_str());
    mqtt.subscribe(top("test/run").c_str());
    mqtt.subscribe(top("calib/cmd").c_str());
    pubHeartbeat();
    pubCalibState();
  }
}

// ===================== SETUP & LOOP =====================
void setup() {
  Serial.begin(115200);
  pinMode(PIN_IR1, INPUT_PULLUP);
  pinMode(PIN_IR2, INPUT_PULLUP);
  pinMode(PIN_BTN, INPUT_PULLUP);
  pinMode(PIN_LED_G, OUTPUT);
  pinMode(PIN_LED_Y, OUTPUT);
  pinMode(PIN_LED_R, OUTPUT);

  loadNVS();

  srvCarousel.attach(PIN_CAROUSEL, 500, 2500);
  srvHatch.attach(PIN_HATCH, 500, 2500);
  srvHatch.write((int)cal.hatchClosed);
  moveCarousel(cal.home);

  Wire.begin();
  rtc.begin();

  Serial2.begin(9600, SERIAL_8N1, PIN_DF_RX, PIN_DF_TX);
  if (dfp.begin(Serial2)) {
    dfp.volume(vol);
    playVoice(6); // Start-up chime
  }

  tft.initR(INITR_BLACKTAB);
  tft.setRotation(1);
  tftStatus("STARTING...");

  WiFi.begin(WIFI_SSID, WIFI_PASS);
  unsigned long t0 = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - t0 < 10000) delay(250);

  mqtt.setServer(MQTT_BROKER, MQTT_PORT);
  mqtt.setBufferSize(2048);
  mqtt.setCallback(onMqtt);
  ensureMqtt();
  tftStatus("IDLE");
}

void loop() {
  if (WiFi.status() == WL_CONNECTED) {
    ensureMqtt();
    mqtt.loop();
  }

  unsigned long nowMs = millis();

  // Heartbeat every 15 s
  if (nowMs - lastHbMs >= HB_MS) {
    lastHbMs = nowMs;
    if (mqtt.connected()) pubHeartbeat();
  }

  // Waiting-for-patient state machine
  if (state == "waiting") {
    bool inTray = irTray();
    unsigned long elapsedSec = (nowMs - waitStartMs) / 1000UL;

    if (!inTray && !wasTaken) {
      wasTaken = true;
      state = "idle";
      setLeds(false, false, false);
      playVoice(2); // Thank you
      JsonDocument doc;
      doc["event"] = "pill_taken";
      doc["compartment"] = activeComp;
      doc["elapsed_sec"] = elapsedSec;
      doc["message"] = "Pill taken.";
      pubJson("status", doc);
      tftStatus("THANK YOU");
      delay(1000);
      tftStatus("IDLE");
    } else if (!reminded && elapsedSec >= REMIND_SEC) {
      reminded = true;
      playVoice(1);
      pubAlert("reminder", activeComp, "Reminder: please take your medicine.");
    } else if (elapsedSec >= MISSED_SEC) {
      finalizeMissed();
    }
  } else if (wasTaken && irTray()) {
    // Pill put back after being taken
    wasTaken = false;
    playVoice(4); // Pill put back
    JsonDocument doc;
    doc["event"] = "pill_returned";
    doc["compartment"] = activeComp;
    doc["elapsed_sec"] = 0;
    doc["message"] = "Pill put back.";
    pubJson("status", doc);
    tftStatus("PUT BACK");
  }

  // Offline-capable RTC Schedule Check
  DateTime dt = rtc.now();
  int curMin = dt.hour() * 60 + dt.minute();
  if (curMin != lastSchedMin) {
    lastSchedMin = curMin;
    uint8_t dow = dt.dayOfTheWeek(); // 0=Sun..6=Sat
    uint8_t bit = (dow == 0) ? 6 : (dow - 1); // 0=Mon..6=Sun
    for (uint8_t i = 0; i < schedCount; i++) {
      if (sched[i].enabled && sched[i].hr == dt.hour() && sched[i].mn == dt.minute()
          && (sched[i].daysMask & (1 << bit))) {
        doDispense(sched[i].comp, sched[i].amt);
        break;
      }
    }
  }

  // Manual button: dispense pocket 1 or clear missed
  if (digitalRead(PIN_BTN) == LOW) {
    delay(50);
    if (digitalRead(PIN_BTN) == LOW) {
      if (state == "idle") doDispense(1, 1);
      while (digitalRead(PIN_BTN) == LOW) delay(20);
    }
  }
}
