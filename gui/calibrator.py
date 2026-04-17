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
        self._devices = []  # List of (index, name) tuples
        self._scan_devices()
        self._auto_select_pico()

    def _scan_devices(self):
        """Scan all connected joysticks and store their names."""
        self._devices = []
        for i in range(pygame.joystick.get_count()):
            js = pygame.joystick.Joystick(i)
            js.init()
            self._devices.append((i, js.get_name()))

    def _auto_select_pico(self):
        """Try to auto-select a device with 'pico' in the name."""
        for idx, name in self._devices:
            if "pico" in name.lower():
                self.select_device(idx)
                return
        # Fallback: select first device if any
        if self._devices:
            self.select_device(0)

    def list_devices(self):
        """Return list of (index, name) tuples for all connected joysticks."""
        return list(self._devices)

    def select_device(self, index):
        """Connect to a specific joystick by index."""
        pygame.event.pump()
        if 0 <= index < pygame.joystick.get_count():
            self.joystick = pygame.joystick.Joystick(index)
            self.joystick.init()

    def read_axis(self, axis=0):
        """Read the specified axis value. Returns float 0.0-1.0 or None."""
        pygame.event.pump()
        if self.joystick is None:
            return None
        try:
            raw = self.joystick.get_axis(axis)
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

    @property
    def selected_index(self):
        if self.joystick is not None:
            return self.joystick.get_id()
        return -1

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
        self.cal_phase = "idle"  # idle, countdown, capture, done
        self.capture_samples = []
        self.countdown_start = 0
        self.capture_start = 0

        # Pressure history for the live graph — three lines
        self.raw_history = []  # Raw ADC values (0-1)
        self.brake_history = []  # Processed brake values from Pico (0-1)
        self.preview_history = []  # Locally computed preview (0-1)
        self.HISTORY_LENGTH = 200

        # EMA state for preview line
        self._preview_ema = 0.0
        self._preview_ema_init = False

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

        # Device selector
        device_frame = ttk.Frame(left)
        device_frame.pack(fill=tk.X, pady=(5, 0))

        ttk.Label(device_frame, text="Device:").pack(side=tk.LEFT, padx=5)
        devices = self.reader.list_devices()
        device_names = [f"{i}: {name}" for i, name in devices]
        self.device_var = tk.StringVar()
        if devices:
            self.device_var.set(
                f"{self.reader.selected_index}: {self.reader.device_name}"
            )
        self.device_combo = ttk.Combobox(
            device_frame,
            textvariable=self.device_var,
            values=device_names,
            state="readonly",
            width=40,
        )
        self.device_combo.pack(side=tk.LEFT, padx=5, fill=tk.X, expand=True)
        self.device_combo.bind("<<ComboboxSelected>>", self._on_device_selected)

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
            to=0.95,
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

    def _on_device_selected(self, event=None):
        """Handle device selection from dropdown."""
        selection = self.device_var.get()
        if selection:
            try:
                index = int(selection.split(":")[0])
                self.reader.select_device(index)
            except (ValueError, IndexError):
                pass

    def _update_smoothing_label(self, *args):
        try:
            self.smoothing_label.config(text=f"α = {self.smoothing_var.get():.2f}")
        except Exception:
            pass

    def _compute_preview(self, raw_01):
        """Apply the current slider settings to a raw ADC value (0-1).

        Mirrors the firmware pipeline: clamp → normalize → deadzone → curve → EMA.
        Returns the previewed value (0-1).
        """
        raw_min = self.raw_min_var.get()
        raw_max = self.raw_max_var.get()

        # Convert 0-1 back to raw ADC integer range
        raw_int = int(raw_01 * 65535)

        # Clamp
        clamped = max(raw_min, min(raw_max, raw_int))

        # Normalize
        if raw_max == raw_min:
            normalized = 0.0
        else:
            normalized = (clamped - raw_min) / (raw_max - raw_min)

        # Deadzone
        dz = self.deadzone_var.get()
        deadzone_frac = dz / (raw_max - raw_min) if raw_max != raw_min else 0.0
        if normalized < deadzone_frac:
            normalized = 0.0
        elif deadzone_frac > 0:
            normalized = (normalized - deadzone_frac) / (1.0 - deadzone_frac)

        # Curve
        curve = self.curve_var.get()
        if curve == "linear":
            pass
        elif curve == "progressive":
            normalized = normalized * normalized
        elif curve == "aggressive":
            normalized = normalized**0.5

        # EMA smoothing (smoothing 0 = none, 0.95 = max; alpha = 1 - smoothing)
        alpha = 1.0 - min(self.smoothing_var.get(), 0.95)
        if not self._preview_ema_init:
            self._preview_ema = normalized
            self._preview_ema_init = True
        else:
            self._preview_ema = alpha * normalized + (1.0 - alpha) * self._preview_ema

        # Invert
        if self.invert_var.get():
            return 1.0 - self._preview_ema
        return self._preview_ema

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
        result = messagebox.showinfo(
            "Auto Calibrate",
            "Press OK, then release the brake completely.\n\n"
            "After the countdown, press and release the brake\n"
            "firmly within 5 seconds.",
        )
        # Start countdown phase
        self.auto_calibrating = True
        self.cal_phase = "countdown"
        self.countdown_start = time.monotonic()
        self.capture_samples = []
        self.auto_cal_btn.config(state=tk.DISABLED)

    def _process_auto_cal(self):
        """Process auto-calibration state machine.

        Phases: countdown (3s) → capture (5s) → done
        During capture, track min and max raw ADC values.
        """
        if not self.auto_calibrating:
            return

        elapsed = (
            time.monotonic() - self.countdown_start
            if self.cal_phase == "countdown"
            else time.monotonic() - self.capture_start
        )

        if self.cal_phase == "countdown":
            remaining = 3 - int(elapsed)
            if remaining > 0:
                self.auto_cal_label.config(
                    text=f"Release brake completely...\n{remaining}..."
                )
            else:
                # Switch to capture phase
                self.cal_phase = "capture"
                self.capture_start = time.monotonic()
                self.auto_cal_label.config(
                    text="Capturing... press and release brake now!"
                )

        elif self.cal_phase == "capture":
            # Read raw ADC value (axis 1) for calibration
            current_raw = self.reader.read_raw_adc_int()
            if current_raw is not None:
                self.capture_samples.append(current_raw)

            remaining = 5 - int(elapsed)
            if remaining > 0:
                self.auto_cal_label.config(
                    text=f"Capturing... press and release brake!\n{remaining}s remaining"
                )
            else:
                # Capture done — compute min and max
                if self.capture_samples:
                    captured_min = min(self.capture_samples)
                    captured_max = max(self.capture_samples)
                    self.raw_min_var.set(captured_min)
                    self.raw_max_var.set(captured_max)
                    self.auto_cal_label.config(
                        text=f"Done! Min={captured_min}, Max={captured_max}\nTweak if needed, then Save."
                    )
                else:
                    self.auto_cal_label.config(text="No data captured. Try again.")

                self.auto_calibrating = False
                self.cal_phase = "done"
                self.auto_cal_btn.config(state=tk.NORMAL)

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
        """Draw the live pressure graph on the canvas — raw and processed lines."""
        self.canvas.delete("all")
        w = self.canvas.winfo_width()
        h = self.canvas.winfo_height()

        if w < 10 or h < 10:
            return

        # Draw grid lines
        for i in range(5):
            y = int(h * i / 4)
            self.canvas.create_line(0, y, w, y, fill="#333333", dash=(2, 4))

        # Draw legend
        legend_y = 12
        self.canvas.create_line(8, legend_y, 28, legend_y, fill="#4488ff", width=2)
        self.canvas.create_text(
            32,
            legend_y,
            text="Raw ADC",
            fill="#4488ff",
            anchor=tk.W,
            font=("Consolas", 9),
        )
        self.canvas.create_line(120, legend_y, 140, legend_y, fill="#44ff44", width=2)
        self.canvas.create_text(
            144,
            legend_y,
            text="Preview",
            fill="#44ff44",
            anchor=tk.W,
            font=("Consolas", 9),
        )
        self.canvas.create_line(230, legend_y, 250, legend_y, fill="#ff4444", width=2)
        self.canvas.create_text(
            254,
            legend_y,
            text="Game Input",
            fill="#ff4444",
            anchor=tk.W,
            font=("Consolas", 9),
        )

        # Draw a history line
        def draw_line(history, color, width=2):
            if len(history) < 2:
                return
            n = len(history)
            coords = []
            for i, val in enumerate(history):
                x = int(w * i / max(n - 1, 1))
                y = int(h * (1.0 - max(0.0, min(1.0, val))))
                coords.extend([x, y])
            self.canvas.create_line(*coords, fill=color, width=width, smooth=True)

        # Raw ADC line (blue)
        draw_line(self.raw_history, "#4488ff", 2)
        # Preview line (green) — local processing with slider settings
        draw_line(self.preview_history, "#44ff44", 2)
        # Processed brake line (red) — actual Pico output, drawn on top
        draw_line(self.brake_history, "#ff4444", 2)

    def _poll_loop(self):
        """Main polling loop — runs at ~30Hz."""
        # Read both axes
        brake_val = self.reader.read_brake()  # Processed (calibrated, curved, smoothed)
        raw_val = self.reader.read_raw_adc()  # Raw ADC (0-1 normalized)
        raw_val_int = self.reader.read_raw_adc_int()

        # Update both histories
        if raw_val is not None:
            self.raw_history.append(raw_val)
            if len(self.raw_history) > self.HISTORY_LENGTH:
                self.raw_history.pop(0)

            # Compute preview with current slider settings
            preview = self._compute_preview(raw_val)
            self.preview_history.append(preview)
            if len(self.preview_history) > self.HISTORY_LENGTH:
                self.preview_history.pop(0)

        if brake_val is not None:
            self.brake_history.append(brake_val)
            if len(self.brake_history) > self.HISTORY_LENGTH:
                self.brake_history.pop(0)

        # Update labels
        if raw_val_int is not None:
            self.raw_label.config(text=f"Raw ADC: {raw_val_int}")
        else:
            self.raw_label.config(text="Raw ADC: --")

        if brake_val is not None:
            self.norm_label.config(text=f"Brake: {brake_val:.1%}")
        else:
            self.norm_label.config(text="Brake: --")

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
