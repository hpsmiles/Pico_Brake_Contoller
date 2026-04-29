#include "hx711_driver.h"
#include <Arduino.h>

bool hx711_probe(uint8_t pin_data) {
    pinMode(pin_data, INPUT_PULLUP);
    delayMicroseconds(10);
    // HX711 drives DATA low when a conversion is ready.
    // If it stays high for 500us, no HX711 is present.
    for (int i = 0; i < 50; i++) {
        if (digitalRead(pin_data) == LOW) return true;
        delayMicroseconds(10);
    }
    return false;
}

int32_t hx711_read_raw(uint8_t pin_sck, uint8_t pin_data, bool channel_A) {
    // Wait for DATA → LOW (conversion ready), 100ms timeout.
    unsigned long deadline = millis() + 100;
    while (digitalRead(pin_data) == HIGH) {
        if (millis() > deadline) return 0;
    }

    // Clock out 24 bits, MSB first.
    int32_t value = 0;
    for (int i = 0; i < 24; i++) {
        digitalWrite(pin_sck, HIGH);
        delayMicroseconds(1);
        value = (value << 1) | digitalRead(pin_data);
        digitalWrite(pin_sck, LOW);
        delayMicroseconds(1);
    }

    // Extra clock pulses set channel/gain for the NEXT conversion:
    //   1 pulse → Channel A, gain 128
    //   2 pulses → Channel B, gain 32
    //   3 pulses → Channel A, gain 64
    int pulses = channel_A ? 1 : 2;
    for (int i = 0; i < pulses; i++) {
        digitalWrite(pin_sck, HIGH);
        delayMicroseconds(1);
        digitalWrite(pin_sck, LOW);
        delayMicroseconds(1);
    }

    // Sign-extend 24-bit two's complement → int32.
    if (value & 0x800000) value |= (int32_t)0xFF000000;

    return value;
}

uint16_t hx711_read_16bit(uint8_t pin_sck, uint8_t pin_data) {
    int32_t raw = hx711_read_raw(pin_sck, pin_data, true);
    if (raw < 0) raw = 0;
    // Top 16 bits of 24-bit range → 0–65535.
    return (uint16_t)(raw >> 8);
}
