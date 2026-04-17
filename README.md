# Brake Controller

Pneumatic brake mod for the Sim Sonn Pro brake pedal. Uses a Raspberry Pi Pico with an XDB401 pressure sensor, CircuitPython firmware, and a PC-side calibration GUI.

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
3. Use Auto Calibrate (3 cycles) or manually set min/max
4. Click "Save to Pico" → press RESET on Pico

### In-Game Configuration

- In your sim (ACC / iRacing / LMU), map the gamepad X-axis to brake
- The Pico appears as a standard USB gamepad

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
| deadzone | 300 | Ignore values below this threshold |
| curve | progressive | Brake response curve |
| smoothing | 0.3 | EMA alpha (0=no smoothing, 1=no filtering) |
| invert | false | Flip brake axis |
| oversample | 16 | ADC samples per reading (16 = ~14-bit effective) |

## Cost

~$72 AUD total. See [wiring diagram](docs/wiring-diagram.md) for full parts list.

## License

This project is provided as-is for personal use.
