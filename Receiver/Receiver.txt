/*
  Version 1.0:
   This program is used for Receiver
   This is program for MCU receiving data from librehardwaremonitor, BMP280,
  signal to ON/OFF Relay/Opto by ESPNOW, showing information on LCD, calculating
  real power consumption of PC system and showing them all in dashboard.
   Author: Chu Xuân Hồng
   Time (dd/mm/yyyy): 08/10/2026
   Version 2.3: Updated web dashboard and added sub screen to show infomation about
  CPU, GPU, Case temperature and PC power.
   Time (dd/mm/yyyy): 25/11/2006
*/
#include <ESP8266WiFi.h>
#include <ESP8266WebServer.h>
#include <Wire.h>
#include <LiquidCrystal_I2C.h>
#include <Adafruit_BMP280.h>
#include <ArduinoJson.h>
#include <EEPROM.h>
#include <math.h>
#include <ctype.h>
#include <string.h>
#include <user_interface.h>
extern "C" {
  #include <espnow.h>
}
// WIFI CONFIGURATION (ONLY 2.4GHz Wi-Fi)
const char* WIFI_SSID = ""; // Please enter wifi name
const char* WIFI_PASS = ""; // Please enter wifi password
// STATIC IP CONFIGURATION
IPAddress local_IP(192, 168, 1, 115);
IPAddress gateway(192, 168, 1, 1);
IPAddress subnet(255, 255, 255, 0);
IPAddress primaryDNS(192, 168, 1, 1);
// HARDWARE CONFIGURATION
#define SDA_PIN D2
#define SCL_PIN D1
#define OPTO_PIN 14            // D5 pin: GPIO14
#define ESPNOW_CHANNEL 8       // Must match sender ESP-NOW channel
#define PULSE_MS 600           // Relay/Opto pulse duration (ms)
#define MAX_PENDING_COMMANDS 10
// EEPROM PERSISTENT STORAGE
#define EEPROM_SIZE 256
#define SETTINGS_MAGIC 0x52455033UL // "REP3"
struct PersistentSettings {
  uint32_t magic;
  uint8_t lcdEnabled;
  uint8_t transparency;
  uint8_t fontSize;
  uint8_t filterMask;          // bit0:Power, bit1:Voltage, bit2:Temp, bit3:Fan, bit4:Env
  char themeMode[12];
  char bg[8];
  char panel[8];
  char text[8];
  char muted[8];
  char accent[8];
};
PersistentSettings appSettings;
ESP8266WebServer server(80);
LiquidCrystal_I2C lcd(0x27, 16, 2);
Adafruit_BMP280 bmp;
bool bmpAvailable = false;
char macStr[18] = "00:00:00:00:00:00";
// ESP-NOW RECEIVER STATE
volatile uint8_t pendingCommands = 0;
bool pulseActive = false;
unsigned long pulseStart = 0;
bool espNowAvailable = false;
// SENSOR (BMP280) & TELEMETRY STATE
float ambientTemp = NAN;
float pressureHpa = NAN;
String latestJson = "{\"sensors\":[]}";
unsigned long lastPcData = 0;
unsigned long lastBmpRead = 0;
unsigned long lastLcdPage = 0;
uint8_t lcdPage = 0;
bool lcdEnabled = true;
// Cached power values for zero-allocation LCD updates
float cpuPowerWatts = NAN;
float gpuPowerWatts = NAN;
float totalPowerWatts = NAN;
float dcOutputWatts = NAN;
float realPowerWatts = NAN;
float psuEfficiency = NAN;
// PSU CONFIGURATION (VSP TEREX VXP1000TGD V2)
const float PSU_RATED_WATTS = 1000.0;
const float CASE_FANS_WATTS = 40.0;
const float RAM_WATTS = 10.0;
const float COOLER_WATTS = 6.0;
const float HDD_WATTS = 9.0;
const float SATA_SSD_WATTS = 4.5;
const float NVME_WATTS = 6.0;
const float OTHER_WATTS = 50.0;
const float OTHER_DC_WATTS =
    CASE_FANS_WATTS + RAM_WATTS + COOLER_WATTS +
    HDD_WATTS + SATA_SSD_WATTS + NVME_WATTS + OTHER_WATTS;
// PSU EFFICIENCY TABLE
const float PSU_LOAD_POINTS[] = {
  5, 10, 20, 30, 40, 50,
  60, 70, 80, 90, 100, 110
};
const float PSU_EFF_POINTS[] = {
  83.38, 88.81, 91.80, 92.78,
  92.95, 92.81, 92.49, 92.19,
  91.79, 91.49, 91.17, 90.82
};
const int PSU_POINTS_COUNT =
    sizeof(PSU_LOAD_POINTS) / sizeof(PSU_LOAD_POINTS[0]);
