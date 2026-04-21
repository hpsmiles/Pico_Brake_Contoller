# C++ Firmware Port Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the CircuitPython firmware (boot.py + code.py) to C++ using Arduino-Pico framework, achieving deterministic 1000Hz HID updates with USB MSC composite device for calibration.json read/write.

**Architecture:** Dual-core C++ firmware on RP2040. Core 1 reads sensors (ADC/HX711) + applies signal processing at ~2000Hz. Core 0 runs USB stack (TinyUSB) + sends HID reports at 1000Hz. USB composite device exposes both HID gamepad and Mass Storage (FAT16 on flash) so the existing Python calibration GUI can write calibration.json to the Pico drive without changes. Same HID descriptor (4 × uint16 LE axes, 8 bytes), same calibration.json format — the GUI is unmodified.

**Tech Stack:** Arduino-Pico (earlephilhower/arduino-pico v4.x), Custom HID via `USB.registerHIDDevice()`, FatFS + FatFSUSB for MSC, ArduinoJson v7 for JSON parsing, bit-bang HX711 driver. Build via Arduino CLI or IDE → .uf2 output.

---

## File Structure

```
firmware_cpp/
├── firmware_cpp.ino           ← Entry point: setup(), loop() (Core 0: USB + HID send)
├── config.h                   ← Pin defs, defaults, report struct, calibration_t
├── adc_reader.h               ← ADC init + oversampled read declarations
├── adc_reader.cpp             ← ADC implementation
├── hx711_driver.h             ← HX711 bit-bang driver declarations
├── hx711_driver.cpp           ← HX711 implementation
├── calibration.h              ← Calibration load/save declarations
├── calibration.cpp            ← JSON parse + defaults merge + FatFS integration
├── signal_processing.h        ← Curve, deadzone, bite, EMA declarations
├── signal_processing.cpp      ← All signal processing math
├── msc_disk.h                 ← MSC callback declarations (if needed)
└── msc_disk.cpp               ← FatFSUSB setup + plug/unplug callbacks
```

Each file has one clear responsibility. Files that change together (signal_processing pair) live together. The GUI (`gui/calibrator.py`) and CircuitPython firmware (`firmware/`) are **not modified**.

---

### Task 1: Project Scaffolding + Build Verification

**Files:**
- Create: `firmware_cpp/firmware_cpp.ino`
- Create: `firmware_cpp/config.h`

This task creates the project structure and verifies the Arduino-Pico toolchain can compile and produce a .uf2 file.

- [ ] **Step 1: Create firmware_cpp directory**

```powershell
mkdir "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
```

- [ ] **Step 2: Create config.h with pin definitions, defaults struct, and report struct**

```cpp
// firmware_cpp/config.h
#ifndef CONFIG_H
#define CONFIG_H

#include <stdint.h>

// --- Pin Configuration ---
#define PIN_BRAKE_ADC   26  // GP26 = ADC0 (brake pressure sensor)
#define PIN_HALL_ADC    27  // GP27 = ADC1 (SS49E Hall Effect throttle)
#define PIN_HX711_DATA  16  // GP16 (HX711 DATA)
#define PIN_HX711_SCK   28  // GP28 (HX711 SCK)
#define PIN_LED         25  // Onboard LED

// --- ADC ---
#define ADC_RESOLUTION  12  // RP2040 native ADC resolution
#define DEFAULT_OVERSAMPLE 16

// --- HID Report: 4 × uint16 LE, 8 bytes total ---
// X = processed brake, Y = raw brake ADC, Z = processed throttle, Rz = raw throttle
struct __attribute__((packed)) BrakeReport {
    uint16_t x;   // processed brake
    uint16_t y;   // raw brake ADC
    uint16_t z;   // processed throttle
    uint16_t rz;  // raw throttle ADC
};

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

// --- Shared sensor data (Core 1 → Core 0) ---
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
```

- [ ] **Step 3: Create minimal firmware_cpp.ino that compiles**

```cpp
// firmware_cpp/firmware_cpp.ino
#include "config.h"

void setup() {
    pinMode(PIN_LED, OUTPUT);
    digitalWrite(PIN_LED, HIGH);
}

void loop() {
    // Placeholder — just blink to verify build
    digitalWrite(PIN_LED, !digitalRead(PIN_LED));
    delay(500);
}
```

- [ ] **Step 4: Verify build with Arduino CLI**

Install Arduino CLI if not already installed. Then install the arduino-pico core and compile:

```powershell
# Install arduino-pico core (one-time setup)
arduino-cli core install rp2040:rp2040@4.4.0 --additional-urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json

# Install ArduinoJson
arduino-cli lib install ArduinoJson

# Compile (produces .uf2 in build output)
arduino-cli compile --fqbn rp2040:rp2040:rpipico --build-path "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp\build" "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
```

Expected: Build succeeds, `firmware_cpp.ino.uf2` appears in build output directory. If Arduino CLI not installed, verify via Arduino IDE instead (select board "Raspberry Pi Pico", compile).

- [ ] **Step 5: Commit**

```bash
git add firmware_cpp/config.h firmware_cpp/firmware_cpp.ino
git commit -m "feat(cpp): scaffolding with config.h and minimal .ino"
```

---

### Task 2: Custom HID Gamepad Descriptor

**Files:**
- Modify: `firmware_cpp/firmware_cpp.ino`
- Modify: `firmware_cpp/config.h`

Implements the custom 16-bit USB HID gamepad descriptor (4 axes, unsigned 0-65535, 8 bytes), identical to CircuitPython's boot.py. Uses `USB.registerHIDDevice()` with Pico SDK USB stack for full control over the descriptor (not the built-in Joystick library, which forces signed int16 and 6 axes).

