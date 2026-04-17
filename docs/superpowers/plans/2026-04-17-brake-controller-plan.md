# Brake Controller Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a single-brake-axis pneumatic brake controller for sim racing using RPi Pico + XDB401 pressure sensor + Tkinter calibration GUI.

**Architecture:** Firmware on Pico runs CircuitPython, reads pressure sensor via ADC with oversampling, applies calibration/curves/smoothing, and sends 16-bit brake values over custom USB HID gamepad descriptor. PC-side Tkinter GUI reads live HID data via pygame, provides manual and auto-calibration, and writes calibration.json to the Pico's CIRCUITPY USB drive.

**Tech Stack:** CircuitPython (RP2040), Python 3 + Tkinter + pygame (PC GUI), custom USB HID descriptor (16-bit axes)

---

### Task 1: Project scaffolding and gitignore

**Files:**
- Create: `.gitignore`
- Create: `firmware/` directory
- Create: `gui/` directory
- Create: `docs/` directory
- Modify: `AGENTS.md` — update with project details

- [ ] **Step 1: Create directory structure**

```bash
mkdir firmware gui docs
```

- [ ] **Step 2: Create .gitignore**

```gitignore
calibration.json
__pycache__/
*.pyc
```

- [ ] **Step 3: Update AGENTS.md with discovered project context**

Replace the stub AGENTS.md content with:

```markdown
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
```

- [ ] **Step 4: Commit**

```bash
git add .gitignore firmware/ gui/ docs/ AGENTS.md
git commit -m "chore: scaffold project structure"
```

---

### Task 2: boot.py — Custom 16-bit HID gamepad descriptor

**Files:**
- Create: `firmware/boot.py`

This file runs once before code.py on Pico boot. It configures a custom USB HID gamepad with 4 × 16-bit axes instead of the default 8-bit axes from adafruit_hid.

- [ ] **Step 1: Write boot.py with custom HID descriptor**

```python
# firmware/boot.py
"""Configure a custom USB HID gamepad with 16-bit axes.

The default adafruit_hid Gamepad uses 8-bit signed axes (-127 to 127),
which only gives 128 distinct brake values — insufficient for trail-braking.

This descriptor declares 4 axes (X, Y, Z, Rz) at 16-bit resolution (0-65535).
Report layout: 8 bytes total — 4 × uint16 little-endian.
Only X axis is used for the brake; others report zero.
"""

import usb_hid

# HID Report Descriptor for a gamepad with 4 axes, 16-bit, no buttons
# Based on USB HID Usage Tables (HUT) for Generic Desktop (0x01) / Gamepad (0x05)
GAMEPAD_DESCRIPTOR = bytes((
    0x05, 0x01,       # Usage Page (Generic Desktop)
    0x09, 0x05,       # Usage (Game Pad)
    0xA1, 0x01,       # Collection (Application)
    0x09, 0x01,       #   Usage (Pointer)
    0xA1, 0x00,       #   Collection (Physical)

    # X axis (brake) — 16-bit
    0x09, 0x30,       #     Usage (X)
    0x09, 0x31,       #     Usage (Y)
    0x09, 0x32,       #     Usage (Z)
    0x09, 0x35,       #     Usage (Rz)

    0x16, 0x00, 0x00, #     Logical Minimum (0)
    0x26, 0xFF, 0xFF, #     Logical Maximum (65535)
    0x75, 0x10,       #     Report Size (16 bits)
    0x95, 0x04,       #     Report Count (4 axes)
    0x81, 0x02,       #     Input (Data, Variable, Absolute)

    0xC0,             #   End Collection (Physical)
    0xC0,             # End Collection (Application)
))

gamepad_device = usb_hid.Device(
    report_descriptor=GAMEPAD_DESCRIPTOR,
    usage_page=0x01,           # Generic Desktop
    usage=0x05,                # Game Pad
    report_ids=(0,),           # Single report, no ID
    in_report_lengths=(8,),    # 4 axes × 2 bytes = 8 bytes
    out_report_lengths=(0,),   # No output reports
)

usb_hid.enable((gamepad_device,))
```

- [ ] **Step 2: Commit**

```bash
git add firmware/boot.py
git commit -m "feat: add boot.py with custom 16-bit HID gamepad descriptor"
```

---

### Task 3: code.py — Firmware core (ADC, oversampling, calibration loading)

