# C++ Firmware Spec — brake_controller

## What It Does

Sim racing pneumatic brake + throttle controller on RPi Pico (RP2040). Reads pressure/Hall/load-cell sensors, processes the signal through a configurable pipeline, and outputs as a USB HID gamepad (4 axes, 16-bit). A PC-side GUI (Python/Tkinter) sends calibration over serial — firmware applies immediately with no reboot.

## Core Requirements

### 1. Dual-Core Operation

- **Core 0:** USB HID send + serial command processing
- **Core 1:** ADC read + signal processing
- Shared `SensorData` struct transferred via `memcpy` with `__sync_synchronize()` memory barrier

### 2. Signal Pipeline

Per channel (brake and throttle), runs on Core 1:

| Step | Operation | Details |
|------|-----------|---------|
| 1 | ADC read | 12-bit native, oversampled, scaled to 16-bit via `(avg << 4) \| (avg >> 8)` |
| 2 | Clamp | Scaled by saturation factor |
| 3 | Normalize | Map to 0.0–1.0 within `[raw_min, raw_max * saturation]` |
| 4 | Deadzone | Remove sub-threshold noise, renormalize |
| 5 | Bite point | Dead-travel before curve (simulates pad-to-rotor gap) |
| 6 | Curve | linear / progressive (t^n) / aggressive (t^(1/n)) / custom (piecewise-linear) |
| 7 | EMA smoothing | alpha = 1 - smoothing; 0 = no filter, 0.95 = heavy |
| 8 | Invert | Flip 0↔1 if enabled |
| 9 | Output | Map to uint16 0–65535 |

### 3. HID Gamepad

- 8-byte report: X=processed brake, Y=raw brake, Z=processed throttle, Rz=raw throttle
- Custom HID descriptor: 4×uint16 LE axes, no report ID, no buttons, no hat, no Output Report
- Logical Minimum: `0x15 0x00` (1-byte, value 0)
- Logical Maximum: `0x27 0xFF 0xFF 0x00 0x00` (4-byte, value 65535 as signed 32-bit positive)
- USB poll rate: 1ms = 1000Hz
- USB VID: `0x239A` (Adafruit/TinyUSB), PID: `0xCAFE`

### 4. Serial Commands

All commands over Serial (115200 baud, DTR asserted). Firmware replies immediately.

| Command | Format | Reply | Description |
|---------|--------|-------|-------------|
| Calibrate | `CAL <json>\n` | `CAL OK` or `CAL ERR` | Parse JSON into `Calibration` struct, apply immediately, write to FatFS, reset EMA state |
| Reboot | `REBOOT\n` | `OK` | `watchdog_reboot(0, 0, 50)` |
| Reboot bootloader | `REBOOT BOOTSEL\n` | `OK` | `reset_usb_boot(0, 0)` — no button press needed |
| Save profile | `PROFILE SAVE <name>\n` | `PROFILE OK` or `PROFILE ERR` | Copy current calibration to `/profiles/<name>.json` |
| Load profile | `PROFILE LOAD <name>\n` | `PROFILE OK` or `PROFILE ERR` | Read profile, apply, write to `/calibration.json` |
| Delete profile | `PROFILE DELETE <name>\n` | `PROFILE OK` or `PROFILE ERR` | Delete `/profiles/<name>.json` |
| List profiles | `PROFILE LIST\n` | `PROFILE <name1,name2,...>` or `PROFILE NONE` | List all profiles |
| Status | `STATUS\n` | `FS:OK/FAIL`, `CAL:OK/DEFAULTS`, `WRITE:OK/FAIL` | Diagnostic snapshot |

### 5. Calibration State Transitions

When `CAL` updates the `cal` struct on Core 0:
- Core 1 reads `cal` on each `loop1()` iteration. No mutex — individual field reads are atomic on ARM Cortex-M0+ for aligned 8/16/32-bit types.
- `brake_ema_init = false; throttle_ema_init = false;` — Forces EMA to re-initialize, preventing stale smoothing state.

### 6. Flash Storage (FatFS)

