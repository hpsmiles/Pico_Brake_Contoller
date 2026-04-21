# Brake & Throttle Controller

Pneumatic brake + throttle controller for the Sim Sonn Pro pedal. Uses a Raspberry Pi Pico with an XDB401 pressure sensor (brake) and SS49E Hall Effect or HX711 load cell (throttle). Two firmware options: CircuitPython (easy prototyping) or C++/Arduino-Pico (deterministic 1kHz HID), plus a PC-side calibration GUI.

**Current release:** v0.2

## Quick Start

### Hardware Setup

1. Assemble plumbing: cylinder → tee → sensor + Schrader valve (see [wiring diagram](docs/wiring-diagram.md))
2. Wire brake sensor to Pico: VCC → VSYS, GND → GND, Signal → 2KΩ → GP26, 3.3KΩ → GND
3. (Optional) Wire throttle sensor — SS49E Hall Effect on GP27 or HX711 load cell on GP16+GP28
4. Mount cylinder to Sim Sonn Pro pedal using M10 hardware + preload spring
5. Pump air via Schrader valve to preload

### Firmware Setup

**Option A: CircuitPython** (easy prototyping, live editing)

1. Hold BOOTSEL on Pico, plug into USB
2. Drag the CircuitPython `.uf2` file onto the RPI-RP2 drive
3. Copy `firmware/boot.py` and `firmware/code.py` to the CIRCUITPY drive root
4. Pico reboots and appears as a USB gamepad

**Option B: C++ / Arduino-Pico** (production, deterministic 1kHz HID, dual-core)

