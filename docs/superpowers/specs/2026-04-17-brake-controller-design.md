# Brake Controller — Design Spec

> Sim racing pneumatic brake mod for Sim Sonn Pro pedal using RPi Pico + pressure sensor.

## Overview

A single-brake-axis pneumatic brake controller. An SDA32x50 hydraulic cylinder is mounted on the Sim Sonn Pro brake pedal, pressurised via a Schrader valve, and read by an XDB401 pressure sensor. An RPi Pico (RP2040) running CircuitPython reads the sensor through a voltage divider, applies calibration and brake curves, and presents the result as a USB HID gamepad axis. A PC-side Tkinter GUI handles calibration.

## 1. Hardware & Wiring

### Components

| Part | Spec | Cost |
|------|------|------|
| SDA32x50 cylinder | G1/8 ports | $15.89 |
| XDB401 pressure sensor | 0–1 MPa, 0.5–4.5V output | $23.39 |
| RPi Pico | RP2040 | owned |
| 10KΩ + 15KΩ resistors | Voltage divider | owned |
| G1/8 BSP tee | Plumbing | $5.89 |
| 1/8 BSPT Schrader valve | Air fill | $9.99 |
| 1/8 NPT Schrader valve | ( cheaper alternative ) | $3.73 |
| M10x60mm bolt | Mounting | $4.00 |
| M6x30mm bolt | Mounting | $5.08 |
| M10x28x1.2mm tee nuts | Mounting | $3.90 |
| M10 knurled thumb nut | On Sim Sonn pedal | owned |
| Preload spring | | owned |
| PTFE tape | Bunnings | ~$3 |
| 1/2" oversized flat washers | Bunnings | ~$3 |

**Total: ~$72 AUD**

### Plumbing

- **Port 1 (sealed):** Cylinder G1/8 → G1/8 tee → sensor + Schrader valve (for pump)
- **Port 2 (atmosphere):** leave open

### Wiring

| Connection | Wire |
|---|---|
| Sensor VCC | Pico VSYS (5V) |
| Sensor GND | Pico GND |
| Sensor Signal | → R1 (10KΩ) → junction |
| Junction | → Pico GP26 (ADC) AND → R2 (15KΩ) → GND |

**Voltage divider math:**
- Divider ratio: R2/(R1+R2) = 15K/25K = 0.6
- Sensor 0.5V → 0.3V at GP26
- Sensor 4.5V → 2.7V at GP26
- Pico ADC range: 0–3.3V — 2.7V is 82% of full scale (safe)

**ADC resolution:** Pico has 12-bit ADC (0–4095), CircuitPython maps to 0–65535. In the 0.3V–2.7V active window this gives ~2,978 distinct values — adequate for sim racing. Oversampling (see firmware) provides noise reduction and nominal resolution improvement.

## 2. Firmware

### Files on Pico (CIRCUITPY drive)

| File | Purpose |
|------|---------|
| `boot.py` | Configures custom 16-bit USB HID gamepad descriptor |
| `code.py` | Main firmware, auto-runs on boot |
| `calibration.json` | Calibration values (written by GUI, read by firmware) |

**`boot.py` is required.** The default `adafruit_hid.gamepad.Gamepad` uses 8-bit signed axes (-127 to 127), giving only 128 distinct brake levels — insufficient for trail-braking precision. A custom HID descriptor in `boot.py` enables 16-bit axes (0–65535). `boot.py` only calls `usb_hid.enable()` — it does NOT call `storage.remount()`, so CIRCUITPY stays fully USB-writable from the host PC.

**No `adafruit_hid` library needed.** With a custom descriptor, `code.py` sends raw report bytes directly via `device.send_report()`. The `Gamepad` helper class is not used.

### Processing pipeline (per loop iteration)

```
1. Oversample ADC (N reads, default 16x) → average
2. Clamp to [raw_min, raw_max]
3. Normalize to 0.0–1.0
4. Apply deadzone (values below threshold → 0.0)
5. Apply brake curve (linear | progressive | aggressive | custom)
6. EMA smoothing (alpha from calibration)
7. Map to 0–65535 and send as HID gamepad X-axis (16-bit, via raw report)
8. Sleep to target ~500Hz loop rate
```

### Oversampling

- Configurable `oversample` count in calibration.json (default: 16)
- Every 4x oversampling gains ~1 effective bit: 16x → ~14-bit effective
- Primary benefit is noise reduction; resolution bump is secondary
- 500Hz loop rate with 16x oversampling = 8000 ADC reads/sec, well within Pico's ~500kHz ADC capability

### Brake curves

| Curve | Formula | Character |
|-------|---------|-----------|
| linear | t | Direct 1:1 mapping |
| progressive | t² | Initial response is soft, ramps up |
| aggressive | √t | Sharp initial bite, tapers |
| custom | Linear interpolation through user-defined points | Future |

### Calibration JSON structure