- `--board-options "flash=2097152_1048576,usbstack=tinyusb"` — 1MB FatFS partition (REQUIRED)
- **Why 1MB not 64KB:** f_mkfs requires ≥128 logical sectors. At 4096 bytes/sector (`useFTL=false`), 64KB = 16 sectors — below the minimum. 1MB = 256 sectors.
- **Why `useFTL=false`:** The SPIFTL wear-levelling layer requires its own on-flash metadata. If the flash was previously written by different firmware (e.g. FatFSUSB), `SPIFTL::start()` cannot parse the existing content. Since calibration is saved rarely, wear levelling is unnecessary.
- Stores `/calibration.json` and `/profiles/` directory
- Not exposed as USB MSC drive — accessed only by firmware via serial
- On first boot: `FatFS.begin()` with `autoFormat=true` handles format automatically, then `FatFS.mkdir("/profiles")`
- FatFS stays mounted for the entire runtime

### 7. Throttle Sensor Auto-Detection

- On boot (Core 1 `setup1()`), probes GP16 for HX711 data-ready signal (DATA goes LOW when ready)
- If HX711 responds → load cell on GP16+GP28
- Falls back → SS49E Hall Effect on GP27 (ADC1)
- Cal field `throttle_sensor`: `"auto"` (use probe result), `"hall"` (force ADC), `"hx711"` (force HX711)
- HX711 GPIO always configured in `setup1()` regardless of probe result, so override works

### 8. Boot Sequence

1. `usb_hid.begin()` — register HID interface (must be before `Serial.begin()`)
2. `Serial.begin(115200)` — register CDC interface
3. `delay(1500)` — wait for USB enumeration (USB IRQ drives this independently; `while(!TinyUSBDevice.mounted())` blocks forever)
4. `rgb_led_init(); rgb_led_boot()` — orange LED
5. `flash_storage_init()` — mount FatFS, create `/profiles/` if new
6. `flash_load_calibration(cal)` — load `/calibration.json` into cal struct
7. LED feedback: Green=cal loaded, Red=using defaults

## Calibration Struct

```cpp
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
    float    curve_points_input[MAX_CURVE_POINTS] = {0.0f, 1.0f};
    float    curve_points_output[MAX_CURVE_POINTS] = {0.0f, 1.0f};
};

struct Calibration {
    uint8_t  oversample = 16;
    bool     throttle_enabled = false;
    char     throttle_sensor[12] = "auto";  // "auto", "hall", "hx711"
    ChannelCal brake;
    ChannelCal throttle;
};
```

## calibration.json Format

```json
{
  "raw_min": 2000, "raw_max": 56000, "deadzone": 300,
  "curve": "linear", "progressive_power": 2.0, "aggressive_power": 2.0,
  "smoothing": 0.3, "invert": false, "oversample": 16,
  "saturation": 1.0, "bite_point": 0.0,
  "curve_points": [[0.0, 0.0], [1.0, 1.0]],
  "throttle_enabled": false, "throttle_sensor": "auto",
  "throttle_raw_min": 2000, "throttle_raw_max": 56000,
  "throttle_deadzone": 300, "throttle_curve": "linear",
  "throttle_progressive_power": 2.0, "throttle_aggressive_power": 2.0,
  "throttle_smoothing": 0.2, "throttle_invert": false,
  "throttle_saturation": 1.0, "throttle_bite_point": 0.0,
  "throttle_curve_points": [[0.0, 0.0], [1.0, 1.0]]
}
```

## Wiring

| Component | Connection |
|-----------|-----------|
| XDB401 brake | VSYS(5V) → R1(2K) → GP26, R2(3.3K) → GND |
| SS49E Hall throttle | VSYS(5V) → R3(2K) → GP27, R4(3.3K) → GND |
| HX711 load cell | VIN→VSYS(5V), GND→GND, SCK→GP28, DATA→GP16 |
| WS2812 RGB LED | GP23 |

## Build & Flash

