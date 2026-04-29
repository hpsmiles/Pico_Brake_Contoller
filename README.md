# Brake & Throttle Controller

Sim racing pneumatic brake + throttle controller for the Sim Sonn Pro pedal. Uses a Raspberry Pi Pico (RP2040) with an XDB401 pressure sensor (brake) and SS49E Hall Effect or HX711 load cell (throttle). C++/Arduino-Pico firmware with a Python/Tkinter calibration GUI.

## Quick Start

### Hardware Setup

1. Assemble plumbing: cylinder → tee → sensor + Schrader valve (see [wiring diagram](docs/wiring-diagram.md))
2. Wire brake sensor to Pico: VCC → VSYS, GND → GND, Signal → 2KΩ → GP26, 3.3KΩ → GND
3. (Optional) Wire throttle — SS49E Hall Effect on GP27 or HX711 load cell on GP16+GP28
4. Wire WS2812 RGB LED to GP23 for status indication
5. Mount cylinder to Sim Sonn Pro pedal using M10 hardware + preload spring
6. Pump air via Schrader valve to preload

### Firmware Setup

1. Hold BOOTSEL on Pico, plug into USB — RPI-RP2 drive appears
2. Build (requires [arduino-cli](https://arduino.github.io/arduino-cli/) with `rp2040:rp2040` core, ArduinoJson v7, Adafruit NeoPixel):
   ```
   arduino-cli compile \
     --fqbn rp2040:rp2040:rpipico \
     --board-options "flash=2097152_1048576,usbstack=tinyusb" \
     --build-path firmware_cpp/build \
     firmware_cpp
   ```
3. Copy `firmware_cpp/build/firmware_cpp.ino.uf2` to the RPI-RP2 drive
4. Pico reboots and appears as a **USB gamepad + COM port** (no USB drive)

See [docs/BUILD.md](docs/BUILD.md) for full build, flash, and serial reference including how to reboot into BOOTSEL without pressing the button.

**LED status:**

| Colour | Meaning |
|--------|---------|
| Orange | Booting |
| Green | Calibration loaded from flash |
| Red | Using factory defaults |
| 1 green blink | Hall Effect throttle detected |
| 2 green blinks | HX711 load cell detected |

### Calibration

**Pre-built exe:** `dist/BrakeCalibrator.exe` — no install required.

**Run from source:**
```bash
pip install -r gui/requirements.txt
python gui/calibrator.py
```

**Calibration flow:**

1. Select your Pico from the device dropdown (auto-detected if name contains "pico")
2. Use Auto Calibrate or manually click **Set Min** (pedal released) and **Set Max** (pedal pressed)
3. Tune curve, smoothing, deadzone, saturation, bite point — the green **Preview** line shows the effect instantly
4. Click **Save to Pico** — applies immediately via serial, no reboot needed

**Rebuilding the exe:**
```bash
.venv\Scripts\python.exe -m PyInstaller BrakeCalibrator.spec --distpath dist --workpath build_pyinstaller --noconfirm
```

### Testing Without a Sensor

- Jumper GP26 to GND for 0%, or to 3.3V for ~85% (due to voltage divider scaling)
- Touch GP26 with a finger for visible noise on the graph

### In-Game Configuration

In your sim (ACC / iRacing / LMU), map the gamepad **X-axis** to brake and **Z-axis** to throttle. The Pico appears as a standard USB gamepad.

## Graph Lines

| Colour | Line | Source |
|--------|------|--------|
| Blue | Raw Input | Unprocessed brake sensor reading (Y-axis) |
| Green | Calibrated Input | Current slider settings applied locally in real time |
| Red | Game Input | Actual Pico output (X-axis, after Save to Pico) |
| Orange | Throttle | Processed throttle output (Z-axis, when enabled) |

## Throttle

Two sensor options — only one is active at a time, both can be wired simultaneously. The firmware auto-detects which is connected on boot.

| Sensor | Wiring | How it works |
|--------|--------|--------------|
| **SS49E Hall Effect** | VCC → VSYS, GND → GND, Vout → 2KΩ → GP27, 3.3KΩ → GND | Analog voltage via ADC, same pipeline as brake |
| **HX711 Load Cell** | VIN → VSYS, GND → GND, SCK → GP28, DATA → GP16 | 24-bit ADC via bit-bang SPI, built into firmware |

- **Auto-detection:** On boot, firmware probes GP16 for an HX711 data-ready signal. If found → load cell. Otherwise → SS49E on GP27.
- Override with `"throttle_sensor": "hall"` or `"hx711"` in calibration (sent via GUI or `CAL` command).

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
| saturation | 1.0 | Scale effective max — lower = reach 100% with less force |
| bite_point | 0.0 | Dead-travel zone simulating pad-to-rotor gap |
| smoothing | 0.3 | EMA smoothing (0 = none, 0.95 = max). Higher = more filtered but more lag |
| invert | false | Flip brake axis |
| oversample | 16 | ADC samples per reading (16 = ~14-bit effective resolution) |

Throttle has identical settings prefixed with `throttle_` (e.g. `throttle_saturation`, `throttle_bite_point`).

## Serial Commands

The firmware exposes a serial interface at 115200 baud (DTR must be asserted). Commands are newline-terminated.

| Command | Response | Description |
|---------|----------|-------------|
| `CAL <json>` | `CAL OK` / `CAL ERR` | Apply calibration immediately and persist to flash |
| `REBOOT` | `OK` | Watchdog reboot into application |
| `REBOOT BOOTSEL` | `OK` | Reboot into USB bootloader (no button press needed) |
| `PROFILE SAVE <name>` | `PROFILE OK` / `PROFILE ERR` | Save current calibration as named profile |
| `PROFILE LOAD <name>` | `PROFILE OK` / `PROFILE ERR` | Load named profile and activate |
| `PROFILE DELETE <name>` | `PROFILE OK` / `PROFILE ERR` | Delete named profile |
| `PROFILE LIST` | `PROFILE <names>` / `PROFILE NONE` | List saved profiles |
| `STATUS` | Multi-line | Report FatFS mount state and write test result |

## Profiles

Save and load named calibration profiles stored in firmware flash under `/profiles/`. Useful for switching between cars or simulators — e.g. "GT3", "F1", "Rally". Managed via the GUI or directly via serial commands.

## Firmware Architecture

Dual-core RP2040:

| Core | Responsibility |
|------|---------------|
| **Core 0** | USB HID send + serial command processing |
| **Core 1** | ADC reading + signal processing |

Signal pipeline (per channel, runs on Core 1):

> ADC read → clamp → normalize → deadzone → bite point → curve → EMA smoothing → invert → HID output

| File | Purpose |
|------|---------|
| `firmware_cpp.ino` | Entry point, dual-core setup/loop |
| `config.h` | Pin defs, Calibration/ChannelCal structs, HID descriptor |
| `adc_reader.h/.cpp` | ADC init + oversampled read (12→16-bit scaling) |
| `hx711_driver.h/.cpp` | HX711 bit-bang driver with auto-probe |
| `signal_processing.h/.cpp` | Full signal pipeline |
| `flash_storage.h/.cpp` | FatFS init, calibration.json load/save, profile CRUD |
| `serial_commands.h/.cpp` | Serial command parser (CAL, REBOOT, PROFILE, STATUS) |
| `rgb_led.h/.cpp` | WS2812 RGB LED status indicators |

**Flash storage:** 1MB FatFS partition (`flash=2097152_1048576`), raw flash mode (`useFTL=false`). Not exposed as a USB drive — accessed only by the firmware via serial calibration commands.

## USB Device Identity

The firmware enumerates as:
- **VID:** `0x239A` (Adafruit / TinyUSB)
- **PID:** `0xCAFE`
- **Interfaces:** CDC serial (COM port) + HID gamepad (composite device)

## Cost

~$72 AUD total (brake only). See [wiring diagram](docs/wiring-diagram.md) for full parts list.

## License

This project is provided as-is for personal use.

## References & Inspiration

- [LandoCode89/Jack-Pneumatic](https://github.com/LandoCode89/Jack-Pneumatic) — ESP32-S3 + ADS1220 24-bit ADC pneumatic brake controller (GPL v3)
- [u/Lando_Read89 — Pneumatic mod for Sim Jack/SimSonn pedals](https://www.reddit.com/r/simracing/comments/1l1h13a/pneumatic_mod_for_sim_jacksimsonn_pedals/) — Reddit post that inspired this project