**Files:**
- Create: `firmware/code.py`

This is the main firmware that runs on Pico boot. It loads calibration, reads the ADC with oversampling, and sends HID reports.

- [ ] **Step 1: Write code.py**

```python
# firmware/code.py
"""Brake controller firmware for RPi Pico (RP2040).

Reads XDB401 pressure sensor via ADC with configurable oversampling,
applies calibration/curves/smoothing, and sends 16-bit brake value
as USB HID gamepad X-axis. Also sends raw ADC value on Y-axis
for the calibration GUI to read.

Hardware:
  Sensor VCC  → Pico VSYS (5V)
  Sensor GND  → Pico GND
  Sensor Signal → R1 (10KΩ) → GP26 (ADC0)
                                   ↓
                               R2 (15KΩ) → GND
"""

import analogio
import board
import json
import struct
import time
import usb_hid
import digitalio

# --- Pin Configuration ---
ADC_PIN = board.GP26
LED_PIN = board.LED

# --- Default Calibration ---
DEFAULTS = {
    "raw_min": 2000,
    "raw_max": 56000,
    "deadzone": 300,
    "curve": "progressive",
    "smoothing": 0.3,
    "invert": False,
    "oversample": 16,
}

# --- LED for status ---
led = digitalio.DigitalInOut(LED_PIN)
led.direction = digitalio.Direction.OUTPUT

# --- Blink LED N times for error signaling ---
def blink_led(count, on_time=0.1, off_time=0.1):
    for _ in range(count):
        led.value = True
        time.sleep(on_time)
        led.value = False
        time.sleep(off_time)

# --- Load calibration from JSON ---
def load_calibration():
    try:
        with open("/calibration.json", "r") as f:
            data = json.load(f)
        # Merge with defaults so missing keys get filled
        cal = dict(DEFAULTS)
        cal.update(data)
        return cal
    except (OSError, ValueError):
        return dict(DEFAULTS)

# --- ADC setup ---
adc = analogio.AnalogIn(ADC_PIN)

def read_adc_oversampled(oversample):
    """Read ADC N times and return the average."""
    total = 0
    for _ in range(oversample):
        total += adc.value
    return total // oversample

# --- Brake curves ---
def apply_curve(t, curve_name):
    """Apply brake curve to normalized value t (0.0 - 1.0)."""
    if curve_name == "linear":
        return t
    elif curve_name == "progressive":
        return t * t
    elif curve_name == "aggressive":
        import math
        return math.sqrt(t)
    else:
        # Default to progressive if unknown
        return t * t

# --- HID device ---
# Find the gamepad device we configured in boot.py
gamepad_device = None
for device in usb_hid.devices:
    if device.usage_page == 0x01 and device.usage == 0x05:
        gamepad_device = device
        break

if gamepad_device is None:
    # No gamepad device found — blink error and halt
    while True:
        blink_led(5, on_time=0.05, off_time=0.05)
        time.sleep(1)

# Report buffer: 4 axes × 2 bytes = 8 bytes
report = bytearray(8)

# --- Load calibration ---
cal = load_calibration()

# Signal calibration status
if cal == DEFAULTS:
    # Using defaults — blink 3 times as warning
    blink_led(3, on_time=0.2, off_time=0.2)

# Turn LED on to indicate running
led.value = True

# --- EMA state ---
ema_value = 0.0
ema_initialized = False

# --- Main loop ---
LOOP_INTERVAL = 0.002  # ~500Hz target

while True:
    loop_start = time.monotonic()

    # 1. Read ADC with oversampling
    raw = read_adc_oversampled(cal["oversample"])

    # 2. Clamp to calibration range
    raw_min = cal["raw_min"]
    raw_max = cal["raw_max"]
    clamped = max(raw_min, min(raw_max, raw))

    # 3. Normalize to 0.0 - 1.0
    if raw_max == raw_min:
        normalized = 0.0
    else:
        normalized = (clamped - raw_min) / (raw_max - raw_min)

    # 4. Apply deadzone
    deadzone = cal["deadzone"] / (raw_max - raw_min) if raw_max != raw_min else 0.0
    if normalized < deadzone:
        normalized = 0.0
    elif deadzone > 0:
        normalized = (normalized - deadzone) / (1.0 - deadzone)

    # 5. Apply brake curve
    normalized = apply_curve(normalized, cal["curve"])

    # 6. EMA smoothing
    alpha = cal["smoothing"]
    if not ema_initialized:
        ema_value = normalized
        ema_initialized = True
    else:
        ema_value = alpha * normalized + (1.0 - alpha) * ema_value

    # 7. Invert if needed
    if cal["invert"]:
        ema_value = 1.0 - ema_value

    # 8. Map to 0-65535 and send HID report
    brake_value = int(ema_value * 65535)
    brake_value = max(0, min(65535, brake_value))

    # Pack 4 axes (X=brake, Y=raw ADC, Z/Rz=0) as little-endian uint16
    # Y axis carries raw oversampled ADC value for the calibration GUI
    struct.pack_into("<HHHH", report, 0, brake_value, raw, 0, 0)

    try:
        gamepad_device.send_report(report)
    except OSError:
        pass  # Retry next loop

    # 9. Sleep to maintain loop rate
    elapsed = time.monotonic() - loop_start
    sleep_time = LOOP_INTERVAL - elapsed
    if sleep_time > 0:
        time.sleep(sleep_time)
```

