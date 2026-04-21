# firmware/code.py
"""Brake + throttle controller firmware for RPi Pico (RP2040).

Reads XDB401 pressure sensor (brake) and SS49E Hall Effect or HX711
load cell (throttle), applies calibration/curves/smoothing, and sends
16-bit values as USB HID gamepad axes.

HID report layout (8 bytes = 4 × uint16 LE):
  X  = processed brake
  Y  = raw brake ADC (for calibration GUI)
  Z  = processed throttle
  Rz = raw throttle ADC (for calibration GUI)

Hardware:
  Brake (XDB401):
    Sensor VCC    → Pico VSYS (5V)
    Sensor GND    → Pico GND
    Sensor Signal → R1 (2KΩ) → GP26 (ADC0)
                                  ↓
                              R2 (3.3KΩ) → GND

  Throttle Option A — SS49E Hall Effect:
    Sensor VCC  → Pico VSYS (5V)
    Sensor GND  → Pico GND
    Sensor Vout → R3 (2KΩ) → GP27 (ADC1)
                                  ↓
                              R4 (3.3KΩ) → GND

  Throttle Option B — HX711 load cell:
    HX711 VIN  → Pico VSYS (5V)
    HX711 GND  → Pico GND
    HX711 SCK  → GP28
    HX711 DATA → GP16

  Both throttle options can be wired simultaneously.
  Firmware auto-detects HX711 on boot; falls back to SS49E.
"""

import analogio
import board
import json
import struct
import time
import usb_hid
import digitalio

# --- Pin Configuration ---
BRAKE_ADC_PIN = board.GP26
HALL_ADC_PIN = board.GP27
HX711_DATA_PIN = board.GP16
HX711_SCK_PIN = board.GP28
LED_PIN = board.LED

# --- Default Calibration ---
BRAKE_DEFAULTS = {
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
}