// ZERO-ALLOCATION STRING HELPER
bool strContainsCI(const char* haystack, const char* needle) {
  if (!haystack || !needle || !*needle) return false;
  for (const char* h = haystack; *h; h++) {
    const char* a = h;
    const char* b = needle;
    while (*a && *b && tolower((unsigned char)*a) == tolower((unsigned char)*b)) {
      a++;
      b++;
    }
    if (!*b) return true;
  }
  return false;
}
// SETTINGS PERSISTENCE HELPERS
bool isValidHexColor(const char* s) {
  if (!s || strlen(s) != 7 || s[0] != '#') return false;
  for (int i = 1; i < 7; i++) {
    if (!isxdigit((unsigned char)s[i])) return false;
  }
  return true;
}
void setDefaultSettings() {
  appSettings.magic = SETTINGS_MAGIC;
  appSettings.lcdEnabled = 1;
  appSettings.transparency = 0;
  appSettings.fontSize = 14;
  appSettings.filterMask = 0x1F;
  strlcpy(appSettings.themeMode, "dark", sizeof(appSettings.themeMode));
  strlcpy(appSettings.bg, "#0b1120", sizeof(appSettings.bg));
  strlcpy(appSettings.panel, "#151f30", sizeof(appSettings.panel));
  strlcpy(appSettings.text, "#e8eef8", sizeof(appSettings.text));
  strlcpy(appSettings.muted, "#93a4bb", sizeof(appSettings.muted));
  strlcpy(appSettings.accent, "#60a5fa", sizeof(appSettings.accent));
}
void saveSettings() {
  appSettings.magic = SETTINGS_MAGIC;
  appSettings.lcdEnabled = lcdEnabled ? 1 : 0;
  PersistentSettings stored;
  EEPROM.get(0, stored);
  if (memcmp(&stored, &appSettings, sizeof(PersistentSettings)) != 0) {
    EEPROM.put(0, appSettings);
    EEPROM.commit();
  }
}
void loadSettings() {
  EEPROM.begin(EEPROM_SIZE);
  EEPROM.get(0, appSettings);
  bool validFont = (appSettings.fontSize == 12 || appSettings.fontSize == 14 ||
                    appSettings.fontSize == 16 || appSettings.fontSize == 18);
  if (appSettings.magic != SETTINGS_MAGIC ||
      appSettings.transparency > 100 ||
      !validFont ||
      !isValidHexColor(appSettings.bg) ||
      !isValidHexColor(appSettings.panel) ||
      !isValidHexColor(appSettings.text) ||
      !isValidHexColor(appSettings.muted) ||
      !isValidHexColor(appSettings.accent)) {
    setDefaultSettings();
    EEPROM.put(0, appSettings);
    EEPROM.commit();
    Serial.println(F("EEPROM initialized with default settings"));
  } else {
    Serial.println(F("Settings loaded from EEPROM"));
  }
  lcdEnabled = (appSettings.lcdEnabled != 0);
}
// LCD HELPER
void lcdLine(byte row, const String &text) {
  char buf[17];
  size_t len = text.length();
  for (size_t i = 0; i < 16; i++) {
    buf[i] = (i < len) ? text[i] : ' ';
  }
  buf[16] = '\0';
  lcd.setCursor(0, row);
  lcd.print(buf);
}
// PSU EFFICIENCY CALCULATION
float getPsuEfficiency(float loadPercent) {
  if (!isfinite(loadPercent) || PSU_RATED_WATTS <= 0) return NAN;
  if (loadPercent <= PSU_LOAD_POINTS[0]) return PSU_EFF_POINTS[0];
  if (loadPercent >= PSU_LOAD_POINTS[PSU_POINTS_COUNT - 1]) {
    return PSU_EFF_POINTS[PSU_POINTS_COUNT - 1];
  }
  for (int i = 0; i < PSU_POINTS_COUNT - 1; i++) {
    float x0 = PSU_LOAD_POINTS[i];
    float x1 = PSU_LOAD_POINTS[i + 1];
    if (loadPercent >= x0 && loadPercent <= x1) {
      float y0 = PSU_EFF_POINTS[i];
      float y1 = PSU_EFF_POINTS[i + 1];
      return y0 + ((loadPercent - x0) / (x1 - x0)) * (y1 - y0);
    }
  }
  return NAN;
}
// REAL POWER ESTIMATION
void calculateRealPower(JsonDocument &doc) {
  cpuPowerWatts = doc["cpuPowerWatts"] | NAN;
  gpuPowerWatts = doc["gpuPowerWatts"] | NAN;
  if (!isfinite(cpuPowerWatts) || !isfinite(gpuPowerWatts) ||
      cpuPowerWatts < 0 || gpuPowerWatts < 0) {
    totalPowerWatts = NAN;
    dcOutputWatts = NAN;
    realPowerWatts = NAN;
    psuEfficiency = NAN;
    doc["totalPowerWatts"] = nullptr;
    doc["dcOutputWatts"] = nullptr;
    doc["realPowerWatts"] = nullptr;
    doc["psuEfficiencyPercent"] = nullptr;
    return;
  }
  totalPowerWatts = cpuPowerWatts + gpuPowerWatts;
  doc["totalPowerWatts"] = totalPowerWatts;
  dcOutputWatts = totalPowerWatts + OTHER_DC_WATTS;
  float loadPercent = (dcOutputWatts / PSU_RATED_WATTS) * 100.0;
  psuEfficiency = getPsuEfficiency(loadPercent);
  if (!isfinite(psuEfficiency) || psuEfficiency <= 0) {
    realPowerWatts = NAN;
    doc["dcOutputWatts"] = dcOutputWatts;
    doc["realPowerWatts"] = nullptr;
    doc["psuEfficiencyPercent"] = nullptr;
    return;
  }
  realPowerWatts = dcOutputWatts / (psuEfficiency / 100.0);
  doc["dcOutputWatts"] = dcOutputWatts;
  doc["realPowerWatts"] = realPowerWatts;
  doc["psuEfficiencyPercent"] = psuEfficiency;
}
// WEB DASHBOARD
const char INDEX_HTML[] PROGMEM = R"HTML(
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="theme-color" content="#0b1120">
<link rel="icon" type="image/svg+xml" href="/favicon.svg">
<title>RePOS — PC Monitor &amp; SCADA</title>
<style>
:root {
  --dashboard-font-size: 14px;
  color-scheme: dark;
  --bg: #0b1120;
  --bg-glow: #152137;
  --panel: #151f30;
  --panel-glass: rgba(21, 31, 48, 1);
  --panel-hover: #1b2940;
  --border: #29374c;
  --text: #e8eef8;
  --muted: #93a4bb;
  --text-secondary: #b9c8dc;
  --accent: #60a5fa;
  --button-bg: rgba(17, 26, 41, 0.72);
  --button-hover: rgba(32, 45, 64, 0.9);
  --button-active-bg: rgba(16, 48, 35, 0.9);
  --button-active-border: #28664b;
  --table-header: #1a2639;
  --table-border: #293548;
  --green: #4ade80;
  --yellow: #facc15;
  --red: #fb7185;
  --gray: #94a3b8;
  --status-green-bg: rgba(16, 41, 30, 1);
  --status-green-border: #216b48;
  --status-yellow-bg: rgba(48, 39, 14, 1);
  --status-yellow-border: #8d711b;
  --status-red-bg: rgba(53, 23, 23, 1);
  --status-red-border: #963b42;
  --radius: 12px;
}
html { font-size: var(--dashboard-font-size); }
* { box-sizing: border-box; }
body {
  margin: 0;
  padding: 20px;
  min-height: 100vh;
  background: radial-gradient(circle at top right, var(--bg-glow), var(--bg) 55%);
  background-color: var(--bg);
  color: var(--text);
  font-family: Inter, "Segoe UI", system-ui, -apple-system, sans-serif;
  font-size: 1rem;
  line-height: 1.5;
  transition: background-color 0.2s, color 0.2s;
}
main { max-width: 1240px; margin: 0 auto; }
.num { font-variant-numeric: tabular-nums; }
/* Header */
.header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
  padding-bottom: 18px;
  border-bottom: 1px solid var(--border);
}
.brand { display: flex; align-items: center; gap: 14px; min-width: 0; }
.brand-icon {
  position: relative;
  width: 48px;
  height: 48px;
  flex: 0 0 48px;
  display: grid;
  place-items: center;
  border: 1px solid var(--status-green-border);
  border-radius: 13px;
  background: var(--status-green-bg);
  color: var(--green);
  font-size: 1.6rem;
  cursor: pointer;
  box-shadow: 0 0 20px rgba(74, 222, 128, 0.14);
  transition: transform 0.15s, box-shadow 0.2s, border-color 0.2s;
}
.brand-icon:hover {
  transform: translateY(-1px) scale(1.03);
  border-color: var(--green);
  box-shadow: 0 0 24px rgba(74, 222, 128, 0.28);
}
.brand-icon:active { transform: scale(0.96); }
.brand-icon::after {
  content: "⚙";
  position: absolute;
  right: -4px;
  bottom: -4px;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: var(--panel);
  border: 1px solid var(--border);
  color: var(--text-secondary);
  font-size: 0.65rem;
  display: grid;
  place-items: center;
}
h1 {
  margin: 0;
  font-size: clamp(1.25rem, 2.4vw, 1.65rem);
  font-weight: 750;
  letter-spacing: -0.02em;
  line-height: 1.25;
}
.sub { margin-top: 3px; color: var(--muted); font-size: 0.85rem; }
.sub strong { color: var(--text-secondary); font-weight: 600; }
.header-meta {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  color: var(--muted);
  font-size: 0.82rem;
}
.conn-badge {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 4px 11px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: var(--panel-glass);
  font-size: 0.78rem;
  font-weight: 700;
  color: var(--text);
}
.connection-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--yellow); }
.connection-dot.online { background: var(--green); box-shadow: 0 0 8px var(--green); }
.connection-dot.offline { background: var(--red); box-shadow: 0 0 8px var(--red); }
/* Buttons */
.btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 7px;
  padding: 9px 14px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--button-bg);
  color: var(--text-secondary);
  font: inherit;
  font-size: 0.85rem;
  font-weight: 650;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s, color 0.15s, transform 0.1s;
}
.btn:hover:not(:disabled) { background: var(--button-hover); color: var(--text); }
.btn:active:not(:disabled) { transform: scale(0.98); }
.btn:disabled { opacity: 0.55; cursor: not-allowed; }
.btn.active {
  background: var(--button-active-bg);
  border-color: var(--button-active-border);
  color: var(--green);
}
.btn-power {
  background: var(--status-green-bg);
  border-color: var(--status-green-border);
  color: var(--green);
  font-weight: 750;
}
.btn-power:hover:not(:disabled) {
  filter: brightness(1.15);
  background: var(--status-green-bg);
  color: var(--green);
}
/* Alert Banner */
.alert {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px 20px;
  margin: 18px 0 14px;
  padding: 13px 17px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel-glass);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
}
.alert-left { display: flex; align-items: center; gap: 12px; }
.alert-icon { font-size: 1.15rem; line-height: 1; }
.alert-title { font-size: 0.92rem; font-weight: 750; letter-spacing: 0.02em; }
.alert-message { font-size: 0.84rem; color: var(--text-secondary); }
.legend { display: flex; flex-wrap: wrap; gap: 12px; color: var(--muted); font-size: 0.78rem; }
.legend span { display: inline-flex; align-items: center; gap: 5px; }
.dot { width: 8px; height: 8px; border-radius: 50%; display: inline-block; }
.dot-green { background: var(--green); }
.dot-yellow { background: var(--yellow); }
.dot-red { background: var(--red); }
.dot-gray { background: var(--gray); }
/* Unified 4-Column Pinned Overview Grid */
.grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  margin: 14px 0;
}
.card {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  min-width: 0;
  padding: 15px 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel-glass);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
  transition: transform 0.15s, border-color 0.15s, filter 0.15s;
}
.card:hover { transform: translateY(-1px); filter: brightness(1.06); }
.card-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 8px; }
.label { color: var(--muted); font-size: 0.84rem; font-weight: 500; }
.value {
  margin-top: 8px;
  font-size: clamp(1.4rem, 2.1vw, 1.85rem);
  font-weight: 750;
  line-height: 1.2;
  letter-spacing: -0.02em;
}
.unit { color: var(--muted); font-size: 0.84rem; font-weight: 500; margin-left: 2px; }
.note { margin-top: 8px; color: var(--muted); font-size: 0.76rem; line-height: 1.4; }
/* Remote Control integrated cleanly into the 4-column grid */
.remote-card {
  grid-column: span 3;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 12px;
}
.remote-card:hover { transform: none; filter: none; }
.remote-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  flex: 1;
}
.remote-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 14px;
  border-radius: 10px;
  border: 1px solid var(--border);
  background: var(--button-bg);
}
.remote-item-label { font-size: 0.86rem; font-weight: 650; color: var(--text); }
.remote-item .btn { min-width: 138px; min-height: 38px; }
/* Status Themes (Placed AFTER .card and .alert) */
.level-green { background: var(--status-green-bg); border-color: var(--status-green-border); color: var(--green); }
.level-yellow { background: var(--status-yellow-bg); border-color: var(--status-yellow-border); color: var(--yellow); }
.level-red { background: var(--status-red-bg); border-color: var(--status-red-border); color: var(--red); }
.level-gray { background: var(--panel-glass); border-color: var(--border); color: var(--text-secondary); }
/* Display Filters Bar */
.filter-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px 18px;
  margin: 14px 0;
  padding: 13px 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel-glass);
  backdrop-filter: blur(8px);
  -webkit-backdrop-filter: blur(8px);
}
.filter-bar-title { font-size: 0.9rem; font-weight: 700; color: var(--text); }
.filter-chips { display: flex; flex-wrap: wrap; gap: 8px; }
.chip {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 7px 13px;
  border-radius: 9px;
  border: 1px solid var(--border);
  background: var(--button-bg);
  color: var(--muted);
  font-size: 0.84rem;
  font-weight: 600;
  cursor: pointer;
  user-select: none;
  transition: all 0.15s;
}
.chip input { position: absolute; opacity: 0; pointer-events: none; }
.chip:hover { background: var(--button-hover); color: var(--text); }
.chip.checked {
  background: var(--button-active-bg);
  border-color: var(--button-active-border);
  color: var(--green);
}
.chip-dot { width: 7px; height: 7px; border-radius: 50%; background: currentColor; }
/* Appearance Modal */
.modal-backdrop {
  position: fixed;
  inset: 0;
  z-index: 100;
  display: grid;
  place-items: center;
  padding: 16px;
  background: rgba(5, 8, 16, 0.72);
  backdrop-filter: blur(6px);
  -webkit-backdrop-filter: blur(6px);
}
.modal-backdrop[hidden] { display: none !important; }
.modal-card {
  width: 100%;
  max-width: 560px;
  max-height: 90vh;
  overflow-y: auto;
  padding: 22px;
  border-radius: 16px;
  border: 1px solid var(--border);
  background: var(--panel);
  color: var(--text);
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.55);
}
.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-bottom: 14px;
  border-bottom: 1px solid var(--border);
}
.modal-title { display: flex; align-items: center; gap: 10px; font-size: 1.05rem; font-weight: 750; }
.modal-title-badge {
  width: 30px;
  height: 30px;
  display: grid;
  place-items: center;
  border-radius: 8px;
  background: var(--status-green-bg);
  border: 1px solid var(--status-green-border);
  color: var(--green);
}
.setting-group { margin-top: 18px; }
.setting-label {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}
.segmented {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 6px;
  padding: 4px;
  border-radius: 10px;
  border: 1px solid var(--border);
  background: var(--button-bg);
}
.seg-btn {
  padding: 8px 6px;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 0.82rem;
  font-weight: 650;
  cursor: pointer;
  transition: 0.15s;
}
.seg-btn:hover { color: var(--text); }
.seg-btn.active {
  background: var(--panel);
  border-color: var(--accent);
  color: var(--text);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
}
.swatch-grid { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 8px; }
.swatch-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  padding: 10px 6px;
  border-radius: 10px;
  border: 1px solid var(--border);
  background: var(--button-bg);
  cursor: pointer;
  transition: border-color 0.15s, transform 0.1s;
}
.swatch-card:hover { border-color: var(--accent); transform: translateY(-1px); }
.swatch-card input[type="color"] {
  width: 34px;
  height: 34px;
  padding: 0;
  border: 2px solid var(--border);
  border-radius: 50%;
  background: transparent;
  cursor: pointer;
  overflow: hidden;
}
.swatch-card input[type="color"]::-webkit-color-swatch-wrapper { padding: 0; }
.swatch-card input[type="color"]::-webkit-color-swatch { border: none; border-radius: 50%; }
.swatch-name { font-size: 0.75rem; font-weight: 650; color: var(--text-secondary); }
.swatch-hex { font-size: 0.7rem; font-family: monospace; color: var(--muted); }
.slider-wrap {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 14px;
  border-radius: 10px;
  border: 1px solid var(--border);
  background: var(--button-bg);
}
.slider-wrap input[type="range"] { flex: 1; accent-color: var(--accent); cursor: pointer; }
.slider-pill {
  min-width: 48px;
  text-align: center;
  padding: 3px 8px;
  border-radius: 6px;
  background: var(--panel);
  border: 1px solid var(--border);
  font-size: 0.8rem;
  font-weight: 700;
  color: var(--accent);
}
.modal-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  margin-top: 20px;
  padding-top: 14px;
  border-top: 1px solid var(--border);
}
/* Hardware Monitoring Toolbar & Views */
.view-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin: 22px 0 12px;
}
.view-title { display: flex; align-items: center; gap: 10px; }
.view-title h2 { margin: 0; font-size: 1.1rem; font-weight: 750; }
.count-badge {
  padding: 3px 9px;
  border-radius: 999px;
  border: 1px solid var(--border);
  background: var(--panel-glass);
  color: var(--muted);
  font-size: 0.78rem;
  font-weight: 600;
}
.view-switch {
  display: inline-flex;
  gap: 4px;
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--button-bg);
}
.view-btn {
  padding: 7px 12px;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 0.82rem;
  font-weight: 650;
  cursor: pointer;
  transition: 0.15s;
}
.view-btn:hover { color: var(--text); background: var(--button-hover); }
.view-btn.active {
  color: var(--green);
  background: var(--button-active-bg);
  border-color: var(--button-active-border);
}
/* Sensor Table */
.wrap {
  width: 100%;
  overflow-x: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--panel-glass);
  backdrop-filter: blur(8px);
}
table { width: 100%; border-collapse: collapse; min-width: 580px; }
th, td {
  padding: 11px 14px;
  border-bottom: 1px solid var(--table-border);
  text-align: left;
  font-size: 0.86rem;
}
th { background: var(--table-header); color: var(--muted); font-weight: 650; white-space: nowrap; }
th.num-col, td.num-col { text-align: right; }
tbody tr:last-child td { border-bottom: 0; }
tbody tr:hover { filter: brightness(1.1); }
.sensor-row-green { background: var(--status-green-bg); }
.sensor-row-yellow { background: var(--status-yellow-bg); }
.sensor-row-red { background: var(--status-red-bg); }
.sensor-row-gray { background: transparent; }
.status-pill {
  display: inline-block;
  padding: 3px 8px;
  border: 1px solid transparent;
  border-radius: 6px;
  font-size: 0.72rem;
  font-weight: 750;
  letter-spacing: 0.03em;
  white-space: nowrap;
}
.status {
  margin-top: 18px;
  padding: 11px 14px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: var(--panel-glass);
  color: var(--muted);
  font-size: 0.78rem;
  line-height: 1.6;
}
footer {
  margin-top: 22px;
  padding: 16px 0 4px;
  border-top: 1px solid var(--border);
  color: var(--muted);
  font-size: 0.78rem;
  text-align: center;
}
footer strong { color: var(--text-secondary); }

