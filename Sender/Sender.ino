/*
  This program is using for MCU sender sending signal to MCU receiver by ESPNOW.
In this project i use it to ON/OFF my pc from far away, you should use MCU offers
a better balance of performance and cost; in this case, I happened to have plenty 
of ESP8266s on hand, so I just used one of those.
  Author: Chu Xuân Hồng
  Time (dd/mm/yyyy): 08/10/2026
*/
#include <ESP8266WiFi.h>       
#include <user_interface.h>
extern "C" {
  #include <espnow.h>
}
#define BUTTON_PIN 14 // D2 = GPIO4
#define WIFI_CHANNEL 8
const uint8_t receiverMAC[] = {
  0xEC, 0xFA, 0xBC, 0x8F, 0x11, 0x87
};
const uint8_t COMMAND = 1;
volatile bool wakeDetected = false;
volatile bool sendFinished = false;
volatile uint8_t sendStatus = 1;
void wakeCallback() {
  wakeDetected = true;
}
void sendCallback(uint8_t *mac, uint8_t status) {
  sendStatus = status;
  sendFinished = true;
}
void waitButtonRelease() {
  while (digitalRead(BUTTON_PIN) == LOW) {
    delay(5);
  }
  delay(30);
}
void sleepUntilButton() {
  wakeDetected = false;
  WiFi.disconnect();
  WiFi.mode(WIFI_OFF);
  delay(1);
  wifi_fpm_set_sleep_type(LIGHT_SLEEP_T);
  wifi_fpm_open();
  gpio_pin_wakeup_enable(
    GPIO_ID_PIN(BUTTON_PIN),
    GPIO_PIN_INTR_LOLEVEL
  );
  wifi_fpm_set_wakeup_cb(wakeCallback);
  wifi_fpm_do_sleep(0xFFFFFFF);
  delay(10);
  wifi_fpm_close();
}
bool sendCommand() {
  WiFi.mode(WIFI_STA);
  WiFi.disconnect();
  wifi_set_channel(WIFI_CHANNEL);
  if (esp_now_init() != 0) {
    WiFi.mode(WIFI_OFF);
    return false;
  }
  esp_now_set_self_role(ESP_NOW_ROLE_CONTROLLER);
  esp_now_register_send_cb(sendCallback);
  if (esp_now_add_peer(
        (uint8_t*)receiverMAC,
        ESP_NOW_ROLE_SLAVE,
        WIFI_CHANNEL,
        NULL,
        0
      ) != 0) {
    esp_now_deinit();
    WiFi.mode(WIFI_OFF);
    return false;
  }
  sendFinished = false;
  sendStatus = 1;
  int result = esp_now_send(
    (uint8_t*)receiverMAC,
    (uint8_t*)&COMMAND,
    sizeof(COMMAND)
  );
  if (result == 0) {
    unsigned long start = millis();
    while (!sendFinished && millis() - start < 500) {
      delay(1);
    }
  }
  bool success = sendFinished && sendStatus == 0;
  esp_now_deinit();
  WiFi.disconnect();
  WiFi.mode(WIFI_OFF);
  return success;
}
void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  waitButtonRelease();
}
void loop() {
  sleepUntilButton();
  if (digitalRead(BUTTON_PIN) == LOW) {
    delay(30);
    if (digitalRead(BUTTON_PIN) == LOW) {
      sendCommand();
      waitButtonRelease();
    }
  }
}