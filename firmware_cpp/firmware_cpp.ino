#include <Arduino.h>
#include <Adafruit_TinyUSB.h>
#include <hardware/watchdog.h>
#include <pico/bootrom.h>

#include "config.h"
#include "adc_reader.h"
#include "hx711_driver.h"
#include "signal_processing.h"
#include "flash_storage.h"
#include "serial_commands.h"
#include "rgb_led.h"

// === Shared state ===
Calibration cal;
volatile SensorData sensor_data = {};

// HX711 detected by probe in setup1(); can be overridden by cal.throttle_sensor.
bool hx711_detected = false;

// Per-channel EMA state (owned by Core 1, reset by Core 0 via flag).
float brake_ema_state    = 0.0f;
float throttle_ema_state = 0.0f;
bool  brake_ema_init     = false;
bool  throttle_ema_init  = false;

// Serial line accumulator.
String serial_buffer = "";

// Set by REBOOT command; Core 0 triggers watchdog reboot on next tick.
bool reboot_requested = false;

// Flash init result — read by STATUS command.
bool fs_ok  = false;
bool cal_ok = false;

// === USB HID ===
// Poll interval 1ms → 1 kHz ceiling; host may poll less frequently.
Adafruit_USBD_HID usb_hid(hid_report_descriptor, sizeof(hid_report_descriptor),
                           HID_ITF_PROTOCOL_NONE, 1, false);

// ============================================================
// Core 0 — USB HID send + serial command processing
// ============================================================

void setup() {
    // Register interfaces before USB connects.  Order matters: HID first so
    // it lands on MI_02 after the CDC control/data pair (MI_00/MI_01).
    usb_hid.begin();
    Serial.begin(115200);

    // Fixed wait — arduino-pico USB IRQ drives enumeration independently;
    // polling TinyUSBDevice.mounted() here blocks because tud_task() is not
    // called until after setup() returns on this port.
    delay(1500);

    rgb_led_init();
    rgb_led_boot();

    fs_ok = flash_storage_init();
    if (fs_ok) {
        cal_ok = flash_load_calibration(cal);
    }

    if (cal_ok) {
        rgb_led_cal_ok();    // green
    } else {
        rgb_led_defaults();  // red = factory defaults
    }
}

void loop() {
    // --- Serial command processing ---
    while (Serial.available()) {
        char c = Serial.read();
        if (c == '\n') {
            serial_process_command(serial_buffer, cal, reboot_requested,
                                   brake_ema_init, throttle_ema_init,
                                   fs_ok, cal_ok);
            serial_buffer = "";
        } else if (c != '\r') {
            serial_buffer += c;
        }
    }

    // --- Reboot ---
    if (reboot_requested) {
        delay(50);
        watchdog_reboot(0, 0, 50);
        while (true) {}
    }

    // --- Read sensor snapshot from Core 1 ---
    SensorData snap;
    __sync_synchronize();
    memcpy(&snap, (const void*)&sensor_data, sizeof(SensorData));

    // --- HID report: X=brake_proc, Y=brake_raw, Z=throttle_proc, Rz=throttle_raw ---
    uint8_t report[8];
    report[0] = snap.brake_processed & 0xFF;
    report[1] = (snap.brake_processed >> 8) & 0xFF;
    report[2] = snap.brake_raw & 0xFF;
    report[3] = (snap.brake_raw >> 8) & 0xFF;
    report[4] = snap.throttle_processed & 0xFF;
    report[5] = (snap.throttle_processed >> 8) & 0xFF;
    report[6] = snap.throttle_raw & 0xFF;
    report[7] = (snap.throttle_raw >> 8) & 0xFF;

    if (usb_hid.ready()) {
        usb_hid.sendReport(0, report, sizeof(report));
    }

    delay(1);  // 1 kHz loop rate
}

// ============================================================
// Core 1 — ADC read + signal processing
// ============================================================

void setup1() {
    adc_reader_init();

    // Always configure HX711 GPIO so the cal.throttle_sensor override works.
    pinMode(PIN_HX711_SCK, OUTPUT);
    digitalWrite(PIN_HX711_SCK, LOW);
    pinMode(PIN_HX711_DATA, INPUT);

    hx711_detected = hx711_probe(PIN_HX711_DATA);

    if (hx711_detected) {
        rgb_led_blink_code(BLINK_HX711, 0, 255, 0);  // 2 green blinks
    }
}

void loop1() {
    // --- Brake: always ADC ---
    uint16_t brake_raw       = adc_read_oversampled(PIN_BRAKE_ADC, cal.oversample);
    uint16_t brake_processed = process_channel(brake_raw, cal.brake,
                                               brake_ema_state, brake_ema_init);

    // --- Throttle ---
    uint16_t throttle_raw       = 0;
    uint16_t throttle_processed = 0;

    if (cal.throttle_enabled) {
        bool use_hx711 = false;
        if (strcmp(cal.throttle_sensor, "hx711") == 0) {
            use_hx711 = true;
        } else if (strcmp(cal.throttle_sensor, "hall") == 0) {
            use_hx711 = false;
        } else {
            // "auto" — use HX711 if detected, otherwise Hall
            use_hx711 = hx711_detected;
        }

        if (use_hx711) {
            throttle_raw = hx711_read_16bit(PIN_HX711_SCK, PIN_HX711_DATA);
        } else {
            throttle_raw = adc_read_oversampled(PIN_THROTTLE_ADC, cal.oversample);
        }
        throttle_processed = process_channel(throttle_raw, cal.throttle,
                                             throttle_ema_state, throttle_ema_init);
    }

    // --- Publish to Core 0 ---
    SensorData next = { brake_raw, brake_processed, throttle_raw, throttle_processed };
    __sync_synchronize();
    memcpy((void*)&sensor_data, &next, sizeof(SensorData));
}
