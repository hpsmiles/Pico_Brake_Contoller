#include "signal_processing.h"
#include <math.h>
#include <string.h>

// Step 1: Clamp to saturation ceiling before normalising.
static uint16_t clamp_raw(uint16_t raw, const ChannelCal& cal) {
    uint16_t effective_max = (uint16_t)((float)cal.raw_max * cal.saturation);
    return (raw > effective_max) ? effective_max : raw;
}

// Step 2: Normalise [raw_min, raw_max*saturation] → 0.0–1.0.
static float normalize(uint16_t raw, const ChannelCal& cal) {
    uint16_t effective_max = (uint16_t)((float)cal.raw_max * cal.saturation);
    if (raw <= cal.raw_min) return 0.0f;
    if (raw >= effective_max) return 1.0f;
    return (float)(raw - cal.raw_min) / (float)(effective_max - cal.raw_min);
}

// Step 3: Deadzone — suppress noise near zero, then renormalise above threshold.
static float apply_deadzone(float value, const ChannelCal& cal) {
    float dz = (float)cal.deadzone / (float)(cal.raw_max - cal.raw_min);
    if (value <= dz) return 0.0f;
    return (value - dz) / (1.0f - dz);
}

// Step 4: Bite point — simulates pad-to-rotor gap; dead-travel before response.
static float apply_bite_point(float value, float bite_point) {
    if (bite_point <= 0.0f) return value;
    if (value <= bite_point) return 0.0f;
    return (value - bite_point) / (1.0f - bite_point);
}

// Step 5: Curve shaping — linear / progressive (t^n) / aggressive (t^(1/n)) / custom.
static float apply_curve(float value, const ChannelCal& cal) {
    if (strcmp(cal.curve, "progressive") == 0) {
        float n = (cal.progressive_power > 0.0f) ? cal.progressive_power : 1.0f;
        return powf(value, n);
    }
    if (strcmp(cal.curve, "aggressive") == 0) {
        float n = (cal.aggressive_power > 0.0f) ? cal.aggressive_power : 1.0f;
        return powf(value, 1.0f / n);
    }
    if (strcmp(cal.curve, "custom") == 0) {
        if (cal.num_curve_points < 2) return value;
        if (value <= cal.curve_points_input[0])
            return cal.curve_points_output[0];
        if (value >= cal.curve_points_input[cal.num_curve_points - 1])
            return cal.curve_points_output[cal.num_curve_points - 1];
        for (uint8_t i = 0; i < cal.num_curve_points - 1; i++) {
            float x0 = cal.curve_points_input[i];
            float x1 = cal.curve_points_input[i + 1];
            if (value >= x0 && value <= x1) {
                float span = x1 - x0;
                if (span <= 0.0f) return cal.curve_points_output[i];
                float t = (value - x0) / span;
                return cal.curve_points_output[i] +
                       t * (cal.curve_points_output[i + 1] - cal.curve_points_output[i]);
            }
        }
        return value;
    }
    return value;  // "linear" or unknown
}

// Step 6: Exponential moving average.  alpha = 1 - smoothing.
static float apply_ema(float value, const ChannelCal& cal, float& state, bool& initialized) {
    float alpha = 1.0f - cal.smoothing;
    if (alpha < EMA_ALPHA_MIN) alpha = EMA_ALPHA_MIN;
    if (alpha > EMA_ALPHA_MAX) alpha = EMA_ALPHA_MAX;
    if (!initialized) {
        state = value;
        initialized = true;
        return value;
    }
    state = alpha * value + (1.0f - alpha) * state;
    return state;
}

// Step 7: Optional axis inversion.
static float apply_invert(float value, bool invert) {
    return invert ? (1.0f - value) : value;
}

// Step 8: Float 0.0–1.0 → uint16 0–65535.
static uint16_t to_uint16(float value) {
    if (value <= 0.0f) return 0;
    if (value >= 1.0f) return 65535;
    return (uint16_t)(value * 65535.0f);
}

uint16_t process_channel(
    uint16_t raw_value,
    const ChannelCal& cal,
    float& ema_state,
    bool& ema_initialized
) {
    uint16_t clamped   = clamp_raw(raw_value, cal);
    float    normed    = normalize(clamped, cal);
    float    dezoned   = apply_deadzone(normed, cal);
    float    bitten    = apply_bite_point(dezoned, cal.bite_point);
    float    curved    = apply_curve(bitten, cal);
    float    smoothed  = apply_ema(curved, cal, ema_state, ema_initialized);
    float    inverted  = apply_invert(smoothed, cal.invert);
    return   to_uint16(inverted);
}
