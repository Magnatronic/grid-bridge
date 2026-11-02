// GameDongle - the ChatterSwitch GAME dongle (MOUSE-PLAN.md, step 7).
//
// An M5Stack AtomS3U in a USB port of the Xbox Adaptive Controller (or a PC): a USB
// gamepad laid out the way the XAC takes it, moved by a ChatterSwitch over ESP-NOW
// (the stick's GAME settings: Send by: Wireless).
//
// Pairing: a new dongle pairs with the first stick that does "Pair dongle". To pair a
// paired dongle with another stick, hold its button 3 s (the LED flashes blue), then
// "Pair dongle" on the stick within 30 s. It then only takes that stick's packets.
// Its button presses gamepad button 1 (to check it in the XAC's Properties without a stick).
//
// The XAC's USB ports give it 100 mA unless the XAC is on its own 5 V 2 A power supply,
// and the Wi-Fi start-up surge is more: on the XAC without its supply the dongle resets
// over and over (red LED, again and again). Tested 28 Oct 2026.
//
// LED: red = started, Wi-Fi not yet (the first 2 s); flashing blue = pairing (new, or
// the button held 3 s); blue = waiting for its stick; green = its stick is sending;
// white = button held; purple = no Wi-Fi (the button was held while plugging in: the
// gamepad alone).
// Serial (USB): INFO -> its MAC address, its stick, whether Wi-Fi is on, how many packets came.
//   LED r g b -> that colour (until the LED next changes). FORGET -> forget its stick.
//
// Arduino IDE: board M5AtomS3, USB Mode: USB-OTG (TinyUSB), USB CDC On Boot: Enabled
// (sketch.yaml has them for arduino-cli).

#include "USB.h"
#include "USBHID.h"
#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <esp_mac.h>
#include <Preferences.h>

#if ARDUINO_USB_MODE != 0
#error "Set Tools > USB Mode: USB-OTG (TinyUSB) - the gamepad needs it"
#endif

static const int      BUTTON_PIN = 41;        // the AtomS3U's button (pressed = LOW)
static const uint8_t  CHANNEL = 1;            // ESP-NOW channel (the stick uses the same)
static const uint32_t LOST_MS = 500;          // nothing from the stick this long: stick centred, buttons up
static const uint32_t RADIO_AFTER_MS = 2000;  // Wi-Fi starts this long after power-up (a power cut then shows as red, then dark)
static const uint32_t PAIR_HOLD_MS = 3000;    // hold the button this long to pair again
static const uint32_t PAIR_MS = 30000;        // ...then a stick can pair for this long
static const uint8_t  LED_DIM = 10;           // LED brightness (0-255); low, for the XAC's 100 mA

// The XAC gamepad (the same as ChatterSwitch's GAME over USB): one report with no report ID,
// X, Y, Z, Rz 0-255 (128 = middle), a hat, 12 buttons = 6 bytes. Written flat (one
// collection, no output report), as the core's report map check needs.
static const uint8_t XAC_REPORT_DESC[] = {
  0x05, 0x01, 0x09, 0x04, 0xA1, 0x01,              // Generic Desktop, Joystick, Application
  0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00,
  0x75, 0x08, 0x95, 0x04, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35, 0x81, 0x02,  // X Y Z Rz
  0x25, 0x07, 0x46, 0x3B, 0x01, 0x65, 0x14, 0x75, 0x04, 0x95, 0x01, 0x09, 0x39, 0x81, 0x42,  // hat 0-7, null
  0x65, 0x00, 0x25, 0x01, 0x45, 0x01, 0x75, 0x01, 0x95, 0x0C,
  0x05, 0x09, 0x19, 0x01, 0x29, 0x0C, 0x81, 0x02,  // buttons 1-12
  0xC0
};
class XacGamepad : public USBHIDDevice {
  USBHID hid;
public:
  XacGamepad() { hid.addDevice(this, sizeof(XAC_REPORT_DESC)); }
  uint16_t _onGetDescriptor(uint8_t* dst) override {
    memcpy(dst, XAC_REPORT_DESC, sizeof(XAC_REPORT_DESC));
    return sizeof(XAC_REPORT_DESC);
  }
  void begin() { hid.begin(); }
  bool ready() { return hid.ready(); }
  // sticks -127..127; button bits 0-11; the hat (d-pad) always centred
  bool send(int8_t lx, int8_t ly, int8_t rx, int8_t ry, uint16_t buttons) {
    uint8_t r[6] = {(uint8_t)(lx + 128), (uint8_t)(ly + 128), (uint8_t)(rx + 128), (uint8_t)(ry + 128),
                    (uint8_t)(0x0F | (buttons << 4)), (uint8_t)(buttons >> 4)};
    return hid.SendReport(0, r, sizeof(r));
  }
};
// A global: USB starts before setup (CDC On Boot) and the core adds the gamepad when it's made.
XacGamepad gamepad;

