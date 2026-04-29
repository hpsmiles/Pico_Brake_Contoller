#ifndef SIGNAL_PROCESSING_H
#define SIGNAL_PROCESSING_H

#include <stdint.h>
#include "config.h"

// Full per-channel pipeline: clamp → normalize → deadzone → bite → curve → EMA → invert.
// ema_state and ema_initialized are persistent per-channel state (must not be shared).
// Returns processed value 0–65535.
uint16_t process_channel(
    uint16_t raw_value,
    const ChannelCal& cal,
    float& ema_state,
    bool& ema_initialized
);

#endif // SIGNAL_PROCESSING_H
