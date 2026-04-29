// firmware_cpp/hx711_driver.cpp
#include "hx711_driver.h"
#include <Arduino.h>

HX711Driver::HX711Driver(uint8_t data_pin, uint8_t sck_pin)
    : _data_pin(data_pin), _sck_pin(sck_pin) {}

void HX711Driver::init() {
    pinMode(_sck_pin, OUTPUT);
    digitalWrite(_sck_pin, LOW);

    pinMode(_data_pin, INPUT_PULLUP);
    _initialized = true;
}

bool HX711Driver::probe(uint32_t timeout_ms) {
    init();  // Ensure pins are configured

    // HX711 pulls DATA LOW when data is ready
    // Wait up to timeout_ms for DATA to go LOW
    uint32_t deadline = millis() + timeout_ms;
    while (millis() < deadline) {
        if (digitalRead(_data_pin) == LOW) {
            // HX711 is present -- clean up by completing a read cycle and powering down
            // Pulse SCK 27 times (25 completes read + 1 sets gain + 1 extra = power down)
            for (int i = 0; i < 27; i++) {
                digitalWrite(_sck_pin, HIGH);
                delayMicroseconds(1);
                digitalWrite(_sck_pin, LOW);
                delayMicroseconds(1);
            }
            return true;
        }
        delay(10);
    }
    return false;  // No HX711 detected
}

int32_t HX711Driver::read(uint8_t gain_pulses) {
    if (!_initialized) init();

    // Wait for DATA to go LOW (data ready)
    uint32_t timeout = millis() + 500;
    while (digitalRead(_data_pin) == HIGH) {
        if (millis() > timeout) return 0;  // Timeout
        tight_loop_contents();
    }

    uint32_t value = 0;

    // Disable interrupts during shift to prevent data corruption
    uint32_t irq_state = save_and_disable_interrupts();

    // Shift in 24 bits MSB first
    for (int i = 0; i < 24; i++) {
        digitalWrite(_sck_pin, HIGH);
        delayMicroseconds(1);
        value <<= 1;
        if (digitalRead(_data_pin)) {
            value |= 1;
        }
        digitalWrite(_sck_pin, LOW);
        delayMicroseconds(1);
    }

    // Extra clock pulses to set gain/channel for next reading
    for (int i = 0; i < gain_pulses; i++) {
        digitalWrite(_sck_pin, HIGH);
        delayMicroseconds(1);
        digitalWrite(_sck_pin, LOW);
        delayMicroseconds(1);
    }

    restore_interrupts(irq_state);

    // Convert 24-bit two's complement to int32_t
    if (value & 0x800000) {
        value |= 0xFF000000;  // Sign extend
    }

    return (int32_t)value;
}

uint16_t HX711Driver::read_u16(uint8_t gain_pulses) {
    int32_t raw = read(gain_pulses);

    // Match CircuitPython's mapping: clamp negative to 0, shift right by 8
    if (raw < 0) raw = 0;
    uint32_t shifted = (uint32_t)raw >> 8;

    // Clamp to 16-bit range
    if (shifted > 65535) shifted = 65535;
    return (uint16_t)shifted;
}

void HX711Driver::power_down() {
    digitalWrite(_sck_pin, HIGH);
    delayMicroseconds(64);
}

void HX711Driver::power_up(uint8_t gain_pulses) {
    digitalWrite(_sck_pin, LOW);
    read(gain_pulses);  // First read after power-up sets gain
}
