// firmware_cpp/calibration.cpp
#include "calibration.h"
#include <ArduinoJson.h>
#include <LittleFS.h>

// Helper: load a ChannelCal from a JSON object
static void load_channel_cal(JsonObjectConst obj, ChannelCal& cal, const char* prefix) {
    char key[32];

    snprintf(key, sizeof(key), "%sraw_min", prefix);
    if (obj[key].is<uint16_t>()) cal.raw_min = obj[key].as<uint16_t>();

    snprintf(key, sizeof(key), "%sraw_max", prefix);
    if (obj[key].is<uint16_t>()) cal.raw_max = obj[key].as<uint16_t>();

    snprintf(key, sizeof(key), "%sdeadzone", prefix);
    if (obj[key].is<uint16_t>()) cal.deadzone = obj[key].as<uint16_t>();

    snprintf(key, sizeof(key), "%scurve", prefix);
    if (obj[key].is<const char*>()) {
        const char* curve = obj[key].as<const char*>();
        strncpy(cal.curve, curve, sizeof(cal.curve) - 1);
        cal.curve[sizeof(cal.curve) - 1] = '\0';
    }

    snprintf(key, sizeof(key), "%sprogressive_power", prefix);
    if (obj[key].is<float>()) cal.progressive_power = obj[key].as<float>();

    snprintf(key, sizeof(key), "%saggressive_power", prefix);
    if (obj[key].is<float>()) cal.aggressive_power = obj[key].as<float>();

    snprintf(key, sizeof(key), "%ssmoothing", prefix);
    if (obj[key].is<float>()) cal.smoothing = obj[key].as<float>();

    snprintf(key, sizeof(key), "%sinvert", prefix);
    if (obj[key].is<bool>()) cal.invert = obj[key].as<bool>();

    snprintf(key, sizeof(key), "%ssaturation", prefix);
    if (obj[key].is<float>()) cal.saturation = obj[key].as<float>();

    snprintf(key, sizeof(key), "%sbite_point", prefix);
    if (obj[key].is<float>()) cal.bite_point = obj[key].as<float>();

    // Custom curve points
    snprintf(key, sizeof(key), "%scurve_points", prefix);
    if (obj[key].is<JsonArrayConst>()) {
        JsonArrayConst points = obj[key].as<JsonArrayConst>();
        uint8_t idx = 0;
        for (JsonVariantConst point : points) {
            if (idx >= ChannelCal::MAX_CURVE_POINTS) break;
            if (point.is<JsonArrayConst>()) {
                JsonArrayConst pt = point.as<JsonArrayConst>();
                if (pt.size() >= 2) {
                    cal.curve_points_input[idx]  = pt[0].as<float>();
                    cal.curve_points_output[idx] = pt[1].as<float>();
                    idx++;
                }
            }
        }
        cal.num_curve_points = idx;
    }
}

Calibration load_calibration() {
    Calibration cal;  // Starts with all defaults from config.h

    File f = LittleFS.open("/calibration.json", "r");
    if (!f) {
        return cal;  // File not found -- return defaults
    }

    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, f);
    f.close();

    if (err) {
        return cal;  // Parse error -- return defaults
    }

    JsonObjectConst obj = doc.as<JsonObjectConst>();

    // Top-level keys
    if (obj["oversample"].is<uint8_t>()) cal.oversample = obj["oversample"].as<uint8_t>();
    if (obj["throttle_enabled"].is<bool>()) cal.throttle_enabled = obj["throttle_enabled"].as<bool>();
    if (obj["throttle_sensor"].is<const char*>()) {
        const char* sensor = obj["throttle_sensor"].as<const char*>();
        strncpy(cal.throttle_sensor, sensor, sizeof(cal.throttle_sensor) - 1);
        cal.throttle_sensor[sizeof(cal.throttle_sensor) - 1] = '\0';
    }

    // Brake channel keys have no prefix
    load_channel_cal(obj, cal.brake, "");

    // Throttle channel keys have "throttle_" prefix
    load_channel_cal(obj, cal.throttle, "throttle_");

    return cal;
}
