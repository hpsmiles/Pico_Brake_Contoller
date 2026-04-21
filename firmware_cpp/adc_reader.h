// firmware_cpp/adc_reader.h
#ifndef ADC_READER_H
#define ADC_READER_H

#include <stdint.h>

void adc_reader_init();
// Read ADC channel with oversampling. Returns value in 0-65535 range
// (matching CircuitPython's 16-bit scaling from 12-bit ADC).
// channel: 0=GP26, 1=GP27, 2=GP28, 3=GP29
uint16_t adc_read_oversampled(uint8_t channel, uint8_t oversample_count);

#endif // ADC_READER_H
