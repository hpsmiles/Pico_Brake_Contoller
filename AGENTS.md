# AGENTS.md — brake_controller

## Project

Sim racing pneumatic brake + throttle controller for Sim Sonn Pro pedal. RPi Pico (RP2040) + XDB401 pressure sensor (brake) + SS49E Hall Effect or HX711 load cell (throttle). C++/Arduino-Pico firmware (production, deterministic 1kHz HID, dual-core). Python/Tkinter calibration GUI with serial-based calibration (no reboot needed). CircuitPython firmware is legacy/prototyping only — only care about C++ firmware.

## Commands

- Run calibration GUI: `python gui/calibrator.py`
- Build GUI exe: `.venv\Scripts\python.exe -m PyInstaller BrakeCalibrator.spec --distpath dist --workpath build_pyinstaller --noconfirm`
- Build C++ firmware: `arduino-cli compile --fqbn rp2040:rp2040:rpipico --board-options "flash=2097152_1048576,usbstack=tinyusb" --build-path firmware_cpp/build firmware_cpp`
- Flash via serial (no button): send `REBOOT BOOTSEL\n` on COM port at 115200 baud (DTR asserted), then copy .uf2 to RPI-RP2 drive
- No test suite

## Architecture

- `firmware_cpp/` — C++ firmware (Arduino-Pico): dual-core, serial calibration, FatFS storage
- `gui/calibrator.py` — PC-side Tkinter calibration tool (pygame for HID, pyserial for CAL commands)
- `calibration.json` — Lives on Pico flash only, NOT in repo

## C++ Firmware Files

| File | Purpose |
|------|---------|
| `firmware_cpp.ino` | Entry point: dual-core setup/loop (Core 0: USB+serial, Core 1: ADC+processing) |
| `config.h` | Pin defs, Calibration/ChannelCal structs, HID descriptor (4×uint16, no Output Report) |
| `adc_reader.h/.cpp` | ADC init + oversampled read (12→16-bit scaling) |
| `hx711_driver.h/.cpp` | HX711 bit-bang driver with probe/read/16-bit mapping |
| `signal_processing.h/.cpp` | Clamp, normalize, deadzone, bite, curve (linear/progressive/aggressive/custom), EMA, invert |
| `flash_storage.h/.cpp` | FatFS init, calibration.json load/save, profile CRUD (`/profiles/<name>.json`) |
| `serial_commands.h/.cpp` | Serial command parser: CAL, REBOOT, REBOOT BOOTSEL, PROFILE SAVE/LOAD/DELETE/LIST, STATUS |
| `rgb_led.h/.cpp` | WS2812 RGB LED status (GP23): Orange=boot, Green=cal loaded, Red=defaults, blink codes |

Build: `arduino-cli compile --fqbn rp2040:rp2040:rpipico --board-options "flash=2097152_1048576,usbstack=tinyusb" --build-path firmware_cpp/build firmware_cpp`

Flash: Copy `firmware_cpp/build/firmware_cpp.ino.uf2` to Pico via BOOTSEL, OR send `REBOOT BOOTSEL\n` over serial to avoid pressing the button.

USB identity: VID=0x239A (Adafruit/TinyUSB), PID=0xCAFE. Composite device: CDC serial (COM port) + HID gamepad.

## Key constraints

- No USB MSC drive — calibration is sent via serial CAL command only
- FatFS partition: 1MB (`flash=2097152_1048576`), `useFTL=false` (raw 4096-byte sectors). 64KB was below f_mkfs 128-sector minimum. SPIFTL disabled because it cannot parse pre-existing non-SPIFTL flash content.
- `usbstack=tinyusb` board option is REQUIRED — without it the compiler errors with "TinyUSB is not selected"
- USB setup order: `usb_hid.begin()` then `Serial.begin()` — reversed order drops the CDC interface. Use `delay(1500)` not `while(!TinyUSBDevice.mounted())` which blocks forever on this port.
- CDC serial requires DTR asserted before TinyUSB flushes TX. pyserial sets DTR by default.
- HID Logical Maximum must be 4-byte (`0x27 0xFF 0xFF 0x00 0x00` = 65535 signed 32-bit). 2-byte (`0x26 0xFF 0xFF`) encodes -1, causing axis wrap past 32767.
- HID report: 8 bytes (4×uint16 LE). X=processed brake, Y=raw brake, Z=processed throttle, Rz=raw throttle.
- ADC is 12-bit, scaled to 16-bit via `(avg << 4) | (avg >> 8)`. Oversampling default 16×.
- Brake wiring: VCC→VSYS(5V), Signal→R1(2K)→GP26, R2(3.3K)→GND
- Throttle sensors (set via `throttle_sensor` in calibration, default `"auto"`):
  - **SS49E Hall Effect**: VCC→VSYS(5V), Vout→R3(2K)→GP27(ADC1), R4(3.3K)→GND
  - **HX711 load cell**: VIN→VSYS(5V), GND→GND, SCK→GP28, DATA→GP16
- Both throttle sensors can be wired simultaneously — auto-detects HX711 on boot (probes GP16 for data-ready signal LOW)
- Smoothing slider: 0=none, 0.95=max. Internally `alpha = 1 - smoothing`.
- Saturation (0.1–1.0): Scales effective raw_max — reach 100% output with less force.
- Bite point (0.0–0.5): Dead-travel zone simulating pad-to-rotor gap; applied after deadzone, before curve.
- Curve types: linear, progressive (t^n), aggressive (t^1/n), custom (piecewise-linear).
- Profiles stored in `/profiles/` on firmware flash. Managed via serial PROFILE commands or GUI.
- HID Output Report NOT supported with arduino-pico (causes CM_PROB_FAILED_START on Windows).

## Serial Commands

115200 baud, newline-terminated, DTR asserted:

| Command | Response | Description |
|---------|----------|-------------|
| `CAL <json>` | `CAL OK` / `CAL ERR` | Apply + persist calibration, reset EMA |
| `REBOOT` | `OK` | Watchdog reboot into application |
| `REBOOT BOOTSEL` | `OK` | Reboot into USB bootloader |
| `PROFILE SAVE <name>` | `PROFILE OK` / `PROFILE ERR` | Save named profile |
| `PROFILE LOAD <name>` | `PROFILE OK` / `PROFILE ERR` | Load + activate named profile |
| `PROFILE DELETE <name>` | `PROFILE OK` / `PROFILE ERR` | Delete named profile |
| `PROFILE LIST` | `PROFILE <names>` / `PROFILE NONE` | List profiles |
| `STATUS` | Multi-line | FatFS state + write test |

## GUI features

- **3-line live graph:** Blue=Raw Input, Green=Calibrated Input (settings applied locally), Red=Game Input (Pico output), Orange=Throttle
- **Device selector:** Dropdown lists all connected gamepads, auto-selects device with "pico" in name
- **Auto-calibration:** 3s countdown → 5s capture (tracks min and max raw ADC values)
- **Preview line:** Applies current slider settings locally in real time, no save needed
- **Interactive curve editor:** Drag-to-edit control points; click to add, right-click to delete; presets
- **Profiles:** Managed via serial PROFILE commands (GUI calls them in background thread)
- **Save flow:** Sends `CAL <json>\n` over serial (VID=0x239A/PID=0xCAFE); firmware applies immediately and persists

## calibration.json format

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
