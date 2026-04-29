#ifndef RGB_LED_H
#define RGB_LED_H

#include <stdint.h>

void rgb_led_init();
void rgb_led_set_color(uint8_t r, uint8_t g, uint8_t b);
void rgb_led_off();

void rgb_led_boot();      // Orange — booting
void rgb_led_cal_ok();    // Green  — calibration loaded from flash
void rgb_led_defaults();  // Red    — using factory defaults
void rgb_led_fatal();     // 10× rapid red blinks, then stays red

// Blocking: blink count times with the given colour.
void rgb_led_blink_code(int count, uint8_t r, uint8_t g, uint8_t b);

#endif // RGB_LED_H
