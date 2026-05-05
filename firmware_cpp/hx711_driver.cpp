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
    // Non-blocking: if DATA is HIGH (no new sample ready), return last value.
    // HX711 converts at 10Hz (100ms), so we'd block Core 1 for that long
    // if we waited. Instead, return the previous sample and let the caller
    // run at full speed — the next read will pick up the new value.
    static int32_t last_value = 0;

    if (digitalRead(pin_data) == HIGH) {
        return last_value;  // No new conversion ready yet
    }

    // Disable interrupts during bit-bang clock-out to prevent timing jitter
    // from Core 0 USB/serial interrupts corrupting HX711 reads on Core 1.
    // The 24+1 clock pulses take ~100us; USB endpoints tolerate brief IRQ blocks.
    uint32_t irq_save = save_and_disable_interrupts();

    // Clock out 24 bits, MSB first.
    // HX711 datasheet: data valid on rising edge of SCK.
    // Use 2us delays for reliable timing on RP2040.
    int32_t value = 0;
    for (int i = 0; i < 24; i++) {
        digitalWrite(pin_sck, HIGH);
        delayMicroseconds(2);
        value = (value << 1) | digitalRead(pin_data);
        digitalWrite(pin_sck, LOW);
        delayMicroseconds(2);
    }

    // Extra clock pulses set channel/gain for the NEXT conversion:
    //   1 pulse → Channel A, gain 128
    //   2 pulses → Channel B, gain 32
    //   3 pulses → Channel A, gain 64
    int pulses = channel_A ? 1 : 2;
    for (int i = 0; i < pulses; i++) {
        digitalWrite(pin_sck, HIGH);
        delayMicroseconds(2);
        digitalWrite(pin_sck, LOW);
        delayMicroseconds(2);
    }

    // Re-enable interrupts as soon as critical timing section is done.
    restore_interrupts(irq_save);

    // Sign-extend 24-bit two's complement → int32.
    if (value & 0x800000) value |= (int32_t)0xFF000000;

    last_value = value;
    return value;
}

uint16_t hx711_read_16bit(uint8_t pin_sck, uint8_t pin_data) {
    int32_t raw = hx711_read_raw(pin_sck, pin_data, true);
    // HX711 returns 24-bit two's complement. Offset to unsigned and shift
    // right by 8 to map the full 24-bit range into 16 bits.
    // The calibration system normalizes the actual signal range via raw_min/raw_max.
    uint32_t unsigned_raw = ((uint32_t)raw) + 0x800000u;
    return (uint16_t)(unsigned_raw >> 8);
}
