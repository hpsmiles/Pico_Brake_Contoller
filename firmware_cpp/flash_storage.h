#ifndef FLASH_STORAGE_H
#define FLASH_STORAGE_H

#include <cstddef>
#include <stdbool.h>
#include "config.h"

// Mount FatFS. Formats partition on first boot if needed.
// Returns true on success.
bool flash_storage_init();

// Load /calibration.json into cal_out.
// Returns true if loaded; on false, cal_out retains its defaults.
bool flash_load_calibration(Calibration& cal_out);

// Serialise cal to /calibration.json. Returns true on success.
bool flash_save_calibration(const Calibration& cal);

// Named profile CRUD — all return true on success.
bool flash_profile_save(const char* name, const Calibration& cal);
bool flash_profile_load(const char* name, Calibration& cal_out);
bool flash_profile_delete(const char* name);

// Write comma-separated profile names (without .json) into buffer.
// Returns true always; buffer is empty string if no profiles exist.
bool flash_profile_list(char* buffer, size_t buf_size);

#endif // FLASH_STORAGE_H