@media (max-width: 960px) {
  .grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .remote-card { grid-column: 1 / -1; }
}
@media (max-width: 640px) {
  body { padding: 12px; }
  .header-meta { align-items: flex-start; }
  .grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 9px; }
  .remote-grid { grid-template-columns: 1fr; }
  .swatch-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .card { padding: 13px; }
}
@media (max-width: 380px) {
  .grid { grid-template-columns: 1fr; }
}
</style>
</head>
<body>
<main>
  <!-- Header -->
  <header class="header">
    <div class="brand">
      <button type="button" id="btnToggleAppearance" class="brand-icon"
              title="Appearance &amp; Theme Settings" aria-label="Open Appearance Settings">
        ⌘
      </button>
      <div>
        <h1>RePOS: Remote Power Operation &amp; SCADA</h1>
        <div class="sub">
          Real-time PC Hardware Monitoring &amp; Remote Control ·
          <strong>LibreHardwareMonitor</strong>
        </div>
      </div>
    </div>
    <div class="header-meta">
      <div class="conn-badge">
        <span id="connectionDot" class="connection-dot"></span>
        <span id="connectionText">CONNECTING...</span>
      </div>
      <div>IP: <strong id="deviceIpLabel">--</strong> · Update: <span id="lastUpdate">--</span></div>
    </div>
  </header>
  <!-- System Alert + Legend -->
  <div id="alertBox" class="alert level-gray">
    <div class="alert-left">
      <div class="alert-icon" id="alertIcon">●</div>
      <div>
        <div class="alert-title" id="alertTitle">WAITING FOR DATA</div>
        <div class="alert-message" id="alertMessage">Waiting for hardware sensor data...</div>
      </div>
    </div>
    <div class="legend">
      <span><i class="dot dot-green"></i> Normal</span>
      <span><i class="dot dot-yellow"></i> Warning</span>
      <span><i class="dot dot-red"></i> Critical</span>
      <span><i class="dot dot-gray"></i> None</span>
    </div>
  </div>
  <!-- Unified 4-Column Pinned Overview + Remote Control -->
  <div class="grid" id="metricGrid">
    <!-- Row 1: Power -->
    <div class="card power-card" id="cardCpuPower">
      <div class="card-head">
        <div class="label">CPU Power</div>
        <span class="status-pill level-gray" id="pillCpuPower">NONE</span>
      </div>
      <div class="value num"><span id="cpuPower">--</span><span class="unit">W</span></div>
    </div>
    <div class="card power-card" id="cardGpuPower">
      <div class="card-head">
        <div class="label">GPU Power</div>
        <span class="status-pill level-gray" id="pillGpuPower">NONE</span>
      </div>
      <div class="value num"><span id="gpuPower">--</span><span class="unit">W</span></div>
    </div>
    <div class="card power-card" id="cardTotalPower">
      <div class="card-head">
        <div class="label">CPU + GPU Total</div>
        <span class="status-pill level-gray" id="pillTotalPower">NONE</span>
      </div>
      <div class="value num"><span id="totalPower">--</span><span class="unit">W</span></div>
    </div>
    <div class="card power-card" id="cardDcPower">
      <div class="card-head">
        <div class="label">PC Power (DC)</div>
        <span class="status-pill level-gray" id="pillDcPower">NONE</span>
      </div>
      <div class="value num"><span id="dcOutput">--</span><span class="unit">W</span></div>
    </div>
    <!-- Row 2: Power & Environment -->
    <div class="card power-card" id="cardRealPower">
      <div class="card-head">
        <div class="label">Actual Power (AC)</div>
        <span class="status-pill level-gray" id="pillRealPower">NONE</span>
      </div>
      <div class="value num"><span id="realPower">--</span><span class="unit">W</span></div>
    </div>
    <div class="card power-card" id="cardEfficiency">
      <div class="card-head">
        <div class="label">PSU Efficiency</div>
        <span class="status-pill level-gray" id="pillEfficiency">NONE</span>
      </div>
      <div class="value num"><span id="psuEfficiency">--</span><span class="unit">%</span></div>
    </div>
    <div class="card env-card" id="cardAmbient">
      <div class="card-head">
        <div class="label">PC Case Temperature</div>
        <span class="status-pill level-gray" id="pillAmbient">NONE</span>
      </div>
      <div class="value num"><span id="ambient">--</span><span class="unit">°C</span></div>
    </div>
    <div class="card env-card" id="cardPressure">
      <div class="card-head">
        <div class="label">PC Case Pressure</div>
        <span class="status-pill level-gray" id="pillPressure">NONE</span>
      </div>
      <div class="value num"><span id="pressure">--</span><span class="unit">hPa</span></div>
    </div>
    <!-- Row 3: CPU Temps + GPU Core -->
    <div class="card" id="cardCpuCoreMax">
      <div class="card-head">
        <div class="label">CPU Core Max</div>
        <span class="status-pill level-gray" id="pillCpuCoreMax">NONE</span>
      </div>
      <div class="value num"><span id="cpuCoreMax">--</span><span class="unit">°C</span></div>
    </div>
    <div class="card" id="cardCpuCoreAvg">
      <div class="card-head">
        <div class="label">CPU Core Average</div>
        <span class="status-pill level-gray" id="pillCpuCoreAvg">NONE</span>
      </div>
      <div class="value num"><span id="cpuCoreAvg">--</span><span class="unit">°C</span></div>
    </div>
    <div class="card" id="cardCpuPackage">
      <div class="card-head">
        <div class="label">CPU Package</div>
        <span class="status-pill level-gray" id="pillCpuPackage">NONE</span>
      </div>
      <div class="value num"><span id="cpuPackage">--</span><span class="unit">°C</span></div>
    </div>
    <div class="card" id="cardGpuCoreTemp">
      <div class="card-head">
        <div class="label">GPU Core</div>
        <span class="status-pill level-gray" id="pillGpuCoreTemp">NONE</span>
      </div>
      <div class="value num"><span id="gpuCoreTemp">--</span><span class="unit">°C</span></div>
    </div>
    <!-- Row 4: GPU Temps + CPU/Case Fans -->
    <div class="card" id="cardGpuHotspot">
      <div class="card-head">
        <div class="label">GPU Hot Spot</div>
        <span class="status-pill level-gray" id="pillGpuHotspot">NONE</span>
      </div>
      <div class="value num"><span id="gpuHotspot">--</span><span class="unit">°C</span></div>
    </div>
    <div class="card" id="cardGpuMemJunction">
      <div class="card-head">
        <div class="label">GPU Memory Junction</div>
        <span class="status-pill level-gray" id="pillGpuMemJunction">NONE</span>
      </div>
      <div class="value num"><span id="gpuMemJunction">--</span><span class="unit">°C</span></div>
    </div>
    <div class="card" id="cardCpuFan">
      <div class="card-head">
        <div class="label">CPU Fan</div>
        <span class="status-pill level-gray" id="pillCpuFan">NONE</span>
      </div>
      <div class="value num"><span id="cpuFan">--</span><span class="unit">RPM</span></div>
    </div>
    <div class="card" id="cardCaseFan">
      <div class="card-head">
        <div class="label">Case Fan</div>
        <span class="status-pill level-gray" id="pillCaseFan">NONE</span>
      </div>
      <div class="value num"><span id="caseFan">--</span><span class="unit">RPM</span></div>
    </div>
    <!-- Row 5: GPU Fan + Remote Hardware Control -->
    <div class="card" id="cardGpuFan">
      <div class="card-head">
        <div class="label">GPU Fan</div>
        <span class="status-pill level-gray" id="pillGpuFan">NONE</span>
      </div>
      <div class="value num"><span id="gpuFan">--</span><span class="unit">RPM</span></div>
    </div>
    <section class="card remote-card">
      <div class="label" style="font-weight:700;color:var(--text)">Remote Hardware Control</div>
      <div class="remote-grid">
        <div class="remote-item">
          <span class="remote-item-label">Power Switch</span>
          <button type="button" id="btnPcPower" class="btn btn-power">
            PC POWER
          </button>
        </div>
        <div class="remote-item">
          <span class="remote-item-label">LCD</span>
          <button type="button" id="btnLcdToggle" class="btn active">
            LCD: ON
          </button>
        </div>
      </div>
    </section>
  </div>
  <!-- Display Filters Bar -->
  <section class="filter-bar">
    <span class="filter-bar-title">Display Filters</span>
    <div class="filter-chips">
      <label class="chip checked" for="optPower">
        <input type="checkbox" id="optPower" checked>
        <span class="chip-dot"></span> Power
      </label>
      <label class="chip checked" for="optVoltage">
        <input type="checkbox" id="optVoltage" checked>
        <span class="chip-dot"></span> Voltage
      </label>
      <label class="chip checked" for="optTemp">
        <input type="checkbox" id="optTemp" checked>
        <span class="chip-dot"></span> Temperature
      </label>
      <label class="chip checked" for="optFan">
        <input type="checkbox" id="optFan" checked>
        <span class="chip-dot"></span> Fan Speed
      </label>
      <label class="chip checked" for="optEnv">
        <input type="checkbox" id="optEnv" checked>
        <span class="chip-dot"></span> Environment
      </label>
    </div>
  </section>
  <!-- Detailed Sensor Monitoring -->
  <div class="view-toolbar">
    <div class="view-title">
      <h2>Hardware Monitoring (Details)</h2>
      <span class="count-badge" id="sensorCount">0 sensors</span>
    </div>
    <div class="view-switch" role="group" aria-label="Dashboard display mode">
      <button type="button" class="view-btn active" id="btnCardView" aria-pressed="true">
        ⊞ Card View
      </button>
      <button type="button" class="view-btn" id="btnTableView" aria-pressed="false">
        ≡ Table View
      </button>
    </div>
  </div>
  <!-- Card View -->
  <section id="sensorCardView">
    <div class="grid" id="sensorCards">
      <div class="card">
        <div class="label">Sensor data</div>
        <div class="note">Waiting for sensor data...</div>
      </div>
    </div>
  </section>
  <!-- Table View -->
  <section id="sensorTableView" hidden>
    <div class="wrap">
      <table>
        <thead>
          <tr>
            <th>Device</th>
            <th>Sensor</th>
            <th class="num-col">Value</th>
            <th>Unit</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody id="rows">
          <tr><td colspan="5">Waiting for sensor data...</td></tr>
        </tbody>
      </table>
    </div>
  </section>
  <div class="status" id="status">Connecting to MCU...</div>
  <footer>
    RePOS: Remote Power Operation &amp; SCADA · Developed by <strong>Chu Xuân Hồng</strong>
  </footer>
