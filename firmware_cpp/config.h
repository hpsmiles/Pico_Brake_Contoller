#ifndef CONFIG_H
#define CONFIG_H

#include <stdint.h>

// === Pin Definitions ===
constexpr uint8_t PIN_BRAKE_ADC    = 26;  // GP26, ADC0
constexpr uint8_t PIN_THROTTLE_ADC = 27;  // GP27, ADC1
constexpr uint8_t PIN_HX711_SCK    = 28;  // GP28
constexpr uint8_t PIN_HX711_DATA   = 16;  // GP16
constexpr uint8_t PIN_RGB_LED      = 23;  // GP23, WS2812

// === ADC ===
constexpr uint16_t ADC_OVERSAMPLE_DEFAULT = 16;

// === HID ===
constexpr uint8_t HID_REPORT_SIZE = 8;  // 4 × uint16 LE

// === LED Blink Codes ===
constexpr int BLINK_HALL     = 1;
constexpr int BLINK_HX711    = 2;
constexpr int BLINK_DEFAULTS = 3;
constexpr int BLINK_FATAL    = 10;

// === EMA ===
constexpr float EMA_ALPHA_MIN = 0.05f;  // max smoothing
constexpr float EMA_ALPHA_MAX = 1.0f;   // no smoothing

// === Flash ===
static const char* const PROFILES_DIR     = "/profiles";
static const char* const CALIBRATION_FILE = "/calibration.json";

// === ChannelCal Struct ===
struct ChannelCal {
    uint16_t raw_min = 2000;
    uint16_t raw_max = 56000;
    uint16_t deadzone = 300;
    char     curve[16] = "linear";
    float    progressive_power = 2.0f;
    float    aggressive_power = 2.0f;
    float    smoothing = 0.3f;
    bool     invert = false;
    float    saturation = 1.0f;
    float    bite_point = 0.0f;
    static const uint8_t MAX_CURVE_POINTS = 10;
    uint8_t  num_curve_points = 2;
    float    curve_points_input[MAX_CURVE_POINTS]  = {0.0f, 1.0f};
    float    curve_points_output[MAX_CURVE_POINTS] = {0.0f, 1.0f};
};

// === Calibration Struct ===
struct Calibration {
    uint8_t    oversample = 16;
    bool       throttle_enabled = false;
    char       throttle_sensor[12] = "auto";  // "auto", "hall", "hx711"
    ChannelCal brake;
    ChannelCal throttle;
};

// === SensorData — shared between cores via memcpy + memory barrier ===
struct SensorData {
    uint16_t brake_raw;
    uint16_t brake_processed;
    uint16_t throttle_raw;
    uint16_t throttle_processed;
};

// === HID Report Descriptor ===
// 4×uint16 axes: X=brake processed, Y=brake raw, Z=throttle processed, Rz=throttle raw
// No report ID, no buttons, no hat, no Output Report
static const uint8_t hid_report_descriptor[] = {
    0x05, 0x01,              // Usage Page (Generic Desktop)
    0x09, 0x04,              // Usage (Joystick)
    0xA1, 0x01,              // Collection (Application)
    0x05, 0x01,              //   Usage Page (Generic Desktop)
    0x09, 0x30,              //   Usage (X)
    0x09, 0x31,              //   Usage (Y)
    0x09, 0x32,              //   Usage (Z)
    0x09, 0x35,              //   Usage (Rz)
    0x15, 0x00,              //   Logical Minimum (0)
    0x27, 0xFF, 0xFF, 0x00, 0x00,  //   Logical Maximum (65535) — 4-byte signed = 0x0000FFFF
    0x75, 0x10,              //   Report Size (16)
    0x95, 0x04,              //   Report Count (4)
    0x81, 0x02,              //   Input (Data, Variable, Absolute)
    0xC0                     // End Collection
};

#endif // CONFIG_H