```bash
# Build
arduino-cli compile \
  --fqbn rp2040:rp2040:rpipico \
  --board-options "flash=2097152_1048576,usbstack=tinyusb" \
  --build-path firmware_cpp/build \
  firmware_cpp

# Flash via BOOTSEL button
copy firmware_cpp\build\firmware_cpp.ino.uf2 <RPI-RP2 drive>:\

# Flash via serial (no button press)
# 1. Send "REBOOT BOOTSEL\n" at 115200 baud with DTR asserted
# 2. Copy .uf2 to the RPI-RP2 drive that appears
```

## Key Learnings

### HID Logical Maximum must be 4-byte for unsigned 16-bit axes

The HID spec treats Logical Minimum and Maximum as **signed** integers. A 2-byte `0xFF 0xFF` encodes **-1** in signed 16-bit — not 65535. Windows normalises axis values against Logical Max, so when the axis value reaches 65535 (= -1 signed), it wraps to the bottom of the scale.

Fix: use 4-byte encoding so 0x0000FFFF = 65535 (positive signed 32-bit):
```cpp
0x15, 0x00,                    // Logical Minimum (0)
0x27, 0xFF, 0xFF, 0x00, 0x00,  // Logical Maximum (65535)
```

### USB interface registration order matters

`usb_hid.begin()` must be called **before** `Serial.begin()`. The arduino-pico USB stack assigns interface numbers in registration order. HID first → MI_02 (after CDC control/data pair on MI_00/MI_01). Reversed order results in only the HID interface enumerating (CDC is dropped).

### `while(!TinyUSBDevice.mounted()) delay(1)` blocks forever

On arduino-pico, the USB IRQ drives enumeration independently of the main loop. In `setup()`, calling `delay(1)` does not yield to the USB task sufficiently to flip `mounted()`. Use `delay(1500)` instead.

### TinyUSB CDC only flushes TX when DTR is asserted

pyserial asserts DTR by default. Raw serial terminal tools and PowerShell's `SerialPort` class do NOT assert DTR by default — you must set `DtrEnable = true`. Without DTR, `Serial.println()` calls buffer but never transmit.

### FatFS 64KB partition is below f_mkfs minimum

`f_mkfs` requires `sz_vol >= 128` (logical sectors). With `useFTL=false` (4096-byte sectors): 65536 / 4096 = 16 sectors — below the minimum. Use `flash=2097152_1048576` (1MB = 256 sectors).

### SPIFTL cannot parse pre-existing non-SPIFTL flash

With `useFTL=true` (default), the SPIFTL wear-levelling layer reads its own metadata format from flash. If the flash previously held a raw FAT or different firmware, `SPIFTL::start()` fails. By the time `format()` is called, `disk_initialize()` has already set `started=true`, so `disk_format()` (which calls `_ftl->format()`) is a no-op. The subsequent `f_mkfs()` writes through a corrupt FTL state. Fix: use `useFTL=false` for raw flash access.

### Pico serial port identification

Our firmware enumerates with **VID=0x239A** (Adafruit/TinyUSB) and **PID=0xCAFE**. CircuitPython/MicroPython use **VID=0x2E8A** (Raspberry Pi Foundation). GUI port detection must check `vid == 0x239A and pid == 0xCAFE` for our firmware, with `vid == 0x2E8A` as fallback for other Pico firmware.

### HID Output Report is broken with arduino-pico

Including an HID Output Report in the descriptor causes `CM_PROB_FAILED_START` on Windows with the arduino-pico USB stack. Use serial for bidirectional communication only.

### watchdog_reboot() vs reset_usb_boot()

- `watchdog_reboot(0, 0, 50)` — reboots into the application firmware after 50ms
- `reset_usb_boot(0, 0)` — reboots into the USB bootloader (BOOTSEL mode) — allows flashing without pressing the button

### PyInstaller must run from the venv

`.venv\Scripts\python.exe -m PyInstaller` — the venv has pygame-ce with built-in PyInstaller hooks. System Python's PyInstaller cannot detect pygame imported inside a `try/except`. Also add `serial`, `serial.tools`, `serial.tools.list_ports` to `hiddenimports` since they are imported lazily inside functions.
