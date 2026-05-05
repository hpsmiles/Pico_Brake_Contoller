#ifndef HX711_DRIVER_H
#define HX711_DRIVER_H

#include <stdint.h>

// Returns true if HX711 pulls DATA low within ~500us (data-ready signal).
bool hx711_probe(uint8_t pin_data);

// Read one 24-bit sample via bit-bang. Non-blocking: if data-ready is LOW,
// reads immediately; otherwise returns the previous sample. This avoids
// blocking Core 1 for up to 100ms waiting for the HX711's 10Hz conversion.
// channel_A=true → Channel A gain 128; false → Channel B gain 32.
// Returns raw 24-bit two's-complement value sign-extended to int32.
int32_t hx711_read_raw(uint8_t pin_sck, uint8_t pin_data, bool channel_A = true);

// Read Channel A and map the 24-bit positive range to 0–65535.
// Non-blocking: returns last valid sample if HX711 hasn't finished converting.
uint16_t hx711_read_16bit(uint8_t pin_sck, uint8_t pin_data);

#endif // HX711_DRIVER_H
