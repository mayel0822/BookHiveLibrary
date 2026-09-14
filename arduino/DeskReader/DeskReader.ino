// BookHive — Reader 2 (Librarian Desk / Transaction)
// Does NOT write to the database. Broadcasts the UID over SignalR so whichever
// staff page is open (Borrow, Computer Transaction, Sectioning, MIS Registration)
// auto-fills itself. The server does not check X-Device-Key for this endpoint yet,
// so it's sent here only so the header is already in place if that changes later.

#include <SPI.h>
#include <MFRC522.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <WiFiClientSecure.h>
#include "secrets.h"   // defines WIFI_SSID, WIFI_PASSWORD, DEVICE_KEY — see ../secrets.example.h

#define SS_PIN  5
#define RST_PIN 22

// SERVER HOST FOR LOCAL TESTING
//const char* SERVER_HOST = "192.168.0.106";

// SERVER HOST FOR AZURE PUBLISH
const char* SERVER_HOST = "bookhivelibrary-efdkf2fue4hmhwfq.southeastasia-01.azurewebsites.net";

const char* TAP_ENDPOINT = "/Librarian/DeskTap";   // desk broadcast-only endpoint

const unsigned long WIFI_CONNECT_TIMEOUT_MS = 15000;
const unsigned long TAP_COOLDOWN_MS = 3000;

MFRC522 rfid(SS_PIN, RST_PIN);

String lastUid = "";
unsigned long lastTapTime = 0;

void connectWiFi() {
  Serial.print("Connecting to WiFi");
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  unsigned long start = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - start < WIFI_CONNECT_TIMEOUT_MS) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();
  if (WiFi.status() == WL_CONNECTED) {
    Serial.print("WiFi connected, IP: ");
    Serial.println(WiFi.localIP());
  } else {
    Serial.println("WiFi connect timed out, will retry in loop().");
  }
}

String readUid() {
  String uid = "";
  for (byte i = 0; i < rfid.uid.size; i++) {
    if (rfid.uid.uidByte[i] < 0x10) uid += "0";
    uid += String(rfid.uid.uidByte[i], HEX);
  }
  uid.toUpperCase();
  return uid;
}

void sendTap(const String& uid) {
  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("WiFi not connected, skipping tap.");
    return;
  }

  // HTTP CLIENT FOR LOCAL TESTING
  //WiFiClient client;
  //String url = String("http://") + SERVER_HOST + ":5200" + TAP_ENDPOINT + "?rfidNumber=" + uid;

  // HTTP CLIENT FOR AZURE PUBLISH
  WiFiClientSecure client;
  client.setInsecure();
  String url = String("https://") + SERVER_HOST + TAP_ENDPOINT + "?rfidNumber=" + uid;

  HTTPClient http;
  http.begin(client, url);
  http.setTimeout(15000);   // Azure can take longer than the 5s default to wake from idle
  http.addHeader("X-Device-Key", DEVICE_KEY);

  int code = http.POST("");
  if (code == 200) {
    String response = http.getString();
    Serial.print("Tap OK: ");
    Serial.println(response);
  } else {
    Serial.print("Tap failed, HTTP code: ");
    Serial.println(code);
  }

  http.end();
}

void setup() {
  Serial.begin(115200);
  SPI.begin(18, 19, 23, 5);

  rfid.PCD_Init();

  byte version = rfid.PCD_ReadRegister(MFRC522::VersionReg);
  Serial.print("RC522 Version: 0x");
  Serial.println(version, HEX);

  if (version == 0x00 || version == 0xFF) {
    Serial.println("ERROR: RC522 not found! Check wiring.");
  } else {
    Serial.println("RC522 OK! Scan your card...");
  }

  connectWiFi();
}

void loop() {
  if (WiFi.status() != WL_CONNECTED) connectWiFi();

  static unsigned long lastHeartbeat = 0;
  if (millis() - lastHeartbeat > 2000) {
    Serial.println("Scanning...");
    lastHeartbeat = millis();
  }

  if (!rfid.PICC_IsNewCardPresent()) return;
  if (!rfid.PICC_ReadCardSerial()) return;

  String uid = readUid();
  unsigned long now = millis();

  bool sameCardTooSoon = (uid == lastUid) && (now - lastTapTime < TAP_COOLDOWN_MS);
  if (!sameCardTooSoon) {
    Serial.print("UID: ");
    Serial.println(uid);
    sendTap(uid);
    lastUid = uid;
    lastTapTime = now;
  }

  rfid.PICC_HaltA();
  rfid.PCD_StopCrypto1();
}