// A packet (the same as TalkingSwitch.ino's)
struct __attribute__((packed)) GamePacket {
  uint8_t magic;      // 'G' the gamepad, 'P' pair (stick to everyone), 'A' pair answer (dongle to stick)
  uint8_t version;    // 1
  int8_t lx, ly, rx, ry;
  uint16_t buttons;   // bit 0 = button 1
};

Preferences prefs;
bool noRadio = false;  // the button was held at power-up: no Wi-Fi
bool radioOn = false;
bool haveStick = false;       // paired (its MAC saved as "stick")
uint8_t stickMac[6] = {};
uint32_t pairUntil = 0;       // the button was held: pairing until then

portMUX_TYPE rxLock = portMUX_INITIALIZER_UNLOCKED;
GamePacket rxPacket = {};
uint32_t rxAt = 0, rxCount = 0;
uint32_t asksHeard = 0;          // checking: pair asks heard
uint8_t askMac[6];               // a stick asking to pair (set in the Wi-Fi task, then asked)
volatile bool asked = false;

bool pairing() { return !haveStick || pairUntil; }

// Runs in the Wi-Fi task: keep the newest packet from its stick; note a pairing stick.
void onReceive(const esp_now_recv_info_t* info, const uint8_t* data, int len) {
  if (len != sizeof(GamePacket) || data[1] != 1) return;
  if (data[0] == 'P') {
    asksHeard++;
    if (!asked) { memcpy(askMac, info->src_addr, 6); asked = true; }
    return;
  }
  if (data[0] != 'G' || !haveStick || memcmp(info->src_addr, stickMac, 6)) return;
  portENTER_CRITICAL(&rxLock);
  memcpy(&rxPacket, data, sizeof(GamePacket));
  rxAt = millis();
  rxCount++;
  portEXIT_CRITICAL(&rxLock);
}

// The AtomS3U's LED has red and green swapped
void setLed(uint8_t r, uint8_t g, uint8_t b) { rgbLedWrite(RGB_BUILTIN, g, r, b); }

void addPeer(const uint8_t* mac) {
  if (esp_now_is_peer_exist(mac)) return;
  esp_now_peer_info_t p = {};
  memcpy(p.peer_addr, mac, 6);
  p.channel = CHANNEL;
  p.ifidx = WIFI_IF_STA;
  esp_now_add_peer(&p);
}

void setup() {
  USB.usbPower(100);  // what the XAC's ports give without its power supply (asking for 500 also works)
  setCpuFrequencyMhz(80);  // the least Wi-Fi allows; saves power
  Serial.begin(115200);
  gamepad.begin();
  prefs.begin("dongle", false);
  haveStick = prefs.getBytes("stick", stickMac, 6) == 6;
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  delay(5);
  noRadio = digitalRead(BUTTON_PIN) == LOW;
  if (noRadio) setLed(LED_DIM, 0, LED_DIM);  // purple
  else setLed(LED_DIM, 0, 0);                // red
}

// Wi-Fi for ESP-NOW only (no network is joined or made), started gently: on the XAC's
// 100 mA ports the start-up surge resets the dongle (tested 28 Oct 2026; this didn't
// stop it, the XAC's power supply did). So the sending power is capped before the radio
// starts (a "country" with a 2 dBm limit), not after, and nothing is stored in flash.
void startRadio() {
  radioOn = true;
  esp_event_loop_create_default();  // (already made is fine)
  wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
  if (esp_wifi_init(&cfg) != ESP_OK) { Serial.println("Wi-Fi failed to start"); return; }
  esp_wifi_set_storage(WIFI_STORAGE_RAM);
  esp_wifi_set_mode(WIFI_MODE_STA);
  wifi_country_t country = {};
  memcpy(country.cc, "GB", 3);
  country.schan = 1;
  country.nchan = 13;
  country.max_tx_power = 2;  // dBm
  country.policy = WIFI_COUNTRY_POLICY_MANUAL;
  esp_wifi_set_country(&country);
  esp_wifi_start();
  esp_wifi_set_max_tx_power(8);  // 0.25 dBm steps: 2 dBm
  esp_wifi_set_channel(CHANNEL, WIFI_SECOND_CHAN_NONE);
  if (esp_now_init() == ESP_OK) esp_now_register_recv_cb(onReceive);
  else Serial.println("ESP-NOW failed to start");
}

