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
        # Fallback: check for boot_out.txt (CircuitPython always creates this)
        for letter in string.ascii_uppercase:
            drive = f"{letter}:\\"
            if os.path.exists(drive):
                try:
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
    AXIS_RAW = 1  # Raw ADC for calibration

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
        self.captured_mins = []  # Per-cycle minimums
        self.captured_maxs = []  # Per-cycle maximums
        self.capture_samples = []  # Samples within current capture window
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
        self.raw_min_entry = ttk.Entry(
            cal_frame, textvariable=self.raw_min_var, width=10
        )
        self.raw_min_entry.grid(row=0, column=1, padx=5)

        ttk.Button(cal_frame, text="Set Min", command=self._set_min).grid(
            row=0, column=2
        )

        ttk.Label(cal_frame, text="Raw Max:").grid(row=1, column=0, sticky=tk.W)
        self.raw_max_var = tk.IntVar(value=self.DEFAULTS["raw_max"])
        self.raw_max_entry = ttk.Entry(
            cal_frame, textvariable=self.raw_max_var, width=10
        )
        self.raw_max_entry.grid(row=1, column=1, padx=5)

        ttk.Button(cal_frame, text="Set Max", command=self._set_max).grid(
            row=1, column=2
        )

        # Auto calibrate
        auto_frame = ttk.LabelFrame(right, text="Auto Calibrate", padding=5)
        auto_frame.pack(fill=tk.X, pady=(0, 5))

        self.auto_cal_btn = ttk.Button(
            auto_frame, text="Auto Calibrate", command=self._start_auto_cal
        )
        self.auto_cal_btn.pack(fill=tk.X)

        self.auto_cal_label = ttk.Label(auto_frame, text="", wraplength=200)
        self.auto_cal_label.pack(fill=tk.X, pady=(5, 0))

        # Settings
        settings_frame = ttk.LabelFrame(right, text="Settings", padding=5)
        settings_frame.pack(fill=tk.X, pady=(0, 5))

        ttk.Label(settings_frame, text="Curve:").pack(anchor=tk.W)
        self.curve_var = tk.StringVar(value=self.DEFAULTS["curve"])
        curve_combo = ttk.Combobox(
            settings_frame,
            textvariable=self.curve_var,
            values=self.CURVES,
            state="readonly",
            width=15,
        )
        curve_combo.pack(fill=tk.X, pady=(0, 5))

        ttk.Label(settings_frame, text="Smoothing:").pack(anchor=tk.W)
        self.smoothing_var = tk.DoubleVar(value=self.DEFAULTS["smoothing"])
        smoothing_scale = ttk.Scale(
            settings_frame,
            from_=0.0,
            to=1.0,
            variable=self.smoothing_var,
            orient=tk.HORIZONTAL,
        )
        smoothing_scale.pack(fill=tk.X)
        self.smoothing_label = ttk.Label(
            settings_frame, text=f"α = {self.DEFAULTS['smoothing']:.2f}"
        )
        self.smoothing_label.pack(anchor=tk.W)
        self.smoothing_var.trace_add("write", self._update_smoothing_label)

        ttk.Label(settings_frame, text="Deadzone:").pack(anchor=tk.W)
        self.deadzone_var = tk.IntVar(value=self.DEFAULTS["deadzone"])
        deadzone_scale = ttk.Scale(
            settings_frame,
            from_=0,
            to=1000,
            variable=self.deadzone_var,
            orient=tk.HORIZONTAL,
        )
        deadzone_scale.pack(fill=tk.X)

        ttk.Label(settings_frame, text="Oversample:").pack(anchor=tk.W)
        self.oversample_var = tk.IntVar(value=self.DEFAULTS["oversample"])
        oversample_combo = ttk.Combobox(
            settings_frame,
            textvariable=self.oversample_var,
            values=[str(x) for x in self.OVERSAMPLE_OPTIONS],
            state="readonly",
            width=10,
        )
        oversample_combo.pack(fill=tk.X, pady=(0, 5))

        self.invert_var = tk.BooleanVar(value=self.DEFAULTS["invert"])
        ttk.Checkbutton(settings_frame, text="Invert", variable=self.invert_var).pack(
            anchor=tk.W
        )

        # Save button
        self.save_btn = ttk.Button(
            right, text="Save to Pico", command=self._save_calibration
        )
        self.save_btn.pack(fill=tk.X, pady=(5, 0))

        # Status bar
        self.status_var = tk.StringVar(value="Initializing...")
        status_bar = ttk.Label(
            self, textvariable=self.status_var, relief=tk.SUNKEN, anchor=tk.W
        )
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
        self.captured_mins = []  # Per-cycle minimums
        self.captured_maxs = []  # Per-cycle maximums
        self.capture_samples = []  # Samples within current capture window
        self.cal_phase = "min_capture"
        self.capture_start = time.monotonic()
        self.auto_cal_label.config(
            text="Cycle 1/3: Release brake completely...\nCapturing MIN (2s)..."
        )
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
                window_min = (
                    min(self.capture_samples)
                    if self.capture_samples
                    else self.DEFAULTS["raw_min"]
                )
                self.captured_mins.append(window_min)
                self.capture_samples = []
                self.cal_phase = "max_capture"
                self.capture_start = time.monotonic()
                self.auto_cal_label.config(
                    text=f"Cycle {self.cal_cycle + 1}/3: Press brake to MAX...\nCapturing MAX (4s)..."
                )

        elif self.cal_phase == "max_capture":
            if current_raw is not None:
                self.capture_samples.append(current_raw)
            if elapsed >= 4.0:
                # Take maximum of all samples in this window
                window_max = (
                    max(self.capture_samples)
                    if self.capture_samples
                    else self.DEFAULTS["raw_max"]
                )
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
                        text=f"Done! Min={avg_min}, Max={avg_max}\nTweak values if needed, then Save."
                    )
                    self.auto_cal_btn.config(state=tk.NORMAL)
                else:
                    # Next cycle
                    self.cal_phase = "min_capture"
                    self.capture_start = time.monotonic()
                    self.auto_cal_label.config(
                        text=f"Cycle {self.cal_cycle + 1}/3: Release brake...\nCapturing MIN (2s)..."
                    )

    def _save_calibration(self):
        """Write calibration.json to the CIRCUITPY drive."""
        if not self.circuitpy_drive:
            messagebox.showerror(
                "Error", "CIRCUITPY drive not found. Is the Pico connected?"
            )
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
            messagebox.showinfo(
                "Saved",
                f"Calibration saved to {filepath}\n\nPress RESET on Pico or reconnect.",
            )
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
            self.canvas.create_line(
                points[i][0],
                points[i][1],
                points[i + 1][0],
                points[i + 1][1],
                fill=color,
                width=2,
            )

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