- [ ] **Step 2: Commit**

```bash
git add firmware/code.py
git commit -m "feat: add main firmware with ADC, oversampling, curves, and HID output"
```

---

### Task 4: calibrator.py — Calibration GUI

**Files:**
- Create: `gui/calibrator.py`
- Create: `gui/requirements.txt`

The PC-side calibration tool. It reads live HID data from the Pico via pygame, shows real-time brake pressure, and provides manual + auto calibration with save to CIRCUITPY drive.

- [ ] **Step 1: Write requirements.txt**

```
pygame>=2.0
```

- [ ] **Step 2: Write gui/calibrator.py**

```python
# gui/calibrator.py
"""Brake controller calibration GUI.

Reads live brake data from Pico HID gamepad via pygame,
provides manual and auto-calibration, and writes
calibration.json to the CIRCUITPY USB drive.

Usage:
    python calibrator.py
"""

import json
import os
import platform
import sys
import time
import struct
import tkinter as tk
from tkinter import ttk, messagebox

try:
    import pygame
except ImportError:
    print("pygame is required. Install with: pip install pygame")
    sys.exit(1)


# --- CIRCUITPY drive detection ---

def find_circuitpy_drive():
    """Find the CIRCUITPY USB drive on any platform."""
    system = platform.system()
    if system == "Windows":
        import ctypes
        import string
        kernel32 = ctypes.windll.kernel32
        for letter in string.ascii_uppercase:
            drive = f"{letter}:\\"
            if kernel32.GetVolumeNameForVolumeMountPointW:
                try:
                    volume_name = ctypes.create_unicode_buffer(256)
                    if kernel32.GetVolumeInformationW(
                        drive, volume_name, 256, None, None, None, None, 0
                    ):
                        if volume_name.value == "CIRCUITPY":
                            return drive
                except Exception:
                    continue
        # Fallback: check for CIRCUITPY label using simpler method
        for letter in string.ascii_uppercase:
            drive = f"{letter}:\\"
            if os.path.exists(drive):
                try:
                    # Check if boot_out.txt exists (CircuitPython always creates this)
                    if os.path.exists(os.path.join(drive, "boot_out.txt")):
                        return drive
                except Exception:
                    continue
    elif system == "Linux":
        import glob
        paths = glob.glob("/media/*/CIRCUITPY")
        if paths:
            return paths[0]
        paths = glob.glob("/run/media/*/CIRCUITPY")
        if paths:
            return paths[0]
    elif system == "Darwin":  # macOS
        path = "/Volumes/CIRCUITPY"
        if os.path.exists(path):
            return path
    return None


# --- Pico gamepad reader via pygame ---

class PicoReader:
    """Read brake axis values from Pico HID gamepad via pygame.

    The Pico sends:
      - Axis 0 (X): processed brake value (0-65535 after calibration/curve/smoothing)
      - Axis 1 (Y): raw oversampled ADC value (0-65535, for calibration)
    """

    AXIS_BRAKE = 0  # Processed brake
    AXIS_RAW = 1     # Raw ADC for calibration

    def __init__(self):
        pygame.init()
        pygame.joystick.init()
        self.joystick = None
        self._connect()

    def _connect(self):
        """Connect to the first available joystick (should be the Pico)."""
        if pygame.joystick.get_count() > 0:
            self.joystick = pygame.joystick.Joystick(0)
            self.joystick.init()

    def read_axis(self, axis=0):
        """Read the specified axis value. Returns float 0.0-1.0 or None."""
        pygame.event.pump()
        if self.joystick is None:
            # Try reconnecting
            self._connect()
            if self.joystick is None:
                return None
        try:
            # pygame normalizes axes to -1.0 to 1.0 for gamepads
            # Our 16-bit axes send 0-65535, pygame maps to 0.0-1.0 (first half of -1 to 1)
            raw = self.joystick.get_axis(axis)
            # Map from pygame's -1..1 to our 0..1
            return (raw + 1.0) / 2.0
        except Exception:
            return None

    def read_brake(self):
        """Read processed brake value (axis 0). Returns float 0.0-1.0 or None."""
        return self.read_axis(self.AXIS_BRAKE)

    def read_raw_adc(self):
        """Read raw ADC value (axis 1). Returns float 0.0-1.0 or None."""
        return self.read_axis(self.AXIS_RAW)

    def read_raw_adc_int(self):
        """Read raw ADC as integer 0-65535. Returns int or None."""
        val = self.read_raw_adc()
        if val is not None:
            return int(val * 65535)
        return None
        try:
            # pygame normalizes axes to -1.0 to 1.0 for gamepads
            # Our brake axis sends 0-65535, pygame maps to 0.0-1.0 (first half of -1 to 1)
            raw = self.joystick.get_axis(axis)
            # Map from pygame's -1..1 to our 0..1
            return (raw + 1.0) / 2.0
        except Exception:
            return None

    def read_raw_axis(self, axis=0):
        """Read axis and return the raw pygame value (-1.0 to 1.0)."""
        pygame.event.pump()
        if self.joystick is None:
            self._connect()
            if self.joystick is None:
                return None
        try:
            return self.joystick.get_axis(axis)
        except Exception:
            return None

    @property
    def connected(self):
        return self.joystick is not None

    @property
    def device_name(self):
        if self.joystick:
            return self.joystick.get_name()
        return "Not connected"

    def quit(self):
        pygame.quit()


# --- Main GUI ---

class BrakeCalibrator(tk.Tk):
    """Main calibration GUI window."""

    # Default calibration values
    DEFAULTS = {
        "raw_min": 2000,
        "raw_max": 56000,
        "deadzone": 300,
        "curve": "progressive",
        "smoothing": 0.3,
        "invert": False,
        "oversample": 16,
    }

    CURVES = ["linear", "progressive", "aggressive"]
    OVERSAMPLE_OPTIONS = [1, 4, 16, 64]

    def __init__(self):
        super().__init__()
        self.title("Brake Controller Calibrator")
        self.resizable(True, True)
        self.minsize(700, 500)

        self.reader = PicoReader()
        self.circuitpy_drive = find_circuitpy_drive()
        self.auto_calibrating = False
        self.cal_cycle = 0
        self.cal_phase = "idle"  # idle, min_capture, max_capture, done
        self.captured_mins = []   # Per-cycle minimums
        self.captured_maxs = []   # Per-cycle maximums
        self.capture_samples = [] # Samples within current capture window
        self.capture_start = 0

        # Pressure history for the live graph
        self.pressure_history = []
        self.HISTORY_LENGTH = 200

        self._build_ui()
        self._update_status()
        self._poll_loop()

    def _build_ui(self):
        """Build the complete UI layout."""
        # Main container
        main = ttk.Frame(self, padding=10)
        main.pack(fill=tk.BOTH, expand=True)

        # Left panel — live pressure graph
        left = ttk.LabelFrame(main, text="Live Brake Pressure", padding=5)
        left.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 5))

        self.canvas = tk.Canvas(left, bg="black", width=300, height=400)
        self.canvas.pack(fill=tk.BOTH, expand=True)

        # Raw/normalized value labels below graph
        info_frame = ttk.Frame(left)
        info_frame.pack(fill=tk.X, pady=(5, 0))

        self.raw_label = ttk.Label(info_frame, text="Raw ADC: --")
        self.raw_label.pack(side=tk.LEFT, padx=5)

        self.norm_label = ttk.Label(info_frame, text="Brake: --")
        self.norm_label.pack(side=tk.LEFT, padx=5)

        self.device_label = ttk.Label(info_frame, text="Device: --")
        self.device_label.pack(side=tk.RIGHT, padx=5)

        # Right panel — controls
        right = ttk.LabelFrame(main, text="Calibration", padding=5)
        right.pack(side=tk.RIGHT, fill=tk.Y, padx=(5, 0))

        # Manual calibration
        cal_frame = ttk.LabelFrame(right, text="Manual Calibration", padding=5)
        cal_frame.pack(fill=tk.X, pady=(0, 5))

        ttk.Label(cal_frame, text="Raw Min:").grid(row=0, column=0, sticky=tk.W)
        self.raw_min_var = tk.IntVar(value=self.DEFAULTS["raw_min"])
        self.raw_min_entry = ttk.Entry(cal_frame, textvariable=self.raw_min_var, width=10)
        self.raw_min_entry.grid(row=0, column=1, padx=5)

        ttk.Button(cal_frame, text="Set Min", command=self._set_min).grid(row=0, column=2)

        ttk.Label(cal_frame, text="Raw Max:").grid(row=1, column=0, sticky=tk.W)
        self.raw_max_var = tk.IntVar(value=self.DEFAULTS["raw_max"])
        self.raw_max_entry = ttk.Entry(cal_frame, textvariable=self.raw_max_var, width=10)
        self.raw_max_entry.grid(row=1, column=1, padx=5)

        ttk.Button(cal_frame, text="Set Max", command=self._set_max).grid(row=1, column=2)

        # Auto calibrate
        auto_frame = ttk.LabelFrame(right, text="Auto Calibrate", padding=5)
        auto_frame.pack(fill=tk.X, pady=(0, 5))

        self.auto_cal_btn = ttk.Button(auto_frame, text="Auto Calibrate", command=self._start_auto_cal)
        self.auto_cal_btn.pack(fill=tk.X)

        self.auto_cal_label = ttk.Label(auto_frame, text="", wraplength=200)
        self.auto_cal_label.pack(fill=tk.X, pady=(5, 0))

        # Settings
        settings_frame = ttk.LabelFrame(right, text="Settings", padding=5)
        settings_frame.pack(fill=tk.X, pady=(0, 5))

        ttk.Label(settings_frame, text="Curve:").pack(anchor=tk.W)
        self.curve_var = tk.StringVar(value=self.DEFAULTS["curve"])
        curve_combo = ttk.Combobox(settings_frame, textvariable=self.curve_var,
                                    values=self.CURVES, state="readonly", width=15)
        curve_combo.pack(fill=tk.X, pady=(0, 5))

        ttk.Label(settings_frame, text="Smoothing:").pack(anchor=tk.W)
        self.smoothing_var = tk.DoubleVar(value=self.DEFAULTS["smoothing"])
        smoothing_scale = ttk.Scale(settings_frame, from_=0.0, to=1.0,
                                     variable=self.smoothing_var, orient=tk.HORIZONTAL)
        smoothing_scale.pack(fill=tk.X)
        self.smoothing_label = ttk.Label(settings_frame, text=f"α = {self.DEFAULTS['smoothing']:.2f}")
        self.smoothing_label.pack(anchor=tk.W)
        self.smoothing_var.trace_add("write", self._update_smoothing_label)

        ttk.Label(settings_frame, text="Deadzone:").pack(anchor=tk.W)
        self.deadzone_var = tk.IntVar(value=self.DEFAULTS["deadzone"])
        deadzone_scale = ttk.Scale(settings_frame, from_=0, to=1000,
                                    variable=self.deadzone_var, orient=tk.HORIZONTAL)
        deadzone_scale.pack(fill=tk.X)

        ttk.Label(settings_frame, text="Oversample:").pack(anchor=tk.W)
        self.oversample_var = tk.IntVar(value=self.DEFAULTS["oversample"])
        oversample_combo = ttk.Combobox(settings_frame, textvariable=self.oversample_var,
                                         values=[str(x) for x in self.OVERSAMPLE_OPTIONS],
                                         state="readonly", width=10)
        oversample_combo.pack(fill=tk.X, pady=(0, 5))

        self.invert_var = tk.BooleanVar(value=self.DEFAULTS["invert"])
        ttk.Checkbutton(settings_frame, text="Invert", variable=self.invert_var).pack(anchor=tk.W)

        # Save button
        self.save_btn = ttk.Button(right, text="Save to Pico", command=self._save_calibration)
        self.save_btn.pack(fill=tk.X, pady=(5, 0))

        # Status bar
        self.status_var = tk.StringVar(value="Initializing...")
        status_bar = ttk.Label(self, textvariable=self.status_var, relief=tk.SUNKEN, anchor=tk.W)
        status_bar.pack(side=tk.BOTTOM, fill=tk.X)

    def _update_smoothing_label(self, *args):
        try:
            self.smoothing_label.config(text=f"α = {self.smoothing_var.get():.2f}")
        except Exception:
            pass

    def _update_status(self):
        """Update drive and device status."""
        parts = []
        if self.circuitpy_drive:
            parts.append(f"Drive: {self.circuitpy_drive}")
        else:
            parts.append("CIRCUITPY drive not found")
        if self.reader.connected:
            parts.append(f"Device: {self.reader.device_name}")
        else:
            parts.append("No gamepad detected")
        self.status_var.set(" | ".join(parts))

    def _set_min(self):
        """Set raw_min to current raw ADC value."""
        raw = self.reader.read_raw_adc_int()
        if raw is not None:
            self.raw_min_var.set(raw)

    def _set_max(self):
        """Set raw_max to current raw ADC value."""
        raw = self.reader.read_raw_adc_int()
        if raw is not None:
            self.raw_max_var.set(raw)

    def _start_auto_cal(self):
        """Begin auto-calibration flow."""
        self.auto_calibrating = True
        self.cal_cycle = 0
        self.captured_mins = []   # Per-cycle minimums
        self.captured_maxs = []   # Per-cycle maximums
        self.capture_samples = [] # Samples within current capture window
        self.cal_phase = "min_capture"
        self.capture_start = time.monotonic()
        self.auto_cal_label.config(text="Cycle 1/3: Release brake completely...\nCapturing MIN (2s)...")
        self.auto_cal_btn.config(state=tk.DISABLED)

    def _process_auto_cal(self):
        """Process auto-calibration state machine.

        For each capture window, we track all samples then take the
        min (or max) of that window. Final values are the average
        across 3 cycles.
        """
        if not self.auto_calibrating:
            return

        elapsed = time.monotonic() - self.capture_start
        # Read raw ADC value (axis 1) for calibration
        current_raw = self.reader.read_raw_adc_int()

        if self.cal_phase == "min_capture":
            if current_raw is not None:
                self.capture_samples.append(current_raw)
            if elapsed >= 2.0:
                # Take minimum of all samples in this window
                window_min = min(self.capture_samples) if self.capture_samples else self.DEFAULTS["raw_min"]
                self.captured_mins.append(window_min)
                self.capture_samples = []
                self.cal_phase = "max_capture"
                self.capture_start = time.monotonic()
                self.auto_cal_label.config(
                    text=f"Cycle {self.cal_cycle + 1}/3: Press brake to MAX...\nCapturing MAX (4s)...")

        elif self.cal_phase == "max_capture":
            if current_raw is not None:
                self.capture_samples.append(current_raw)
            if elapsed >= 4.0:
                # Take maximum of all samples in this window
                window_max = max(self.capture_samples) if self.capture_samples else self.DEFAULTS["raw_max"]
                self.captured_maxs.append(window_max)
                self.capture_samples = []
                self.cal_cycle += 1
                if self.cal_cycle >= 3:
                    # All 3 cycles done — compute averages across cycles
                    avg_min = sum(self.captured_mins) // len(self.captured_mins)
                    avg_max = sum(self.captured_maxs) // len(self.captured_maxs)
                    self.raw_min_var.set(avg_min)
                    self.raw_max_var.set(avg_max)
                    self.auto_calibrating = False
                    self.cal_phase = "done"
                    self.auto_cal_label.config(
                        text=f"Done! Min={avg_min}, Max={avg_max}\nTweak values if needed, then Save.")
                    self.auto_cal_btn.config(state=tk.NORMAL)
                else:
                    # Next cycle
                    self.cal_phase = "min_capture"
                    self.capture_start = time.monotonic()
                    self.auto_cal_label.config(
                        text=f"Cycle {self.cal_cycle + 1}/3: Release brake...\nCapturing MIN (2s)...")

    def _save_calibration(self):
        """Write calibration.json to the CIRCUITPY drive."""
        if not self.circuitpy_drive:
            messagebox.showerror("Error", "CIRCUITPY drive not found. Is the Pico connected?")
            return

        cal = {
            "raw_min": self.raw_min_var.get(),
            "raw_max": self.raw_max_var.get(),
            "deadzone": self.deadzone_var.get(),
            "curve": self.curve_var.get(),
            "smoothing": round(self.smoothing_var.get(), 2),
            "invert": self.invert_var.get(),
            "oversample": int(self.oversample_var.get()),
        }

        filepath = os.path.join(self.circuitpy_drive, "calibration.json")
        try:
            with open(filepath, "w") as f:
                json.dump(cal, f, indent=2)
            messagebox.showinfo("Saved",
                                f"Calibration saved to {filepath}\n\nPress RESET on Pico or reconnect.")
        except OSError as e:
            messagebox.showerror("Error", f"Could not write to CIRCUITPY:\n{e}")

    def _draw_graph(self):
        """Draw the live pressure graph on the canvas."""
        self.canvas.delete("all")
        w = self.canvas.winfo_width()
        h = self.canvas.winfo_height()

        if w < 10 or h < 10:
            return

        # Draw grid lines
        for i in range(5):
            y = int(h * i / 4)
            self.canvas.create_line(0, y, w, y, fill="#333333", dash=(2, 4))

        # Draw pressure history
        if len(self.pressure_history) < 2:
            return

        points = []
        n = len(self.pressure_history)
        for i, val in enumerate(self.pressure_history):
            x = int(w * i / max(n - 1, 1))
            y = int(h * (1.0 - val))  # Invert: 0 at bottom, 1 at top
            points.append((x, y))

        # Draw as connected line
        for i in range(len(points) - 1):
            # Color gradient: green at low pressure, red at high
            val = self.pressure_history[i]
            r = int(val * 255)
            g = int((1.0 - val) * 255)
            color = f"#{r:02x}{g:02x}00"
            self.canvas.create_line(points[i][0], points[i][1],
                                     points[i+1][0], points[i+1][1],
                                     fill=color, width=2)

    def _poll_loop(self):
        """Main polling loop — runs at ~30Hz."""
        # Read processed brake value for the graph
        brake_val = self.reader.read_brake()
        # Read raw ADC value for calibration display
        raw_val = self.reader.read_raw_adc_int()

        if brake_val is not None:
            # Update pressure history (uses processed brake for visual)
            self.pressure_history.append(brake_val)
            if len(self.pressure_history) > self.HISTORY_LENGTH:
                self.pressure_history.pop(0)

        if raw_val is not None:
            self.raw_label.config(text=f"Raw ADC: {raw_val}")
        else:
            self.raw_label.config(text="Raw ADC: --")

        if brake_val is not None:
            self.norm_label.config(text=f"Brake: {brake_val:.1%}")
        else:
            self.norm_label.config(text="Brake: --")

        self.device_label.config(text=f"Device: {self.reader.device_name}")

        # Process auto-calibration
        self._process_auto_cal()

        # Draw graph
        self._draw_graph()

        # Update status periodically
        self._update_status()

        # Schedule next poll
        self.after(33, self._poll_loop)

    def destroy(self):
        self.reader.quit()
        super().destroy()


if __name__ == "__main__":
    app = BrakeCalibrator()
    app.mainloop()
```

- [ ] **Step 3: Commit**

```bash
git add gui/calibrator.py gui/requirements.txt
git commit -m "feat: add Tkinter calibration GUI with auto-calibrate and live pressure graph"
```

---

### Task 5: Wiring diagram documentation

**Files:**
- Create: `docs/wiring-diagram.md`

- [ ] **Step 1: Write wiring diagram**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add docs/wiring-diagram.md
git commit -m "docs: add wiring diagram and parts list"
```

---

### Task 6: README.md

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write README**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add README with setup and usage guide"
```

---

### Task 7: Update AGENTS.md with final project state

**Files:**
- Modify: `AGENTS.md`

The AGENTS.md was updated in Task 1 with initial content. After all files are written, verify it still accurately reflects the project and update if needed.

- [ ] **Step 1: Review and update AGENTS.md**

Read the current AGENTS.md and verify it matches the final project structure and conventions. The content from Task 1 should be accurate. Fix any discrepancies.

- [ ] **Step 2: Commit if changed**

```bash
git add AGENTS.md
git commit -m "docs: update AGENTS.md to reflect final project structure"
```
