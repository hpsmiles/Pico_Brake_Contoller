// firmware_cpp/config.h
#ifndef CONFIG_H
#define CONFIG_H

#include <stdint.h>
#include <cstddef>

// --- Pin Configuration ---
#define PIN_BRAKE_ADC   26  // GP26 = ADC0 (brake pressure sensor)
#define PIN_HALL_ADC    27  // GP27 = ADC1 (SS49E Hall Effect throttle)
#define PIN_HX711_DATA  16  // GP16 (HX711 DATA)
#define PIN_HX711_SCK   28  // GP28 (HX711 SCK)
#define PIN_NEOPIXEL    23  // GP23 (WS2812 RGB LED)
// PIN_LED and ADC_RESOLUTION are already defined by the arduino-pico variant header

// --- ADC ---
#define BRAKE_ADC_RESOLUTION  12  // RP2040 native ADC resolution
#define DEFAULT_OVERSAMPLE 16

// --- HID Report: 4 x uint16 LE, 8 bytes total ---
// X = processed brake, Y = raw brake ADC, Z = processed throttle, Rz = raw throttle
struct __attribute__((packed)) BrakeReport {
    uint16_t x;   // processed brake
    uint16_t y;   // raw brake ADC
    uint16_t z;   // processed throttle
    uint16_t rz;  // raw throttle ADC
};

// --- Custom HID Report Descriptor (matches CircuitPython boot.py input axes) ---
// 4 axes (X, Y, Z, Rz), 16-bit unsigned, no buttons, no hat, no report ID
// NOTE: Output Report removed — it causes CM_PROB_FAILED_START with arduino-pico USB stack.
// The GUI falls back to "press RESET on Pico" message instead of auto-reset via HID.
static const uint8_t GAMEPAD_HID_DESCRIPTOR[] = {
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
    0x75, 0x10,        //     Report Size (16 bits)
    0x95, 0x04,        //     Report Count (4 axes)
    0x81, 0x02,        //     Input (Data, Variable, Absolute)
    0xC0,              //   End Collection (Physical)
    0xC0               // End Collection (Application)
};
static const size_t GAMEPAD_HID_DESCRIPTOR_LEN = sizeof(GAMEPAD_HID_DESCRIPTOR);

// --- Calibration defaults (must match CircuitPython DEFAULTS exactly) ---
struct ChannelCal {
    uint16_t raw_min      = 2000;
    uint16_t raw_max      = 56000;
    uint16_t deadzone     = 300;
    char     curve[16]    = "linear";       // "linear", "progressive", "aggressive", "custom"
    float    progressive_power = 2.0f;
    float    aggressive_power  = 2.0f;
    float    smoothing    = 0.3f;
    bool     invert       = false;
    float    saturation   = 1.0f;
    float    bite_point   = 0.0f;
    // Custom curve points stored separately (up to 10 points)
    static const uint8_t MAX_CURVE_POINTS = 10;
    uint8_t  num_curve_points = 2;
    float    curve_points_input[MAX_CURVE_POINTS]  = {0.0f, 1.0f};
    float    curve_points_output[MAX_CURVE_POINTS] = {0.0f, 1.0f};
};

struct Calibration {
    uint8_t  oversample         = 16;
    bool     throttle_enabled   = false;
    // "auto", "hall", "load_cell"
    char     throttle_sensor[12] = "auto";
    ChannelCal brake;
    ChannelCal throttle;
};

// --- Shared sensor data (Core 1 -> Core 0) ---
struct SensorData {
    uint16_t brake_raw;
    uint16_t brake_processed;
    uint16_t throttle_raw;
    uint16_t throttle_processed;
};

// --- Throttle sensor type (resolved at boot) ---
enum class ThrottleSensor : uint8_t {
    NONE = 0,
    HALL = 1,
    LOAD_CELL = 2
};

#endif // CONFIG_H
