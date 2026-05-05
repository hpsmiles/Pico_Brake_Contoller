#include "serial_commands.h"
#include "flash_storage.h"
#include <Arduino.h>
#include <ArduinoJson.h>
#include <pico/bootrom.h>
#include <FatFS.h>
extern FS FatFS;

static void apply_json_to_cal(const String& json, Calibration& cal) {
    JsonDocument doc;
    if (deserializeJson(doc, json)) return;
    JsonObjectConst obj = doc.as<JsonObjectConst>();

    if (obj["oversample"].is<JsonVariantConst>())       cal.oversample       = obj["oversample"].as<uint8_t>();
    if (obj["throttle_enabled"].is<JsonVariantConst>()) cal.throttle_enabled = obj["throttle_enabled"].as<bool>();
    {
        const char* v = obj["throttle_sensor"];
        if (v && v[0]) { strncpy(cal.throttle_sensor, v, sizeof(cal.throttle_sensor)-1); cal.throttle_sensor[sizeof(cal.throttle_sensor)-1] = '\0'; }
    }

    auto parse_channel = [&](const char* prefix, ChannelCal& ch) {
        char key[64];

#define TRY(suffix, field, type) \
        snprintf(key, sizeof(key), "%s" suffix, prefix); \
        if (obj[key].is<JsonVariantConst>()) ch.field = obj[key].as<type>()

        TRY("raw_min",           raw_min,           uint16_t);
        TRY("raw_max",           raw_max,            uint16_t);
        TRY("deadzone",          deadzone,           uint16_t);
        TRY("progressive_power", progressive_power,  float);
        TRY("aggressive_power",  aggressive_power,   float);
        TRY("smoothing",         smoothing,          float);
        TRY("invert",            invert,             bool);
        TRY("saturation",        saturation,         float);
        TRY("bite_point",        bite_point,         float);
#undef TRY

        snprintf(key, sizeof(key), "%scurve", prefix);
        {
            const char* v = obj[key];
            if (v && v[0]) { strncpy(ch.curve, v, sizeof(ch.curve)-1); ch.curve[sizeof(ch.curve)-1] = '\0'; }
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
    };

    parse_channel("",          cal.brake);
    parse_channel("throttle_", cal.throttle);
}

bool serial_process_command(
    const String& line,
    Calibration& cal,
    bool& reboot_flag,
    bool& brake_ema_init,
    bool& throttle_ema_init,
    bool  fs_ok,
    bool  cal_ok
) {
    String cmd = line;
    cmd.trim();

    // CAL? — read back current calibration as JSON
    if (cmd == "CAL?") {
        Serial.print("CAL ");
        Serial.println(cal_to_json(cal));
        return true;
    }

    // CAL <json>
    if (cmd.startsWith("CAL ")) {
        String json = cmd.substring(4);
        json.trim();
        JsonDocument doc;
        if (deserializeJson(doc, json)) {
            Serial.println("CAL ERR");
            return true;
        }
        apply_json_to_cal(json, cal);
        brake_ema_init    = false;
        throttle_ema_init = false;
        Serial.println(flash_save_calibration(cal) ? "CAL OK" : "CAL ERR");
        return true;
    }

    // REBOOT
    if (cmd == "REBOOT") {
        Serial.println("OK");
        reboot_flag = true;
        return true;
    }

    // PROFILE SAVE <name>
    if (cmd.startsWith("PROFILE SAVE ")) {
        String name = cmd.substring(13); name.trim();
        if (name.length() == 0) { Serial.println("PROFILE ERR"); return true; }
        Serial.println(flash_profile_save(name.c_str(), cal) ? "PROFILE OK" : "PROFILE ERR");
        return true;
    }

    // PROFILE LOAD <name>
    if (cmd.startsWith("PROFILE LOAD ")) {
        String name = cmd.substring(13); name.trim();
        if (name.length() == 0) { Serial.println("PROFILE ERR"); return true; }
        if (flash_profile_load(name.c_str(), cal)) {
            flash_save_calibration(cal);
            brake_ema_init    = false;
            throttle_ema_init = false;
            Serial.println("PROFILE OK");
        } else {
            Serial.println("PROFILE ERR");
        }
        return true;
    }

    // PROFILE DELETE <name>
    if (cmd.startsWith("PROFILE DELETE ")) {
        String name = cmd.substring(15); name.trim();
        if (name.length() == 0) { Serial.println("PROFILE ERR"); return true; }
        Serial.println(flash_profile_delete(name.c_str()) ? "PROFILE OK" : "PROFILE ERR");
        return true;
    }

    // PROFILE LIST
    if (cmd == "PROFILE LIST") {
        char buf[512];
        flash_profile_list(buf, sizeof(buf));
        if (buf[0]) {
            Serial.print("PROFILE ");
            Serial.println(buf);
        } else {
            Serial.println("PROFILE NONE");
        }
        return true;
    }

    // STATUS — diagnostic snapshot
    if (cmd == "STATUS") {
        Serial.print("FS:");        Serial.println(fs_ok  ? "OK"  : "FAIL");
        Serial.print("CAL:");       Serial.println(cal_ok ? "OK"  : "DEFAULTS");
        // Try a test write to verify FatFS is writable
        bool writable = false;
        if (fs_ok) {
            File f = FatFS.open("/_test", "w");
            if (f) { f.print("x"); f.close(); FatFS.remove("/_test"); writable = true; }
        }
        Serial.print("WRITE:");     Serial.println(writable ? "OK" : "FAIL");
        return true;
    }

    // REBOOT BOOTSEL — reboot into USB bootloader (no button press needed)
    if (cmd == "REBOOT BOOTSEL") {
        Serial.println("OK");
        delay(50);
        reset_usb_boot(0, 0);
        while (true) {}
    }

    return false;
}