1. Hold BOOTSEL on Pico, plug into USB
2. Build (requires [arduino-cli](https://arduino.github.io/arduino-cli/) with rp2040:rp2040 core + ArduinoJson v7):
   ```
   arduino-cli compile --fqbn rp2040:rp2040:rpipico --board-options "flash=2097152_65536" --build-path firmware_cpp/build firmware_cpp
   ```
3. Copy `firmware_cpp/build/firmware_cpp.ino.uf2` to the RPI-RP2 drive
4. Pico reboots as a USB gamepad + a small drive (via LittleFS + SingleFileDrive)

**Note:** C++ firmware appears as "PIcoBrake" drive, CircuitPython appears as "CIRCUITPY" — the GUI auto-detects both. C++ firmware does not support auto-reset via hidapi (press RESET on Pico after saving), CircuitPython does.

### Calibration

**Python GUI** (original Tkinter-based):

1. Install GUI dependencies: `pip install -r gui/requirements.txt`
2. Run: `python gui/calibrator.py`

**C# GUI** (new WPF-based, dark theme, modern charts):

1. Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Run the published exe from `gui_cs/bin/Release/BrakeCalibrator.exe`, or build from source:
   ```
   dotnet run --project gui_cs
   ```

**Shared calibration flow** (both GUIs):

1. Select your Pico from the device dropdown (auto-detected if name contains "pico")
2. Use Auto Calibrate or manually set min/max
3. Tune curve, smoothing, deadzone, saturation, bite point — the green **Preview** line shows the effect instantly
4. Click "Save to Pico" — CircuitPython firmware auto-resets (requires hidapi), C++ firmware requires pressing RESET on Pico

### Testing Without a Sensor

- Jumper GP26 to GND for 0%, or to 3.3V for ~85% (due to voltage divider scaling)
- Touch GP26 with a finger for visible noise on the graph

### In-Game Configuration

- In your sim (ACC / iRacing / LMU), map the gamepad X-axis to brake, Z-axis to throttle
- The Pico appears as a standard USB gamepad

## Graph Lines

| Color | Line | Source |
|-------|------|--------|
| Blue | Raw Input | Unprocessed brake sensor reading from Y-axis |
| Green | Calibrated Input | Current slider settings applied locally (real-time) |
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

Save and load named calibration profiles stored on the Pico drive under `profiles/`. Useful for switching between different cars or simulators — e.g. "GT3", "F1", "Rally".

## Building the GUI Executable

**Python GUI:**

```bash
pip install pygame-ce hidapi pyinstaller
pyinstaller --onefile --windowed --name BrakeCalibrator --distpath dist gui/calibrator.py
```

Output: `dist/BrakeCalibrator.exe` (~20-150MB depending on Python environment)

**C# GUI:**

```bash
# Framework-dependent (requires .NET 8 Desktop Runtime on target, ~19MB exe)
dotnet publish gui_cs -c Release -r win-x64

# Self-contained (no runtime needed, ~173MB exe)
dotnet publish gui_cs -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `gui_cs/bin/Release/net8.0-windows/win-x64/publish/BrakeCalibrator.exe`

## C++ Firmware Architecture

The C++ firmware (`firmware_cpp/`) uses Arduino-Pico with dual-core:

| Core | Responsibility |
|------|---------------|
| **Core 0** | USB HID — sends 8-byte gamepad reports at 1kHz poll rate |
| **Core 1** | ADC reading + signal processing (oversample → clamp → normalize → deadzone → bite → curve → EMA) |

| File | Purpose |
|------|---------|
| `firmware_cpp.ino` | Entry point, dual-core main loop |
| `config.h` | Pin defs, Calibration/ChannelCal structs, HID descriptor |
| `adc_reader.h/.cpp` | ADC init + oversampled read (12→16-bit scaling) |
| `hx711_driver.h/.cpp` | HX711 bit-bang driver with auto-probe |
| `signal_processing.h/.cpp` | Full signal pipeline (clamp → EMA → invert) |
| `calibration.h/.cpp` | JSON config loading via ArduinoJson v7 + LittleFS |
| `msc_disk.h/.cpp` | LittleFS + SingleFileDrive (HID + MSC composite USB) |

**Key differences from CircuitPython:**
- Deterministic 1kHz HID — no garbage collection pauses, no interpreter overhead
- Dual-core: ADC processing runs independently from USB
- LittleFS + SingleFileDrive replaces CIRCUITPY — the Pico appears as both a gamepad and a small USB drive
- No auto-reset via hidapi (arduino-pico USB stack doesn't support HID Output Reports)
- Build requires `--board-options "flash=2097152_65536"` for filesystem partition

## C# GUI Architecture

The C# GUI (`gui_cs/`) is a WPF application with LiveCharts2 charts, dark theme, and MVVM architecture:

| File | Purpose |
|------|---------|
| `Models/CalibrationData.cs` | ChannelCal, CalibrationData models, curve presets |
| `Services/HidReader.cs` | SharpDX DirectInput joystick reader (~30Hz poll) |
| `Services/SignalProcessing.cs` | Local preview pipeline (mirrors firmware math exactly) |
| `Services/HidReset.cs` | hidapi Output Report for auto-reset (CircuitPython only) |
| `Services/CircuitPyDrive.cs` | Cross-platform CIRCUITPY/PIcoBrake drive detection |
| `Services/CalibrationFileService.cs` | JSON load/save + profile CRUD |
| `ViewModels/MainViewModel.cs` | MVVM ViewModel with CommunityToolkit.Mvvm |
| `Views/CurveEditorWindow.xaml/.cs` | Interactive drag-to-edit curve editor |
| `Resources/DarkTheme.xaml` | GitHub-dark inspired theme with custom controls |

**Key differences from Python GUI:**
- Dark themed with LiveCharts2 (SkiaSharp) for smooth animated charts
- MVVM architecture (CommunityToolkit.Mvvm) instead of Tkinter procedural
- Same calibration.json format — both GUIs are interchangeable
- Interactive curve editor with draggable control points directly on the chart

## Cost

~$72 AUD total (brake only). See [wiring diagram](docs/wiring-diagram.md) for full parts list.

## License

This project is provided as-is for personal use.

## References & Inspiration

- [LandoCode89/Jack-Pneumatic](https://github.com/LandoCode89/Jack-Pneumatic) — ESP32-S3 + ADS1220 24-bit ADC pneumatic brake controller (GPL v3)
- [u/Lando_Read89 — Pneumatic mod for Sim Jack/SimSonn pedals](https://www.reddit.com/r/simracing/comments/1l1h13a/pneumatic_mod_for_sim_jacksimsonn_pedals/) — Reddit post that inspired this project