</main>
<!-- Appearance Settings Modal (Opened via ⌘ Icon) -->
<div class="modal-backdrop" id="appearanceModal" hidden>
  <div class="modal-card" role="dialog" aria-modal="true" aria-labelledby="appearanceTitle">
    <div class="modal-header">
      <div class="modal-title" id="appearanceTitle">
        <span class="modal-title-badge">⌘</span>
        <span>Appearance Settings</span>
      </div>
      <button type="button" id="btnCloseAppearance" class="btn" style="padding:5px 10px">✕</button>
    </div>
    <div class="setting-group">
      <div class="setting-label"><span>Theme Preset</span></div>
      <div class="segmented" id="themePresetGroup">
        <button type="button" class="seg-btn active" data-mode="dark">Dark</button>
        <button type="button" class="seg-btn" data-mode="midnight">Midnight</button>
        <button type="button" class="seg-btn" data-mode="light">Light</button>
        <button type="button" class="seg-btn" data-mode="custom">Custom</button>
      </div>
    </div>
    <div class="setting-group">
      <div class="setting-label"><span>Color Palette</span></div>
      <div class="swatch-grid">
        <label class="swatch-card">
          <input type="color" id="bgColor" value="#0b1120">
          <span class="swatch-name">Background</span>
          <span class="swatch-hex" id="bgColorHex">#0b1120</span>
        </label>
        <label class="swatch-card">
          <input type="color" id="panelColor" value="#151f30">
          <span class="swatch-name">Panels</span>
          <span class="swatch-hex" id="panelColorHex">#151f30</span>
        </label>
        <label class="swatch-card">
          <input type="color" id="textColor" value="#e8eef8">
          <span class="swatch-name">Text</span>
          <span class="swatch-hex" id="textColorHex">#e8eef8</span>
        </label>
        <label class="swatch-card">
          <input type="color" id="mutedColor" value="#93a4bb">
          <span class="swatch-name">Muted</span>
          <span class="swatch-hex" id="mutedColorHex">#93a4bb</span>
        </label>
        <label class="swatch-card">
          <input type="color" id="accentColor" value="#60a5fa">
          <span class="swatch-name">Accent</span>
          <span class="swatch-hex" id="accentColorHex">#60a5fa</span>
        </label>
      </div>
    </div>
    <div class="setting-group">
      <div class="setting-label"><span>Font Size</span></div>
      <div class="segmented" id="fontSizeGroup">
        <button type="button" class="seg-btn" data-size="12px">Small</button>
        <button type="button" class="seg-btn active" data-size="14px">Normal</button>
        <button type="button" class="seg-btn" data-size="16px">Large</button>
        <button type="button" class="seg-btn" data-size="18px">XL</button>
      </div>
    </div>
    <div class="setting-group">
      <div class="setting-label">
        <span>Panel Transparency</span>
        <span style="text-transform:none;font-weight:500">0% solid – 100% clear</span>
      </div>
      <div class="slider-wrap">
        <input type="range" id="transparency" min="0" max="100" step="1" value="0">
        <span id="transparencyValue" class="slider-pill num">0%</span>
      </div>
    </div>
    <div class="modal-footer">
      <div style="display:flex;gap:8px;align-items:center">
        <button type="button" id="resetTheme" class="btn">Reset Default</button>
        <span class="note" id="themeMessage" style="margin:0"></span>
      </div>
      <button type="button" id="saveTheme" class="btn active">Save</button>
    </div>
  </div>
