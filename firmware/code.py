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

    # 6. EMA smoothing (smoothing 0 = none, 0.95 = max; alpha = 1 - smoothing)
    alpha = 1.0 - min(cal["smoothing"], 0.95)
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
