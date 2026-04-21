// firmware_cpp/signal_processing.h
#ifndef SIGNAL_PROCESSING_H
#define SIGNAL_PROCESSING_H

#include <stdint.h>
#include "config.h"

// Process a single channel (brake or throttle) from raw ADC to 0-65535 output.
// raw: 16-bit ADC value (0-65535, already oversampled)
// cal: channel calibration settings
// ema: current EMA state (modified in place)
// ema_init: whether EMA has been initialized (modified in place)
// Returns: processed value 0-65535
uint16_t process_channel(uint16_t raw, const ChannelCal& cal,
                         float& ema, bool& ema_init);

#endif // SIGNAL_PROCESSING_H