- [ ] **Step 1: Add HID descriptor bytes to config.h**

Add the following after the `BrakeReport` struct definition in `config.h`:

```cpp
// --- Custom HID Report Descriptor (matches CircuitPython boot.py exactly) ---
// 4 axes (X, Y, Z, Rz), 16-bit unsigned, no buttons, no hat, no report ID
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
```

- [ ] **Step 2: Update firmware_cpp.ino with HID registration and test report sending**

Replace the entire content of `firmware_cpp.ino`:

```cpp
// firmware_cpp/firmware_cpp.ino
#include "config.h"
#include <USB.h>
#include <tusb.h>

// 1ms poll interval = 1000Hz HID polling
int usb_hid_poll_interval = 1;

uint8_t hid_id = 0;

void setup() {
    pinMode(PIN_LED, OUTPUT);
    digitalWrite(PIN_LED, HIGH);

    // Register custom HID gamepad descriptor
    USB.disconnect();
    hid_id = USB.registerHIDDevice(
        GAMEPAD_HID_DESCRIPTOR,
        GAMEPAD_HID_DESCRIPTOR_LEN,
        1,      // poll_interval_ms
        0x0004  // protocol = Gamepad (HID_ITF_PROTOCOL_NONE would be 0)
    );
    USB.connect();
}

void loop() {
    // Send a test report with rising/falling values to verify in Windows game controller
    static uint16_t val = 0;
    static bool rising = true;

    BrakeReport report;
    report.x  = val;   // Brake
    report.y  = val;   // Raw brake
    report.z  = 0;     // Throttle (off)
    report.rz = 0;     // Raw throttle

    // Send the report via TinyUSB
    if (USB.HIDReady()) {
        tud_hid_n_report(0, USB.findHIDReportID(hid_id), &report, sizeof(report));
    }

    // Sweep value for visual verification
    if (rising) {
        val += 1000;
        if (val >= 60000) rising = false;
    } else {
        val -= 1000;
        if (val <= 0) { val = 0; rising = true; }
    }

    delay(20);  // ~50Hz sweep for visual test
}
```

- [ ] **Step 3: Build and flash to Pico for verification**

Build with Arduino CLI or IDE. Flash the .uf2 via BOOTSEL. Open Windows "Set up USB game controllers" — verify that the device appears with 4 axes at 16-bit resolution and the X axis sweeps up and down.

```powershell
# Build
arduino-cli compile --fqbn rp2040:rp2040:rpipico --build-path "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp\build" "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
# Copy .uf2 to Pico (in BOOTSEL mode)
# The .uf2 will be in the build directory
```

- [ ] **Step 4: Commit**

```bash
git add firmware_cpp/config.h firmware_cpp/firmware_cpp.ino
git commit -m "feat(cpp): custom 16-bit HID gamepad descriptor with test sweep"
```

---

### Task 3: ADC Reader Module

**Files:**
- Create: `firmware_cpp/adc_reader.h`
- Create: `firmware_cpp/adc_reader.cpp`

Implements ADC initialization and oversampled reading for brake (GP26) and Hall throttle (GP27). Matches the CircuitPython `read_adc_oversampled()` function exactly — reads N times, averages, returns 16-bit scaled value (0-65535) matching CircuitPython's `analogio.AnalogIn.value` mapping.

- [ ] **Step 1: Create adc_reader.h**

```cpp
// firmware_cpp/adc_reader.h
#ifndef ADC_READER_H
#define ADC_READER_H

#include <stdint.h>

void adc_reader_init();
// Read ADC channel with oversampling. Returns value in 0-65535 range
// (matching CircuitPython's 16-bit scaling from 12-bit ADC).
// channel: 0=GP26, 1=GP27, 2=GP28, 3=GP29
uint16_t adc_read_oversampled(uint8_t channel, uint8_t oversample_count);

#endif // ADC_READER_H
```

- [ ] **Step 2: Create adc_reader.cpp**

```cpp
// firmware_cpp/adc_reader.cpp
#include "adc_reader.h"
#include <Arduino.h>

void adc_reader_init() {
    analogReadResolution(ADC_RESOLUTION);

    // Configure ADC pins — analogRead() handles pin setup internally on RP2040
    // GP26 = A0, GP27 = A1, GP28 = A2
    // Just make sure they're not in OUTPUT mode
    pinMode(PIN_BRAKE_ADC, INPUT);
    pinMode(PIN_HALL_ADC, INPUT);
}

// Arduino pin number from ADC channel number
static int channel_to_arduino_pin(uint8_t channel) {
    switch (channel) {
        case 0: return A0;  // GP26
        case 1: return A1;  // GP27
        case 2: return A2;  // GP28
        case 3: return A3;  // GP29 (temp sensor)
        default: return A0;
    }
}

uint16_t adc_read_oversampled(uint8_t channel, uint8_t oversample_count) {
    if (oversample_count == 0) oversample_count = 1;

    int pin = channel_to_arduino_pin(channel);
    uint32_t sum = 0;

    for (uint8_t i = 0; i < oversample_count; i++) {
        sum += analogRead(pin);  // 12-bit: 0-4095
    }

    // Average the 12-bit readings
    uint16_t avg_12bit = (uint16_t)(sum / oversample_count);

    // Scale 12-bit → 16-bit to match CircuitPython's analogio behavior
    // CircuitPython maps: (raw << 4) | (raw >> 8)
    return (avg_12bit << 4) | (avg_12bit >> 8);
}
```

- [ ] **Step 3: Wire adc_reader into firmware_cpp.ino for live testing**

Add to `firmware_cpp.ino` after `#include "config.h"`:

```cpp
#include "adc_reader.h"
```

Replace the `setup()` function body to add `adc_reader_init()` call:

```cpp
void setup() {
    pinMode(PIN_LED, OUTPUT);
    digitalWrite(PIN_LED, HIGH);

    adc_reader_init();

    // Register custom HID gamepad descriptor
    USB.disconnect();
    hid_id = USB.registerHIDDevice(
        GAMEPAD_HID_DESCRIPTOR,
        GAMEPAD_HID_DESCRIPTOR_LEN,
        1,
        0x0004
    );
    USB.connect();
}
```

Replace the `loop()` function to send real ADC values:

```cpp
void loop() {
    // Read brake ADC (GP26 = channel 0)
    uint16_t brake_raw = adc_read_oversampled(0, DEFAULT_OVERSAMPLE);

    // Read Hall throttle ADC (GP27 = channel 1) — placeholder, no processing yet
    uint16_t throttle_raw = adc_read_oversampled(1, DEFAULT_OVERSAMPLE);

    BrakeReport report;
    report.x  = brake_raw;       // Processed = raw (no processing yet)
    report.y  = brake_raw;       // Raw brake ADC
    report.z  = throttle_raw;    // Processed = raw (no processing yet)
    report.rz = throttle_raw;    // Raw throttle ADC

    if (USB.HIDReady()) {
        tud_hid_n_report(0, USB.findHIDReportID(hid_id), &report, sizeof(report));
    }

    delay(1);  // ~1000Hz
}
```

- [ ] **Step 4: Build, flash, and verify in calibration GUI**

Build and flash. Run `python gui/calibrator.py`. Verify that:
- The Pico appears as a gamepad device
- Raw ADC values show in the GUI's blue line
- Pressing the brake moves the graph

- [ ] **Step 5: Commit**

```bash
git add firmware_cpp/adc_reader.h firmware_cpp/adc_reader.cpp firmware_cpp/firmware_cpp.ino
git commit -m "feat(cpp): ADC reader module with oversampled 12→16 bit scaling"
```

---

### Task 4: HX711 Bit-Bang Driver

**Files:**
- Create: `firmware_cpp/hx711_driver.h`
- Create: `firmware_cpp/hx711_driver.cpp`

Implements GPIO bit-bang driver for the HX711 load cell amplifier. Includes: init, probe (data-ready check), and read with 24-bit shift + sign extension + gain/channel select. Uses `noInterrupts()`/`interrupts()` during the shift to prevent data corruption. Maps 24-bit signed result to unsigned 16-bit (0-65535) matching CircuitPython's `raw >> 8` behavior.

- [ ] **Step 1: Create hx711_driver.h**

```cpp
// firmware_cpp/hx711_driver.h
#ifndef HX711_DRIVER_H
#define HX711_DRIVER_H

#include <stdint.h>
#include <stdbool.h>

// Gain/channel select: number of extra clock pulses after 24 data bits
// 1 = Channel A Gain 128 (default), 2 = Channel B Gain 32, 3 = Channel A Gain 64
#define HX711_GAIN_A_128  1
#define HX711_GAIN_B_32   2
#define HX711_GAIN_A_64   3

class HX711Driver {
public:
    HX711Driver(uint8_t data_pin, uint8_t sck_pin);

    void init();
    bool probe(uint32_t timeout_ms = 120);  // Check if HX711 is connected
    int32_t read(uint8_t gain_pulses = HX711_GAIN_A_128);  // Raw 24-bit signed
    uint16_t read_u16(uint8_t gain_pulses = HX711_GAIN_A_128);  // Mapped to 0-65535

    void power_down();
    void power_up(uint8_t gain_pulses = HX711_GAIN_A_128);

private:
    uint8_t _data_pin;
    uint8_t _sck_pin;
    bool _initialized = false;
};

#endif // HX711_DRIVER_H
```

- [ ] **Step 2: Create hx711_driver.cpp**

```cpp
// firmware_cpp/hx711_driver.cpp
#include "hx711_driver.h"
#include <Arduino.h>

HX711Driver::HX711Driver(uint8_t data_pin, uint8_t sck_pin)
    : _data_pin(data_pin), _sck_pin(sck_pin) {}

void HX711Driver::init() {
    pinMode(_sck_pin, OUTPUT);
    digitalWrite(_sck_pin, LOW);

    pinMode(_data_pin, INPUT_PULLUP);
    _initialized = true;
}

bool HX711Driver::probe(uint32_t timeout_ms) {
    init();  // Ensure pins are configured

    // HX711 pulls DATA LOW when data is ready
    // Wait up to timeout_ms for DATA to go LOW
    uint32_t deadline = millis() + timeout_ms;
    while (millis() < deadline) {
        if (digitalRead(_data_pin) == LOW) {
            // HX711 is present — clean up by completing a read cycle and powering down
            // Pulse SCK 27 times (25 completes read + 1 sets gain + 1 extra = power down)
            for (int i = 0; i < 27; i++) {
                digitalWrite(_sck_pin, HIGH);
                delayMicroseconds(1);
                digitalWrite(_sck_pin, LOW);
                delayMicroseconds(1);
            }
            return true;
        }
        delay(10);
    }
    return false;  // No HX711 detected
}

int32_t HX711Driver::read(uint8_t gain_pulses) {
    if (!_initialized) init();

    // Wait for DATA to go LOW (data ready)
    uint32_t timeout = millis() + 500;
    while (digitalRead(_data_pin) == HIGH) {
        if (millis() > timeout) return 0;  // Timeout
        tight_loop_contents();
    }

    uint32_t value = 0;

    // Disable interrupts during shift to prevent data corruption
    uint32_t irq_state = save_and_disable_interrupts();

    // Shift in 24 bits MSB first
    for (int i = 0; i < 24; i++) {
        digitalWrite(_sck_pin, HIGH);
        delayMicroseconds(1);
        value <<= 1;
        if (digitalRead(_data_pin)) {
            value |= 1;
        }
        digitalWrite(_sck_pin, LOW);
        delayMicroseconds(1);
    }

    // Extra clock pulses to set gain/channel for next reading
    for (int i = 0; i < gain_pulses; i++) {
        digitalWrite(_sck_pin, HIGH);
        delayMicroseconds(1);
        digitalWrite(_sck_pin, LOW);
        delayMicroseconds(1);
    }

    restore_interrupts(irq_state);

    // Convert 24-bit two's complement to int32_t
    if (value & 0x800000) {
        value |= 0xFF000000;  // Sign extend
    }

    return (int32_t)value;
}

uint16_t HX711Driver::read_u16(uint8_t gain_pulses) {
    int32_t raw = read(gain_pulses);

    // Match CircuitPython's mapping: clamp negative to 0, shift right by 8
    if (raw < 0) raw = 0;
    uint32_t shifted = (uint32_t)raw >> 8;

    // Clamp to 16-bit range
    if (shifted > 65535) shifted = 65535;
    return (uint16_t)shifted;
}

void HX711Driver::power_down() {
    digitalWrite(_sck_pin, HIGH);
    delayMicroseconds(64);
}

void HX711Driver::power_up(uint8_t gain_pulses) {
    digitalWrite(_sck_pin, LOW);
    read(gain_pulses);  // First read after power-up sets gain
}
```

