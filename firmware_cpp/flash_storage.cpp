#include "flash_storage.h"
#include <Arduino.h>
#include <FatFS.h>
#include <ArduinoJson.h>

extern FS FatFS;

static bool fs_mounted = false;

bool flash_storage_init() {
    // Use raw 4096-byte sectors (no SPIFTL wear-levelling).
    // SPIFTL requires its own on-flash metadata; any previous firmware that
    // wrote raw FAT (e.g. FatFSUSB) leaves the metadata region in a state that
    // SPIFTL::start() cannot parse, breaking mount and format.
    // Calibration is saved rarely, so wear levelling is unnecessary here.
    FatFS.setConfig(FatFSConfig(true /* autoFormat */, false /* useFTL */));

    // begin() auto-formats on first boot (autoFormat=true) and remounts.
    if (!FatFS.begin()) {
        return false;
    }

    fs_mounted = true;

    // Ensure the profiles directory exists (created only on first format,
    // but harmless to call on subsequent boots).
    FatFS.mkdir(PROFILES_DIR);
    return true;
}

// --- JSON helpers ---

static void parse_channel(JsonObjectConst obj, ChannelCal& ch, const char* prefix) {
    char key[64];

#define TRY_FIELD(suffix, target, type) \
    snprintf(key, sizeof(key), "%s" suffix, prefix); \
    if (obj[key].is<JsonVariantConst>()) (target) = obj[key].as<type>()

    TRY_FIELD("raw_min",            ch.raw_min,            uint16_t);
    TRY_FIELD("raw_max",            ch.raw_max,            uint16_t);
    TRY_FIELD("deadzone",           ch.deadzone,           uint16_t);
    TRY_FIELD("progressive_power",  ch.progressive_power,  float);
    TRY_FIELD("aggressive_power",   ch.aggressive_power,   float);
    TRY_FIELD("smoothing",          ch.smoothing,          float);
    TRY_FIELD("invert",             ch.invert,             bool);
    TRY_FIELD("saturation",         ch.saturation,         float);
    TRY_FIELD("bite_point",         ch.bite_point,         float);

#undef TRY_FIELD

    snprintf(key, sizeof(key), "%scurve", prefix);
    {
        const char* v = obj[key];
        if (v && v[0]) { strncpy(ch.curve, v, sizeof(ch.curve) - 1); ch.curve[sizeof(ch.curve)-1] = '\0'; }
    }

    snprintf(key, sizeof(key), "%scurve_points", prefix);
    if (obj[key].is<JsonArrayConst>()) {
        JsonArrayConst pts = obj[key].as<JsonArrayConst>();
        uint8_t i = 0;
        for (JsonArrayConst pt : pts) {
            if (i >= ChannelCal::MAX_CURVE_POINTS) break;
            if (pt.size() >= 2) {
                ch.curve_points_input[i]  = pt[0].as<float>();
                ch.curve_points_output[i] = pt[1].as<float>();
                i++;
            }
        }
        ch.num_curve_points = i;
    }
}

static void from_json(const String& json, Calibration& cal) {
    JsonDocument doc;
    if (deserializeJson(doc, json)) return;
    JsonObjectConst obj = doc.as<JsonObjectConst>();

    if (obj["oversample"].is<JsonVariantConst>())      cal.oversample       = obj["oversample"].as<uint8_t>();
    if (obj["throttle_enabled"].is<JsonVariantConst>()) cal.throttle_enabled = obj["throttle_enabled"].as<bool>();
    {
        const char* v = obj["throttle_sensor"];
        if (v && v[0]) { strncpy(cal.throttle_sensor, v, sizeof(cal.throttle_sensor)-1); cal.throttle_sensor[sizeof(cal.throttle_sensor)-1] = '\0'; }
    }

    parse_channel(obj, cal.brake,    "");
    parse_channel(obj, cal.throttle, "throttle_");
}

