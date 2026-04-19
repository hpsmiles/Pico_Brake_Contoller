# gui/calibrator.py
"""Brake controller calibration GUI.

Reads live brake and throttle data from Pico HID gamepad via pygame,
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
from tkinter import ttk, messagebox, simpledialog


class ToolTip:
    """Hover tooltip for any Tkinter widget."""

    def __init__(self, widget, text):
        self.widget = widget
        self.text = text
        self.tip_window = None
        widget.bind("<Enter>", self._show)
        widget.bind("<Leave>", self._hide)

    def _show(self, event=None):
        if self.tip_window:
            return
        x = self.widget.winfo_rootx() + 20
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 5
        self.tip_window = tw = tk.Toplevel(self.widget)
        tw.wm_overrideredirect(True)
        tw.wm_geometry(f"+{x}+{y}")
        label = tk.Label(
            tw, text=self.text, justify=tk.LEFT,
            background="#ffffe0", relief=tk.SOLID, borderwidth=1,
            font=("Segoe UI", 9), padx=6, pady=3,
        )
        label.pack()

    def _hide(self, event=None):
        if self.tip_window:
            self.tip_window.destroy()
            self.tip_window = None


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
    """Read brake and throttle axis values from Pico HID gamepad via pygame.

    The Pico sends:
      - Axis 0 (X): processed brake value (0-65535 after calibration/curve/smoothing)
      - Axis 1 (Y): raw oversampled ADC value for brake (0-65535, for calibration)
      - Axis 2 (Z): processed throttle value (0-65535, or 0 if throttle disabled)
      - Axis 3 (Rz): raw throttle ADC value (0-65535, or 0 if throttle disabled)
    """

    AXIS_BRAKE = 0  # Processed brake
    AXIS_RAW = 1  # Raw brake ADC for calibration
    AXIS_THROTTLE = 2  # Processed throttle
    AXIS_THROTTLE_RAW = 3  # Raw throttle ADC for calibration

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
        """Read raw brake ADC value (axis 1). Returns float 0.0-1.0 or None."""
        return self.read_axis(self.AXIS_RAW)

    def read_raw_adc_int(self):
        """Read raw brake ADC as integer 0-65535. Returns int or None."""
        val = self.read_raw_adc()
        if val is not None:
            return int(val * 65535)
        return None

    def read_throttle(self):
        """Read processed throttle value (axis 2). Returns float 0.0-1.0 or None."""
        return self.read_axis(self.AXIS_THROTTLE)

    def read_throttle_raw(self):
        """Read raw throttle ADC value (axis 3). Returns float 0.0-1.0 or None."""
        return self.read_axis(self.AXIS_THROTTLE_RAW)

    def read_throttle_raw_int(self):
        """Read raw throttle ADC as integer 0-65535. Returns int or None."""
        val = self.read_throttle_raw()
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
        "curve": "linear",
        "progressive_power": 2.0,
        "aggressive_power": 2.0,
        "smoothing": 0.3,
        "invert": False,
        "oversample": 16,
        "saturation": 1.0,
        "bite_point": 0.0,
        "curve_points": [[0.0, 0.0], [1.0, 1.0]],
        # Throttle defaults
        "throttle_enabled": False,
        "throttle_sensor": "auto",
        "throttle_raw_min": 2000,
        "throttle_raw_max": 56000,
        "throttle_deadzone": 300,
        "throttle_curve": "linear",
        "throttle_progressive_power": 2.0,
        "throttle_aggressive_power": 2.0,
        "throttle_smoothing": 0.2,
        "throttle_invert": False,
        "throttle_saturation": 1.0,
        "throttle_bite_point": 0.0,
        "throttle_curve_points": [[0.0, 0.0], [1.0, 1.0]],
    }

    CURVES = ["linear", "progressive", "aggressive", "custom"]
    CURVE_PRESETS = {
        "linear": [[0.0, 0.0], [1.0, 1.0]],
        "progressive": [[0.0, 0.0], [0.25, 0.06], [0.5, 0.25], [0.75, 0.56], [1.0, 1.0]],
        "aggressive": [[0.0, 0.0], [0.25, 0.44], [0.5, 0.75], [0.75, 0.94], [1.0, 1.0]],
        "S-curve": [[0.0, 0.0], [0.25, 0.1], [0.5, 0.5], [0.75, 0.9], [1.0, 1.0]],
    }
    OVERSAMPLE_OPTIONS = [1, 4, 16, 64]

    def __init__(self):
        super().__init__()
        self.title("Brake Controller Calibrator")
        self.resizable(True, True)
        self.minsize(850, 550)

        self.reader = PicoReader()
        self.circuitpy_drive = find_circuitpy_drive()
        self.auto_calibrating = False
        self.cal_phase = "idle"  # idle, countdown, capture, done
        self.cal_target = tk.IntVar(value=0)  # 0=brake, 1=throttle
        self.capture_samples = []
        self.countdown_start = 0
        self.capture_start = 0

        # Pressure history for the live graph — four lines
        self.raw_history = []  # Raw brake ADC values (0-1)
        self.brake_history = []  # Processed brake values from Pico (0-1)
        self.preview_history = []  # Locally computed brake preview (0-1)
        self.throttle_raw_history = []  # Raw throttle ADC values (0-1)
        self.throttle_history = []  # Processed throttle values from Pico (0-1)
        self.throttle_preview_history = []  # Locally computed throttle preview (0-1)
        self.HISTORY_LENGTH = 200

        # EMA state for preview lines
        self._preview_ema = 0.0
        self._preview_ema_init = False
        self._throttle_preview_ema = 0.0
        self._throttle_preview_ema_init = False

        # Throttle UI widgets (for show/hide)
        self.throttle_widgets = []

        self._build_ui()
        self._update_status()
        self._poll_loop()

    def _build_ui(self):
        """Build the complete UI layout."""
        # Main container
        main = ttk.Frame(self, padding=10)
        main.pack(fill=tk.BOTH, expand=True)

        # Left panel — live pressure graph
        self.left_frame = ttk.LabelFrame(main, text="Live Brake Pressure", padding=5)
        self.left_frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(0, 5))

        self.canvas = tk.Canvas(self.left_frame, bg="black", width=300, height=400)
        self.canvas.pack(fill=tk.BOTH, expand=True)

        # Raw/normalized value labels below graph
        info_frame = ttk.Frame(self.left_frame)
        info_frame.pack(fill=tk.X, pady=(5, 0))

        self.raw_label = ttk.Label(info_frame, text="Raw ADC: --")
        self.raw_label.pack(side=tk.LEFT, padx=5)

        self.norm_label = ttk.Label(info_frame, text="Brake: --")
        self.norm_label.pack(side=tk.LEFT, padx=5)

        # Throttle labels (initially hidden)
        self.throttle_raw_label = ttk.Label(info_frame, text="Throttle Raw: --")
        self.throttle_norm_label = ttk.Label(info_frame, text="Throttle: --")

        # Device selector
        device_frame = ttk.Frame(self.left_frame)
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
        ToolTip(self.device_combo, "Select which gamepad device to read from.\nAuto-selects any device with 'pico' in the name.")

        # Right panel — controls
        right = ttk.LabelFrame(main, text="Calibration", padding=5)
        right.pack(side=tk.RIGHT, fill=tk.Y, padx=(5, 0))

        # Auto calibrate (top)
        auto_frame = ttk.LabelFrame(right, text="Auto Calibrate", padding=5)
        auto_frame.pack(fill=tk.X, pady=(0, 5))

        self.auto_cal_btn = ttk.Button(
            auto_frame, text="Auto Calibrate", command=self._start_auto_cal
        )
        self.auto_cal_btn.pack(fill=tk.X)
        ToolTip(self.auto_cal_btn, "Automatically detect min and max sensor values.\nRelease pedal for minimum, then press firmly for maximum.")

        self.auto_cal_label = ttk.Label(auto_frame, text="", wraplength=200)
        self.auto_cal_label.pack(fill=tk.X, pady=(5, 0))

        # Profile management
        profile_frame = ttk.LabelFrame(right, text="Profiles", padding=5)
        profile_frame.pack(fill=tk.X, pady=(0, 5))

        profile_list_frame = ttk.Frame(profile_frame)
        profile_list_frame.pack(fill=tk.X)

        self.profile_var = tk.StringVar()
        self.profile_combo = ttk.Combobox(
            profile_list_frame, textvariable=self.profile_var, state="readonly", width=18,
        )
        self.profile_combo.pack(side=tk.LEFT, padx=(0, 5))
        ToolTip(self.profile_combo, "Select a saved calibration profile to load or delete.")
        self._refresh_profile_list()

        profile_btn_frame = ttk.Frame(profile_frame)
        profile_btn_frame.pack(fill=tk.X, pady=(5, 0))

        save_btn = ttk.Button(profile_btn_frame, text="Save", command=self._save_profile, width=7)
        save_btn.pack(side=tk.LEFT, padx=2)
        ToolTip(save_btn, "Save current settings as a named profile on CIRCUITPY.")
        load_btn = ttk.Button(profile_btn_frame, text="Load", command=self._load_profile, width=7)
        load_btn.pack(side=tk.LEFT, padx=2)
        ToolTip(load_btn, "Load a saved profile. You still need to Save to Pico to activate it.")
        del_btn = ttk.Button(profile_btn_frame, text="Delete", command=self._delete_profile, width=7)
        del_btn.pack(side=tk.LEFT, padx=2)
        ToolTip(del_btn, "Delete the selected profile from CIRCUITPY.")

        # Throttle section
        self.throttle_frame = ttk.LabelFrame(right, text="Throttle", padding=5)
        self.throttle_frame.pack(fill=tk.X, pady=(0, 5))

        self.throttle_enabled_var = tk.BooleanVar(value=self.DEFAULTS["throttle_enabled"])
        self.throttle_enabled_check = ttk.Checkbutton(
            self.throttle_frame,
            text="Use Throttle",
            variable=self.throttle_enabled_var,
            command=self._on_throttle_toggle,
        )
        self.throttle_enabled_check.pack(anchor=tk.W)
        ToolTip(self.throttle_enabled_check, "Enable throttle pedal input on the Z/Rz axes.")
        self.throttle_widgets.append(self.throttle_enabled_check)

        self.throttle_sensor_label = ttk.Label(self.throttle_frame, text="Sensor: --")
        self.throttle_sensor_label.pack(anchor=tk.W, pady=(5, 0))

        # Throttle calibration controls container
        self.throttle_controls_frame = ttk.Frame(self.throttle_frame)
        self.throttle_controls_frame.pack(fill=tk.X, pady=(5, 0))
        self.throttle_widgets.append(self.throttle_controls_frame)

        # Throttle Manual Calibration
        ttk.Label(self.throttle_controls_frame, text="Raw Min:").grid(row=0, column=0, sticky=tk.W)
        self.throttle_raw_min_var = tk.IntVar(value=self.DEFAULTS["throttle_raw_min"])
        self.throttle_raw_min_entry = ttk.Entry(
            self.throttle_controls_frame, textvariable=self.throttle_raw_min_var, width=10
        )
        self.throttle_raw_min_entry.grid(row=0, column=1, padx=5)

        throttle_set_min_btn = ttk.Button(self.throttle_controls_frame, text="Set Min", command=self._set_throttle_min)
        throttle_set_min_btn.grid(row=0, column=2)
        ToolTip(throttle_set_min_btn, "Set throttle raw_min to the current sensor reading.\nRelease pedal completely, then click.")

        ttk.Label(self.throttle_controls_frame, text="Raw Max:").grid(row=1, column=0, sticky=tk.W)
        self.throttle_raw_max_var = tk.IntVar(value=self.DEFAULTS["throttle_raw_max"])
        self.throttle_raw_max_entry = ttk.Entry(
            self.throttle_controls_frame, textvariable=self.throttle_raw_max_var, width=10
        )
        self.throttle_raw_max_entry.grid(row=1, column=1, padx=5)

        throttle_set_max_btn = ttk.Button(self.throttle_controls_frame, text="Set Max", command=self._set_throttle_max)
        throttle_set_max_btn.grid(row=1, column=2)
        ToolTip(throttle_set_max_btn, "Set throttle raw_max to the current sensor reading.\nPress pedal firmly, then click.")

        # Throttle Settings
        ttk.Label(self.throttle_controls_frame, text="Curve:").grid(row=2, column=0, sticky=tk.W, pady=(5, 0))
        self.throttle_curve_var = tk.StringVar(value=self.DEFAULTS["throttle_curve"])
        throttle_curve_combo = ttk.Combobox(
            self.throttle_controls_frame,
            textvariable=self.throttle_curve_var,
            values=self.CURVES,
            state="readonly",
            width=12,
        )
        throttle_curve_combo.grid(row=2, column=1, sticky=tk.W, pady=(5, 0))
        ToolTip(throttle_curve_combo, "Select throttle response curve type.\n• Linear: 1:1 mapping\n• Progressive: exponential rise\n• Aggressive: fast initial response\n• Custom: editable control points")
        throttle_preview_btn = ttk.Button(self.throttle_controls_frame, text="Preview", width=7,
                    command=lambda: self._show_curve_dialog("throttle"))
        throttle_preview_btn.grid(row=2, column=2, padx=(5, 0), pady=(5, 0))
        ToolTip(throttle_preview_btn, "Open interactive curve editor for throttle.")

        ttk.Label(self.throttle_controls_frame, text="Smoothing:").grid(row=3, column=0, sticky=tk.W)
        self.throttle_smoothing_var = tk.DoubleVar(value=self.DEFAULTS["throttle_smoothing"])
        throttle_smoothing_scale = ttk.Scale(
            self.throttle_controls_frame,
            from_=0.0,
            to=0.95,
            variable=self.throttle_smoothing_var,
            orient=tk.HORIZONTAL,
        )
        throttle_smoothing_scale.grid(row=3, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(throttle_smoothing_scale, "Throttle signal smoothing. 0 = no filter, higher = more smoothing.\nHigher values reduce noise but add latency.")
        self.throttle_smoothing_label = ttk.Label(
            self.throttle_controls_frame, text=f"α = {self.DEFAULTS['throttle_smoothing']:.2f}"
        )
        self.throttle_smoothing_label.grid(row=4, column=0, columnspan=3, sticky=tk.W)
        self.throttle_smoothing_var.trace_add("write", self._update_throttle_smoothing_label)

        ttk.Label(self.throttle_controls_frame, text="Deadzone:").grid(row=5, column=0, sticky=tk.W)
        self.throttle_deadzone_var = tk.IntVar(value=self.DEFAULTS["throttle_deadzone"])
        throttle_deadzone_scale = ttk.Scale(
            self.throttle_controls_frame,
            from_=0,
            to=1000,
            variable=self.throttle_deadzone_var,
            orient=tk.HORIZONTAL,
        )
        throttle_deadzone_scale.grid(row=5, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(throttle_deadzone_scale, "Ignore small throttle inputs near zero.\nEliminates drift from sensor noise at rest.")

        self.throttle_invert_var = tk.BooleanVar(value=self.DEFAULTS["throttle_invert"])
        throttle_invert_cb = ttk.Checkbutton(self.throttle_controls_frame, text="Invert", variable=self.throttle_invert_var)
        throttle_invert_cb.grid(row=6, column=0, columnspan=3, sticky=tk.W)
        ToolTip(throttle_invert_cb, "Invert throttle axis. Use if pedal reads 100% when released.")

        ttk.Label(self.throttle_controls_frame, text="Saturation:").grid(row=7, column=0, sticky=tk.W)
        self.throttle_saturation_var = tk.DoubleVar(value=self.DEFAULTS["throttle_saturation"])
        throttle_saturation_scale = ttk.Scale(
            self.throttle_controls_frame,
            from_=0.1,
            to=1.0,
            variable=self.throttle_saturation_var,
            orient=tk.HORIZONTAL,
        )
        throttle_saturation_scale.grid(row=7, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(throttle_saturation_scale, "Scale the effective max sensor value.\nLower values let you reach 100% with less pedal travel.")
        self.throttle_saturation_label = ttk.Label(
            self.throttle_controls_frame, text=f"{self.DEFAULTS['throttle_saturation']:.0%}"
        )
        self.throttle_saturation_label.grid(row=8, column=0, columnspan=3, sticky=tk.W)
        self.throttle_saturation_var.trace_add("write", self._update_throttle_saturation_label)

        ttk.Label(self.throttle_controls_frame, text="Bite Point:").grid(row=9, column=0, sticky=tk.W)
        self.throttle_bite_point_var = tk.DoubleVar(value=self.DEFAULTS["throttle_bite_point"])
        throttle_bite_point_scale = ttk.Scale(
            self.throttle_controls_frame,
            from_=0.0,
            to=0.5,
            variable=self.throttle_bite_point_var,
            orient=tk.HORIZONTAL,
        )
        throttle_bite_point_scale.grid(row=9, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(throttle_bite_point_scale, "Dead-travel zone simulating pedal free-play.\nOutput stays at 0% until force exceeds this threshold.")
        self.throttle_bite_point_label = ttk.Label(
            self.throttle_controls_frame, text=f"{self.DEFAULTS['throttle_bite_point']:.0%}"
        )
        self.throttle_bite_point_label.grid(row=10, column=0, columnspan=3, sticky=tk.W)
        self.throttle_bite_point_var.trace_add("write", self._update_throttle_bite_point_label)

        # Throttle curve power (hidden from main UI, accessible via Preview dialog)
        self.throttle_progressive_power_var = tk.DoubleVar(value=self.DEFAULTS["throttle_progressive_power"])
        self.throttle_aggressive_power_var = tk.DoubleVar(value=self.DEFAULTS["throttle_aggressive_power"])
        self.throttle_curve_points_var = [[0.0, 0.0], [1.0, 1.0]]

        # Brake section (bottom)
        brake_frame = ttk.LabelFrame(right, text="Brake", padding=5)
        brake_frame.pack(fill=tk.X, pady=(0, 5))

        # Brake Manual Calibration
        ttk.Label(brake_frame, text="Raw Min:").grid(row=0, column=0, sticky=tk.W)
        self.raw_min_var = tk.IntVar(value=self.DEFAULTS["raw_min"])
        self.raw_min_entry = ttk.Entry(
            brake_frame, textvariable=self.raw_min_var, width=10
        )
        self.raw_min_entry.grid(row=0, column=1, padx=5)

        set_min_btn = ttk.Button(brake_frame, text="Set Min", command=self._set_min)
        set_min_btn.grid(row=0, column=2)
        ToolTip(set_min_btn, "Set raw_min to the current sensor reading.\nRelease the pedal completely, then click.")

        ttk.Label(brake_frame, text="Raw Max:").grid(row=1, column=0, sticky=tk.W)
        self.raw_max_var = tk.IntVar(value=self.DEFAULTS["raw_max"])
        self.raw_max_entry = ttk.Entry(
            brake_frame, textvariable=self.raw_max_var, width=10
        )
        self.raw_max_entry.grid(row=1, column=1, padx=5)

        set_max_btn = ttk.Button(brake_frame, text="Set Max", command=self._set_max)
        set_max_btn.grid(row=1, column=2)
        ToolTip(set_max_btn, "Set raw_max to the current sensor reading.\nPress the pedal firmly, then click.")

        # Brake Settings
        ttk.Label(brake_frame, text="Curve:").grid(row=2, column=0, sticky=tk.W, pady=(5, 0))
        self.curve_var = tk.StringVar(value=self.DEFAULTS["curve"])
        curve_combo = ttk.Combobox(
            brake_frame,
            textvariable=self.curve_var,
            values=self.CURVES,
            state="readonly",
            width=12,
        )
        curve_combo.grid(row=2, column=1, sticky=tk.W, pady=(5, 0))
        ToolTip(curve_combo, "Select brake response curve type.\n• Linear: 1:1 mapping\n• Progressive: exponential rise (trail braking friendly)\n• Aggressive: fast initial bite\n• Custom: editable control points")
        brake_preview_btn = ttk.Button(brake_frame, text="Preview", width=7,
                    command=lambda: self._show_curve_dialog("brake"))
        brake_preview_btn.grid(row=2, column=2, padx=(5, 0), pady=(5, 0))
        ToolTip(brake_preview_btn, "Open interactive curve editor for brake.")

        ttk.Label(brake_frame, text="Smoothing:").grid(row=3, column=0, sticky=tk.W)
        self.smoothing_var = tk.DoubleVar(value=self.DEFAULTS["smoothing"])
        smoothing_scale = ttk.Scale(
            brake_frame,
            from_=0.0,
            to=0.95,
            variable=self.smoothing_var,
            orient=tk.HORIZONTAL,
        )
        smoothing_scale.grid(row=3, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(smoothing_scale, "Brake signal smoothing. 0 = no filter, higher = more smoothing.\nHigher values reduce noise but add latency.")
        self.smoothing_label = ttk.Label(
            brake_frame, text=f"α = {self.DEFAULTS['smoothing']:.2f}"
        )
        self.smoothing_label.grid(row=4, column=0, columnspan=3, sticky=tk.W)
        self.smoothing_var.trace_add("write", self._update_smoothing_label)

        ttk.Label(brake_frame, text="Deadzone:").grid(row=5, column=0, sticky=tk.W)
        self.deadzone_var = tk.IntVar(value=self.DEFAULTS["deadzone"])
        deadzone_scale = ttk.Scale(
            brake_frame,
            from_=0,
            to=1000,
            variable=self.deadzone_var,
            orient=tk.HORIZONTAL,
        )
        deadzone_scale.grid(row=5, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(deadzone_scale, "Ignore small brake inputs near zero.\nEliminates drift from sensor noise at rest.")

        ttk.Label(brake_frame, text="Saturation:").grid(row=6, column=0, sticky=tk.W)
        self.saturation_var = tk.DoubleVar(value=self.DEFAULTS["saturation"])
        saturation_scale = ttk.Scale(
            brake_frame,
            from_=0.1,
            to=1.0,
            variable=self.saturation_var,
            orient=tk.HORIZONTAL,
        )
        saturation_scale.grid(row=6, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(saturation_scale, "Scale the effective max sensor value.\nLower values let you reach 100% brake with less physical force.\nE.g. 80% = 80% of max pressure = full brake in-game.")
        self.saturation_label = ttk.Label(
            brake_frame, text=f"{self.DEFAULTS['saturation']:.0%}"
        )
        self.saturation_label.grid(row=7, column=0, columnspan=3, sticky=tk.W)
        self.saturation_var.trace_add("write", self._update_saturation_label)

        ttk.Label(brake_frame, text="Bite Point:").grid(row=8, column=0, sticky=tk.W)
        self.bite_point_var = tk.DoubleVar(value=self.DEFAULTS["bite_point"])
        bite_point_scale = ttk.Scale(
            brake_frame,
            from_=0.0,
            to=0.5,
            variable=self.bite_point_var,
            orient=tk.HORIZONTAL,
        )
        bite_point_scale.grid(row=8, column=1, columnspan=2, sticky=tk.EW)
        ToolTip(bite_point_scale, "Dead-travel zone simulating pad-to-rotor gap.\nBelow this threshold, brake output stays at 0%.\nMimics the initial free travel before brakes engage.")
        self.bite_point_label = ttk.Label(
            brake_frame, text=f"{self.DEFAULTS['bite_point']:.0%}"
        )
        self.bite_point_label.grid(row=9, column=0, columnspan=3, sticky=tk.W)
        self.bite_point_var.trace_add("write", self._update_bite_point_label)

        ttk.Label(brake_frame, text="Oversample:").grid(row=10, column=0, sticky=tk.W)
        self.oversample_var = tk.IntVar(value=self.DEFAULTS["oversample"])
        oversample_combo = ttk.Combobox(
            brake_frame,
            textvariable=self.oversample_var,
            values=[str(x) for x in self.OVERSAMPLE_OPTIONS],
            state="readonly",
            width=10,
        )
        oversample_combo.grid(row=10, column=1, columnspan=2, sticky=tk.W, pady=(0, 5))
        ToolTip(oversample_combo, "Number of ADC samples averaged per reading.\nHigher = less noise but slower response.\n16 is a good default.")

        self.invert_var = tk.BooleanVar(value=self.DEFAULTS["invert"])
        invert_cb = ttk.Checkbutton(brake_frame, text="Invert", variable=self.invert_var)
        invert_cb.grid(row=11, column=0, columnspan=3, sticky=tk.W)
        ToolTip(invert_cb, "Invert brake axis. Use if pedal reads 100% when released.")

        # Brake curve power (hidden from main UI, accessible via Preview dialog)
        self.brake_progressive_power_var = tk.DoubleVar(value=self.DEFAULTS["progressive_power"])
        self.brake_aggressive_power_var = tk.DoubleVar(value=self.DEFAULTS["aggressive_power"])
        self.brake_curve_points_var = [[0.0, 0.0], [1.0, 1.0]]

        # Save button
        self.save_btn = ttk.Button(
            right, text="Save to Pico", command=self._save_calibration
        )
        self.save_btn.pack(fill=tk.X, pady=(5, 0))
        ToolTip(self.save_btn, "Write calibration.json to CIRCUITPY drive.\nPress RESET on Pico afterwards to apply changes.")

        # Status bar
        self.status_var = tk.StringVar(value="Initializing...")
        status_bar = ttk.Label(
            self, textvariable=self.status_var, relief=tk.SUNKEN, anchor=tk.W
        )
        status_bar.pack(side=tk.BOTTOM, fill=tk.X)

        # Initial throttle state
        self._on_throttle_toggle()

    def _on_throttle_toggle(self):
        """Handle throttle enable/disable toggle."""
        enabled = self.throttle_enabled_var.get()

        # Show/hide throttle controls
        if enabled:
            self.throttle_controls_frame.pack(fill=tk.X, pady=(5, 0))
            self.throttle_raw_label.pack(side=tk.LEFT, padx=5)
            self.throttle_norm_label.pack(side=tk.LEFT, padx=5)
            self.left_frame.config(text="Live Data")
            self.title("Brake & Throttle Calibrator")
        else:
            self.throttle_controls_frame.pack_forget()
            self.throttle_raw_label.pack_forget()
            self.throttle_norm_label.pack_forget()
            self.left_frame.config(text="Live Brake Pressure")
            self.title("Brake Controller Calibrator")
            # Clear throttle histories when disabled
            self.throttle_raw_history.clear()
            self.throttle_history.clear()
            self.throttle_preview_history.clear()

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

    def _update_throttle_smoothing_label(self, *args):
        try:
            self.throttle_smoothing_label.config(text=f"α = {self.throttle_smoothing_var.get():.2f}")
        except Exception:
            pass

    def _update_saturation_label(self, *args):
        try:
            self.saturation_label.config(text=f"{self.saturation_var.get():.0%}")
        except Exception:
            pass

    def _update_bite_point_label(self, *args):
        try:
            self.bite_point_label.config(text=f"{self.bite_point_var.get():.0%}")
        except Exception:
            pass

    def _update_throttle_saturation_label(self, *args):
        try:
            self.throttle_saturation_label.config(text=f"{self.throttle_saturation_var.get():.0%}")
        except Exception:
            pass

    def _update_throttle_bite_point_label(self, *args):
        try:
            self.throttle_bite_point_label.config(text=f"{self.throttle_bite_point_var.get():.0%}")
        except Exception:
            pass

    def _show_curve_dialog(self, target):
        """Show interactive curve editor popup with drag-to-edit control points."""
        dialog = tk.Toplevel(self)
        dialog.title(f"Curve Editor — {target.capitalize()}")
        dialog.transient(self)
        dialog.resizable(False, False)

        if target == "brake":
            curve_var = self.curve_var
            prog_var = tk.DoubleVar(value=self.brake_progressive_power_var.get())
            aggr_var = tk.DoubleVar(value=self.brake_aggressive_power_var.get())
            initial_points = list(self.brake_curve_points_var or [[0.0, 0.0], [1.0, 1.0]])
        else:
            curve_var = self.throttle_curve_var
            prog_var = tk.DoubleVar(value=self.throttle_progressive_power_var.get())
            aggr_var = tk.DoubleVar(value=self.throttle_aggressive_power_var.get())
            initial_points = list(self.throttle_curve_points_var or [[0.0, 0.0], [1.0, 1.0]])

        # Deep copy initial points (mutable list for editing)
        points = [[p[0], p[1]] for p in initial_points]

        # Canvas dimensions
        cw, ch = 500, 400
        pad = 40
        pw = cw - 2 * pad
        ph = ch - 2 * pad

        # Curve type selector + preset buttons
        top_frame = ttk.Frame(dialog, padding=5)
        top_frame.pack(fill=tk.X, padx=10, pady=(10, 0))

        ttk.Label(top_frame, text="Curve:").pack(side=tk.LEFT)
        dialog_curve_var = tk.StringVar(value=curve_var.get())
        curve_combo = ttk.Combobox(
            top_frame, textvariable=dialog_curve_var,
            values=self.CURVES, state="readonly", width=10,
        )
        curve_combo.pack(side=tk.LEFT, padx=5)

        def on_dialog_curve_change(*_):
            val = dialog_curve_var.get()
            curve_var.set(val)
            if val in self.CURVE_PRESETS and val != "custom":
                points[:] = [[p[0], p[1]] for p in self.CURVE_PRESETS[val]]
                redraw()
        dialog_curve_var.trace_add("write", on_dialog_curve_change)

        ttk.Separator(top_frame, orient=tk.VERTICAL).pack(side=tk.LEFT, fill=tk.Y, padx=10)
        ttk.Label(top_frame, text="Presets:").pack(side=tk.LEFT)

        for preset_name in ["linear", "progressive", "aggressive", "S-curve"]:
            def make_preset_cb(name=preset_name):
                def cb():
                    pts = self.CURVE_PRESETS[name]
                    points[:] = [[pp[0], pp[1]] for pp in pts]
                    if name in ("linear", "progressive", "aggressive"):
                        dialog_curve_var.set(name)
                    else:
                        dialog_curve_var.set("custom")
                    redraw()
                return cb
            ttk.Button(top_frame, text=preset_name.capitalize(), command=make_preset_cb(), width=10).pack(side=tk.LEFT, padx=2)

        # Canvas
        canvas = tk.Canvas(dialog, bg="#1a1a1a", width=cw, height=ch, highlightthickness=0)
        canvas.pack(padx=10, pady=5)

        # Info label
        ttk.Label(
            dialog,
            text="Drag points to edit · Click empty space to add · Right-click point to delete",
            font=("Consolas", 9),
        ).pack(padx=10)

        # Strength sliders (for progressive/aggressive)
        slider_frame = ttk.Frame(dialog, padding=5)
        slider_frame.pack(fill=tk.X, padx=10)

        prog_label = ttk.Label(slider_frame, text=f"Progressive power: {prog_var.get():.1f}")
        prog_label.pack(anchor=tk.W)
        ttk.Scale(slider_frame, from_=1.1, to=5.0, variable=prog_var, orient=tk.HORIZONTAL).pack(fill=tk.X)

        aggr_label = ttk.Label(slider_frame, text=f"Aggressive power: {aggr_var.get():.1f}")
        aggr_label.pack(anchor=tk.W, pady=(5, 0))
        ttk.Scale(slider_frame, from_=1.1, to=5.0, variable=aggr_var, orient=tk.HORIZONTAL).pack(fill=tk.X)

        def update_slider_labels(*_):
            try:
                prog_label.config(text=f"Progressive power: {prog_var.get():.1f}")
                aggr_label.config(text=f"Aggressive power: {aggr_var.get():.1f}")
            except Exception:
                pass
        prog_var.trace_add("write", update_slider_labels)
        aggr_var.trace_add("write", update_slider_labels)

        # Apply / Cancel buttons
        def apply_and_close():
            if target == "brake":
                self.brake_progressive_power_var.set(round(prog_var.get(), 1))
                self.brake_aggressive_power_var.set(round(aggr_var.get(), 1))
                self.brake_curve_points_var = [[p[0], p[1]] for p in points]
            else:
                self.throttle_progressive_power_var.set(round(prog_var.get(), 1))
                self.throttle_aggressive_power_var.set(round(aggr_var.get(), 1))
                self.throttle_curve_points_var = [[p[0], p[1]] for p in points]
            dialog.destroy()

        btn_frame = ttk.Frame(dialog, padding=10)
        btn_frame.pack()
        ttk.Button(btn_frame, text="Apply", command=apply_and_close, width=10).pack(side=tk.LEFT, padx=5)
        ttk.Button(btn_frame, text="Cancel", command=dialog.destroy, width=10).pack(side=tk.LEFT, padx=5)

        # Coordinate helpers
        def to_canvas(px, py):
            x = pad + int(pw * px)
            y = (ch - pad) - int(ph * py)
            return x, y

        def from_canvas(cx, cy):
            px = (cx - pad) / pw
            py = ((ch - pad) - cy) / ph
            return max(0.0, min(1.0, px)), max(0.0, min(1.0, py))

        # Drag state
        drag_index = [None]

        def redraw(*_):
            canvas.delete("all")

            # Axes
            canvas.create_line(pad, pad, pad, ch - pad, fill="#888888", width=1)
            canvas.create_line(pad, ch - pad, cw - pad, ch - pad, fill="#888888", width=1)

            # Axis labels
            canvas.create_text(pad - 5, pad, text="1.0", fill="#888888", anchor=tk.E, font=("Consolas", 8))
            canvas.create_text(pad - 5, ch - pad, text="0.0", fill="#888888", anchor=tk.E, font=("Consolas", 8))
            canvas.create_text(pad, ch - pad + 12, text="0", fill="#888888", anchor=tk.W, font=("Consolas", 8))
            canvas.create_text(cw - pad, ch - pad + 12, text="1.0", fill="#888888", anchor=tk.E, font=("Consolas", 8))

            # Grid
            for i in range(1, 4):
                y = pad + int(ph * i / 4)
                canvas.create_line(pad, y, cw - pad, y, fill="#333333", dash=(2, 4))
                val = 1.0 - i / 4
                canvas.create_text(pad - 5, y, text=f"{val:.1f}", fill="#666666", anchor=tk.E, font=("Consolas", 7))
            for i in range(1, 4):
                x = pad + int(pw * i / 4)
                canvas.create_line(x, pad, x, ch - pad, fill="#333333", dash=(2, 4))
                val = i / 4
                canvas.create_text(x, ch - pad + 12, text=f"{val:.1f}", fill="#666666", anchor=tk.N, font=("Consolas", 7))

            # Diagonal reference line (linear)
            lx0, ly0 = to_canvas(0, 0)
            lx1, ly1 = to_canvas(1, 1)
            canvas.create_line(lx0, ly0, lx1, ly1, fill="#333355", width=1, dash=(4, 4))

            # Draw the curve based on current type
            current_curve = dialog_curve_var.get()
            if current_curve == "custom":
                if len(points) >= 2:
                    coords = []
                    for p in points:
                        cx, cy = to_canvas(p[0], p[1])
                        coords.extend([cx, cy])
                    canvas.create_line(*coords, fill="#44ff44", width=3, smooth=False)
            elif current_curve == "linear":
                lx0, ly0 = to_canvas(0, 0)
                lx1, ly1 = to_canvas(1, 1)
                canvas.create_line(lx0, ly0, lx1, ly1, fill="#6688ff", width=3)
            elif current_curve == "progressive":
                pp = prog_var.get()
                coords = []
                for i in range(60):
                    t = i / 59
                    v = t ** pp
                    cx, cy = to_canvas(t, v)
                    coords.extend([cx, cy])
                canvas.create_line(*coords, fill="#44ff44", width=3, smooth=False)
            elif current_curve == "aggressive":
                ap = aggr_var.get()
                coords = []
                for i in range(60):
                    t = i / 59
                    v = t ** (1.0 / ap)
                    cx, cy = to_canvas(t, v)
                    coords.extend([cx, cy])
                canvas.create_line(*coords, fill="#ff4444", width=3, smooth=False)

            # Draw control points (always visible)
            for i, p in enumerate(points):
                cx, cy = to_canvas(p[0], p[1])
                r = 6
                color = "#ffaa00" if (i == 0 or i == len(points) - 1) else "#ffffff"
                canvas.create_oval(cx - r, cy - r, cx + r, cy + r, fill=color, outline="#888888", width=1)

            # Point count
            canvas.create_text(cw - pad - 5, pad + 5, text=f"{len(points)} pts", fill="#666666",
                              anchor=tk.NE, font=("Consolas", 8))

        # Mouse interaction
        def on_press(event):
            best_dist = 15
            best_idx = None
            for i, p in enumerate(points):
                cx, cy = to_canvas(p[0], p[1])
                dist = ((event.x - cx) ** 2 + (event.y - cy) ** 2) ** 0.5
                if dist < best_dist:
                    best_dist = dist
                    best_idx = i
            if best_idx is not None:
                drag_index[0] = best_idx
            else:
                # Add new point
                px, py = from_canvas(event.x, event.y)
                if px > 0.01 and px < 0.99:
                    points.append([px, py])
                    points.sort(key=lambda p: p[0])
                    dialog_curve_var.set("custom")
                    redraw()

        def on_motion(event):
            if drag_index[0] is not None:
                i = drag_index[0]
                px, py = from_canvas(event.x, event.y)
                if i == 0:
                    px = 0.0
                elif i == len(points) - 1:
                    px = 1.0
                else:
                    min_x = points[i - 1][0] + 0.01
                    max_x = points[i + 1][0] - 0.01
                    px = max(min_x, min(max_x, px))
                py = max(0.0, min(1.0, py))
                points[i] = [px, py]
                if dialog_curve_var.get() != "custom":
                    dialog_curve_var.set("custom")
                redraw()

        def on_release(event):
            drag_index[0] = None

        def on_right_click(event):
            best_dist = 15
            best_idx = None
            for i, p in enumerate(points):
                if i == 0 or i == len(points) - 1:
                    continue
                cx, cy = to_canvas(p[0], p[1])
                dist = ((event.x - cx) ** 2 + (event.y - cy) ** 2) ** 0.5
                if dist < best_dist:
                    best_dist = dist
                    best_idx = i
            if best_idx is not None:
                points.pop(best_idx)
                dialog_curve_var.set("custom")
                redraw()

        canvas.bind("<ButtonPress-1>", on_press)
        canvas.bind("<B1-Motion>", on_motion)
        canvas.bind("<ButtonRelease-1>", on_release)
        canvas.bind("<ButtonPress-3>", on_right_click)

        # Redraw on slider/curve changes
        prog_var.trace_add("write", redraw)
        aggr_var.trace_add("write", redraw)
        curve_var.trace_add("write", redraw)
        redraw()

        # Center on parent
        dialog.update_idletasks()
        x = self.winfo_x() + (self.winfo_width() - dialog.winfo_width()) // 2
        y = self.winfo_y() + (self.winfo_height() - dialog.winfo_height()) // 2
        dialog.geometry(f"+{x}+{y}")

    @staticmethod
    def _interpolate_custom(t, points):
        """Piecewise-linear interpolation through control points."""
        if not points or len(points) < 2:
            return t
        if t <= points[0][0]:
            return points[0][1]
        if t >= points[-1][0]:
            return points[-1][1]
        for i in range(len(points) - 1):
            x0, y0 = points[i]
            x1, y1 = points[i + 1]
            if x0 <= t <= x1:
                if x1 == x0:
                    return y0
                frac = (t - x0) / (x1 - x0)
                return y0 + frac * (y1 - y0)
        return t

    def _compute_preview(self, raw_01):
        """Apply the current slider settings to a raw ADC value (0-1).

        Mirrors the firmware pipeline: clamp → normalize → deadzone → curve → EMA.
        Returns the previewed value (0-1).
        """
        raw_min = self.raw_min_var.get()
        raw_max = self.raw_max_var.get()
        saturation = self.saturation_var.get()
        raw_max_eff = raw_min + (raw_max - raw_min) * min(saturation, 1.0)

        # Convert 0-1 back to raw ADC integer range
        raw_int = int(raw_01 * 65535)

        # Clamp
        clamped = max(raw_min, min(raw_max_eff, raw_int))

        # Normalize
        if raw_max_eff == raw_min:
            normalized = 0.0
        else:
            normalized = (clamped - raw_min) / (raw_max_eff - raw_min)

        # Deadzone
        dz = self.deadzone_var.get()
        deadzone_frac = dz / (raw_max - raw_min) if raw_max != raw_min else 0.0
        if normalized < deadzone_frac:
            normalized = 0.0
        elif deadzone_frac > 0:
            normalized = (normalized - deadzone_frac) / (1.0 - deadzone_frac)

        # Bite point (dead-travel)
        bite = self.bite_point_var.get()
        if bite > 0.0:
            if normalized < bite:
                normalized = 0.0
            else:
                normalized = (normalized - bite) / (1.0 - bite)

        # Curve
        curve = self.curve_var.get()
        if curve == "linear":
            pass
        elif curve == "progressive":
            power = self.brake_progressive_power_var.get()
            normalized = normalized ** power
        elif curve == "aggressive":
            power = self.brake_aggressive_power_var.get()
            normalized = normalized ** (1.0 / power)
        elif curve == "custom":
            if self.brake_curve_points_var and len(self.brake_curve_points_var) >= 2:
                normalized = self._interpolate_custom(normalized, self.brake_curve_points_var)

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

    def _compute_throttle_preview(self, raw_01):
        """Apply the current throttle slider settings to a raw ADC value (0-1).

        Mirrors the firmware pipeline: clamp → normalize → deadzone → curve → EMA.
        Returns the previewed value (0-1).
        """
        raw_min = self.throttle_raw_min_var.get()
        raw_max = self.throttle_raw_max_var.get()
        saturation = self.throttle_saturation_var.get()
        raw_max_eff = raw_min + (raw_max - raw_min) * min(saturation, 1.0)

        # Convert 0-1 back to raw ADC integer range
        raw_int = int(raw_01 * 65535)

        # Clamp
        clamped = max(raw_min, min(raw_max_eff, raw_int))

        # Normalize
        if raw_max_eff == raw_min:
            normalized = 0.0
        else:
            normalized = (clamped - raw_min) / (raw_max_eff - raw_min)

        # Deadzone
        dz = self.throttle_deadzone_var.get()
        deadzone_frac = dz / (raw_max - raw_min) if raw_max != raw_min else 0.0
        if normalized < deadzone_frac:
            normalized = 0.0
        elif deadzone_frac > 0:
            normalized = (normalized - deadzone_frac) / (1.0 - deadzone_frac)

        # Bite point (dead-travel)
        bite = self.throttle_bite_point_var.get()
        if bite > 0.0:
            if normalized < bite:
                normalized = 0.0
            else:
                normalized = (normalized - bite) / (1.0 - bite)

        # Curve
        curve = self.throttle_curve_var.get()
        if curve == "linear":
            pass
        elif curve == "progressive":
            power = self.throttle_progressive_power_var.get()
            normalized = normalized ** power
        elif curve == "aggressive":
            power = self.throttle_aggressive_power_var.get()
            normalized = normalized ** (1.0 / power)
        elif curve == "custom":
            if self.throttle_curve_points_var and len(self.throttle_curve_points_var) >= 2:
                normalized = self._interpolate_custom(normalized, self.throttle_curve_points_var)

        # EMA smoothing (smoothing 0 = none, 0.95 = max; alpha = 1 - smoothing)
        alpha = 1.0 - min(self.throttle_smoothing_var.get(), 0.95)
        if not self._throttle_preview_ema_init:
            self._throttle_preview_ema = normalized
            self._throttle_preview_ema_init = True
        else:
            self._throttle_preview_ema = alpha * normalized + (1.0 - alpha) * self._throttle_preview_ema

        # Invert
        if self.throttle_invert_var.get():
            return 1.0 - self._throttle_preview_ema
        return self._throttle_preview_ema

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

    def _set_throttle_min(self):
        """Set throttle_raw_min to current raw throttle ADC value."""
        raw = self.reader.read_throttle_raw_int()
        if raw is not None:
            self.throttle_raw_min_var.set(raw)

    def _set_throttle_max(self):
        """Set throttle_raw_max to current raw throttle ADC value."""
        raw = self.reader.read_throttle_raw_int()
        if raw is not None:
            self.throttle_raw_max_var.set(raw)

    def _start_auto_cal(self):
        """Begin auto-calibration flow."""
        # If throttle is enabled, ask which axis to calibrate
        if self.throttle_enabled_var.get():
            self._show_cal_target_dialog()
        else:
            # Just calibrate brake as before
            self.cal_target.set(0)
            self._show_auto_cal_instructions()

    def _show_cal_target_dialog(self):
        """Show dialog to select brake or throttle calibration."""
        dialog = tk.Toplevel(self)
        dialog.title("Select Calibration Target")
        dialog.transient(self)
        dialog.grab_set()
        dialog.resizable(False, False)

        ttk.Label(dialog, text="Calibrate Brake or Throttle?", padding=10).pack()

        btn_frame = ttk.Frame(dialog, padding=10)
        btn_frame.pack()

        def select_brake():
            self.cal_target.set(0)
            dialog.destroy()
            self._show_auto_cal_instructions()

        def select_throttle():
            self.cal_target.set(1)
            dialog.destroy()
            self._show_auto_cal_instructions()

        ttk.Button(btn_frame, text="Brake", command=select_brake, width=12).pack(side=tk.LEFT, padx=5)
        ttk.Button(btn_frame, text="Throttle", command=select_throttle, width=12).pack(side=tk.LEFT, padx=5)

        dialog.update_idletasks()
        x = self.winfo_x() + (self.winfo_width() - dialog.winfo_width()) // 2
        y = self.winfo_y() + (self.winfo_height() - dialog.winfo_height()) // 2
        dialog.geometry(f"+{x}+{y}")

    def _show_auto_cal_instructions(self):
        """Show auto-calibration instructions and start countdown."""
        target = "brake" if self.cal_target.get() == 0 else "throttle"
        
        result = messagebox.showinfo(
            "Auto Calibrate",
            f"Press OK, then release the {target} completely.\n\n"
            f"After the countdown, press and release the {target}\n"
            f"firmly within 5 seconds.",
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

        target = "brake" if self.cal_target.get() == 0 else "throttle"

        if self.cal_phase == "countdown":
            remaining = 3 - int(elapsed)
            if remaining > 0:
                self.auto_cal_label.config(
                    text=f"Release {target} completely...\n{remaining}..."
                )
            else:
                # Switch to capture phase
                self.cal_phase = "capture"
                self.capture_start = time.monotonic()
                self.auto_cal_label.config(
                    text=f"Capturing... press and release {target} now!"
                )

        elif self.cal_phase == "capture":
            # Read raw ADC value for calibration based on target
            if self.cal_target.get() == 0:
                # Calibrating brake: use axis 1 (raw brake)
                current_raw = self.reader.read_raw_adc_int()
            else:
                # Calibrating throttle: use axis 3 (raw throttle)
                current_raw = self.reader.read_throttle_raw_int()
            
            if current_raw is not None:
                self.capture_samples.append(current_raw)

            remaining = 5 - int(elapsed)
            if remaining > 0:
                self.auto_cal_label.config(
                    text=f"Capturing... press and release {target}!\n{remaining}s remaining"
                )
            else:
                # Capture done — compute min and max
                if self.capture_samples:
                    captured_min = min(self.capture_samples)
                    captured_max = max(self.capture_samples)
                    
                    if self.cal_target.get() == 0:
                        # Brake calibration
                        self.raw_min_var.set(captured_min)
                        self.raw_max_var.set(captured_max)
                    else:
                        # Throttle calibration
                        self.throttle_raw_min_var.set(captured_min)
                        self.throttle_raw_max_var.set(captured_max)
                    
                    self.auto_cal_label.config(
                        text=f"Done! Min={captured_min}, Max={captured_max}\nTweak if needed, then Save."
                    )
                else:
                    self.auto_cal_label.config(text="No data captured. Try again.")

                self.auto_calibrating = False
                self.cal_phase = "done"
                self.auto_cal_btn.config(state=tk.NORMAL)

    def _get_profiles_dir(self):
        """Return the profiles directory path on CIRCUITPY."""
        if not self.circuitpy_drive:
            return None
        return os.path.join(self.circuitpy_drive, "profiles")

    def _refresh_profile_list(self):
        """Scan profiles directory and update the dropdown."""
        profiles = []
        profiles_dir = self._get_profiles_dir()
        if profiles_dir and os.path.isdir(profiles_dir):
            for f in sorted(os.listdir(profiles_dir)):
                if f.endswith(".json"):
                    profiles.append(f[:-5])
        self.profile_combo["values"] = profiles
        if profiles:
            self.profile_var.set(profiles[0])
        else:
            self.profile_var.set("")

    def _build_cal_dict(self):
        """Build calibration dict from current GUI settings."""
        return {
            "raw_min": self.raw_min_var.get(),
            "raw_max": self.raw_max_var.get(),
            "deadzone": self.deadzone_var.get(),
            "curve": self.curve_var.get(),
            "progressive_power": round(self.brake_progressive_power_var.get(), 1),
            "aggressive_power": round(self.brake_aggressive_power_var.get(), 1),
            "smoothing": round(self.smoothing_var.get(), 2),
            "invert": self.invert_var.get(),
            "oversample": int(self.oversample_var.get()),
            "saturation": round(self.saturation_var.get(), 2),
            "bite_point": round(self.bite_point_var.get(), 2),
            "curve_points": self.brake_curve_points_var if self.brake_curve_points_var else [[0.0, 0.0], [1.0, 1.0]],
            "throttle_enabled": self.throttle_enabled_var.get(),
            "throttle_sensor": self.DEFAULTS["throttle_sensor"],
            "throttle_raw_min": self.throttle_raw_min_var.get(),
            "throttle_raw_max": self.throttle_raw_max_var.get(),
            "throttle_deadzone": self.throttle_deadzone_var.get(),
            "throttle_curve": self.throttle_curve_var.get(),
            "throttle_progressive_power": round(self.throttle_progressive_power_var.get(), 1),
            "throttle_aggressive_power": round(self.throttle_aggressive_power_var.get(), 1),
            "throttle_smoothing": round(self.throttle_smoothing_var.get(), 2),
            "throttle_invert": self.throttle_invert_var.get(),
            "throttle_saturation": round(self.throttle_saturation_var.get(), 2),
            "throttle_bite_point": round(self.throttle_bite_point_var.get(), 2),
            "throttle_curve_points": self.throttle_curve_points_var if self.throttle_curve_points_var else [[0.0, 0.0], [1.0, 1.0]],
        }

    def _apply_cal_dict(self, cal):
        """Apply a calibration dict to the GUI variables."""
        def safe_set(var, key, default=None, converter=None):
            val = cal.get(key, default)
            if val is not None:
                if converter:
                    val = converter(val)
                var.set(val)

        safe_set(self.raw_min_var, "raw_min", 2000, int)
        safe_set(self.raw_max_var, "raw_max", 56000, int)
        safe_set(self.deadzone_var, "deadzone", 300, int)
        safe_set(self.curve_var, "curve", "linear")
        safe_set(self.brake_progressive_power_var, "progressive_power", 2.0, float)
        safe_set(self.brake_aggressive_power_var, "aggressive_power", 2.0, float)
        safe_set(self.smoothing_var, "smoothing", 0.3, float)
        safe_set(self.invert_var, "invert", False)
        safe_set(self.oversample_var, "oversample", 16, int)
        safe_set(self.saturation_var, "saturation", 1.0, float)
        safe_set(self.bite_point_var, "bite_point", 0.0, float)
        self.brake_curve_points_var = cal.get("curve_points", [[0.0, 0.0], [1.0, 1.0]])

        safe_set(self.throttle_enabled_var, "throttle_enabled", False)
        safe_set(self.throttle_raw_min_var, "throttle_raw_min", 2000, int)
        safe_set(self.throttle_raw_max_var, "throttle_raw_max", 56000, int)
        safe_set(self.throttle_deadzone_var, "throttle_deadzone", 300, int)
        safe_set(self.throttle_curve_var, "throttle_curve", "linear")
        safe_set(self.throttle_progressive_power_var, "throttle_progressive_power", 2.0, float)
        safe_set(self.throttle_aggressive_power_var, "throttle_aggressive_power", 2.0, float)
        safe_set(self.throttle_smoothing_var, "throttle_smoothing", 0.2, float)
        safe_set(self.throttle_invert_var, "throttle_invert", False)
        safe_set(self.throttle_saturation_var, "throttle_saturation", 1.0, float)
        safe_set(self.throttle_bite_point_var, "throttle_bite_point", 0.0, float)
        self.throttle_curve_points_var = cal.get("throttle_curve_points", [[0.0, 0.0], [1.0, 1.0]])

        self._on_throttle_toggle()

    def _save_profile(self):
        """Save current settings as a named profile."""
        if not self.circuitpy_drive:
            messagebox.showerror("Error", "CIRCUITPY drive not found.")
            return

        name = simpledialog.askstring("Save Profile", "Profile name:", parent=self)
        if not name:
            return
        name = "".join(c for c in name if c.isalnum() or c in (" ", "-", "_")).strip()
        if not name:
            return

        profiles_dir = self._get_profiles_dir()
        os.makedirs(profiles_dir, exist_ok=True)

        cal = self._build_cal_dict()
        filepath = os.path.join(profiles_dir, f"{name}.json")
        try:
            with open(filepath, "w") as f:
                json.dump(cal, f, indent=2)
            messagebox.showinfo("Saved", f"Profile '{name}' saved.")
            self._refresh_profile_list()
            self.profile_var.set(name)
        except OSError as e:
            messagebox.showerror("Error", f"Could not save profile:\n{e}")

    def _load_profile(self):
        """Load a named profile and apply settings to the GUI."""
        name = self.profile_var.get()
        if not name:
            messagebox.showwarning("No Profile", "Select a profile to load.")
            return

        profiles_dir = self._get_profiles_dir()
        filepath = os.path.join(profiles_dir, f"{name}.json")

        try:
            with open(filepath, "r") as f:
                cal = json.load(f)
        except (OSError, ValueError) as e:
            messagebox.showerror("Error", f"Could not read profile:\n{e}")
            return

        self._apply_cal_dict(cal)
        messagebox.showinfo("Loaded", f"Profile '{name}' loaded. Save to Pico to activate.")

    def _delete_profile(self):
        """Delete the selected profile file."""
        name = self.profile_var.get()
        if not name:
            messagebox.showwarning("No Profile", "Select a profile to delete.")
            return

        if not messagebox.askyesno("Delete Profile", f"Delete profile '{name}'?"):
            return

        profiles_dir = self._get_profiles_dir()
        filepath = os.path.join(profiles_dir, f"{name}.json")
        try:
            os.remove(filepath)
            self._refresh_profile_list()
        except OSError as e:
            messagebox.showerror("Error", f"Could not delete profile:\n{e}")

    def _save_calibration(self):
        """Write calibration.json to the CIRCUITPY drive."""
        if not self.circuitpy_drive:
            messagebox.showerror(
                "Error", "CIRCUITPY drive not found. Is the Pico connected?"
            )
            return

        cal = self._build_cal_dict()

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

        throttle_enabled = self.throttle_enabled_var.get()

        # Draw grid lines
        for i in range(5):
            y = int(h * i / 4)
            self.canvas.create_line(0, y, w, y, fill="#333333", dash=(2, 4))

        # Draw legend
        legend_y = 12
        legend_x = 8
        
        # Brake legend items
        self.canvas.create_line(legend_x, legend_y, legend_x + 20, legend_y, fill="#4488ff", width=2)
        self.canvas.create_text(
            legend_x + 24,
            legend_y,
            text="Raw ADC",
            fill="#4488ff",
            anchor=tk.W,
            font=("Consolas", 9),
        )
        legend_x += 90
        
        self.canvas.create_line(legend_x, legend_y, legend_x + 20, legend_y, fill="#44ff44", width=2)
        self.canvas.create_text(
            legend_x + 24,
            legend_y,
            text="Preview",
            fill="#44ff44",
            anchor=tk.W,
            font=("Consolas", 9),
        )
        legend_x += 85
        
        self.canvas.create_line(legend_x, legend_y, legend_x + 20, legend_y, fill="#ff4444", width=2)
        self.canvas.create_text(
            legend_x + 24,
            legend_y,
            text="Game Input",
            fill="#ff4444",
            anchor=tk.W,
            font=("Consolas", 9),
        )
        
        if throttle_enabled:
            legend_x += 105
            self.canvas.create_line(legend_x, legend_y, legend_x + 20, legend_y, fill="#ff8800", width=2)
            self.canvas.create_text(
                legend_x + 24,
                legend_y,
                text="Throttle",
                fill="#ff8800",
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
        
        # Throttle line (orange) — only when enabled
        if throttle_enabled:
            draw_line(self.throttle_history, "#ff8800", 2)

    def _poll_loop(self):
        """Main polling loop — runs at ~30Hz."""
        # Read brake axes
        brake_val = self.reader.read_brake()  # Processed (calibrated, curved, smoothed)
        raw_val = self.reader.read_raw_adc()  # Raw ADC (0-1 normalized)
        raw_val_int = self.reader.read_raw_adc_int()

        # Read throttle axes
        throttle_val = self.reader.read_throttle()
        throttle_raw_val = self.reader.read_throttle_raw()
        throttle_raw_val_int = self.reader.read_throttle_raw_int()

        throttle_enabled = self.throttle_enabled_var.get()

        # Update brake histories
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

        # Update throttle histories (only if enabled)
        if throttle_enabled:
            if throttle_raw_val is not None:
                self.throttle_raw_history.append(throttle_raw_val)
                if len(self.throttle_raw_history) > self.HISTORY_LENGTH:
                    self.throttle_raw_history.pop(0)

                # Compute throttle preview with current slider settings
                throttle_preview = self._compute_throttle_preview(throttle_raw_val)
                self.throttle_preview_history.append(throttle_preview)
                if len(self.throttle_preview_history) > self.HISTORY_LENGTH:
                    self.throttle_preview_history.pop(0)

            if throttle_val is not None:
                self.throttle_history.append(throttle_val)
                if len(self.throttle_history) > self.HISTORY_LENGTH:
                    self.throttle_history.pop(0)

        # Update brake labels
        if raw_val_int is not None:
            self.raw_label.config(text=f"Raw ADC: {raw_val_int}")
        else:
            self.raw_label.config(text="Raw ADC: --")

        if brake_val is not None:
            self.norm_label.config(text=f"Brake: {brake_val:.1%}")
        else:
            self.norm_label.config(text="Brake: --")

        # Update throttle labels
        if throttle_enabled:
            if throttle_raw_val_int is not None:
                self.throttle_raw_label.config(text=f"Throttle Raw: {throttle_raw_val_int}")
            else:
                self.throttle_raw_label.config(text="Throttle Raw: --")

            if throttle_val is not None:
                self.throttle_norm_label.config(text=f"Throttle: {throttle_val:.1%}")
            else:
                self.throttle_norm_label.config(text="Throttle: --")

            # Update sensor type label based on whether we get non-zero values
            if throttle_raw_val_int and throttle_raw_val_int > 100:
                # Show sensor type - firmware auto-detects, we show what was configured or auto
                sensor_config = self.DEFAULTS.get("throttle_sensor", "auto")
                if sensor_config == "auto":
                    self.throttle_sensor_label.config(text="Sensor: Auto-detected")
                else:
                    sensor_name = "Hall Effect" if sensor_config == "hall" else "HX711 Load Cell"
                    self.throttle_sensor_label.config(text=f"Sensor: {sensor_name}")

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
