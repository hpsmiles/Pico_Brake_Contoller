// firmware_cpp/msc_disk.h
// FatFS + FatFSUSB for USB MSC access to calibration.json
#ifndef MSC_DISK_H
#define MSC_DISK_H

#include <stdbool.h>

// Mount FatFS only (no USB). Call FIRST in setup() to read calibration
// before any USB interface registration happens.
bool msc_disk_mount();

// Register FatFSUSB and start USB MSC. Call AFTER registering HID device
// but BEFORE USB.connect(). The single USB.connect() (inside FatFSUSB.begin)
// will enumerate both HID and MSC together.
bool msc_disk_init();

// Whether the PC currently has the USB drive mounted.
// While true, firmware must NOT access FatFS (cooperative exclusion).
bool msc_disk_is_pc_connected();

// Whether the FatFS filesystem is available and mounted.
bool msc_disk_is_fs_available();

// Ensure FatFS is mounted and available for Pico-side access.
// Safe to call anytime — returns true if FatFS is usable, false if PC has the drive.
// Handles the FatFS.end/.begin cycle automatically.
bool msc_disk_begin_fs();

// Returns true if calibration.json has changed since the last call.
// Uses a content hash (not just file size) so same-size rewrites are detected.
// Only call when msc_disk_is_pc_connected() == false.
bool msc_disk_cal_changed();

// Re-mount FatFS after PC ejects the drive.
// Called internally by onUnplug callback; also safe to call from hot-reload if needed.
void msc_disk_remount();

#endif // MSC_DISK_H
