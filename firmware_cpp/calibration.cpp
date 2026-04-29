// firmware_cpp/calibration.cpp
#include "calibration.h"
#include "msc_disk.h"
#include <ArduinoJson.h>
#include <FatFS.h>

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

    File f = FatFS.open("/calibration.json", "r");
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

bool parse_calibration_json(const char* json_str, Calibration& cal) {
    cal = Calibration();  // Start with defaults

    JsonDocument doc;
    DeserializationError err = deserializeJson(doc, json_str);
    if (err) {
        return false;
    }

    JsonObjectConst obj = doc.as<JsonObjectConst>();

    if (obj["oversample"].is<uint8_t>()) cal.oversample = obj["oversample"].as<uint8_t>();
    if (obj["throttle_enabled"].is<bool>()) cal.throttle_enabled = obj["throttle_enabled"].as<bool>();
    if (obj["throttle_sensor"].is<const char*>()) {
        const char* sensor = obj["throttle_sensor"].as<const char*>();
        strncpy(cal.throttle_sensor, sensor, sizeof(cal.throttle_sensor) - 1);
        cal.throttle_sensor[sizeof(cal.throttle_sensor) - 1] = '\0';
    }

    load_channel_cal(obj, cal.brake, "");
    load_channel_cal(obj, cal.throttle, "throttle_");

    return true;
}

// Helper: write a ChannelCal to a JSON object
static void save_channel_cal(JsonObject obj, const ChannelCal& cal, const char* prefix) {
    char key[32];

    snprintf(key, sizeof(key), "%sraw_min", prefix);
    obj[key] = cal.raw_min;

    snprintf(key, sizeof(key), "%sraw_max", prefix);
    obj[key] = cal.raw_max;

    snprintf(key, sizeof(key), "%sdeadzone", prefix);
    obj[key] = cal.deadzone;

    snprintf(key, sizeof(key), "%scurve", prefix);
    obj[key] = cal.curve;

    snprintf(key, sizeof(key), "%sprogressive_power", prefix);
    obj[key] = cal.progressive_power;

    snprintf(key, sizeof(key), "%saggressive_power", prefix);
    obj[key] = cal.aggressive_power;

    snprintf(key, sizeof(key), "%ssmoothing", prefix);
    obj[key] = cal.smoothing;

    snprintf(key, sizeof(key), "%sinvert", prefix);
    obj[key] = cal.invert;

    snprintf(key, sizeof(key), "%ssaturation", prefix);
    obj[key] = cal.saturation;

    snprintf(key, sizeof(key), "%sbite_point", prefix);
    obj[key] = cal.bite_point;

    // Custom curve points
    snprintf(key, sizeof(key), "%scurve_points", prefix);
    JsonArray points = obj[key].to<JsonArray>();
    for (uint8_t i = 0; i < cal.num_curve_points; i++) {
        JsonArray pt = points.add<JsonArray>();
        pt.add(cal.curve_points_input[i]);
        pt.add(cal.curve_points_output[i]);
    }
}

bool save_calibration(const Calibration& cal) {
    // Must not access FatFS while PC has the drive mounted
    if (msc_disk_is_pc_connected()) {
        return false;
    }

    // Ensure FatFS is mounted
    if (!msc_disk_begin_fs()) {
        return false;
    }

    JsonDocument doc;
    JsonObject obj = doc.to<JsonObject>();

    obj["oversample"] = cal.oversample;
    obj["throttle_enabled"] = cal.throttle_enabled;
    obj["throttle_sensor"] = cal.throttle_sensor;

    save_channel_cal(obj, cal.brake, "");
    save_channel_cal(obj, cal.throttle, "throttle_");

    File f = FatFS.open("/calibration.json", "w");
    if (!f) {
        return false;
    }

    serializeJson(doc, f);
    f.close();
    return true;
}