static String to_json(const Calibration& cal) {
    JsonDocument doc;
    JsonObject root = doc.to<JsonObject>();

    // Brake channel (no prefix)
    root["raw_min"]           = cal.brake.raw_min;
    root["raw_max"]           = cal.brake.raw_max;
    root["deadzone"]          = cal.brake.deadzone;
    root["curve"]             = cal.brake.curve;
    root["progressive_power"] = cal.brake.progressive_power;
    root["aggressive_power"]  = cal.brake.aggressive_power;
    root["smoothing"]         = cal.brake.smoothing;
    root["invert"]            = cal.brake.invert;
    root["saturation"]        = cal.brake.saturation;
    root["bite_point"]        = cal.brake.bite_point;
    {
        JsonArray pts = root["curve_points"].to<JsonArray>();
        for (uint8_t i = 0; i < cal.brake.num_curve_points; i++) {
            JsonArray pt = pts.add<JsonArray>();
            pt.add(cal.brake.curve_points_input[i]);
            pt.add(cal.brake.curve_points_output[i]);
        }
    }

    root["oversample"]       = cal.oversample;
    root["throttle_enabled"] = cal.throttle_enabled;
    root["throttle_sensor"]  = cal.throttle_sensor;

    // Throttle channel ("throttle_" prefix)
    root["throttle_raw_min"]           = cal.throttle.raw_min;
    root["throttle_raw_max"]           = cal.throttle.raw_max;
    root["throttle_deadzone"]          = cal.throttle.deadzone;
    root["throttle_curve"]             = cal.throttle.curve;
    root["throttle_progressive_power"] = cal.throttle.progressive_power;
    root["throttle_aggressive_power"]  = cal.throttle.aggressive_power;
    root["throttle_smoothing"]         = cal.throttle.smoothing;
    root["throttle_invert"]            = cal.throttle.invert;
    root["throttle_saturation"]        = cal.throttle.saturation;
    root["throttle_bite_point"]        = cal.throttle.bite_point;
    {
        JsonArray pts = root["throttle_curve_points"].to<JsonArray>();
        for (uint8_t i = 0; i < cal.throttle.num_curve_points; i++) {
            JsonArray pt = pts.add<JsonArray>();
            pt.add(cal.throttle.curve_points_input[i]);
            pt.add(cal.throttle.curve_points_output[i]);
        }
    }

    String out;
    serializeJson(doc, out);
    return out;
}

static bool write_json_to_path(const char* path, const Calibration& cal) {
    FatFS.remove(path);
    File f = FatFS.open(path, "w");
    if (!f) return false;
    String json = to_json(cal);
    size_t n = f.print(json);
    f.close();
    return n == json.length();
}

static bool load_from_path(const char* path, Calibration& cal_out) {
    File f = FatFS.open(path, "r");
    if (!f) return false;
    String json = f.readString();
    f.close();
    if (json.length() == 0) return false;
    from_json(json, cal_out);
    return true;
}

// --- Public API ---

bool flash_load_calibration(Calibration& cal_out) {
    if (!fs_mounted) return false;
    return load_from_path(CALIBRATION_FILE, cal_out);
}

bool flash_save_calibration(const Calibration& cal) {
    if (!fs_mounted) return false;
    return write_json_to_path(CALIBRATION_FILE, cal);
}

bool flash_profile_save(const char* name, const Calibration& cal) {
    if (!fs_mounted) return false;
    String path = String(PROFILES_DIR) + "/" + name + ".json";
    return write_json_to_path(path.c_str(), cal);
}

bool flash_profile_load(const char* name, Calibration& cal_out) {
    if (!fs_mounted) return false;
    String path = String(PROFILES_DIR) + "/" + name + ".json";
    return load_from_path(path.c_str(), cal_out);
}

bool flash_profile_delete(const char* name) {
    if (!fs_mounted) return false;
    String path = String(PROFILES_DIR) + "/" + name + ".json";
    return FatFS.remove(path.c_str());
}

bool flash_profile_list(char* buffer, size_t buf_size) {
    if (!fs_mounted) { buffer[0] = '\0'; return true; }

    buffer[0] = '\0';
    size_t pos = 0;
    bool first = true;

    Dir dir = FatFS.openDir(PROFILES_DIR);
    while (dir.next()) {
        if (!dir.isFile()) continue;
        String fname = dir.fileName();
        int dot = fname.lastIndexOf('.');
        if (dot > 0) fname = fname.substring(0, dot);

        if (!first && pos + 1 < buf_size) buffer[pos++] = ',';
        first = false;

        size_t len = fname.length();
        if (pos + len < buf_size) {
            memcpy(buffer + pos, fname.c_str(), len);
            pos += len;
        }
    }

    buffer[pos] = '\0';
    return true;
}
