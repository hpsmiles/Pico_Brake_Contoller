# Brake Controller — Wiring Diagram

## Parts List

| Part | Spec | Ref |
|------|------|-----|
| SDA32x50 cylinder | G1/8 ports | $15.89 |
| XDB401 pressure sensor | 0–1 MPa, 0.5–4.5V | $23.39 |
| RPi Pico | RP2040 | owned |
| 10KΩ resistor | R1 (voltage divider) | owned |
| 15KΩ resistor | R2 (voltage divider) | owned |
| G1/8 BSP tee | Plumbing | $5.89 |
| 1/8 BSPT or NPT Schrader valve | Air fill | ~$4-10 |
| M10x60mm bolt | Mounting | $4.00 |
| M6x30mm bolt | Mounting | $5.08 |
| M10x28x1.2mm tee nuts | Mounting | $3.90 |
| M10 knurled thumb nut | On Sim Sonn pedal | owned |
| Preload spring | | owned |
| PTFE tape | Thread seal | ~$3 |
| 1/2" oversized flat washers | | ~$3 |

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

## Voltage Divider Math

- Ratio: R2 / (R1 + R2) = 15K / 25K = 0.6
- Sensor 0.5V output → 0.3V at GP26
- Sensor 4.5V output → 2.7V at GP26
- Pico ADC reference: 3.3V
- 2.7V = 82% of full scale (safe, within range)

## Pico Pin Reference

| Pin | Function |
|-----|----------|
| VSYS | 5V output (powered via USB) |
| GND | Ground |
| GP26 | ADC0 — brake sensor input |
| LED | Onboard LED (status indicator) |