// A stick asked to pair while pairing: it's ours now. Answer it (a few times, in case one is lost).
void pairWith(const uint8_t* mac) {
  if (haveStick && memcmp(stickMac, mac, 6)) esp_now_del_peer(stickMac);
  memcpy(stickMac, mac, 6);
  haveStick = true;
  pairUntil = 0;
  prefs.putBytes("stick", stickMac, 6);
  addPeer(stickMac);
  GamePacket a = {'A', 1, 0, 0, 0, 0, 0};
  for (int i = 0; i < 3; i++) { esp_now_send(stickMac, (const uint8_t*)&a, sizeof(a)); delay(20); }
}

void loop() {
  const uint32_t now = millis();
  if (!radioOn && !noRadio && now > RADIO_AFTER_MS) startRadio();
  if (pairUntil && (int32_t)(now - pairUntil) >= 0) pairUntil = 0;
  if (asked) {
    uint8_t mac[6];
    memcpy(mac, askMac, 6);
    // pairing, or its own stick asking again (it didn't hear the answer)
    if (pairing() || (haveStick && !memcmp(mac, stickMac, 6))) pairWith(mac);
    asked = false;
  }

  GamePacket p;
  uint32_t at, count;
  portENTER_CRITICAL(&rxLock);
  p = rxPacket;
  at = rxAt;
  count = rxCount;
  portEXIT_CRITICAL(&rxLock);
  const bool live = count && now - at < LOST_MS;
  if (!live) p = GamePacket{'G', 1, 0, 0, 0, 0, 0};  // no stick: centred, buttons up
  const bool button = digitalRead(BUTTON_PIN) == LOW;
  if (button) p.buttons |= 1;

  // holding the button 3 s: pair again
  static uint32_t downSince = 0;
  if (!button) downSince = 0;
  else if (!downSince) downSince = now;
  else if (now - downSince >= PAIR_HOLD_MS && radioOn && !pairUntil) pairUntil = now + PAIR_MS;

  // the report, when something changed
  static GamePacket sent = {'G', 1, 0, 0, 0, 0, 0xFFFF};
  if (memcmp(&p, &sent, sizeof(p)) && gamepad.ready() && gamepad.send(p.lx, p.ly, p.rx, p.ry, p.buttons)) sent = p;

  // LED: white = button; then once Wi-Fi is on, green / flashing blue / blue (before that,
  // red or purple from setup). Flashing blue: blue half the time, off the other half
  static int8_t ledWas = -1;
  const bool flashOn = (now / 400) % 2 == 0;
  const int8_t led = button && !(pairUntil && flashOn) ? 3 : !radioOn ? (noRadio ? 2 : 1) : live ? 4
                     : pairing() ? (flashOn ? 5 : 6) : 5;
  if (led != ledWas) {
    ledWas = led;
    switch (led) {
      case 3: setLed(LED_DIM, LED_DIM, LED_DIM); break;
      case 2: setLed(LED_DIM, 0, LED_DIM); break;
      case 1: setLed(LED_DIM, 0, 0); break;
      case 4: setLed(0, LED_DIM, 0); break;
      case 6: setLed(0, 0, 0); break;
      default: setLed(0, 0, LED_DIM); break;
    }
  }

  if (Serial.available()) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();
    if (cmd == "INFO") {
      uint8_t mac[6] = {};
      esp_read_mac(mac, ESP_MAC_WIFI_STA);
      char stick[18] = "";
      if (haveStick) snprintf(stick, sizeof(stick), "%02X:%02X:%02X:%02X:%02X:%02X", stickMac[0], stickMac[1],
                              stickMac[2], stickMac[3], stickMac[4], stickMac[5]);
      Serial.printf("@INFO {\"dongle\":1,\"mac\":\"%02X:%02X:%02X:%02X:%02X:%02X\",\"stick\":\"%s\",\"pairing\":%s,"
                    "\"radio\":%s,\"packets\":%lu,\"live\":%s,\"asks\":%lu}\n",
                    mac[0], mac[1], mac[2], mac[3], mac[4], mac[5], stick, pairing() ? "true" : "false",
                    radioOn ? "true" : "false", (unsigned long)count, live ? "true" : "false", (unsigned long)asksHeard);
    } else if (cmd.startsWith("LED ")) {  // LED r g b: to check the colours (until the LED next changes)
      int r = 0, g = 0, b = 0;
      sscanf(cmd.c_str() + 4, "%d %d %d", &r, &g, &b);
      setLed(r, g, b);
    } else if (cmd == "FORGET") {  // forget its stick: the next one to pair is it
      if (haveStick && radioOn) esp_now_del_peer(stickMac);
      haveStick = false;
      prefs.remove("stick");
      Serial.println("@OK");
    }
  }
  delay(2);
}
