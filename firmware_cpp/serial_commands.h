#ifndef SERIAL_COMMANDS_H
#define SERIAL_COMMANDS_H

#include <Arduino.h>
#include "config.h"

// Parse one complete serial line (no newline).
// CAL?                — read back current calibration as JSON
// CAL <json>          — apply + persist calibration, reset EMA
// REBOOT              — set reboot_flag; caller reboots on next loop tick
// PROFILE SAVE <name> — save current cal as named profile
// PROFILE LOAD <name> — load profile, persist as current, reset EMA
// PROFILE DELETE <name>
// PROFILE LIST
// Returns true if a known command was matched.
bool serial_process_command(
    const String& line,
    Calibration& cal,
    bool& reboot_flag,
    bool& brake_ema_init,
    bool& throttle_ema_init,
    bool  fs_ok,
    bool  cal_ok
);

#endif // SERIAL_COMMANDS_H
