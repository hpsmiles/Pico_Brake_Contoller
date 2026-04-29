// firmware_cpp/adc_reader.cpp
#include "adc_reader.h"
#include "config.h"
#include <Arduino.h>

void adc_reader_init() {
    analogReadResolution(BRAKE_ADC_RESOLUTION);

    // Configure ADC pins -- analogRead() handles pin setup internally on RP2040
    // GP26 = A0, GP27 = A1, GP28 = A2
    // Just make sure they're not in OUTPUT mode
    pinMode(PIN_BRAKE_ADC, INPUT);
    pinMode(PIN_HALL_ADC, INPUT);
}

// Arduino pin number from ADC channel number
static int channel_to_arduino_pin(uint8_t channel) {
    switch (channel) {
        case 0: return A0;  // GP26
        case 1: return A1;  // GP27
        case 2: return A2;  // GP28
        case 3: return A3;  // GP29 (temp sensor)
        default: return A0;
    }
}

uint16_t adc_read_oversampled(uint8_t channel, uint8_t oversample_count) {
    if (oversample_count == 0) oversample_count = 1;

    int pin = channel_to_arduino_pin(channel);
    uint32_t sum = 0;

    for (uint8_t i = 0; i < oversample_count; i++) {
        sum += analogRead(pin);  // 12-bit: 0-4095
    }

    // Average the 12-bit readings
    uint16_t avg_12bit = (uint16_t)(sum / oversample_count);

    // Scale 12-bit -> 16-bit to match CircuitPython's analogio behavior
    // CircuitPython maps: (raw << 4) | (raw >> 8)
    return (avg_12bit << 4) | (avg_12bit >> 8);
}