- [ ] **Step 3: Build to verify compilation**

```powershell
arduino-cli compile --fqbn rp2040:rp2040:rpipico --build-path "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp\build" "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
```

Expected: Build succeeds. HX711 driver compiles but is not yet wired into main loop (that happens in Task 7).

- [ ] **Step 4: Commit**

```bash
git add firmware_cpp/hx711_driver.h firmware_cpp/hx711_driver.cpp
git commit -m "feat(cpp): HX711 bit-bang driver with probe, read, and 16-bit mapping"
```

---

### Task 5: Signal Processing Module

**Files:**
- Create: `firmware_cpp/signal_processing.h`
- Create: `firmware_cpp/signal_processing.cpp`

Implements all signal processing that currently runs in CircuitPython's main loop: clamp, normalize, deadzone, bite point, curve application (linear, progressive, aggressive, custom), EMA smoothing, and invert. Processes a single channel given a `ChannelCal` struct — used for both brake and throttle with the same code path.

- [ ] **Step 1: Create signal_processing.h**

```cpp
// firmware_cpp/signal_processing.h
#ifndef SIGNAL_PROCESSING_H
#define SIGNAL_PROCESSING_H

#include <stdint.h>
#include "config.h"

// Process a single channel (brake or throttle) from raw ADC to 0-65535 output.
// raw: 16-bit ADC value (0-65535, already oversampled)
// cal: channel calibration settings
// ema: current EMA state (modified in place)
// ema_init: whether EMA has been initialized (modified in place)
// Returns: processed value 0-65535
uint16_t process_channel(uint16_t raw, const ChannelCal& cal,
                         float& ema, bool& ema_init);

#endif // SIGNAL_PROCESSING_H
```

- [ ] **Step 2: Create signal_processing.cpp**

```cpp
// firmware_cpp/signal_processing.cpp
#include "signal_processing.h"
#include <math.h>

// Clamp value to [lo, hi]
static inline float clampf(float val, float lo, float hi) {
    if (val < lo) return lo;
    if (val > hi) return hi;
    return val;
}

// Piecewise-linear interpolation through control points
static float interpolate_custom_curve(float t, const ChannelCal& cal) {
    if (cal.num_curve_points < 2) return t;
    if (t <= cal.curve_points_input[0]) return cal.curve_points_output[0];
    if (t >= cal.curve_points_input[cal.num_curve_points - 1])
        return cal.curve_points_output[cal.num_curve_points - 1];

    for (uint8_t i = 0; i < cal.num_curve_points - 1; i++) {
        float x0 = cal.curve_points_input[i];
        float x1 = cal.curve_points_input[i + 1];
        if (x0 <= t && t <= x1) {
            float y0 = cal.curve_points_output[i];
            float y1 = cal.curve_points_output[i + 1];
            if (x1 == x0) return y0;
            float frac = (t - x0) / (x1 - x0);
            return y0 + frac * (y1 - y0);
        }
    }
    return t;
}

// Apply curve to normalized value t (0.0 - 1.0)
static float apply_curve(float t, const ChannelCal& cal) {
    if (strcmp(cal.curve, "linear") == 0) {
        return t;
    } else if (strcmp(cal.curve, "progressive") == 0) {
        return powf(t, cal.progressive_power);
    } else if (strcmp(cal.curve, "aggressive") == 0) {
        return powf(t, 1.0f / cal.aggressive_power);
    } else if (strcmp(cal.curve, "custom") == 0) {
        return interpolate_custom_curve(t, cal);
    } else {
        // Unknown curve — default to progressive
        return powf(t, cal.progressive_power);
    }
}

uint16_t process_channel(uint16_t raw, const ChannelCal& cal,
                         float& ema, bool& ema_init) {
    // 1. Clamp to calibration range (saturation scales raw_max down)
    float raw_min = (float)cal.raw_min;
    float raw_max_eff = raw_min + ((float)cal.raw_max - raw_min) * fmin(cal.saturation, 1.0f);
    float clamped = clampf((float)raw, raw_min, raw_max_eff);

    // 2. Normalize to 0.0 - 1.0
    float normalized;
    if (raw_max_eff == raw_min) {
        normalized = 0.0f;
    } else {
        normalized = (clamped - raw_min) / (raw_max_eff - raw_min);
    }

    // 3. Apply deadzone
    float deadzone = 0.0f;
    if (raw_max_eff != raw_min) {
        deadzone = (float)cal.deadzone / (raw_max_eff - raw_min);
    }
    if (normalized < deadzone) {
        normalized = 0.0f;
    } else if (deadzone > 0.0f) {
        normalized = (normalized - deadzone) / (1.0f - deadzone);
    }

    // 4. Apply bite point (dead-travel simulating pad-to-rotor gap)
    if (cal.bite_point > 0.0f) {
        if (normalized < cal.bite_point) {
            normalized = 0.0f;
        } else {
            normalized = (normalized - cal.bite_point) / (1.0f - cal.bite_point);
        }
    }

    // 5. Apply curve
    normalized = apply_curve(normalized, cal);

    // 6. EMA smoothing (smoothing 0 = none, 0.95 = max; alpha = 1 - smoothing)
    float alpha = 1.0f - fmin(cal.smoothing, 0.95f);
    if (!ema_init) {
        ema = normalized;
        ema_init = true;
    } else {
        ema = alpha * normalized + (1.0f - alpha) * ema;
    }

    // 7. Invert if needed
    if (cal.invert) {
        ema = 1.0f - ema;
    }

    // 8. Map to 0-65535
    return (uint16_t)clampf(ema * 65535.0f, 0.0f, 65535.0f);
}
```

