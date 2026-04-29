#include "adc_reader.h"
#include <Arduino.h>

void adc_reader_init() {
    analogReadResolution(12);
}

uint16_t adc_read_oversampled(uint8_t pin, uint16_t oversample_count) {
    if (oversample_count == 0) oversample_count = 1;

    uint32_t sum = 0;
    for (uint16_t i = 0; i < oversample_count; i++) {
        sum += analogRead(pin);
    }
    uint16_t avg = (uint16_t)(sum / oversample_count);

    // Scale 12-bit (0–4095) → 16-bit (0–65535): spread low bits to fill range.
    return (uint16_t)((avg << 4) | (avg >> 8));
}
