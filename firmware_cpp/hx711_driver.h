// firmware_cpp/hx711_driver.h
#ifndef HX711_DRIVER_H
#define HX711_DRIVER_H

#include <stdint.h>
#include <stdbool.h>

// Gain/channel select: number of extra clock pulses after 24 data bits
// 1 = Channel A Gain 128 (default), 2 = Channel B Gain 32, 3 = Channel A Gain 64
#define HX711_GAIN_A_128  1
#define HX711_GAIN_B_32   2
#define HX711_GAIN_A_64   3

class HX711Driver {
public:
    HX711Driver(uint8_t data_pin, uint8_t sck_pin);

    void init();
    bool probe(uint32_t timeout_ms = 120);  // Check if HX711 is connected
    int32_t read(uint8_t gain_pulses = HX711_GAIN_A_128);  // Raw 24-bit signed
    uint16_t read_u16(uint8_t gain_pulses = HX711_GAIN_A_128);  // Mapped to 0-65535

    void power_down();
    void power_up(uint8_t gain_pulses = HX711_GAIN_A_128);

private:
    uint8_t _data_pin;
    uint8_t _sck_pin;
    bool _initialized = false;
};

#endif // HX711_DRIVER_H
