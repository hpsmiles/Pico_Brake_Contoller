// Minimal HID gamepad test v4 - NO FatFS at all
#include <Arduino.h>
#include <USB.h>
#include <tusb.h>
#include <tusb-hid.h>
#include "class/hid/hid_device.h"

int usb_hid_poll_interval = 1;

static const uint8_t GAMEPAD_DESC[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop)
    0x09, 0x05,        // Usage (Game Pad)
    0xA1, 0x01,        // Collection (Application)
    0x09, 0x01,        //   Usage (Pointer)
    0xA1, 0x00,        //   Collection (Physical)
    0x09, 0x30,        //     Usage (X)
    0x09, 0x31,        //     Usage (Y)
    0x09, 0x32,        //     Usage (Z)
    0x09, 0x35,        //     Usage (Rz)
    0x16, 0x00, 0x00,  //     Logical Minimum (0)
    0x26, 0xFF, 0xFF,  //     Logical Maximum (65535)
    0x75, 0x10,        //     Report Size (16)
    0x95, 0x04,        //     Report Count (4)
    0x81, 0x02,        //     Input (Data, Variable, Absolute)
    0xC0,              //   End Collection (Physical)
    0xC0               // End Collection (Application) — NO Output Report
};

struct __attribute__((packed)) Report { uint16_t x, y, z, rz; };

uint8_t hid_id = 0;

void setup() {
    pinMode(LED_BUILTIN, OUTPUT);
    
    USB.disconnect();
    hid_id = USB.registerHIDDevice(GAMEPAD_DESC, sizeof(GAMEPAD_DESC), 30, 0x0004);
    
    // No FatFS at all — just test HID + Output Report
    // bool fs_ok = FatFS.begin();
    // if (!fs_ok) { FatFS.format(); fs_ok = FatFS.begin(); }
    // if (fs_ok) { FatFSUSB.begin(); }
    
    USB.connect();
    
    // Blink once = alive
    for (int i = 0; i < 3; i++) {
        digitalWrite(LED_BUILTIN, HIGH); delay(100);
        digitalWrite(LED_BUILTIN, LOW); delay(100);
    }
}

void loop() {
    static uint16_t val = 0;
    static bool rising = true;
    
    Report rpt = { val, val, 0, 0 };
    
    CoreMutex m(&USB.mutex);
    tud_task();
    if (USB.HIDReady()) {
        tud_hid_n_report(0, USB.findHIDReportID(hid_id), &rpt, sizeof(rpt));
    }
    tud_task();
    
    if (rising) { val += 2000; if (val >= 60000) rising = false; }
    else { val -= 2000; if (val == 0) rising = true; }
    
    delay(20);
}