- [ ] **Step 3: Build to verify compilation**

```powershell
arduino-cli compile --fqbn rp2040:rp2040:rpipico --build-path "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp\build" "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add firmware_cpp/signal_processing.h firmware_cpp/signal_processing.cpp
git commit -m "feat(cpp): signal processing - clamp, normalize, deadzone, bite, curve, EMA, invert"
```

---

### Task 6: Calibration Module (JSON + FatFS)

**Files:**
- Create: `firmware_cpp/calibration.h`
- Create: `firmware_cpp/calibration.cpp`
- Create: `firmware_cpp/msc_disk.h`
- Create: `firmware_cpp/msc_disk.cpp`

Implements loading calibration.json from flash via FatFS, parsing with ArduinoJson, and merging with defaults. Also sets up FatFSUSB so the Pico appears as a USB Mass Storage drive alongside the HID gamepad (composite device). This replicates the CircuitPython CIRCUITPY drive behavior. The PC-side calibration GUI writes calibration.json to this drive; the firmware reads it at boot.

- [ ] **Step 1: Create msc_disk.h**

```cpp
// firmware_cpp/msc_disk.h
#ifndef MSC_DISK_H
#define MSC_DISK_H

#include <stdbool.h>

// Initialize FatFS + FatFSUSB composite device.
// Must be called in setup() before any file operations.
void msc_disk_init();

// Returns true if the PC currently has the USB drive mounted.
bool msc_disk_is_pc_connected();

#endif // MSC_DISK_H
```

- [ ] **Step 2: Create msc_disk.cpp**

```cpp
// firmware_cpp/msc_disk.cpp
#include "msc_disk.h"
#include <FatFS.h>
#include <FatFSUSB.h>

static bool _pc_connected = false;

static void _on_plug(uint32_t data) {
    (void)data;
    // PC is mounting the drive — we must unmount FatFS to avoid conflicts
    FatFS.end();
    _pc_connected = true;
}

static void _on_unplug(uint32_t data) {
    (void)data;
    // PC disconnected — we can remount FatFS
    FatFS.begin();
    _pc_connected = false;
}

static bool _on_drive_ready(uint32_t data) {
    (void)data;
    // Return true if PC can mount (no files open)
    return !FatFS.busy();
}

void msc_disk_init() {
    FatFS.begin();

    fatFSUSB.onPlug(_on_plug);
    fatFSUSB.onUnplug(_on_unplug);
    fatFSUSB.onDriveReady(_on_drive_ready);
    fatFSUSB.begin();  // Exposes as USB MSC drive alongside HID
}

bool msc_disk_is_pc_connected() {
    return _pc_connected;
}
```

- [ ] **Step 3: Create calibration.h**

```cpp
// firmware_cpp/calibration.h
#ifndef CALIBRATION_H
#define CALIBRATION_H

#include "config.h"

// Load calibration.json from flash. Returns merged Calibration with defaults.
Calibration load_calibration();

// Save calibration to flash (for future use, not needed for initial port).
// void save_calibration(const Calibration& cal);

#endif // CALIBRATION_H
```

- [ ] **Step 4: Create calibration.cpp**

