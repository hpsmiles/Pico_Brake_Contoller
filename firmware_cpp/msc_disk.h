// firmware_cpp/msc_disk.h
// LittleFS + SingleFileDrive for USB MSC access to calibration.json
#ifndef MSC_DISK_H
#define MSC_DISK_H

#include <stdbool.h>

// Initialize LittleFS and SingleFileDrive. Call AFTER USB.disconnect(), BEFORE USB.connect().
// Returns true if filesystem was mounted successfully.
bool msc_disk_init();

// Whether the PC currently has the SingleFileDrive mounted (from our side).
// Always returns false with SingleFileDrive (it handles coordination internally).
bool msc_disk_is_pc_connected();

// Whether the LittleFS filesystem is available and mounted.
bool msc_disk_is_fs_available();

#endif // MSC_DISK_H
