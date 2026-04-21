# firmware/boot.py
"""Configure a custom USB HID gamepad with 16-bit axes.

The default adafruit_hid Gamepad uses 8-bit signed axes (-127 to 127),
which only gives 128 distinct brake values — insufficient for trail-braking.

This descriptor declares 4 axes (X, Y, Z, Rz) at 16-bit resolution (0-65535).
Report layout: 8 bytes total — 4 × uint16 little-endian.
X axis = processed brake, Y axis = raw ADC (for calibration GUI), Z/Rz = 0.

An Output Report (8 bytes host→device) is also declared for commands.
Sending 0xDE 0xAD 0x00 0x00 0x00 0x00 0x00 0x00 triggers a Pico reboot.
"""

import usb_hid

# HID Report Descriptor for a gamepad with 4 axes, 16-bit, no buttons
# Based on USB HID Usage Tables (HUT) for Generic Desktop (0x01) / Gamepad (0x05)
GAMEPAD_DESCRIPTOR = bytes(
    (
        0x05,
        0x01,  # Usage Page (Generic Desktop)
        0x09,
        0x05,  # Usage (Game Pad)
        0xA1,
        0x01,  # Collection (Application)
        0x09,
        0x01,  #   Usage (Pointer)
        0xA1,
        0x00,  #   Collection (Physical)
        # X axis (brake) — 16-bit
        0x09,
        0x30,  #     Usage (X)
        0x09,
        0x31,  #     Usage (Y)
        0x09,
        0x32,  #     Usage (Z)
        0x09,
        0x35,  #     Usage (Rz)
        0x16,
        0x00,
        0x00,  #     Logical Minimum (0)
        0x26,
        0xFF,
        0xFF,  #     Logical Maximum (65535)
        0x75,
        0x10,  #     Report Size (16 bits)
        0x95,
        0x04,  #     Report Count (4 axes)
        0x81,
        0x02,  #     Input (Data, Variable, Absolute)
        0xC0,  #   End Collection (Physical)
        # Output Report: 8 bytes for host→device commands
        0x75,
        0x08,  #     Report Size (8 bits)
        0x95,
        0x08,  #     Report Count (8 bytes)
        0x91,
        0x02,  #     Output (Data, Variable, Absolute)
        0xC0,  # End Collection (Application)
    )
)

gamepad_device = usb_hid.Device(
    report_descriptor=GAMEPAD_DESCRIPTOR,
    usage_page=0x01,  # Generic Desktop
    usage=0x05,  # Game Pad
    report_ids=(0,),  # Single report, no ID
    in_report_lengths=(8,),  # 4 axes × 2 bytes = 8 bytes
    out_report_lengths=(8,),  # 8 bytes for host commands (reset, etc.)
)

usb_hid.enable((gamepad_device,))