THROTTLE_DEFAULTS = {
    "throttle_enabled": False,
    "throttle_sensor": "auto",  # "auto", "hall", "load_cell"
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

DEFAULTS = {}
DEFAULTS.update(BRAKE_DEFAULTS)
DEFAULTS.update(THROTTLE_DEFAULTS)

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
brake_adc = analogio.AnalogIn(BRAKE_ADC_PIN)


def read_adc_oversampled(adc_pin, oversample):
    """Read ADC N times and return the average."""
    total = 0
    for _ in range(oversample):
        total += adc_pin.value
    return total // oversample


# --- HX711 detection ---
def probe_hx711():
    """Check if an HX711 is connected on GP16 (DATA) + GP28 (SCK).

    The HX711 pulls DATA LOW when a new reading is ready.
    We pulse SCK a few times and check if DATA responds.
    Returns True if HX711 detected, False otherwise.
    """
    data_pin = digitalio.DigitalInOut(HX711_DATA_PIN)
    data_pin.direction = digitalio.Direction.INPUT
    data_pin.pull = digitalio.Pull.UP

    sck_pin = digitalio.DigitalInOut(HX711_SCK_PIN)
    sck_pin.direction = digitalio.Direction.OUTPUT
    sck_pin.value = False

    # Wait up to 120ms for DATA to go LOW (HX711 data-ready at 10 SPS = 100ms)
    deadline = time.monotonic() + 0.12
    while time.monotonic() < deadline:
        if not data_pin.value:
            # DATA is LOW — HX711 is present and has data ready
            # Power down: pulse SCK high once extra (after 25+ pulses = channel B gain 128
            # then one more = power down). Just do a clean power-down.
            for _ in range(27):  # 25 to complete a read cycle + 1 for power down + 1 extra
                sck_pin.value = True
                time.sleep(0.000001)
                sck_pin.value = False
                time.sleep(0.000001)
            data_pin.deinit()
            sck_pin.deinit()
            return True
        time.sleep(0.01)

    # No response — no HX711 connected
    data_pin.deinit()
    sck_pin.deinit()
    return False


# --- Load calibration ---
cal = load_calibration()

# --- Determine throttle sensor type ---
throttle_sensor = "none"  # Will be "hall", "load_cell", or "none"

if cal.get("throttle_enabled", False):
    sensor_config = cal.get("throttle_sensor", "auto")
    if sensor_config == "auto":
        if probe_hx711():
            throttle_sensor = "load_cell"
        else:
            throttle_sensor = "hall"
    elif sensor_config == "load_cell":
        throttle_sensor = "load_cell"
    else:
        throttle_sensor = "hall"

# --- Initialize throttle hardware ---
hall_adc = None
hx711 = None

if throttle_sensor == "hall":
    hall_adc = analogio.AnalogIn(HALL_ADC_PIN)
    blink_led(1, on_time=0.1, off_time=0.1)  # 1 blink = Hall Effect detected
elif throttle_sensor == "load_cell":
    try:
        from adafruit_hx711 import HX711_GPIO
        data_pin = digitalio.DigitalInOut(HX711_DATA_PIN)
        sck_pin = digitalio.DigitalInOut(HX711_SCK_PIN)
        hx711 = HX711_GPIO(data_pin, sck_pin)
        blink_led(2, on_time=0.1, off_time=0.2)  # 2 blinks = HX711 detected
    except (ImportError, OSError):
        # HX711 library not available or init failed — fall back to Hall
        throttle_sensor = "hall"
        hall_adc = analogio.AnalogIn(HALL_ADC_PIN)
        blink_led(4, on_time=0.1, off_time=0.1)  # 4 blinks = HX711 failed, fell back


# --- Brake curves ---
def interpolate_custom_curve(t, points):
    """Piecewise-linear interpolation through control points.

    points: list of [input, output] pairs, e.g. [[0.0, 0.0], [0.5, 0.3], [1.0, 1.0]]
    Input values must be sorted ascending. t is clamped to [0, 1].
    """
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


def apply_curve(t, curve_name, power=2.0, curve_points=None):
    """Apply brake curve to normalized value t (0.0 - 1.0).

    power controls the curve strength:
      - progressive: t^power (higher = more curve)
      - aggressive: t^(1/power) (higher = more curve)
      - linear: unaffected by power
      - custom: piecewise-linear through curve_points
    """
    if curve_name == "linear":
        return t
    elif curve_name == "progressive":
        return t ** power
    elif curve_name == "aggressive":
        return t ** (1.0 / power)
    elif curve_name == "custom":
        if curve_points and len(curve_points) >= 2:
            return interpolate_custom_curve(t, curve_points)
        return t
    else:
        # Default to progressive if unknown
        return t ** power


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

# Signal calibration status
if cal == DEFAULTS:
    # Using defaults — blink 3 times as warning
    blink_led(3, on_time=0.2, off_time=0.2)

# Turn LED on to indicate running
led.value = True

# --- EMA state ---
brake_ema = 0.0
brake_ema_init = False
throttle_ema = 0.0
throttle_ema_init = False


# --- Read throttle value ---
def read_throttle_raw():
    """Read raw throttle value depending on sensor type.

    Returns int (0-65535 for Hall, raw 24-bit scaled for HX711).
    """
    if throttle_sensor == "hall" and hall_adc is not None:
        return read_adc_oversampled(hall_adc, cal.get("oversample", 16))
    elif throttle_sensor == "load_cell" and hx711 is not None:
        # HX711 returns signed 24-bit value; map to 0-65535
        raw = hx711.read_raw()
        # Map from 24-bit signed range to 0-65535
        # Clamp negative values to 0
        if raw < 0:
            raw = 0
        # Scale down from 24-bit to 16-bit range
        return min(65535, raw >> 8)
    return 0

# --- Main loop ---
LOOP_INTERVAL = 0.002  # ~500Hz target

while True:
    loop_start = time.monotonic()

    # ── BRAKE ──
    # 1. Read ADC with oversampling
    raw = read_adc_oversampled(brake_adc, cal["oversample"])

    # 2. Clamp to calibration range (saturation scales raw_max down)
    raw_min = cal["raw_min"]
    raw_max_eff = raw_min + (cal["raw_max"] - raw_min) * min(cal.get("saturation", 1.0), 1.0)
    clamped = max(raw_min, min(raw_max_eff, raw))

    # 3. Normalize to 0.0 - 1.0
    if raw_max_eff == raw_min:
        normalized = 0.0
    else:
        normalized = (clamped - raw_min) / (raw_max_eff - raw_min)

    # 4. Apply deadzone
    deadzone = cal["deadzone"] / (raw_max_eff - raw_min) if raw_max_eff != raw_min else 0.0
    if normalized < deadzone:
        normalized = 0.0
    elif deadzone > 0:
        normalized = (normalized - deadzone) / (1.0 - deadzone)

    # 4.5 Apply bite point (dead-travel simulating pad-to-rotor gap)
    bite = cal.get("bite_point", 0.0)
    if bite > 0.0:
        if normalized < bite:
            normalized = 0.0
        else:
            normalized = (normalized - bite) / (1.0 - bite)

    # 5. Apply brake curve
    normalized = apply_curve(
        normalized, cal["curve"],
        cal.get("progressive_power", 2.0),
        cal.get("curve_points", None),
    )

    # 6. EMA smoothing (smoothing 0 = none, 0.95 = max; alpha = 1 - smoothing)
    alpha = 1.0 - min(cal["smoothing"], 0.95)
    if not brake_ema_init:
        brake_ema = normalized
        brake_ema_init = True
    else:
        brake_ema = alpha * normalized + (1.0 - alpha) * brake_ema

    # 7. Invert if needed
    if cal["invert"]:
        brake_ema = 1.0 - brake_ema

    # 8. Map to 0-65535
    brake_value = max(0, min(65535, int(brake_ema * 65535)))

    # ── THROTTLE ──
    throttle_value = 0
    throttle_raw = 0
    if throttle_sensor != "none":
        # 1. Read throttle sensor
        throttle_raw = read_throttle_raw()

        # 2. Clamp to calibration range (saturation scales raw_max down)
        t_raw_min = cal.get("throttle_raw_min", 2000)
        t_raw_max = cal.get("throttle_raw_max", 56000)
        t_raw_max_eff = t_raw_min + (t_raw_max - t_raw_min) * min(cal.get("throttle_saturation", 1.0), 1.0)
        t_clamped = max(t_raw_min, min(t_raw_max_eff, throttle_raw))

        # 3. Normalize
        if t_raw_max_eff == t_raw_min:
            t_normalized = 0.0
        else:
            t_normalized = (t_clamped - t_raw_min) / (t_raw_max_eff - t_raw_min)

        # 4. Deadzone
        t_deadzone = cal.get("throttle_deadzone", 300) / (t_raw_max_eff - t_raw_min) if t_raw_max_eff != t_raw_min else 0.0
        if t_normalized < t_deadzone:
            t_normalized = 0.0
        elif t_deadzone > 0:
            t_normalized = (t_normalized - t_deadzone) / (1.0 - t_deadzone)

        # 4.5 Bite point
        t_bite = cal.get("throttle_bite_point", 0.0)
        if t_bite > 0.0:
            if t_normalized < t_bite:
                t_normalized = 0.0
            else:
                t_normalized = (t_normalized - t_bite) / (1.0 - t_bite)

        # 5. Curve
        t_normalized = apply_curve(
            t_normalized, cal.get("throttle_curve", "linear"),
            cal.get("throttle_progressive_power", 2.0),
            cal.get("throttle_curve_points", None),
        )

        # 6. EMA smoothing
        t_alpha = 1.0 - min(cal.get("throttle_smoothing", 0.2), 0.95)
        if not throttle_ema_init:
            throttle_ema = t_normalized
            throttle_ema_init = True
        else:
            throttle_ema = t_alpha * t_normalized + (1.0 - t_alpha) * throttle_ema

        # 7. Invert
        if cal.get("throttle_invert", False):
            throttle_ema = 1.0 - throttle_ema

        # 8. Map to 0-65535
        throttle_value = max(0, min(65535, int(throttle_ema * 65535)))

    # Pack 4 axes as little-endian uint16
    # X = processed brake, Y = raw brake ADC, Z = processed throttle, Rz = raw throttle
    struct.pack_into("<HHHH", report, 0, brake_value, raw, throttle_value, throttle_raw)

    try:
        gamepad_device.send_report(report)
    except OSError:
        pass  # Retry next loop

    # Sleep to maintain loop rate
    elapsed = time.monotonic() - loop_start
    sleep_time = LOOP_INTERVAL - elapsed
    if sleep_time > 0:
        time.sleep(sleep_time)
