// firmware_cpp/calibration.h
#ifndef CALIBRATION_H
#define CALIBRATION_H

#include "config.h"

// Load calibration.json from flash. Returns merged Calibration with defaults.
Calibration load_calibration();

// Parse a JSON string into a Calibration struct. Returns true on success.
bool parse_calibration_json(const char* json_str, Calibration& cal);

// Save calibration to FatFS as /calibration.json. Returns true on success.
bool save_calibration(const Calibration& cal);

#endif // CALIBRATION_H
