# AGENTS.md — brake_controller

## Project

Sim racing pneumatic brake + throttle controller for Sim Sonn Pro pedal. RPi Pico (RP2040) + XDB401 pressure sensor (brake) + SS49E Hall Effect or HX711 load cell (throttle), CircuitPython firmware, Tkinter calibration GUI.

**Current release:** v0.1 (pre-release)

## Commands

- Run calibration GUI: `python gui/calibrator.py`
- Build GUI exe: `.venv\Scripts\pyinstaller --onefile --windowed --name BrakeCalibrator --distpath dist gui/calibrator.py`
- No test suite yet

## Architecture

- `firmware/boot.py` — Custom 16-bit USB HID gamepad descriptor (runs before code.py)
- `firmware/code.py` — Main Pico firmware: ADC read → oversample → clamp (with saturation) → normalize → deadzone → bite point → curve → EMA → HID send (X=brake, Y=raw ADC for GUI, Z=processed throttle, Rz=raw throttle)
- `gui/calibrator.py` — PC-side Tkinter calibration tool (pygame-ce for HID input, writes calibration.json to CIRCUITPY)
- `calibration.json` — Device-specific, NOT in repo, lives on Pico only

## Key constraints

- boot.py only configures USB HID — does NOT call storage.remount(), CIRCUITPY stays USB-writable
- No adafruit_hid library — custom descriptor + raw report bytes
- HID report: 8 bytes (4 × uint16 LE axes). X=processed brake, Y=raw ADC for calibration GUI, Z=processed throttle, Rz=raw throttle ADC.
- Pico ADC is 12-bit, CircuitPython maps to 0-65535. Oversampling default 16x for noise reduction.
- Brake wiring: VCC→VSYS(5V), Signal→R1(2K)→GP26, R2(3.3K)→GND
- Throttle has two sensor options (one active at a time, set in calibration.json `throttle_sensor`):
  - **SS49E Hall Effect**: VCC→VSYS(5V), Vout→R3(2K)→GP27(ADC1), R4(3.3K)→GND (same voltage divider as brake)
  - **HX711 load cell**: HX711 VIN→VSYS(5V), GND→GND, SCK→GP28, DATA→GP16 (uses `adafruit_hx711` library)
- SS49E reads via analogio on GP27 (same code path as brake), HX711 reads via pseudo-SPI on GP16+GP28 (different code path)
- Both can be wired simultaneously — firmware auto-detects HX711 on boot (probes GP16 for data-ready signal), falls back to SS49E on GP27
- Smoothing slider: 0 = none, 0.95 = max. Internally alpha = 1 - smoothing (higher slider = more filtering)
- Saturation (0.1–1.0, default 1.0): Scales the effective raw_max, so you can reach 100% output without pressing as hard
- Bite point (0.0–0.5, default 0.0): Dead-travel zone simulating pad-to-rotor gap; applied after deadzone but before curve
- Curve types: linear, progressive (t^n), aggressive (t^(1/n)), custom (piecewise-linear through editable control points)
- Custom curves: `curve_points` list of `[input, output]` pairs; linear interpolation between points; editable in GUI via drag-to-edit
- Profiles: stored in `profiles/` directory on CIRCUITPY as full calibration.json copies; GUI saves/loads/deletes profiles
- CIRCUITPY is always USB-writable from PC — GUI writes calibration.json, firmware reads it on boot

## GUI features

- **3-line live graph:** Blue = Raw ADC, Green = Preview (slider settings applied locally), Red = Game Input (actual Pico output), Orange = Throttle (when enabled)
- **Device selector:** Dropdown lists all connected gamepads, auto-selects device with "pico" in name
- **Auto-calibration:** Single-window flow — 3s countdown → 5s capture (tracks min and max)
- **Preview line:** Applies current curve/smoothing/deadzone/min/max/saturation/bite-point settings locally in real time, no save+reboot needed
- **Interactive curve editor:** Drag-to-edit control points on graph; click to add points, right-click to delete; preset buttons (Linear, Progressive, Aggressive, S-curve)
- **Profiles:** Save, load, and delete named calibration profiles stored on CIRCUITPY
- **Save flow:** Save to Pico writes calibration.json to CIRCUITPY drive, then press RESET on Pico

## Setup (fresh Pico)

1. Flash CircuitPython .uf2 via BOOTSEL
2. Copy boot.py + code.py to CIRCUITPY root
3. Wire sensor
4. Pico appears as USB gamepad
5. Run gui/calibrator.py to calibrate

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