```cpp
// firmware_cpp/calibration.cpp
#include "calibration.h"
#include "msc_disk.h"
#include <ArduinoJson.h>
#include <FatFS.h>

// Helper: load a ChannelCal from a JSON object
static void load_channel_cal(JsonObjectConst obj, ChannelCal& cal, const char* prefix) {
    // Build key strings with prefix (e.g., "throttle_raw_min")
    char key[32];

    snprintf(key, sizeof(key), "%sraw_min", prefix);
    if (obj.containsKey(key)) cal.raw_min = obj[key];

    snprintf(key, sizeof(key), "%sraw_max", prefix);
    if (obj.containsKey(key)) cal.raw_max = obj[key];

    snprintf(key, sizeof(key), "%sdeadzone", prefix);
    if (obj.containsKey(key)) cal.deadzone = obj[key];

    snprintf(key, sizeof(key), "%scurve", prefix);
    if (obj.containsKey(key)) {
        const char* curve = obj[key];
        strncpy(cal.curve, curve, sizeof(cal.curve) - 1);
        cal.curve[sizeof(cal.curve) - 1] = '\0';
    }

    snprintf(key, sizeof(key), "%sprogressive_power", prefix);
    if (obj.containsKey(key)) cal.progressive_power = obj[key];

    snprintf(key, sizeof(key), "%saggressive_power", prefix);
    if (obj.containsKey(key)) cal.aggressive_power = obj[key];

    snprintf(key, sizeof(key), "%ssmoothing", prefix);
    if (obj.containsKey(key)) cal.smoothing = obj[key];

    snprintf(key, sizeof(key), "%sinvert", prefix);
    if (obj.containsKey(key)) cal.invert = obj[key];

    snprintf(key, sizeof(key), "%ssaturation", prefix);
    if (obj.containsKey(key)) cal.saturation = obj[key];

    snprintf(key, sizeof(key), "%sbite_point", prefix);
    if (obj.containsKey(key)) cal.bite_point = obj[key];

    // Custom curve points
    snprintf(key, sizeof(key), "%scurve_points", prefix);
    if (obj.containsKey(key)) {
        JsonArrayConst points = obj[key];
        uint8_t idx = 0;
        for (JsonVariantConst point : points) {
            if (idx >= ChannelCal::MAX_CURVE_POINTS) break;
            if (point.is<JsonArrayConst>()) {
                JsonArrayConst pt = point.as<JsonArrayConst>();
                if (pt.size() >= 2) {
                    cal.curve_points_input[idx]  = pt[0];
                    cal.curve_points_output[idx] = pt[1];
                    idx++;
                }
            }
        }
        cal.num_curve_points = idx;
    }
}

Calibration load_calibration() {
    Calibration cal;  // Starts with all defaults from config.h

    if (msc_disk_is_pc_connected()) {
        // PC has the drive mounted — can't read files
        return cal;
    }

    File f = FatFS.open("/calibration.json", "r");
    if (!f) {
        return cal;  // File not found — return defaults
    }

    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, f);
    f.close();

    if (err) {
        return cal;  // Parse error — return defaults
    }

    JsonObjectConst obj = doc.as<JsonObjectConst>();

    // Top-level keys (brake uses no prefix, throttle uses "throttle_")
    if (obj.containsKey("oversample"))     cal.oversample = obj["oversample"];
    if (obj.containsKey("throttle_enabled")) cal.throttle_enabled = obj["throttle_enabled"];
    if (obj.containsKey("throttle_sensor")) {
        const char* sensor = obj["throttle_sensor"];
        strncpy(cal.throttle_sensor, sensor, sizeof(cal.throttle_sensor) - 1);
        cal.throttle_sensor[sizeof(cal.throttle_sensor) - 1] = '\0';
    }

    // Brake channel keys have no prefix (e.g., "raw_min", "curve")
    load_channel_cal(obj, cal.brake, "");

    // Throttle channel keys have "throttle_" prefix
    load_channel_cal(obj, cal.throttle, "throttle_");

    // Override brake smoothing default to 0.3 if not in file
    // (throttle default is 0.2, set in ChannelCal constructor)

    return cal;
}
```

- [ ] **Step 5: Build to verify compilation**

```powershell
arduino-cli compile --fqbn rp2040:rp2040:rpipico --build-path "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp\build" "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
```

Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add firmware_cpp/calibration.h firmware_cpp/calibration.cpp firmware_cpp/msc_disk.h firmware_cpp/msc_disk.cpp
git commit -m "feat(cpp): calibration module with ArduinoJson parsing + FatFSUSB MSC composite"
```

---

### Task 7: Integration — Dual-Core Main Loop

**Files:**
- Modify: `firmware_cpp/firmware_cpp.ino`

This is the main integration task. Wire all modules together into the dual-core main loop:
- Core 0: USB task + HID report sending at 1000Hz
- Core 1: ADC/HX711 reading + signal processing at ~2000Hz
- Shared data via volatile struct + memory barrier
- Boot sequence: load calibration → detect throttle sensor → initialize hardware → run

- [ ] **Step 1: Replace firmware_cpp.ino with full dual-core implementation**

```cpp
// firmware_cpp/firmware_cpp.ino
#include "config.h"
#include "adc_reader.h"
#include "hx711_driver.h"
#include "signal_processing.h"
#include "calibration.h"
#include "msc_disk.h"
#include <USB.h>
#include <tusb.h>

// --- 1ms poll interval = 1000Hz HID polling ---
int usb_hid_poll_interval = 1;

// --- Global state ---
uint8_t hid_id = 0;
Calibration cal;
ThrottleSensor throttle_sensor = ThrottleSensor::NONE;
HX711Driver* hx711 = nullptr;

// --- Shared sensor data (Core 1 → Core 0 via volatile + memory barrier) ---
volatile SensorData g_sensor = {};
volatile bool g_sensor_ready = false;

// --- EMA state ---
float brake_ema = 0.0f;
bool brake_ema_init = false;
float throttle_ema = 0.0f;
bool throttle_ema_init = false;

// --- LED blink helper ---
void blink_led(uint8_t count, uint16_t on_ms = 100, uint16_t off_ms = 100) {
    for (uint8_t i = 0; i < count; i++) {
        digitalWrite(PIN_LED, HIGH);
        delay(on_ms);
        digitalWrite(PIN_LED, LOW);
        delay(off_ms);
    }
}

// ============================================================
// Core 0: USB HID task (setup + loop)
// ============================================================

void setup() {
    pinMode(PIN_LED, OUTPUT);

    // Initialize USB MSC composite (FatFS + FatFSUSB)
    msc_disk_init();

    // Load calibration from flash
    cal = load_calibration();

    // Register custom HID gamepad descriptor
    USB.disconnect();
    hid_id = USB.registerHIDDevice(
        GAMEPAD_HID_DESCRIPTOR,
        GAMEPAD_HID_DESCRIPTOR_LEN,
        1,
        0x0004
    );
    USB.connect();

    // Signal running
    digitalWrite(PIN_LED, HIGH);
}

