#ifndef ADC_READER_H
#define ADC_READER_H

#include <stdint.h>

// Set ADC to 12-bit mode. Call once from setup1().
void adc_reader_init();

// Read pin oversample_count times, average, then scale 12-bit → 16-bit.
// Returns 0–65535.
uint16_t adc_read_oversampled(uint8_t pin, uint16_t oversample_count);

#endif // ADC_READER_H
