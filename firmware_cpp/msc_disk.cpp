// firmware_cpp/msc_disk.cpp
// LittleFS + SingleFileDrive: exports calibration.json as a USB mass storage drive.
// The PC sees a small drive it can read/write. The Pico reads calibration.json via LittleFS.
//
// SingleFileDrive is read-only from the PC side for the exported file — but the PC can
// delete the file and write a new one. We close files after each write so SingleFileDrive
// can see the latest version.
//
// IMPORTANT: SingleFileDrive conflicts with FatFS and FatFSUSB (they both define tud_msc_*
// callbacks). Only use ONE of these at a time.

#include "msc_disk.h"
#include <LittleFS.h>
#include <SingleFileDrive.h>

static bool fs_available = false;

bool msc_disk_init() {
    // Try to mount LittleFS
    fs_available = LittleFS.begin();
    if (!fs_available) {
        // Flash may be unformatted (first boot or CircuitPython flash)
        LittleFS.format();
        fs_available = LittleFS.begin();
    }

    if (!fs_available) {
        return false;
    }

    // Create calibration.json if it doesn't exist so SingleFileDrive has something to export
    if (!LittleFS.exists("/calibration.json")) {
        File f = LittleFS.open("/calibration.json", "w");
        if (f) {
            f.print("{}");
            f.close();
        }
    }

    // Export calibration.json as a USB drive visible to the PC
    // The drive label will be whatever SingleFileDrive defaults to
    singleFileDrive.begin("/calibration.json", "calibration.json");

    return true;
}

bool msc_disk_is_pc_connected() {
    // SingleFileDrive handles PC mount/unmount internally.
    // We don't need to track this ourselves.
    return false;
}

bool msc_disk_is_fs_available() {
    return fs_available;
}
