# Brake & Throttle Controller

Pneumatic brake + throttle controller for the Sim Sonn Pro pedal. Uses a Raspberry Pi Pico with an XDB401 pressure sensor (brake) and SS49E Hall Effect or HX711 load cell (throttle), CircuitPython firmware, and a PC-side calibration GUI.

**Current release:** v0.1 (pre-release)

## Quick Start

### Hardware Setup

1. Assemble plumbing: cylinder → tee → sensor + Schrader valve (see [wiring diagram](docs/wiring-diagram.md))
2. Wire brake sensor to Pico: VCC → VSYS, GND → GND, Signal → 2KΩ → GP26, 3.3KΩ → GND
3. (Optional) Wire throttle sensor — SS49E Hall Effect on GP27 or HX711 load cell on GP16+GP28
4. Mount cylinder to Sim Sonn Pro pedal using M10 hardware + preload spring
5. Pump air via Schrader valve to preload

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
5. Tune curve, smoothing, deadzone, saturation, bite point — the green **Preview** line shows the effect instantly
6. Click "Save to Pico" → press RESET on Pico → the red Game Input line matches the Preview

### Testing Without a Sensor

- Jumper GP26 to GND for 0%, or to 3.3V for ~85% (due to voltage divider scaling)
- Touch GP26 with a finger for visible noise on the graph

### In-Game Configuration

- In your sim (ACC / iRacing / LMU), map the gamepad X-axis to brake, Z-axis to throttle
- The Pico appears as a standard USB gamepad

## Graph Lines

| Color | Line | Source |
|-------|------|--------|
| Blue | Raw ADC | Unprocessed brake sensor reading from Y-axis |
| Green | Preview | Current slider settings applied locally (real-time) |
| Red | Game Input | Actual Pico output (X-axis, after save + reboot) |
| Orange | Throttle | Processed throttle output (Z-axis, when enabled) |

## Throttle

Two sensor options — only one is active at a time, but both can be wired simultaneously. The firmware auto-detects which is connected on boot.

| Sensor | Wiring | How it works |
|--------|--------|--------------|
| **SS49E Hall Effect** | VCC → VSYS, GND → GND, Vout → 2KΩ → GP27, 3.3KΩ → GND | Analog voltage via ADC, same code path as brake |
| **HX711 Load Cell** | VIN → VSYS, GND → GND, SCK → GP28, DATA → GP16 | 24-bit ADC via pseudo-SPI (requires `adafruit_hx711` library) |

- **Auto-detection:** On boot, firmware probes GP16 for an HX711 data-ready signal. If found → load cell. Otherwise → SS49E on GP27.
- Override with `"throttle_sensor": "hall"` or `"load_cell"` in `calibration.json` (default: `"auto"`).
- Throttle axis appears as the gamepad Z-axis in-game. Raw throttle ADC is sent on Rz for the calibration GUI.

## Brake Curves

| Curve | Feel |
|-------|------|
| linear | 1:1 — pressure matches pedal force |
| progressive | Soft initial bite, ramps up (t^n) — good for trail braking |
| aggressive | Sharp initial bite, tapers off (t^1/n) |
| custom | Drag-to-edit control points — create S-curves and arbitrary shapes |

## Calibration Settings

| Setting | Default | Description |
|---------|---------|-------------|
| raw_min | 2000 | ADC value at zero pressure |
| raw_max | 56000 | ADC value at max pressure |
| deadzone | 300 | Ignore values below this threshold (prevents ghost presses) |
| curve | linear | Brake response curve type |
| saturation | 1.0 | Scale effective max — lower values = reach 100% with less force |
| bite point | 0.0 | Dead-travel zone simulating pad-to-rotor gap |
| smoothing | 0.3 | EMA smoothing (0 = none, 0.95 = max). Higher = more filtered but more lag |
| invert | false | Flip brake axis |
| oversample | 16 | ADC samples per reading (16 = ~14-bit effective resolution) |

Throttle has identical settings prefixed with `throttle_` (e.g. `throttle_saturation`, `throttle_bite_point`).

## Profiles

Save and load named calibration profiles stored on the CIRCUITPY drive under `profiles/`. Useful for switching between different cars or simulators — e.g. "GT3", "F1", "Rally".

## Building the GUI Executable

```bash
uv venv .venv
.venv\Scripts\pip install pygame-ce pyinstaller
.venv\Scripts\pyinstaller --onefile --windowed --name BrakeCalibrator --distpath dist gui/calibrator.py
```

Output: `dist/BrakeCalibrator.exe` (~18MB)

## Cost

~$72 AUD total (brake only). See [wiring diagram](docs/wiring-diagram.md) for full parts list.

## License

This project is provided as-is for personal use.

## References & Inspiration

- [LandoCode89/Jack-Pneumatic](https://github.com/LandoCode89/Jack-Pneumatic) — ESP32-S3 + ADS1220 24-bit ADC pneumatic brake controller (GPL v3)
- [u/Lando_Read89 — Pneumatic mod for Sim Jack/SimSonn pedals](https://www.reddit.com/r/simracing/comments/1l1h13a/pneumatic_mod_for_sim_jacksimsonn_pedals/) — Reddit post that inspired this project
