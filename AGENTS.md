# AGENTS.md — brake_controller

## Project

Sim racing pneumatic brake controller for Sim Sonn Pro pedal. RPi Pico (RP2040) + XDB401 pressure sensor + voltage divider, CircuitPython firmware, Tkinter calibration GUI.

## Commands

- Run calibration GUI: `python gui/calibrator.py`
- No test suite yet

## Architecture

- `firmware/boot.py` — Custom 16-bit USB HID gamepad descriptor (runs before code.py)
- `firmware/code.py` — Main Pico firmware: ADC read → oversample → clamp → normalize → curve → EMA → HID send (X=brake, Y=raw ADC for GUI)
- `gui/calibrator.py` — PC-side Tkinter calibration tool (pygame for HID input, writes calibration.json to CIRCUITPY)
- `calibration.json` — Device-specific, NOT in repo, lives on Pico only

## Key constraints

- boot.py only configures USB HID — does NOT call storage.remount(), CIRCUITPY stays USB-writable
- No adafruit_hid library — custom descriptor + raw report bytes
- HID report: 8 bytes (4 × uint16 LE axes). X=processed brake, Y=raw ADC for calibration GUI, Z/Rz=0.
- Pico ADC is 12-bit, CircuitPython maps to 0-65535. Oversampling default 16x for noise reduction.
- Sensor wiring: VCC→VSYS(5V), Signal→R1(10K)→GP26, R2(15K)→GND

## Setup (fresh Pico)

1. Flash CircuitPython .uf2 via BOOTSEL
2. Copy boot.py + code.py to CIRCUITPY root
3. Wire sensor
4. Pico appears as USB gamepad
5. Run gui/calibrator.py to calibrate
