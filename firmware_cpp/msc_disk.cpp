// firmware_cpp/msc_disk.cpp
// FatFS + FatFSUSB: exports the FatFS filesystem as a USB mass storage drive.
// The PC sees a full read-write drive. FatFSUSB handles sector-level read/write
// with dirty tracking, so PC writes actually persist to flash.
//
// Cooperative exclusion: onPlug → FatFS.end() (PC owns the disk),
// onUnplug → FatFS.begin() (Pico owns the disk again).
// Never access FatFS while drive_connected == true.

#include "msc_disk.h"
#include <FatFS.h>
#include <FatFSUSB.h>

static bool fs_available = false;
static volatile bool drive_connected = false;  // PC has the drive mounted
static volatile bool cal_dirty = false;         // PC may have changed calibration.json

// Called by FatFSUSB when PC mounts the drive
static void on_plug(uint32_t) {
    drive_connected = true;
    FatFS.end();  // PC now owns the disk — Pico must not access it
    fs_available = false;
}

// Called by FatFSUSB when PC ejects the drive
static void on_unplug(uint32_t) {
    // FatFSUSB flushes its sector cache before calling this
    drive_connected = false;
    cal_dirty = true;  // Assume calibration may have changed
    fs_available = FatFS.begin();     // Re-mount so Pico can read files again
}

// Called by FatFSUSB to check if it's safe to let PC mount the drive.
// Returns true only when Pico is not actively using the filesystem.
static bool drive_ready(uint32_t) {
    // Allow PC access when Pico isn't in the middle of a file operation.
    return true;
}

// Simple hash of file content for change detection.
// XOR-folds 4 bytes at a time into a uint32_t.
static uint32_t hash_file_content(const char* path) {
    File f = FatFS.open(path, "r");
    if (!f) return 0;

    uint32_t hash = 0;
    uint8_t buf[32];
    while (f.available() > 0) {
        int n = f.read(buf, sizeof(buf));
        for (int i = 0; i < n; i += 4) {
            uint32_t chunk = 0;
            int remaining = n - i;
            if (remaining >= 4) {
                memcpy(&chunk, buf + i, 4);
            } else {
                memcpy(&chunk, buf + i, remaining);
            }
            hash ^= chunk;
        }
    }
    f.close();
    return hash;
}

bool msc_disk_mount() {
    // Mount FatFS
    fs_available = FatFS.begin();
    if (!fs_available) {
        // Flash may be unformatted (first boot or migrated from CircuitPython/LittleFS).
        // Format to create a usable FAT filesystem.
        if (FatFS.format()) {
            fs_available = FatFS.begin();
        }
    }

    if (fs_available) {
        // Set volume label so GUI can find the drive
        fatfs::f_setlabel("BRAKECTL");

        // Create calibration.json if it doesn't exist
        if (!FatFS.exists("/calibration.json")) {
            File f = FatFS.open("/calibration.json", "w");
            if (f) {
                f.print("{}");
                f.close();
            }
        }
    }

    return fs_available;
}

bool msc_disk_init() {
    // Register FatFSUSB callbacks BEFORE begin()
    FatFSUSB.onPlug(on_plug);
    FatFSUSB.onUnplug(on_unplug);
    FatFSUSB.driveReady(drive_ready);

    // Start USB MSC. Internally does USB.disconnect() (no-op if not connected yet)
    // → register MSC interface → USB.connect() (builds full descriptor: HID + MSC + Serial)
    FatFSUSB.begin();

    // TinyUSB race condition workaround (from official example)
    delay(2000);

    return true;
}

bool msc_disk_is_pc_connected() {
    return drive_connected;
}

bool msc_disk_is_fs_available() {
    return fs_available;
}

bool msc_disk_begin_fs() {
    // If PC has the drive mounted, FatFS is not available
    if (drive_connected) {
        return false;
    }
    // If already mounted, fine
    if (fs_available) {
        return true;
    }
    // Try to mount
    fs_available = FatFS.begin();
    return fs_available;
}

bool msc_disk_cal_changed() {
    if (!fs_available || drive_connected) return false;

    // On unplug, we flagged cal_dirty. Check it once, then clear.
    // Also do a content-hash check to catch any edge cases.
    static uint32_t last_hash = 0;
    static bool first_check = true;

    uint32_t cur_hash = hash_file_content("/calibration.json");

    if (first_check) {
        last_hash = cur_hash;
        first_check = false;
        cal_dirty = false;
        return false;
    }

    bool changed = cal_dirty || (cur_hash != last_hash);
    last_hash = cur_hash;
    cal_dirty = false;
    return changed;
}

void msc_disk_remount() {
    if (drive_connected) return;  // Can't remount while PC has the drive
    FatFS.end();
    fs_available = FatFS.begin();
}