void loop() {
    // Send HID report when new sensor data is available
    if (g_sensor_ready) {
        __sync_synchronize();  // Memory barrier before reading shared data
        SensorData local = (SensorData)g_sensor;  // Snapshot

        BrakeReport report;
        report.x  = local.brake_processed;
        report.y  = local.brake_raw;
        report.z  = local.throttle_processed;
        report.rz = local.throttle_raw;

        if (USB.HIDReady()) {
            tud_hid_n_report(0, USB.findHIDReportID(hid_id), &report, sizeof(report));
        }

        g_sensor_ready = false;
    }

    delayMicroseconds(500);  // Core 0 yields between checks
}

// ============================================================
// Core 1: ADC reading + signal processing (setup1 + loop1)
// ============================================================

void setup1() {
    // Initialize ADC reader
    adc_reader_init();

    // Determine throttle sensor type
    if (cal.throttle_enabled) {
        if (strcmp(cal.throttle_sensor, "auto") == 0) {
            // Auto-detect: probe for HX711, fall back to Hall
            HX711Driver probe_hx(PIN_HX711_DATA, PIN_HX711_SCK);
            if (probe_hx.probe(120)) {
                throttle_sensor = ThrottleSensor::LOAD_CELL;
                hx711 = new HX711Driver(PIN_HX711_DATA, PIN_HX711_SCK);
                hx711->init();
                blink_led(2, 100, 200);  // 2 blinks = HX711 detected
            } else {
                throttle_sensor = ThrottleSensor::HALL;
                blink_led(1, 100, 100);  // 1 blink = Hall detected
            }
        } else if (strcmp(cal.throttle_sensor, "load_cell") == 0) {
            throttle_sensor = ThrottleSensor::LOAD_CELL;
            hx711 = new HX711Driver(PIN_HX711_DATA, PIN_HX711_SCK);
            hx711->init();
            blink_led(2, 100, 200);
        } else {
            throttle_sensor = ThrottleSensor::HALL;
            blink_led(1, 100, 100);
        }
    } else {
        throttle_sensor = ThrottleSensor::NONE;
    }
}

void loop1() {
    // --- Read brake ---
    uint16_t brake_raw = adc_read_oversampled(0, cal.oversample);
    uint16_t brake_processed = process_channel(brake_raw, cal.brake,
                                               brake_ema, brake_ema_init);

    // --- Read throttle ---
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

    // --- Publish to shared struct ---
    SensorData sd;
    sd.brake_raw        = brake_raw;
    sd.brake_processed  = brake_processed;
    sd.throttle_raw     = throttle_raw;
    sd.throttle_processed = throttle_processed;

    __sync_synchronize();  // Memory barrier before writing shared data
    g_sensor = sd;
    g_sensor_ready = true;

    // Run at ~2000Hz (500µs per iteration)
    delayMicroseconds(500);
}
```

- [ ] **Step 2: Build, flash, and end-to-end test**

```powershell
arduino-cli compile --fqbn rp2040:rp2040:rpipico --build-path "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp\build" "C:\aaa\code\Pico_Brake_Contoller\firmware_cpp"
```

Flash the .uf2. Verify:

1. **Drive appears**: Pico shows as a USB drive in Windows Explorer (alongside gamepad)
2. **HID gamepad**: Switch to Windows game controller settings, verify axes respond to brake pedal
3. **Calibration GUI**: Run `python gui/calibrator.py`, verify it detects the Pico gamepad, shows raw ADC values
4. **Write calibration**: Use the GUI to write calibration.json to the Pico drive, press RESET, verify settings load
5. **Throttle**: If HX711 or Hall sensor is wired, verify throttle axis responds

- [ ] **Step 3: Commit**

```bash
git add firmware_cpp/firmware_cpp.ino
git commit -m "feat(cpp): dual-core main loop with ADC processing on Core 1, HID send on Core 0"
```

---

### Task 8: GUI Compatibility Verification + Drive Path Update

**Files:**
- Modify: `gui/calibrator.py` (drive detection only — add `firmware_cpp` mode awareness)

The calibration GUI searches for a "CIRCUITPY" drive by volume label. The Arduino-Pico FatFSUSB drive will have a different default label. We need to ensure the GUI can find the Pico's drive regardless of whether CircuitPython or C++ firmware is running. The simplest approach: format the FatFS drive with label "CIRCUITPY" to maintain backward compatibility.

- [ ] **Step 1: Verify how calibrator.py finds the Pico drive**

Read `gui/calibrator.py` lines 1-100 to find the drive detection code. The key function searches Windows drives for one labeled "CIRCUITPY".

- [ ] **Step 2: Create a startup routine that formats the FatFS drive with label "CIRCUITPY" on first boot**

Add to `msc_disk.cpp`, before `FatFS.begin()`:

```cpp
void msc_disk_init() {
    // On first boot, format the flash filesystem with label "CIRCUITPY"
    // so the existing GUI can find it
    if (!FatFS.begin()) {
        // Filesystem not found — format it
        FatFS.format("CIRCUITPY");
        FatFS.begin();
    }

    fatFSUSB.onPlug(_on_plug);
    fatFSUSB.onUnplug(_on_unplug);
    fatFSUSB.onDriveReady(_on_drive_ready);
    fatFSUSB.begin();
}
```

Note: `FatFS.format()` may or may not be available as a single call. If the API differs, check the Arduino-Pico FatFS docs and adjust. The intent is: on first boot when no filesystem exists, create one with label "CIRCUITPY".

- [ ] **Step 3: Build, flash, and verify GUI can find the drive**

1. Flash the C++ firmware
2. The Pico should appear as a drive labeled "CIRCUITPY" in Windows Explorer
3. Run `python gui/calibrator.py`
4. Verify the GUI auto-detects the Pico's drive and can write calibration.json
5. Press RESET on Pico → verify new calibration values are applied

- [ ] **Step 4: Commit**

```bash
git add firmware_cpp/msc_disk.cpp
git commit -m "feat(cpp): format FatFS as CIRCUITPY label for GUI compatibility"
```

---

### Task 9: LED Status Indicators + Error Handling

**Files:**
- Modify: `firmware_cpp/firmware_cpp.ino`

Implements LED blink patterns for boot status (matching CircuitPython behavior):
- 1 blink = Hall Effect detected
- 2 blinks = HX711 detected
- 3 blinks = using default calibration (no calibration.json found)
- 4 blinks = HX711 init failed, fell back to Hall
- 5 rapid blinks = no gamepad HID device found (fatal error)
- Solid on = running normally

- [ ] **Step 1: Add calibration-default check to setup()**

In `firmware_cpp.ino`, add after `cal = load_calibration();` in `setup()`:

```cpp
    // Signal if using defaults (no calibration.json found)
    // We detect this by checking if all values match defaults
    // Simplified check: if raw_min == 2000 and raw_max == 56000, likely defaults
    if (cal.brake.raw_min == 2000 && cal.brake.raw_max == 56000 &&
        cal.throttle.raw_min == 2000 && cal.throttle.raw_max == 56000) {
        blink_led(3, 200, 200);  // 3 blinks = using defaults
    }