```json
{
  "raw_min": 2000,
  "raw_max": 56000,
  "deadzone": 300,
  "curve": "progressive",
  "smoothing": 0.3,
  "invert": false,
  "oversample": 16
}
```

### USB HID report format

Custom gamepad descriptor with 4 axes (X, Y, Z, Rz), each 16-bit unsigned (0–65535). Report layout:

| Byte(s) | Content |
|---------|---------|
| 0–1 | X axis (processed brake) — little-endian uint16 |
| 2–3 | Y axis (raw oversampled ADC) — little-endian uint16 |
| 4–5 | Z axis — little-endian uint16 (unused, 0) |
| 6–7 | Rz axis — little-endian uint16 (unused, 0) |

**Two axes are used:**
- **X axis** = processed brake value (after calibration, curve, smoothing). This is what the sim reads.
- **Y axis** = raw oversampled ADC value (before any processing). This is what the GUI reads for calibration — it needs the raw value to set `raw_min`/`raw_max` accurately.

Z and Rz report zero. Four axes are declared because some sims/handlers expect a minimum number of axes to recognise the device as a gamepad.

Firmware sends reports via `gamepad_device.send_report(report_bytes)` — no `adafruit_hid` helper class.

### Error handling

- Missing or corrupt `calibration.json` → use hardcoded defaults, blink LED rapidly as warning
- ADC reads returning 0 or 65535 (rail-hitting) → clamp and continue
- USB HID send failure → retry next loop, don't crash

## 3. Calibration GUI

**Platform:** Python 3 + Tkinter (cross-platform, runs on PC)

### Dependencies

- `tkinter` (stdlib)
- `pygame` (`pip install pygame` — for reading Pico HID gamepad input)

### GUI layout

- **Left panel:** Live pressure bar/plot (raw ADC value over time)
- **Right panel:** Controls
  - Raw value display (current ADC reading)
  - Normalized value display (0–100%)
  - **Manual calibration:** Set Min / Set Max buttons, editable min/max number inputs
  - **Auto Calibrate button** → walks through 3-cycle capture flow (see below)
  - Curve selector dropdown (linear / progressive / aggressive / custom)
  - Smoothing slider (0.0–1.0)
  - Deadzone slider (0–1000)
  - Oversample selector (1 / 4 / 16 / 64)
  - Invert checkbox
  - Save button → writes calibration.json to CIRCUITPY drive
  - Status bar (drive found/not found, save success/fail)

### Auto-Calibrate flow

1. User presses "Auto Calibrate" button
2. GUI prompts: "Release brake completely, then press Start"
3. User presses Start → GUI captures min over 2 seconds (minimum raw ADC value seen in window)
4. GUI prompts: "Now press brake to MAX pressure, then release"
5. User presses → GUI captures max over 4 seconds (maximum raw ADC value seen in window)
6. GUI prompts: "Release and repeat" — steps 3–5 repeat **3 times total**
7. Final min/max = **average** of the 3 captures
8. Values populate the editable min/max fields — user can manually tweak before saving

### Drive detection

- Windows: Scan drive letters for volume label `CIRCUITPY`
- Linux: Scan `/media/*/CIRCUITPY`
- macOS: Scan `/Volumes/CIRCUITPY`

### Save flow

1. GUI writes `calibration.json` to detected CIRCUITPY drive
2. GUI prompts: "Calibration saved. Press RESET on Pico or reconnect."
3. Pico reboots, reads new calibration, applies immediately

## 4. Project Structure

```
brake_controller/
├── firmware/
│   ├── boot.py              # Custom 16-bit HID gamepad descriptor
│   └── code.py              # Main Pico firmware
├── gui/
│   ├── calibrator.py        # Main GUI application
│   └── requirements.txt     # pygame dependency
├── docs/
│   └── wiring-diagram.md    # Pin/parts reference
├── AGENTS.md
└── README.md
```

- `firmware/` — files that live on the Pico. User copies to CIRCUITPY drive.
- `gui/` — PC-side calibration tool. `python calibrator.py`
- `calibration.json` — NOT in repo. Device-specific, lives on Pico only.
- No `adafruit_hid` library needed — custom HID descriptor + raw report bytes replace it.

### `.gitignore`

- `calibration.json`

## 5. Setup Flow (Fresh Pico)

1. Flash CircuitPython `.uf2` via BOOTSEL
2. Copy `firmware/boot.py` and `firmware/code.py` to CIRCUITPY root
3. Wire sensor per wiring diagram
4. Pico auto-runs code.py, appears as USB gamepad with 16-bit axes
5. Run `gui/calibrator.py` to calibrate
6. Configure in sim (ACC / iRacing / LMU)

## 6. Future Features (V2+)

- Dual axis (clutch/gas on GP27)
- Button inputs
- BLE wireless (Pico W)
- OLED display
- Hot-reload calibration via USB serial
- Pressure alarm
- Custom curve with user-defined interpolation points
- `curves.json` for persistent custom curve definitions
