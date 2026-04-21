// firmware_cpp/firmware_cpp.ino
// Pico Brake Controller — C++ firmware (Arduino-Pico)
// Dual-core: Core 0 = USB HID, Core 1 = ADC + signal processing
// LittleFS + SingleFileDrive for calibration.json USB access

#include "config.h"
#include "adc_reader.h"
#include "hx711_driver.h"
#include "signal_processing.h"
#include "calibration.h"
#include "msc_disk.h"
#include <USB.h>
#include <tusb.h>
#include <tusb-hid.h>
#include "class/hid/hid_device.h"
#include <Adafruit_NeoPixel.h>
#include <LittleFS.h>
#include <SingleFileDrive.h>

int usb_hid_poll_interval = 1;  // 1ms = 1000Hz USB HID polling

uint8_t hid_id = 0;
Calibration cal;
ThrottleSensor throttle_sensor = ThrottleSensor::NONE;
HX711Driver* hx711 = nullptr;
bool cal_loaded = false;

Adafruit_NeoPixel pixel(1, PIN_NEOPIXEL, NEO_GRB + NEO_KHZ800);

volatile SensorData g_sensor = {};
volatile bool g_sensor_ready = false;

float brake_ema = 0.0f;
bool brake_ema_init = false;
float throttle_ema = 0.0f;
bool throttle_ema_init = false;

// --- LED helpers ---

void blink_led(uint8_t count, uint16_t on_ms = 200, uint16_t off_ms = 200) {
    for (uint8_t i = 0; i < count; i++) {
        digitalWrite(PIN_LED, HIGH);
        delay(on_ms);
        digitalWrite(PIN_LED, LOW);
        delay(off_ms);
    }
}

void set_rgb(uint8_t r, uint8_t g, uint8_t b) {
    pixel.setPixelColor(0, pixel.Color(r, g, b));
    pixel.show();
}

// ============================================================
// Core 0: USB HID task
// ============================================================

void setup() {
    pinMode(PIN_LED, OUTPUT);
    pixel.begin();
    pixel.setBrightness(50);

    set_rgb(255, 50, 0);   // Orange = booting
    digitalWrite(PIN_LED, HIGH);

    // Step 1: Initialize filesystem (MUST happen before USB.connect and before setup1)
    bool fs_ok = msc_disk_init();
    if (fs_ok) {
        // Step 2: Load calibration from LittleFS
        cal = load_calibration();
        // Check if calibration is non-default
        cal_loaded = !(cal.brake.raw_min == 2000 && cal.brake.raw_max == 56000 &&
                       cal.throttle.raw_min == 2000 && cal.throttle.raw_max == 56000);
    }

    // Step 3: Register custom HID gamepad
    USB.disconnect();
    hid_id = USB.registerHIDDevice(
        GAMEPAD_HID_DESCRIPTOR,
        GAMEPAD_HID_DESCRIPTOR_LEN,
        30,
        0x0004
    );

    // Step 4: Connect USB (HID gamepad + MSC drive both enumerate)
    USB.connect();

    // LED status
    if (hid_id == 0) {
        // Fatal: no HID device
        while (true) {
            set_rgb(255, 0, 0);  // Red
            blink_led(10, 50, 50);  // 10 rapid blinks = fatal
            delay(1000);
        }
    }

    if (cal_loaded) {
        set_rgb(0, 255, 0);   // Green = calibration loaded
        blink_led(1, 300, 300);
    } else {
        set_rgb(255, 0, 0);   // Red = defaults (no calibration or file not found)
        blink_led(3, 200, 200);
    }

    // Running — LEDs off
    set_rgb(0, 0, 0);
    digitalWrite(PIN_LED, LOW);
}

void loop() {
    if (g_sensor_ready) {
        __sync_synchronize();
        SensorData local;
        memcpy(&local, (const void*)&g_sensor, sizeof(SensorData));

        BrakeReport report;
        report.x  = local.brake_processed;
        report.y  = local.brake_raw;
        report.z  = local.throttle_processed;
        report.rz = local.throttle_raw;

        CoreMutex m(&USB.mutex);
        tud_task();
        if (USB.HIDReady()) {
            tud_hid_n_report(0, USB.findHIDReportID(hid_id), &report, sizeof(report));
        }
        tud_task();

        g_sensor_ready = false;
    }

    delayMicroseconds(500);  // ~2kHz main loop, HID sends at USB poll rate (1ms)
}

// ============================================================
// Core 1: ADC reading + signal processing
// ============================================================

void setup1() {
    adc_reader_init();

    if (cal.throttle_enabled) {
        if (strcmp(cal.throttle_sensor, "auto") == 0) {
            // Probe for HX711 on GP16+GP28
            HX711Driver probe_hx(PIN_HX711_DATA, PIN_HX711_SCK);
            if (probe_hx.probe(120)) {
                throttle_sensor = ThrottleSensor::LOAD_CELL;
                hx711 = new HX711Driver(PIN_HX711_DATA, PIN_HX711_SCK);
                hx711->init();
                blink_led(2, 100, 200);  // 2 blinks = load cell
            } else {
                throttle_sensor = ThrottleSensor::HALL;
                blink_led(1, 100, 100);  // 1 blink = Hall
            }
        } else if (strcmp(cal.throttle_sensor, "load_cell") == 0) {
            throttle_sensor = ThrottleSensor::LOAD_CELL;
            hx711 = new HX711Driver(PIN_HX711_DATA, PIN_HX711_SCK);
            hx711->init();
        } else {
            throttle_sensor = ThrottleSensor::HALL;
        }
    } else {
        throttle_sensor = ThrottleSensor::NONE;
    }
}

void loop1() {
    // Brake: always read
    uint16_t brake_raw = adc_read_oversampled(0, cal.oversample);
    uint16_t brake_processed = process_channel(brake_raw, cal.brake,
                                               brake_ema, brake_ema_init);

    // Throttle: only if enabled
    uint16_t throttle_raw = 0;
    uint16_t throttle_processed = 0;

    if (throttle_sensor == ThrottleSensor::HALL) {
        throttle_raw = adc_read_oversampled(1, cal.oversample);
        throttle_processed = process_channel(throttle_raw, cal.throttle,
                                             throttle_ema, throttle_ema_init);
    } else if (throttle_sensor == ThrottleSensor::LOAD_CELL && hx711 != nullptr) {
        throttle_raw = hx711->read_u16();
        throttle_processed = process_channel(throttle_raw, cal.throttle,
                                             throttle_ema, throttle_ema_init);
    }

    // Publish to shared struct (Core 0 reads this)
    SensorData sd;
    sd.brake_raw          = brake_raw;
    sd.brake_processed    = brake_processed;
    sd.throttle_raw       = throttle_raw;
    sd.throttle_processed = throttle_processed;

    __sync_synchronize();
    memcpy((void*)&g_sensor, &sd, sizeof(SensorData));
    g_sensor_ready = true;

    delayMicroseconds(500);  // ~2kHz ADC read rate
}
