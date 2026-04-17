# Brake Controller

Pneumatic brake mod for the Sim Sonn Pro brake pedal. Uses a Raspberry Pi Pico with an XDB401 pressure sensor, CircuitPython firmware, and a PC-side calibration GUI.

**Current release:** v0.1 (pre-release)

## Quick Start

### Hardware Setup

1. Assemble plumbing: cylinder → tee → sensor + Schrader valve (see [wiring diagram](docs/wiring-diagram.md))
2. Wire sensor to Pico: VCC → VSYS, GND → GND, Signal → 10KΩ → GP26, 15KΩ → GND
3. Mount cylinder to Sim Sonn Pro pedal using M10 hardware + preload spring
4. Pump air via Schrader valve to preload

### Firmware Setup

1. Hold BOOTSEL on Pico, plug into USB
2. Drag the CircuitPython `.uf2` file onto the RPI-RP2 drive
3. Copy `firmware/boot.py` and `firmware/code.py` to the CIRCUITPY drive root
4. Pico reboots and appears as a USB gamepad

### Calibration

1. Install GUI dependencies: `pip install -r gui/requirements.txt`
2. Run: `python gui/calibrator.py`
3. Select your Pico from the device dropdown (auto-detected if name contains "pico")
4. Use Auto Calibrate or manually set min/max
5. Tune curve, smoothing, deadzone — the green **Preview** line shows the effect instantly
6. Click "Save to Pico" → press RESET on Pico → the red Game Input line matches the Preview

### Testing Without a Sensor

- Jumper GP26 to GND for 0%, or to 3.3V for ~82% (due to voltage divider scaling)
- Touch GP26 with a finger for visible noise on the graph

### In-Game Configuration

- In your sim (ACC / iRacing / LMU), map the gamepad X-axis to brake
- The Pico appears as a standard USB gamepad

## Graph Lines

| Color | Line | Source |
|-------|------|--------|
| Blue | Raw ADC | Unprocessed sensor reading from Y-axis |
| Green | Preview | Current slider settings applied locally (real-time) |
| Red | Game Input | Actual Pico output (X-axis, after save + reboot) |

## Brake Curves

| Curve | Feel |
|-------|------|
| linear | 1:1 — pressure matches pedal position |
| progressive | Soft initial bite, ramps up (t²) |
| aggressive | Sharp initial bite, tapers off (√t) |

## Calibration Settings

| Setting | Default | Description |
|---------|---------|-------------|
| raw_min | 2000 | ADC value at zero pressure |
| raw_max | 56000 | ADC value at max pressure |
| deadzone | 300 | Ignore values below this threshold (prevents ghost presses) |
| curve | progressive | Brake response curve |
| smoothing | 0.3 | EMA smoothing (0 = none, 0.95 = max). Higher = more filtered but more lag |
| invert | false | Flip brake axis |
| oversample | 16 | ADC samples per reading (16 = ~14-bit effective resolution) |

## Building the GUI Executable

```bash
uv venv .venv
.venv\Scripts\pip install pygame-ce pyinstaller
.venv\Scripts\pyinstaller --onefile --windowed --name BrakeCalibrator --distpath dist gui/calibrator.py
```

Output: `dist/BrakeCalibrator.exe` (~18MB)

## Cost

~$72 AUD total. See [wiring diagram](docs/wiring-diagram.md) for full parts list.

## License

This project is provided as-is for personal use.

## References & Inspiration

- [LandoCode89/Jack-Pneumatic](https://github.com/LandoCode89/Jack-Pneumatic) — ESP32-S3 + ADS1220 24-bit ADC pneumatic brake controller (GPL v3)
- [u/Lando_Read89 — Reddit build guide](https://www.reddit.com/r/simracing/comments/1jb9z0s/pneumatic_brake_mod_for_sim_sonn_pro_pedal/) — Sim Sonn Pro pneumatic brake mod
- [u/GeneralLegacy — Reddit discussion](https://www.reddit.com/r/simracing/comments/1hxl5qj/diy_pneumatic_brake_mod/) — DIY pneumatic brake mod approach
