// firmware_cpp/calibration.h
#ifndef CALIBRATION_H
#define CALIBRATION_H

#include "config.h"

// Load calibration.json from flash. Returns merged Calibration with defaults.
Calibration load_calibration();

#endif // CALIBRATION_H