</div>
<script>
const $ = id => document.getElementById(id);
let current = null;
let currentView = 'card';
let currentMode = 'dark';
let currentFontSize = '14px';
let isRefreshing = false;
let lastCardSig = '';
let cardRefs = [];
let lastTableSig = '';
let rowRefs = [];
const SENSOR_FILTERS = {
  power: 'optPower',
  voltage: 'optVoltage',
  temperature: 'optTemp',
  fan: 'optFan'
};
function num(v) {
  if (v === null || v === undefined || v === '') return NaN;
  const r = Number(v);
  return Number.isFinite(r) ? r : NaN;
}
function fmt(v) { return Number.isFinite(v) ? v.toFixed(2) : '--'; }
function getLevel(v, y, r) {
  if (!Number.isFinite(v)) return -1;
  if (v >= r) return 2;
  if (v >= y) return 1;
  return 0;
}
function levelClass(l) {
  return l === 0 ? 'level-green' : l === 1 ? 'level-yellow' : l === 2 ? 'level-red' : 'level-gray';
}
function rowClass(l) {
  return l === 0 ? 'sensor-row-green' : l === 1 ? 'sensor-row-yellow' : l === 2 ? 'sensor-row-red' : 'sensor-row-gray';
}
function alertText(l) {
  return l === 0 ? 'NORMAL' : l === 1 ? 'WARNING' : l === 2 ? 'CRITICAL' : 'NONE';
}
function setTextIfChanged(el, txt) {
  if (el && el.textContent !== txt) el.textContent = txt;
}
function updateCard(id, level, pillId) {
  const card = $(id);
  const lc = levelClass(level);
  if (card && !card.classList.contains(lc)) {
    card.classList.remove('level-green', 'level-yellow', 'level-red', 'level-gray');
    card.classList.add(lc);
  }
  if (pillId) {
    const pill = $(pillId);
    if (pill) {
      const nextCls = 'status-pill ' + lc;
      if (pill.className !== nextCls) pill.className = nextCls;
      setTextIfChanged(pill, alertText(level));
    }
  }
}
function updatePinnedMetric(cardId, valueId, pillId, value, yellow, red) {
  setTextIfChanged($(valueId), fmt(value));
  const level = getLevel(value, yellow, red);
  updateCard(cardId, level, pillId);
  return level;
}
function setConnection(online, message) {
  $('connectionDot').className = 'connection-dot ' + (online ? 'online' : 'offline');
  setTextIfChanged($('connectionText'), message);
}
function updateMainAlert(level, message) {
  const nextCls = 'alert ' + levelClass(level);
  if ($('alertBox').className !== nextCls) $('alertBox').className = nextCls;
  setTextIfChanged($('alertIcon'), level === 0 ? '●' : level === 1 ? '▲' : level === 2 ? '■' : '○');
  setTextIfChanged($('alertTitle'),
    level === 0 ? 'SYSTEM NORMAL' :
    level === 1 ? 'WARNING DETECTED' :
    level === 2 ? 'CRITICAL CONDITION' : 'WAITING FOR DATA');
  setTextIfChanged($('alertMessage'), message);
}
function getSensorLevel(sensor) {
  const type = String(sensor.type ?? sensor.Type ?? '').toLowerCase();
  const name = String(sensor.name ?? sensor.Name ?? '').toLowerCase();
  const value = num(sensor.value ?? sensor.Value);
  if (!Number.isFinite(value)) return -1;
  if (type === 'temperature') return getLevel(value, 75, 95);
  if (type === 'fan') return getLevel(value, 2000, 2700);
  if (type === 'voltage') {
    if (name.includes('12v') || name.includes('12 v')) return getLevel(value, 12.60, 12.90);
    if (name.includes('5v') || name.includes('5 v')) return getLevel(value, 5.25, 5.50);
    if (name.includes('3.3v') || name.includes('3.3 v') || name.includes('3v')) {
      return getLevel(value, 3.465, 3.60);
    }
  }
  return -1;
}
function getSensorType(s) { return String(s.type ?? s.Type ?? 'Other'); }
function getSensorName(s) { return String(s.name ?? s.Name ?? 'Unknown'); }
function getSensorDevice(s) { return String(s.device ?? s.Device ?? 'Unknown'); }
function getSensorValue(s) { return num(s.value ?? s.Value); }
function getSensorUnit(s) {
  if (s.unit || s.Unit) return String(s.unit || s.Unit);
  const t = getSensorType(s).toLowerCase();
  if (t === 'power') return 'W';
  if (t === 'voltage') return 'V';
  if (t === 'temperature') return '°C';
  if (t === 'fan') return 'RPM';
  if (t === 'clock') return 'MHz';
  if (t === 'load') return '%';
  return '';
}
function getSensorCategory(s) {
  const t = getSensorType(s).toLowerCase();
  return (t === 'power' || t === 'voltage' || t === 'temperature' || t === 'fan') ? t : 'other';
}
function sensorPassesFilter(s) {
  const cat = getSensorCategory(s);
  if (cat === 'other') return true;
  const id = SENSOR_FILTERS[cat];
  return id ? $(id).checked : true;
}
function isCpuDev(d) {
  return d.includes('ryzen') || d.includes('epyc') || d.includes('threadripper') ||
         d.includes('athlon') || d.includes('cpu') || (d.includes('intel') && !d.includes('arc'));
}
function isGpuDev(d) {
  return d.includes('nvidia') || d.includes('geforce') || d.includes('quadro') ||
         d.includes('rtx') || d.includes('gtx') || d.includes('radeon') || d.includes('arc');
}
function isNuvotonDev(d) {
  return d.includes('nuvoton') || d.includes('nct6798d');
}
function findSensorVal(sensors, predicate) {
  for (const s of sensors) {
    const t = getSensorType(s).toLowerCase();
    const d = getSensorDevice(s).toLowerCase();
    const n = getSensorName(s).toLowerCase();
    if (predicate(t, d, n)) {
      const v = getSensorValue(s);
      if (Number.isFinite(v)) return v;
    }
  }
  return NaN;
}
function showMetricCards() {
  const showPower = $('optPower').checked;
  const showEnv = $('optEnv').checked;
  document.querySelectorAll('.power-card').forEach(c => { c.style.display = showPower ? '' : 'none'; });
  document.querySelectorAll('.env-card').forEach(c => { c.style.display = showEnv ? '' : 'none'; });
}
function setView(view) {
  currentView = view === 'table' ? 'table' : 'card';
  const isCard = currentView === 'card';
  $('sensorCardView').hidden = !isCard;
  $('sensorTableView').hidden = isCard;
  $('btnCardView').classList.toggle('active', isCard);
  $('btnTableView').classList.toggle('active', !isCard);
  $('btnCardView').setAttribute('aria-pressed', String(isCard));
  $('btnTableView').setAttribute('aria-pressed', String(!isCard));
  if (current) renderSensorViews(current);
}
function addCell(row, text, className) {
  const cell = document.createElement('td');
  if (className) cell.className = className;
  cell.textContent = String(text);
  row.appendChild(cell);
  return cell;
}
// In-Place DOM Patching for Sensor Cards
function renderSensorCards(sensors) {
  const container = $('sensorCards');
  if (sensors.length === 0) {
    if (lastCardSig !== '__empty__') {
      lastCardSig = '__empty__';
      cardRefs = [];
      const empty = document.createElement('div');
      empty.className = 'card';
      empty.textContent = 'No sensors available for the selected filters.';
      container.replaceChildren(empty);
    }
    return;
  }
  const sig = sensors.map(s =>
    getSensorDevice(s) + '\0' + getSensorName(s) + '\0' + getSensorUnit(s)
  ).join('\n');
  if (sig !== lastCardSig) {
    lastCardSig = sig;
    cardRefs = [];
    const frag = document.createDocumentFragment();
    sensors.forEach(sensor => {
      const card = document.createElement('div');
      card.className = 'card level-gray';
      const head = document.createElement('div');
      head.className = 'card-head';
      const label = document.createElement('div');
      label.className = 'label';
      label.textContent = getSensorName(sensor);
      const pill = document.createElement('span');
      pill.className = 'status-pill level-gray';
      pill.textContent = 'NONE';
      head.appendChild(label);
      head.appendChild(pill);
      const reading = document.createElement('div');
      reading.className = 'value num';
      const valNode = document.createTextNode('--');
      reading.appendChild(valNode);
      const unit = getSensorUnit(sensor);
      if (unit) {
        const unitSpan = document.createElement('span');
        unitSpan.className = 'unit';
        unitSpan.textContent = unit;
        reading.appendChild(unitSpan);
      }
      const note = document.createElement('div');
      note.className = 'note';
      note.textContent = getSensorDevice(sensor) + ' · ' + getSensorType(sensor);
      card.appendChild(head);
      card.appendChild(reading);
      card.appendChild(note);
      frag.appendChild(card);
      cardRefs.push({ card, pill, valNode });
    });
    container.replaceChildren(frag);
  }
  for (let i = 0; i < sensors.length; i++) {
    const sensor = sensors[i];
    const ref = cardRefs[i];
    const level = getSensorLevel(sensor);
    const lc = levelClass(level);
    const nextCardCls = 'card ' + lc;
    if (ref.card.className !== nextCardCls) ref.card.className = nextCardCls;
    const nextPillCls = 'status-pill ' + lc;
    if (ref.pill.className !== nextPillCls) ref.pill.className = nextPillCls;
    const txtLevel = alertText(level);
    if (ref.pill.textContent !== txtLevel) ref.pill.textContent = txtLevel;
    const txtVal = fmt(getSensorValue(sensor));
    if (ref.valNode.nodeValue !== txtVal) ref.valNode.nodeValue = txtVal;
  }
}
// In-Place DOM Patching for Sensor Table
function renderSensorTable(sensors) {
  const rows = $('rows');
  if (sensors.length === 0) {
    if (lastTableSig !== '__empty__') {
      lastTableSig = '__empty__';
      rowRefs = [];
      const tr = document.createElement('tr');
      const td = addCell(tr, 'No sensors available for the selected filters.');
      td.colSpan = 5;
      rows.replaceChildren(tr);
    }
    return;
  }
  const sig = sensors.map(s =>
    getSensorDevice(s) + '\0' + getSensorName(s) + '\0' + getSensorUnit(s)
  ).join('\n');
  if (sig !== lastTableSig) {
    lastTableSig = sig;
    rowRefs = [];
    const frag = document.createDocumentFragment();
    sensors.forEach(sensor => {
      const tr = document.createElement('tr');
      addCell(tr, getSensorDevice(sensor));
      addCell(tr, getSensorName(sensor));
      const valCell = addCell(tr, '--', 'num num-col');
      addCell(tr, getSensorUnit(sensor));
      const statusCell = document.createElement('td');
      const pill = document.createElement('span');
      pill.className = 'status-pill level-gray';
      pill.textContent = 'NONE';
      statusCell.appendChild(pill);
      tr.appendChild(statusCell);
      frag.appendChild(tr);
      rowRefs.push({ tr, valCell, pill });
    });
    rows.replaceChildren(frag);
  }
  for (let i = 0; i < sensors.length; i++) {
    const sensor = sensors[i];
    const ref = rowRefs[i];
    const level = getSensorLevel(sensor);
    const nextRowCls = rowClass(level);
    if (ref.tr.className !== nextRowCls) ref.tr.className = nextRowCls;
    setTextIfChanged(ref.valCell, fmt(getSensorValue(sensor)));
    const nextPillCls = 'status-pill ' + levelClass(level);
    if (ref.pill.className !== nextPillCls) ref.pill.className = nextPillCls;
    setTextIfChanged(ref.pill, alertText(level));
  }
}
function renderSensorViews(data) {
  const all = Array.isArray(data.sensors) ? data.sensors : [];
  const filtered = all.filter(sensorPassesFilter);
  setTextIfChanged($('sensorCount'), filtered.length + (filtered.length === 1 ? ' sensor' : ' sensors'));
  if (currentView === 'card') renderSensorCards(filtered);
  else renderSensorTable(filtered);
}
function render(data) {
  const cpu = num(data.cpuPowerWatts);
  const gpu = num(data.gpuPowerWatts);
  const total = num(data.totalPowerWatts);
  const dc = num(data.dcOutputWatts);
  const real = num(data.realPowerWatts);
  const eff = num(data.psuEfficiencyPercent);
  const ambient = num(data.ambientTempC);
  const pressure = num(data.pressureHpa);
  setTextIfChanged($('cpuPower'), fmt(cpu));
  setTextIfChanged($('gpuPower'), fmt(gpu));
  setTextIfChanged($('totalPower'), fmt(total));
  setTextIfChanged($('dcOutput'), fmt(dc));
  setTextIfChanged($('realPower'), fmt(real));
  setTextIfChanged($('psuEfficiency'), fmt(eff));
  setTextIfChanged($('ambient'), fmt(ambient));
  setTextIfChanged($('pressure'), fmt(pressure));
  const cpuLevel = getLevel(cpu, 300, 400);
  const gpuLevel = getLevel(gpu, 300, 450);
  const totalLevel = getLevel(total, 600, 800);
  const dcLevel = getLevel(dc, 800, 900);
  const ambientLevel = getLevel(ambient, 45, 55);
  updateCard('cardCpuPower', cpuLevel, 'pillCpuPower');
  updateCard('cardGpuPower', gpuLevel, 'pillGpuPower');
  updateCard('cardTotalPower', totalLevel, 'pillTotalPower');
  updateCard('cardDcPower', dcLevel, 'pillDcPower');
  updateCard('cardRealPower', dcLevel, 'pillRealPower');
  updateCard('cardEfficiency', -1, 'pillEfficiency');
  updateCard('cardAmbient', ambientLevel, 'pillAmbient');
  updateCard('cardPressure', -1, 'pillPressure');
  const sensors = Array.isArray(data.sensors) ? data.sensors : [];
  const valCpuMax = findSensorVal(sensors, (t, d, n) =>
    t === 'temperature' && isCpuDev(d) && n.includes('core max'));
  const valCpuAvg = findSensorVal(sensors, (t, d, n) =>
    t === 'temperature' && isCpuDev(d) && n.includes('core average'));
  const valCpuPkg = findSensorVal(sensors, (t, d, n) =>
    t === 'temperature' && isCpuDev(d) && (n === 'cpu package' || n === 'package' || n.includes('package')));
  const valGpuCore = findSensorVal(sensors, (t, d, n) =>
    t === 'temperature' && isGpuDev(d) && (n === 'gpu core' || n.includes('gpu core')));
  const valGpuHot = findSensorVal(sensors, (t, d, n) =>
    t === 'temperature' && isGpuDev(d) && (n.includes('hot spot') || n.includes('hotspot')));
  const valGpuMem = findSensorVal(sensors, (t, d, n) =>
    t === 'temperature' && isGpuDev(d) && (n.includes('memory junction') || n.includes('gpu memory') || n.includes('vram')));
  const valCpuFan = findSensorVal(sensors, (t, d, n) =>
    t === 'fan' && (n === 'cpu fan' || (isNuvotonDev(d) && n === 'fan #2')));
  const valCaseFan = findSensorVal(sensors, (t, d, n) =>
    t === 'fan' && (n === 'case fan' || (isNuvotonDev(d) && n === 'fan #3')));
  const valGpuFan = findSensorVal(sensors, (t, d, n) =>
    t === 'fan' && (isGpuDev(d) || n.includes('gpu fan')));
  updatePinnedMetric('cardCpuCoreMax', 'cpuCoreMax', 'pillCpuCoreMax', valCpuMax, 75, 95);
  updatePinnedMetric('cardCpuCoreAvg', 'cpuCoreAvg', 'pillCpuCoreAvg', valCpuAvg, 75, 95);
  updatePinnedMetric('cardCpuPackage', 'cpuPackage', 'pillCpuPackage', valCpuPkg, 75, 95);
  updatePinnedMetric('cardGpuCoreTemp', 'gpuCoreTemp', 'pillGpuCoreTemp', valGpuCore, 75, 95);
  updatePinnedMetric('cardGpuHotspot', 'gpuHotspot', 'pillGpuHotspot', valGpuHot, 75, 95);
  updatePinnedMetric('cardGpuMemJunction', 'gpuMemJunction', 'pillGpuMemJunction', valGpuMem, 75, 95);
  updatePinnedMetric('cardCpuFan', 'cpuFan', 'pillCpuFan', valCpuFan, 2000, 2700);
  updatePinnedMetric('cardCaseFan', 'caseFan', 'pillCaseFan', valCaseFan, 2000, 2700);
  updatePinnedMetric('cardGpuFan', 'gpuFan', 'pillGpuFan', valGpuFan, 2000, 2700);
  showMetricCards();
  renderSensorViews(data);
  let highest = Math.max(cpuLevel, gpuLevel, totalLevel, dcLevel, ambientLevel);
  for (const s of sensors) {
    const l = getSensorLevel(s);
    if (l > highest) highest = l;
  }
  const age = num(data.lastPcDataAgeMs);
  const pcOffline = !Number.isFinite(age) || age > 5000;
  if (pcOffline) {
    updateMainAlert(-1, 'PC data is missing or stale. Check the PC monitoring application.');
  } else if (highest === 2) {
    updateMainAlert(2, 'At least one monitored value has reached its critical threshold.');
  } else if (highest === 1) {
    updateMainAlert(1, 'At least one monitored value has reached its warning threshold.');
  } else if (highest === 0) {
    updateMainAlert(0, 'All monitored values are within their configured thresholds.');
  } else {
    updateMainAlert(-1, 'No valid sensor readings are currently available.');
  }
}
async function refresh() {
  if (isRefreshing || document.hidden) return;
  isRefreshing = true;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 2500);
  try {
    const res = await fetch('/api', { cache: 'no-store', signal: ctrl.signal });
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const data = await res.json();
    current = data;
    render(data);
    if (typeof data.lcdEnabled === 'boolean' && !lcdRequestPending) {
      updateLcdButton(data.lcdEnabled);
    }
    const age = num(data.lastPcDataAgeMs);
    const pcOnline = Number.isFinite(age) && age <= 5000;
    setConnection(pcOnline, pcOnline ? 'PC ONLINE' : 'PC OFFLINE');
    setTextIfChanged($('deviceIpLabel'), data.deviceIp || location.hostname);
    setTextIfChanged($('lastUpdate'), new Date().toLocaleTimeString());
    setTextIfChanged($('status'),
      `MCU: ESP8266 | Wi-Fi Channel: ${data.wifiChannel ?? '--'} | Interval: 1s | MAC: ${data.macAddress ?? '--'} | Gateway: ${data.gatewayIp ?? '--'}`);
  } catch (error) {
    setConnection(false, 'MCU OFFLINE');
    setTextIfChanged($('status'), 'Dashboard connection error: ' + (error.name === 'AbortError' ? 'Timeout' : error.message));
    updateMainAlert(-1, 'Cannot connect to the ESP8266. Check Wi-Fi and the device IP address.');
  } finally {
    clearTimeout(timer);
    isRefreshing = false;
  }
}
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) refresh();
});
// LCD Control
let lcdIsEnabled = true;
let lcdRequestPending = false;
function updateLcdButton(enabled) {
  lcdIsEnabled = enabled;
  const button = $('btnLcdToggle');
  setTextIfChanged(button, enabled ? 'LCD: ON' : 'LCD: OFF');
  button.classList.toggle('active', enabled);
  button.disabled = lcdRequestPending;
}
$('btnLcdToggle').addEventListener('click', async () => {
  if (lcdRequestPending) return;
  lcdRequestPending = true;
  const button = $('btnLcdToggle');
  button.disabled = true;
  try {
    const res = await fetch('/lcd/control', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: !lcdIsEnabled })
    });
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const data = await res.json();
    if (data.ok !== true) throw new Error('LCD command failed');
    updateLcdButton(data.lcdEnabled === true);
  } catch {
    button.textContent = '✕ FAILED';
    setTimeout(() => updateLcdButton(lcdIsEnabled), 1500);
  } finally {
    lcdRequestPending = false;
    button.disabled = false;
  }
});
// PC Power Control
let pcPowerRequestPending = false;
$('btnPcPower').addEventListener('click', async () => {
  if (pcPowerRequestPending) return;
  pcPowerRequestPending = true;
  const button = $('btnPcPower');
  button.disabled = true;
  button.textContent = 'SENDING...';
  try {
    const res = await fetch('/pc/power', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ press: true })
    });
    const data = await res.json();
    if (!res.ok || data.ok !== true) throw new Error(data.error || ('HTTP ' + res.status));
    button.textContent = '✓ SENT';
  } catch {
    button.textContent = '✕ FAILED';
  } finally {
    setTimeout(() => {
      pcPowerRequestPending = false;
      button.disabled = false;
      button.textContent = 'PC POWER';
    }, 1200);
  }
});
// View & Display Filters
$('btnCardView').addEventListener('click', () => setView('card'));
$('btnTableView').addEventListener('click', () => setView('table'));
function getFiltersState() {
  return {
    power: $('optPower').checked,
    voltage: $('optVoltage').checked,
    temperature: $('optTemp').checked,
    fan: $('optFan').checked,
    env: $('optEnv').checked
  };
}
function applyFiltersState(f) {
  if (!f || typeof f !== 'object') return;
  const map = {
    optPower: f.power !== false,
    optVoltage: f.voltage !== false,
    optTemp: f.temperature !== false,
    optFan: f.fan !== false,
    optEnv: f.env !== false
  };
  Object.entries(map).forEach(([id, checked]) => {
    const cb = $(id);
    if (!cb) return;
    cb.checked = checked;
    const chip = cb.closest('.chip');
    if (chip) chip.classList.toggle('checked', checked);
  });
}
async function saveFiltersToMcu(f) {
  try { localStorage.setItem('pcDashboardFilters', JSON.stringify(f)); } catch {}
  try {
    await fetch('/filters', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(f)
    });
  } catch {}
}
['optPower', 'optVoltage', 'optTemp', 'optFan', 'optEnv'].forEach(id => {
  const cb = $(id);
  const chip = cb.closest('.chip');
  cb.addEventListener('change', () => {
    if (chip) chip.classList.toggle('checked', cb.checked);
    if (current) render(current);
    else showMetricCards();
    saveFiltersToMcu(getFiltersState());
  });
});
// Appearance Modal (Opened via ⌘ Icon)
function openAppearance() { $('appearanceModal').hidden = false; }
function closeAppearance() { $('appearanceModal').hidden = true; }
$('btnToggleAppearance').addEventListener('click', () => {
  if ($('appearanceModal').hidden) openAppearance();
  else closeAppearance();
});
$('btnCloseAppearance').addEventListener('click', closeAppearance);
$('appearanceModal').addEventListener('click', e => {
  if (e.target === $('appearanceModal')) closeAppearance();
});
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && !$('appearanceModal').hidden) closeAppearance();
});
// Theme Presets & Persistence
const PRESETS = {
  dark: {
    mode: 'dark', bg: '#0b1120', panel: '#151f30',
    text: '#e8eef8', muted: '#93a4bb', accent: '#60a5fa',
    fontSize: '14px', transparency: 0
  },
  midnight: {
    mode: 'midnight', bg: '#060913', panel: '#0f172a',
    text: '#f1f5f9', muted: '#94a3b8', accent: '#38bdf8',
    fontSize: '14px', transparency: 15
  },
  light: {
    mode: 'light', bg: '#f1f5f9', panel: '#ffffff',
    text: '#172033', muted: '#64748b', accent: '#2563eb',
    fontSize: '14px', transparency: 0
  }
};
function hexToRgba(hex, alpha) {
  const clean = hex.replace('#', '');
  const r = parseInt(clean.substring(0, 2), 16);
  const g = parseInt(clean.substring(2, 4), 16);
  const b = parseInt(clean.substring(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}
function isLightHex(hex) {
  const clean = hex.replace('#', '');
  const r = parseInt(clean.substring(0, 2), 16);
  const g = parseInt(clean.substring(2, 4), 16);
  const b = parseInt(clean.substring(4, 6), 16);
  return (0.299 * r + 0.587 * g + 0.114 * b) > 145;
}
function syncSegmentedButtons(mode, fontSize) {
  currentMode = mode;
  currentFontSize = fontSize;
  document.querySelectorAll('#themePresetGroup .seg-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.mode === mode);
  });
  document.querySelectorAll('#fontSizeGroup .seg-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.size === fontSize);
  });
}
function applyTheme(settings) {
  const root = document.documentElement;
  const transparency = Math.min(100, Math.max(0, Number(settings.transparency ?? 0)));
  const opacity = +(1 - transparency / 100).toFixed(2);
  const mode = settings.mode || 'custom';
  const fontSize = ['12px', '14px', '16px', '18px'].includes(settings.fontSize)
    ? settings.fontSize : '14px';
  syncSegmentedButtons(mode, fontSize);
  root.style.setProperty('--bg', settings.bg);
  root.style.setProperty('--panel', settings.panel);
  root.style.setProperty('--panel-glass', hexToRgba(settings.panel, opacity));
  root.style.setProperty('--text', settings.text);
  root.style.setProperty('--muted', settings.muted);
  root.style.setProperty('--accent', settings.accent);
  root.style.setProperty('--dashboard-font-size', fontSize);
  $('bgColor').value = settings.bg;
  $('bgColorHex').textContent = settings.bg;
  $('panelColor').value = settings.panel;
  $('panelColorHex').textContent = settings.panel;
  $('textColor').value = settings.text;
  $('textColorHex').textContent = settings.text;
  $('mutedColor').value = settings.muted;
  $('mutedColorHex').textContent = settings.muted;
  $('accentColor').value = settings.accent;
  $('accentColorHex').textContent = settings.accent;
  $('transparency').value = transparency;
  $('transparencyValue').textContent = transparency + '%';
  const lightBg = mode === 'light' || isLightHex(settings.bg);
  if (lightBg) {
    root.style.setProperty('color-scheme', 'light');
    root.style.setProperty('--bg-glow', '#dbeafe');
    root.style.setProperty('--border', '#cbd5e1');
    root.style.setProperty('--button-bg', `rgba(226, 232, 240, ${Math.max(0.25, opacity)})`);
    root.style.setProperty('--button-hover', '#cbd5e1');
    root.style.setProperty('--button-active-bg', `rgba(220, 252, 231, ${Math.max(0.35, opacity)})`);
    root.style.setProperty('--button-active-border', '#22c55e');
    root.style.setProperty('--table-header', '#e2e8f0');
    root.style.setProperty('--table-border', '#cbd5e1');
    root.style.setProperty('--text-secondary', '#334155');
    root.style.setProperty('--status-green-bg', `rgba(220, 252, 231, ${opacity})`);
    root.style.setProperty('--status-yellow-bg', `rgba(254, 249, 195, ${opacity})`);
    root.style.setProperty('--status-red-bg', `rgba(255, 228, 230, ${opacity})`);
  } else {
    root.style.setProperty('color-scheme', 'dark');
    root.style.setProperty('--bg-glow', '#152137');
    root.style.setProperty('--border', '#29374c');
    root.style.setProperty('--button-bg', `rgba(17, 26, 41, ${Math.max(0.2, opacity * 0.85)})`);
    root.style.setProperty('--button-hover', 'rgba(32, 45, 64, 0.9)');
    root.style.setProperty('--button-active-bg', `rgba(16, 48, 35, ${Math.max(0.25, opacity)})`);
    root.style.setProperty('--button-active-border', '#28664b');
    root.style.setProperty('--table-header', '#1a2639');
    root.style.setProperty('--table-border', '#293548');
    root.style.setProperty('--text-secondary', '#b9c8dc');
    root.style.setProperty('--status-green-bg', `rgba(16, 41, 30, ${opacity})`);
    root.style.setProperty('--status-yellow-bg', `rgba(48, 39, 14, ${opacity})`);
    root.style.setProperty('--status-red-bg', `rgba(53, 23, 23, ${opacity})`);
  }
}
function getThemeSettings() {
  return {
    mode: currentMode,
    bg: $('bgColor').value,
    panel: $('panelColor').value,
    text: $('textColor').value,
    muted: $('mutedColor').value,
    accent: $('accentColor').value,
    fontSize: currentFontSize,
    transparency: Number($('transparency').value)
  };
}
function sanitizeSettings(s) {
  const validHex = v => typeof v === 'string' && /^#[0-9a-fA-F]{6}$/.test(v);
  const validSizes = ['12px', '14px', '16px', '18px'];
  return {
    mode: ['dark', 'midnight', 'light', 'custom'].includes(s.mode) ? s.mode : 'custom',
    bg: validHex(s.bg) ? s.bg : PRESETS.dark.bg,
    panel: validHex(s.panel) ? s.panel : PRESETS.dark.panel,
    text: validHex(s.text) ? s.text : PRESETS.dark.text,
    muted: validHex(s.muted) ? s.muted : PRESETS.dark.muted,
    accent: validHex(s.accent) ? s.accent : PRESETS.dark.accent,
    fontSize: validSizes.includes(s.fontSize) ? s.fontSize : '14px',
    transparency: Number.isFinite(Number(s.transparency))
      ? Math.min(100, Math.max(0, Number(s.transparency))) : 0
  };
}
async function saveSettingsToMcu(settings) {
  try { localStorage.setItem('pcDashboardTheme', JSON.stringify(settings)); } catch {}
  const res = await fetch('/settings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings)
  });
  if (!res.ok) throw new Error('HTTP ' + res.status);
  const data = await res.json();
  if (data.ok !== true) throw new Error('Save failed');
}
document.querySelectorAll('#themePresetGroup .seg-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    const mode = btn.dataset.mode;
    if (PRESETS[mode]) {
      applyTheme({ ...PRESETS[mode], fontSize: currentFontSize });
    } else {
      currentMode = 'custom';
      applyTheme(getThemeSettings());
    }
    $('themeMessage').textContent = 'Preview updated — click Save to MCU.';
  });
});
document.querySelectorAll('#fontSizeGroup .seg-btn').forEach(btn => {
  btn.addEventListener('click', () => {
    currentFontSize = btn.dataset.size;
    applyTheme(getThemeSettings());
    $('themeMessage').textContent = 'Unsaved changes.';
  });
});
['bgColor', 'panelColor', 'textColor', 'mutedColor', 'accentColor', 'transparency'].forEach(id => {
  $(id).addEventListener('input', () => {
    if (id !== 'transparency') currentMode = 'custom';
    applyTheme(getThemeSettings());
    $('themeMessage').textContent = 'Unsaved changes.';
  });
});
$('saveTheme').addEventListener('click', async () => {
  const btn = $('saveTheme');
  btn.disabled = true;
  $('themeMessage').textContent = 'Saving to ESP8266...';
  try {
    await saveSettingsToMcu(getThemeSettings());
    $('themeMessage').textContent = 'Saved to MCU EEPROM!';
  } catch (err) {
    $('themeMessage').textContent = 'Save failed: ' + err.message;
  } finally {
    btn.disabled = false;
  }
});
$('resetTheme').addEventListener('click', async () => {
  const btn = $('resetTheme');
  btn.disabled = true;
  const def = { ...PRESETS.dark };
  applyTheme(def);
  try {
    await saveSettingsToMcu(def);
    $('themeMessage').textContent = 'Reset & saved to MCU.';
  } catch {
    $('themeMessage').textContent = 'Reset locally (MCU offline).';
  } finally {
    btn.disabled = false;
  }
});
(function loadCachedState() {
  try {
    const savedTheme = localStorage.getItem('pcDashboardTheme');
    applyTheme(savedTheme ? sanitizeSettings(JSON.parse(savedTheme)) : PRESETS.dark);
  } catch {
    applyTheme(PRESETS.dark);
  }
  try {
    const savedFilters = localStorage.getItem('pcDashboardFilters');
    if (savedFilters) applyFiltersState(JSON.parse(savedFilters));
  } catch {}
})();
async function loadSettingsFromMcu() {
  try {
    const res = await fetch('/settings', { cache: 'no-store' });
    if (!res.ok) return;
    const data = await res.json();
    const s = sanitizeSettings(data);
    applyTheme(s);
    try { localStorage.setItem('pcDashboardTheme', JSON.stringify(s)); } catch {}
    if (typeof data.lcdEnabled === 'boolean') {
      updateLcdButton(data.lcdEnabled);
    }
    if (data.filters) {
      applyFiltersState(data.filters);
      try { localStorage.setItem('pcDashboardFilters', JSON.stringify(data.filters)); } catch {}
      if (current) render(current);
      else showMetricCards();
    }
  } catch {}
}
// Start Dashboard
setView('card');
showMetricCards();
loadSettingsFromMcu();
refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
)HTML";
// ESP-NOW RECEIVE CALLBACK
void onEspNowReceive(uint8_t *mac, uint8_t *data, uint8_t len) {
  if (len != 1 || data[0] != 1) return;
  if (pendingCommands < MAX_PENDING_COMMANDS) {
    pendingCommands++;
  }
}
// ESP-NOW INITIALIZATION
bool initEspNow() {
  uint8_t currentChannel = WiFi.channel();
  if (currentChannel != ESPNOW_CHANNEL) {
    Serial.println(F("ESP-NOW channel mismatch"));
    Serial.print(F("Current router channel: "));
    Serial.println(currentChannel);
    Serial.print(F("Required channel: "));
    Serial.println(ESPNOW_CHANNEL);
    return false;
  }
  if (esp_now_init() != 0) {
    Serial.println(F("ESP-NOW initialization failed"));
    return false;
  }
  esp_now_set_self_role(ESP_NOW_ROLE_SLAVE);
  if (esp_now_register_recv_cb(onEspNowReceive) != 0) {
    Serial.println(F("ESP-NOW callback registration failed"));
    esp_now_deinit();
    return false;
  }
  Serial.println(F("ESP-NOW receiver ready"));
  Serial.print(F("ESP-NOW channel: "));
  Serial.println(currentChannel);
  return true;
}
// OPTOCOUPLER CONTROL
void processOptoCommands() {
  if (pulseActive && millis() - pulseStart >= PULSE_MS) {
    digitalWrite(OPTO_PIN, HIGH);
    pulseActive = false;
    Serial.println(F("PWR_SW released"));
  }
  if (!pulseActive && pendingCommands > 0) {
    noInterrupts();
    if (pendingCommands > 0) {
      pendingCommands--;
    }
    interrupts();
    digitalWrite(OPTO_PIN, LOW);
    pulseStart = millis();
    pulseActive = true;
    Serial.println(F("PWR_SW pressed"));
  }
}
// RECEIVE PC DATA (Zero String allocation inside sensor loop)
void handleData() {
  if (!server.hasArg("plain")) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Missing request body\"}");
    return;
  }
  DynamicJsonDocument doc(10240);
  DeserializationError err = deserializeJson(doc, server.arg("plain"));
  if (err || !doc.is<JsonObject>() || !doc["sensors"].is<JsonArray>()) {
    Serial.printf("JSON parse error: %s\n", err ? err.c_str() : "Missing sensors array");
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Invalid JSON\"}");
    return;
  }
  JsonArray sensors = doc["sensors"].as<JsonArray>();
  for (int i = (int)sensors.size() - 1; i >= 0; --i) {
    JsonObject sensor = sensors[i];
    const char* device = sensor["device"] | "";
    const char* name = sensor["name"] | "";
    const char* type = sensor["type"] | "";
    if (strContainsCI(device, "nuvoton") || strContainsCI(device, "nct6798d")) {
      if (strcmp(type, "Fan") == 0 && strcmp(name, "Fan #2") == 0) {
        sensor["name"] = "CPU Fan";
      } else if (strcmp(type, "Fan") == 0 && strcmp(name, "Fan #3") == 0) {
        sensor["name"] = "Case Fan";
      } else {
        sensors.remove(i);
      }
    }
  }
  calculateRealPower(doc);
  size_t needed = measureJson(doc) + 1;
  latestJson = "";
  latestJson.reserve(needed);
  serializeJson(doc, latestJson);
  lastPcData = millis();
  Serial.printf("PC data: %u bytes | %u sensors | Heap: %u\n",
                (unsigned)latestJson.length(),
                (unsigned)sensors.size(),
                (unsigned)ESP.getFreeHeap());

  server.send(200, "application/json", "{\"ok\":true}");
}
// WEB API
void handleApi() {
  if (latestJson.length() < 2 || !latestJson.endsWith("}")) {
    server.send(500, "application/json", "{\"error\":\"Invalid stored JSON\"}");
    return;
  }
  String output;
  output.reserve(latestJson.length() + 240);
  output = latestJson.substring(0, latestJson.length() - 1);
  output += F(",\"ambientTempC\":");
  output += isfinite(ambientTemp) ? String(ambientTemp, 2) : F("null");
  output += F(",\"pressureHpa\":");
  output += isfinite(pressureHpa) ? String(pressureHpa, 2) : F("null");
  output += F(",\"lastPcDataAgeMs\":");
  output += (lastPcData == 0) ? String(F("null")) : String((unsigned long)(millis() - lastPcData));
  output += F(",\"lcdEnabled\":");
  output += lcdEnabled ? F("true") : F("false");
  output += F(",\"deviceIp\":\"");
  output += WiFi.localIP().toString();
  output += F("\",\"wifiChannel\":");
  output += String(WiFi.channel());
  output += F(",\"macAddress\":\"");
  output += macStr;
  output += F("\",\"gatewayIp\":\"");
  output += WiFi.gatewayIP().toString();
  output += F("\"}");
  server.sendHeader("Cache-Control", "no-store");
  server.sendHeader("Access-Control-Allow-Origin", "*");
  server.send(200, "application/json", output);
}
// SETTINGS API
void handleGetSettings() {
  StaticJsonDocument<512> doc;
  doc["lcdEnabled"] = lcdEnabled;
  doc["mode"] = appSettings.themeMode;
  doc["bg"] = appSettings.bg;
  doc["panel"] = appSettings.panel;
  doc["text"] = appSettings.text;
  doc["muted"] = appSettings.muted;
  doc["accent"] = appSettings.accent;
  doc["fontSize"] = String(appSettings.fontSize) + "px";
  doc["transparency"] = appSettings.transparency;
  JsonObject filters = doc.createNestedObject("filters");
  filters["power"] = (appSettings.filterMask & (1 << 0)) != 0;
  filters["voltage"] = (appSettings.filterMask & (1 << 1)) != 0;
  filters["temperature"] = (appSettings.filterMask & (1 << 2)) != 0;
  filters["fan"] = (appSettings.filterMask & (1 << 3)) != 0;
  filters["env"] = (appSettings.filterMask & (1 << 4)) != 0;
  String output;
  serializeJson(doc, output);
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", output);
}
void handleSaveSettings() {
  if (!server.hasArg("plain")) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Missing JSON body\"}");
    return;
  }
  StaticJsonDocument<512> doc;
  DeserializationError err = deserializeJson(doc, server.arg("plain"));
  if (err || !doc.is<JsonObject>()) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Invalid JSON\"}");
    return;
  }
  const char* mode = doc["mode"] | "";
  const char* bg = doc["bg"] | "";
  const char* panel = doc["panel"] | "";
  const char* text = doc["text"] | "";
  const char* muted = doc["muted"] | "";
  const char* accent = doc["accent"] | "";
  if (!isValidHexColor(bg) || !isValidHexColor(panel) ||
      !isValidHexColor(text) || !isValidHexColor(muted) ||
      !isValidHexColor(accent)) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Invalid hex color\"}");
    return;
  }
  int t = doc["transparency"] | 0;
  if (t < 0) t = 0;
  if (t > 100) t = 100;
  uint8_t fs = 14;
  if (doc["fontSize"].is<int>()) {
    fs = (uint8_t)doc["fontSize"].as<int>();
  } else if (doc["fontSize"].is<const char*>()) {
    fs = (uint8_t)atoi(doc["fontSize"].as<const char*>());
  }
  if (fs != 12 && fs != 14 && fs != 16 && fs != 18) fs = 14;

  if (strlen(mode) > 0) {
    strlcpy(appSettings.themeMode, mode, sizeof(appSettings.themeMode));
  } else {
    strlcpy(appSettings.themeMode, "custom", sizeof(appSettings.themeMode));
  }
  strlcpy(appSettings.bg, bg, sizeof(appSettings.bg));
  strlcpy(appSettings.panel, panel, sizeof(appSettings.panel));
  strlcpy(appSettings.text, text, sizeof(appSettings.text));
  strlcpy(appSettings.muted, muted, sizeof(appSettings.muted));
  strlcpy(appSettings.accent, accent, sizeof(appSettings.accent));
  appSettings.fontSize = fs;
  appSettings.transparency = (uint8_t)t;
  saveSettings();
  Serial.println(F("Appearance settings saved to EEPROM"));
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", "{\"ok\":true}");
}
void handleSaveFilters() {
  if (!server.hasArg("plain")) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Missing JSON body\"}");
    return;
  }
  StaticJsonDocument<192> doc;
  DeserializationError err = deserializeJson(doc, server.arg("plain"));
  if (err || !doc.is<JsonObject>()) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Invalid JSON\"}");
    return;
  }
  uint8_t mask = 0;
  if (doc["power"] | true)       mask |= (1 << 0);
  if (doc["voltage"] | true)     mask |= (1 << 1);
  if (doc["temperature"] | true) mask |= (1 << 2);
  if (doc["fan"] | true)         mask |= (1 << 3);
  if (doc["env"] | true)         mask |= (1 << 4);
  appSettings.filterMask = mask;
  saveSettings();
  Serial.printf("Display filters saved to EEPROM (mask=0x%02X)\n", mask);
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", "{\"ok\":true}");
}
// LCD WEB CONTROL
void handleLcdStatus() {
  String json = "{\"lcdEnabled\":";
  json += lcdEnabled ? "true" : "false";
  json += "}";
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", json);
}
void handleLcdControl() {
  if (!server.hasArg("plain")) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Missing JSON body\"}");
    return;
  }
  StaticJsonDocument<64> doc;
  DeserializationError err = deserializeJson(doc, server.arg("plain"));
  if (err || !doc["enabled"].is<bool>()) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Invalid enabled value\"}");
    return;
  }
  lcdEnabled = doc["enabled"].as<bool>();
  if (lcdEnabled) {
    lcd.backlight();
    showLcdPage();
  } else {
    lcd.noBacklight();
  }
  saveSettings();
  String json = "{\"ok\":true,\"lcdEnabled\":";
  json += lcdEnabled ? "true" : "false";
  json += "}";
  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", json);
  Serial.printf("LCD backlight saved to EEPROM: %s\n", lcdEnabled ? "ON" : "OFF");
}
// PC POWER DASHBOARD CONTROL
void handlePcPowerControl() {
  if (!server.hasArg("plain")) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Missing JSON body\"}");
    return;
  }
  StaticJsonDocument<64> doc;
  DeserializationError err = deserializeJson(doc, server.arg("plain"));
  if (err || !doc["press"].is<bool>() || doc["press"].as<bool>() != true) {
    server.send(400, "application/json", "{\"ok\":false,\"error\":\"Invalid command\"}");
    return;
  }
  bool queued = false;
  noInterrupts();
  if (pendingCommands < MAX_PENDING_COMMANDS) {
    pendingCommands++;
    queued = true;
  }
  interrupts();
  if (!queued) {
    server.send(429, "application/json", "{\"ok\":false,\"error\":\"Command queue full\"}");
    return;
  }
  server.sendHeader("Cache-Control", "no-store");
  server.send(202, "application/json", "{\"ok\":true,\"queued\":true,\"pulseMs\":600}");
  Serial.println(F("PC power command queued"));
}
// BMP280
void readBmp() {
  if (!bmpAvailable || millis() - lastBmpRead < 1000) return;
  lastBmpRead = millis();
  float temperature = bmp.readTemperature();
  float pressure = bmp.readPressure() / 100.0F;
  if (isfinite(temperature) && isfinite(pressure) && pressure > 0) {
    ambientTemp = temperature;
    pressureHpa = pressure;
  }
}
// LCD DISPLAY
void showLcdPage() {
  String line1;
  String line2;
  switch (lcdPage) {
    case 0:
      line1 = "CPU Power";
      line2 = isfinite(cpuPowerWatts) ? String(cpuPowerWatts, 1) + " W" : "Waiting for PC";
      break;
    case 1:
      line1 = "GPU Power";
      line2 = isfinite(gpuPowerWatts) ? String(gpuPowerWatts, 1) + " W" : "Waiting for PC";
      break;
    case 2:
      line1 = "CPU + GPU Total";
      line2 = isfinite(totalPowerWatts) ? String(totalPowerWatts, 1) + " W" : "Waiting for PC";
      break;
    case 3:
      line1 = "Real Power AC";
      line2 = isfinite(realPowerWatts) ? String(realPowerWatts, 1) + " W EST" : "Waiting for PC";
      break;
    case 4:
      line1 = isfinite(ambientTemp) ? "Temp: " + String(ambientTemp, 1) + " C" : "Temp: --.- C";
      line2 = isfinite(pressureHpa) ? "Pres: " + String(pressureHpa, 1) + " hPa" : "Pres: ---- hPa";
      break;
    default:
      lcdPage = 0;
      line1 = "CPU Power";
      line2 = isfinite(cpuPowerWatts) ? String(cpuPowerWatts, 1) + " W" : "Waiting for PC";
      break;
  }
  lcdLine(0, line1);
  lcdLine(1, line2);
}
// WIFI CONNECTION
bool connectWiFi() {
  WiFi.mode(WIFI_STA);
  WiFi.persistent(false);
  WiFi.setSleepMode(WIFI_NONE_SLEEP);
  WiFi.setAutoReconnect(true);
  if (!WiFi.config(local_IP, gateway, subnet, primaryDNS)) {
    Serial.println(F("Static IP configuration failed"));
    return false;
  }
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  Serial.print(F("Connecting to Wi-Fi"));
  unsigned long startTime = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - startTime < 30000) {
    delay(300);
    Serial.print('.');
  }
  Serial.println();
  if (WiFi.status() != WL_CONNECTED) {
    Serial.println(F("Wi-Fi connection failed"));
    return false;
  }
  strlcpy(macStr, WiFi.macAddress().c_str(), sizeof(macStr));
  Serial.println(F("Wi-Fi connected"));
  Serial.print(F("ESP8266 IP: "));
  Serial.println(WiFi.localIP());
  Serial.print(F("Gateway: "));
  Serial.println(WiFi.gatewayIP());
  Serial.print(F("Wi-Fi channel: "));
  Serial.println(WiFi.channel());
  return true;
}
// SETUP
void setup() {
  Serial.begin(115200);
  delay(100);
  Serial.println();
  Serial.println(F("PC Monitor - ESP8266"));
  loadSettings();
  digitalWrite(OPTO_PIN, HIGH);
  pinMode(OPTO_PIN, OUTPUT);
  Wire.begin(SDA_PIN, SCL_PIN);
  lcd.init();
  if (lcdEnabled) {
    lcd.backlight();
  } else {
    lcd.noBacklight();
  }
  lcdLine(0, "PC Monitor");
  lcdLine(1, "Starting...");
  bmpAvailable = bmp.begin(0x76);
  if (!bmpAvailable) {
    bmpAvailable = bmp.begin(0x77);
  }
  Serial.printf("BMP280 status: %s\n", bmpAvailable ? "FOUND" : "NOT FOUND");
  lcdLine(0, "Connecting WiFi");
  lcdLine(1, "Please wait");
  if (!connectWiFi()) {
    lcdLine(0, "WiFi Failed");
    lcdLine(1, "Check Settings");
    Serial.println(F("Check SSID, password, static IP and subnet."));
    while (true) {
      delay(1000);
    }
  }
  espNowAvailable = initEspNow();
  if (!espNowAvailable) {
    Serial.println(F("Warning: ESP-NOW remote control is unavailable"));
  }
  lcdLine(0, "WiFi Connected");
  lcdLine(1, WiFi.localIP().toString());
  delay(1500);
  server.on("/", HTTP_GET, []() {
    server.send_P(200, "text/html; charset=utf-8", INDEX_HTML);
  });
  server.on("/favicon.svg", HTTP_GET, []() {
    server.send_P(
      200,
      "image/svg+xml",
      R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
<rect width="64" height="64" rx="12" fill="#0b1120"/>
<text x="32" y="46" font-family="Arial,sans-serif" font-size="44" text-anchor="middle" fill="#4ade80">&#8984;</text>
</svg>)SVG"
    );
  });
  server.on("/api", HTTP_GET, handleApi);
  server.on("/data", HTTP_POST, handleData);
  server.on("/settings", HTTP_GET, handleGetSettings);
  server.on("/settings", HTTP_POST, handleSaveSettings);
  server.on("/filters", HTTP_POST, handleSaveFilters);
  server.on("/pc/power", HTTP_POST, handlePcPowerControl);
  server.on("/lcd/status", HTTP_GET, handleLcdStatus);
  server.on("/lcd/control", HTTP_POST, handleLcdControl);
  server.on("/health", HTTP_GET, []() {
    String json = "{\"ok\":true,\"device\":\"ESP8266\",\"freeHeap\":";
    json += String(ESP.getFreeHeap());
    json += ",\"jsonBytes\":";
    json += String(latestJson.length());
    json += "}";
    server.send(200, "application/json", json);
  });
  server.onNotFound([]() {
    server.send(404, "text/plain", "404 - Endpoint not found");
  });
  server.begin();
  Serial.println(F("HTTP server started"));
  Serial.print(F("Dashboard URL: http://"));
  Serial.println(WiFi.localIP());
  lastLcdPage = millis();
  lastBmpRead = 0;
  lcdPage = 0;
  readBmp();
  showLcdPage();
}
// MAIN LOOP
void loop() {
  server.handleClient();
  processOptoCommands();
  readBmp();
  if (millis() - lastLcdPage >= 3000) {
    lastLcdPage = millis();
    lcdPage = (lcdPage + 1) % 5;
    showLcdPage();
  }
  static unsigned long lastWiFiCheck = 0;
  if (millis() - lastWiFiCheck >= 5000) {
    lastWiFiCheck = millis();
    if (WiFi.status() != WL_CONNECTED) {
      Serial.println(F("Wi-Fi disconnected. Reconnecting..."));
      WiFi.reconnect();
    }
  }
  yield();
}