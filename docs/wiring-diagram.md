# Brake Controller — Wiring Diagram

## Parts List

### Brake (required)

| Part | Spec | Ref |
|------|------|-----|
| SDA32x50 cylinder | G1/8 ports | $15.89 |
| XDB401 pressure sensor | 0–1 MPa, 0.5–4.5V | $23.39 |
| RPi Pico | RP2040 | owned |
| 10KΩ resistor | R1 (voltage divider, brake) | owned |
| 15KΩ resistor | R2 (voltage divider, brake) | owned |
| G1/8 BSP tee | Plumbing | $5.89 |
| 1/8 BSPT or NPT Schrader valve | Air fill | ~$4-10 |
| M10x60mm bolt | Mounting | $4.00 |
| M6x30mm bolt | Mounting | $5.08 |
| M10x28x1.2mm tee nuts | Mounting | $3.90 |
| M10 knurled thumb nut | On Sim Sonn pedal | owned |
| Preload spring | | owned |
| PTFE tape | Thread seal | ~$3 |
| 1/2" oversized flat washers | | ~$3 |

### Throttle — Option A: SS49E Hall Effect Sensor

| Part | Spec | Ref |
|------|------|-----|
| SS49E Hall Effect sensor | 3.0–6.5V, 0.86–4.21V out | ~$2-5 |
| 10KΩ resistor | R3 (voltage divider, throttle) | owned |
| 15KΩ resistor | R4 (voltage divider, throttle) | owned |

### Throttle — Option B: HX711 Load Cell

| Part | Spec | Ref |
|------|------|-----|
| HX711 breakout board | 24-bit ADC, pseudo-SPI | ~$3-8 |
| Load cell | 4-wire strain gauge, 1–5kg | ~$3-8 |
| Jumper wires | For HX711→Pico | owned |

## Plumbing

```
Cylinder Port 1 (sealed)
    │
    └── G1/8 → G1/8 BSP tee ──┬── XDB401 pressure sensor
                                │
                                └── Schrader valve (for air pump)

Cylinder Port 2 (atmosphere): leave open
```

- Seal all threads with PTFE tape
- Pump air via Schrader valve to preload pressure

## Electrical Wiring

```
XDB401 Sensor          RPi Pico
┌──────────┐          ┌──────────┐
│ VCC  ────┼──────────┤ VSYS (5V)│
│ GND  ────┼──────────┤ GND      │
│ Signal ──┼──┤       │          │
└──────────┘  │       │          │
              R1      │          │
              10KΩ    │          │
              │       │          │
              ├───────┤ GP26 (ADC0)│
              │       │          │
              R2      │          │
              15KΩ    │          │
              │       │          │
              ├───────┤ GND      │
              │       └──────────┘
             GND
```

## Voltage Divider Math (Brake)

- Ratio: R2 / (R1 + R2) = 15K / 25K = 0.6
- Sensor 0.5V output → 0.3V at GP26
- Sensor 4.5V output → 2.7V at GP26
- Pico ADC reference: 3.3V
- 2.7V = 82% of full scale (safe, within range)

---

## Throttle Wiring — Option A: SS49E Hall Effect Sensor

The SS49E outputs an analog voltage proportional to magnetic field strength.
At VCC=5V: quiescent output ≈ 2.5V, range ≈ 0.86V–4.21V.
Since output exceeds Pico's 3.3V ADC max, a voltage divider is required
(same ratio as the brake sensor).

```
SS49E Sensor            RPi Pico
┌──────────┐          ┌──────────┐
│ VCC  ────┼──────────┤ VSYS (5V)│
│ GND  ────┼──────────┤ GND      │
│ Vout ────┼──┤       │          │
└──────────┘  │       │          │
              R3      │          │
              10KΩ    │          │
              │       │          │
              ├───────┤ GP27 (ADC1)│
              │       │          │
              R4      │          │
              15KΩ    │          │
              │       │          │
              ├───────┤ GND      │
              │       └──────────┘
             GND
```

### Voltage Divider Math (Throttle — SS49E)

- Same ratio as brake: R4 / (R3 + R4) = 15K / 25K = 0.6
- SS49E min output ~0.86V → 0.52V at GP27
- SS49E quiescent ~2.5V → 1.5V at GP27 (no pedal = half range)
- SS49E max output ~4.21V → 2.53V at GP27
- 2.53V = 77% of 3.3V full scale (safe)
- Note: SS49E can also run at 3.3V directly on Pico 3V3(OUT) — output
  range would be ~0.6V–2.6V, no divider needed, but full-scale range
  is smaller. VSYS+divider gives better resolution.

---

## Throttle Wiring — Option B: HX711 Load Cell

The HX711 is a 24-bit ADC with a pseudo-SPI serial interface (2 GPIO pins).
It reads a 4-wire strain gauge (load cell) directly — no voltage divider needed.
Uses the `adafruit_hx711` CircuitPython library.

```
Load Cell          HX711 Breakout       RPi Pico
(4-wire)           (Adafruit-ish)
┌──────────┐      ┌──────────┐        ┌──────────┐
│ Red  (E+) ┼──────┤ E+       │        │          │
│ Blk  (E-) ┼──────┤ E-       │        │          │
│ Grn  (A+) ┼──────┤ A+       │        │          │
│ Wht  (A-) ┼──────┤ A-       │        │          │
└──────────┘      │          │        │          │
                  │ VIN  ────┼────────┤ VSYS (5V)│
                  │ GND  ────┼────────┤ GND      │
                  │ SCK  ────┼────────┤ GP28     │
                  │ DATA ────┼────────┤ GP16     │
                  └──────────┘        └──────────┘
```

### HX711 Notes

- Power HX711 from VSYS (5V) — the breakout has on-board level shifting
- Data rate: 10 SPS (RATE pin low) or 80 SPS (RATE pin high)
- Channel A gain: 128 (default), 64, or 32. Channel B gain: 32.
- Calibration is done via `tare()` (zero) and `determine_scalar()` (scale factor)
- The HX711 reads raw 24-bit values; firmware normalizes to 0–65535 for HID
- **When using HX711, GP27 is used for DATA (digital), not ADC1**
- Install library: copy `adafruit_hx711/` folder to CIRCUITPY/lib/

---

## Throttle Sensor Selection

Both throttle sensors can be wired simultaneously. The firmware configuration
in `calibration.json` determines which is active:

```json
{
  "throttle_sensor": "auto"   // "auto" = auto-detect on boot
                              // "hall" = force SS49E on GP27 ADC1
                              // "load_cell" = force HX711 on GP16+GP28
}
```

- **SS49E** reads via `analogio.AnalogIn(board.GP27)` — same code path as brake
- **HX711** reads via `adafruit_hx711` on GP16 (DATA) + GP28 (SCK)
- Both can be wired simultaneously — firmware auto-detects which is present
- **Auto-detection:** On boot, probe HX711 on GP16+GP28 (DATA goes LOW when data ready).
  If HX711 responds → use load cell. If no response → use SS49E on GP27 ADC1.
  The `throttle_sensor` key in calibration.json can override auto-detection if set.

---

## Pico Pin Reference

| Pin | Function |
|-----|----------|
| VSYS | 5V output (powered via USB) |
| 3V3(OUT) | 3.3V output (alternative power for SS49E) |
| GND | Ground |
| GP26 | ADC0 — brake sensor input (XDB401 via voltage divider) |
| GP16 | HX711 DATA (digital input — load cell only) |
| GP27 | ADC1 — throttle analog input (SS49E via voltage divider) |
| GP28 | HX711 SCK (digital output — load cell only) |
| LED | Onboard LED (status indicator) |
