// firmware_cpp/signal_processing.cpp
#include "signal_processing.h"
#include <math.h>
#include <string.h>

// Clamp value to [lo, hi]
static inline float clampf(float val, float lo, float hi) {
    if (val < lo) return lo;
    if (val > hi) return hi;
    return val;
}

// Piecewise-linear interpolation through control points
static float interpolate_custom_curve(float t, const ChannelCal& cal) {
    if (cal.num_curve_points < 2) return t;
    if (t <= cal.curve_points_input[0]) return cal.curve_points_output[0];
    if (t >= cal.curve_points_input[cal.num_curve_points - 1])
        return cal.curve_points_output[cal.num_curve_points - 1];

    for (uint8_t i = 0; i < cal.num_curve_points - 1; i++) {
        float x0 = cal.curve_points_input[i];
        float x1 = cal.curve_points_input[i + 1];
        if (x0 <= t && t <= x1) {
            float y0 = cal.curve_points_output[i];
            float y1 = cal.curve_points_output[i + 1];
            if (x1 == x0) return y0;
            float frac = (t - x0) / (x1 - x0);
            return y0 + frac * (y1 - y0);
        }
    }
    return t;
}

// Apply curve to normalized value t (0.0 - 1.0)
static float apply_curve(float t, const ChannelCal& cal) {
    if (strcmp(cal.curve, "linear") == 0) {
        return t;
    } else if (strcmp(cal.curve, "progressive") == 0) {
        return powf(t, cal.progressive_power);
    } else if (strcmp(cal.curve, "aggressive") == 0) {
        return powf(t, 1.0f / cal.aggressive_power);
    } else if (strcmp(cal.curve, "custom") == 0) {
        return interpolate_custom_curve(t, cal);
    } else {
        // Unknown curve -- default to progressive
        return powf(t, cal.progressive_power);
    }
}

uint16_t process_channel(uint16_t raw, const ChannelCal& cal,
                         float& ema, bool& ema_init) {
    // 1. Clamp to calibration range (saturation scales raw_max down)
    float raw_min = (float)cal.raw_min;
    float raw_max_eff = raw_min + ((float)cal.raw_max - raw_min) * fmin(cal.saturation, 1.0f);
    float clamped = clampf((float)raw, raw_min, raw_max_eff);

    // 2. Normalize to 0.0 - 1.0
    float normalized;
    if (raw_max_eff == raw_min) {
        normalized = 0.0f;
    } else {
        normalized = (clamped - raw_min) / (raw_max_eff - raw_min);
    }

    // 3. Apply deadzone
    float deadzone = 0.0f;
    if (raw_max_eff != raw_min) {
        deadzone = (float)cal.deadzone / (raw_max_eff - raw_min);
    }
    if (normalized < deadzone) {
        normalized = 0.0f;
    } else if (deadzone > 0.0f) {
        normalized = (normalized - deadzone) / (1.0f - deadzone);
    }

    // 4. Apply bite point (dead-travel simulating pad-to-rotor gap)
    if (cal.bite_point > 0.0f) {
        if (normalized < cal.bite_point) {
            normalized = 0.0f;
        } else {
            normalized = (normalized - cal.bite_point) / (1.0f - cal.bite_point);
        }
    }

    // 5. Apply curve
    normalized = apply_curve(normalized, cal);

    // 6. EMA smoothing (smoothing 0 = none, 0.95 = max; alpha = 1 - smoothing)
    float alpha = 1.0f - fmin(cal.smoothing, 0.95f);
    if (!ema_init) {
        ema = normalized;
        ema_init = true;
    } else {
        ema = alpha * normalized + (1.0f - alpha) * ema;
    }

    // 7. Invert if needed
    if (cal.invert) {
        ema = 1.0f - ema;
    }

    // 8. Map to 0-65535
    return (uint16_t)clampf(ema * 65535.0f, 0.0f, 65535.0f);
}
