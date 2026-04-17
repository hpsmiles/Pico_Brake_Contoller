# AGENTS.md — brake_controller

## Project

Sim racing pneumatic brake controller for Sim Sonn Pro pedal. RPi Pico (RP2040) + XDB401 pressure sensor + voltage divider, CircuitPython firmware, Tkinter calibration GUI.

**Current release:** v0.1 (pre-release)

## Commands

- Run calibration GUI: `python gui/calibrator.py`
- Build GUI exe: `.venv\Scripts\pyinstaller --onefile --windowed --name BrakeCalibrator --distpath dist gui/calibrator.py`
- No test suite yet

## Architecture

- `firmware/boot.py` — Custom 16-bit USB HID gamepad descriptor (runs before code.py)
- `firmware/code.py` — Main Pico firmware: ADC read → oversample → clamp → normalize → deadzone → curve → EMA → HID send (X=brake, Y=raw ADC for GUI)
- `gui/calibrator.py` — PC-side Tkinter calibration tool (pygame-ce for HID input, writes calibration.json to CIRCUITPY)
- `calibration.json` — Device-specific, NOT in repo, lives on Pico only

## Key constraints

- boot.py only configures USB HID — does NOT call storage.remount(), CIRCUITPY stays USB-writable
- No adafruit_hid library — custom descriptor + raw report bytes
- HID report: 8 bytes (4 × uint16 LE axes). X=processed brake, Y=raw ADC for calibration GUI, Z/Rz=0.
- Pico ADC is 12-bit, CircuitPython maps to 0-65535. Oversampling default 16x for noise reduction.
- Sensor wiring: VCC→VSYS(5V), Signal→R1(10K)→GP26, R2(15K)→GND
- Smoothing slider: 0 = none, 0.95 = max. Internally alpha = 1 - smoothing (higher slider = more filtering)
- CIRCUITPY is always USB-writable from PC — GUI writes calibration.json, firmware reads it on boot

## GUI features

- **3-line live graph:** Blue = Raw ADC, Green = Preview (slider settings applied locally), Red = Game Input (actual Pico output)
- **Device selector:** Dropdown lists all connected gamepads, auto-selects device with "pico" in name
- **Auto-calibration:** Single-window flow — 3s countdown → 5s capture (tracks min and max)
- **Preview line:** Applies current curve/smoothing/deadzone/min/max settings locally in real time, no save+reboot needed
- **Save flow:** Save to Pico writes calibration.json to CIRCUITPY drive, then press RESET on Pico

## Setup (fresh Pico)

1. Flash CircuitPython .uf2 via BOOTSEL
2. Copy boot.py + code.py to CIRCUITPY root
3. Wire sensor
4. Pico appears as USB gamepad
5. Run gui/calibrator.py to calibrate