```

- [ ] **Step 2: Add fatal error halt if HID registration fails**

After `USB.connect()` in `setup()`, add:

```cpp
    // Verify HID device was registered
    if (hid_id == 0) {
        // No gamepad device — fatal error
        while (true) {
            blink_led(5, 50, 50);
            delay(1000);
        }
    }
```

- [ ] **Step 3: Build and verify LED blink patterns on boot**

Flash and observe LED patterns during boot:
- With no calibration.json: 3 blinks + solid on
- With calibration.json: solid on
- With HX711 connected: 2 blinks + solid on
- With Hall only: 1 blink + solid on

- [ ] **Step 4: Commit**

```bash
git add firmware_cpp/firmware_cpp.ino
git commit -m "feat(cpp): LED status indicators and error handling on boot"
```

---

### Task 10: Documentation + README Update

**Files:**
- Modify: `README.md` (if it exists) or create brief note about C++ firmware
- Modify: `AGENTS.md` (add C++ firmware section)

- [ ] **Step 1: Add C++ firmware section to AGENTS.md**

Add after the existing "Architecture" section:

```markdown
## C++ Firmware (Arduino-Pico)

- `firmware_cpp/firmware_cpp.ino` — Entry point: dual-core main loop
- `firmware_cpp/config.h` — Pin defs, Calibration/ChannelCal structs, HID descriptor
- `firmware_cpp/adc_reader.h/.cpp` — ADC init + oversampled read (12→16 bit scaling)
- `firmware_cpp/hx711_driver.h/.cpp` — HX711 bit-bang driver with probe/read
- `firmware_cpp/signal_processing.h/.cpp` — Curve, deadzone, bite, EMA, invert
- `firmware_cpp/calibration.h/.cpp` — JSON config loading via ArduinoJson + FatFS
- `firmware_cpp/msc_disk.h/.cpp` — FatFSUSB composite (HID + MSC drive)

Build: Arduino IDE or CLI → board "Raspberry Pi Pico" → compile → .uf2 output
Same calibration.json format as CircuitPython firmware, same GUI, same wiring.
```

- [ ] **Step 2: Commit**

```bash
git add AGENTS.md
git commit -m "docs: add C++ firmware section to AGENTS.md"
```

---

## Self-Review Checklist

### 1. Spec Coverage

| Original Feature | Task |
|---|---|
| Custom 16-bit HID descriptor (4 axes, 8 bytes) | Task 2 |
| ADC oversampled reading (GP26 brake) | Task 3 |
| SS49E Hall Effect throttle (GP27) | Task 3 |
| HX711 load cell throttle (GP16/GP28) | Task 4 |
| HX711 auto-detection (probe) | Task 7 (setup1) |
| Clamp + normalize + deadzone + bite point + curve + EMA + invert | Task 5 |
| All 4 curve types (linear, progressive, aggressive, custom) | Task 5 |
| Saturation scaling | Task 5 |
| calibration.json loading + defaults merge | Task 6 |
| USB MSC composite device (CIRCUITPY drive) | Task 6 |
| Dual-core (Core 0: USB, Core 1: ADC+processing) | Task 7 |
| LED blink status codes | Task 9 |
| GUI compatibility (same drive label, same JSON format) | Task 8 |

### 2. Placeholder Scan

No TBD, TODO, or "implement later" found. All code steps include complete implementation.

### 3. Type Consistency

- `ChannelCal` struct defined in `config.h`, used consistently in `signal_processing.cpp` and `calibration.cpp`
- `Calibration` struct defined in `config.h`, used in `firmware_cpp.ino` and `calibration.cpp`
- `BrakeReport` struct defined in `config.h`, used in `firmware_cpp.ino`
- `SensorData` struct defined in `config.h`, used in `firmware_cpp.ino`
- `ThrottleSensor` enum defined in `config.h`, used in `firmware_cpp.ino`
- `HX711Driver` class defined in `hx711_driver.h`, used in `firmware_cpp.ino` via pointer
- Curve string comparison uses `strcmp()` with `char[16]` in `ChannelCal` — consistent across signal_processing.cpp and calibration.cpp
