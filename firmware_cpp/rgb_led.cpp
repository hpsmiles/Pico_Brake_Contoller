#include "rgb_led.h"
#include "config.h"
#include <Adafruit_NeoPixel.h>

static Adafruit_NeoPixel pixel(1, PIN_RGB_LED, NEO_GRB + NEO_KHZ800);

void rgb_led_init() {
    pixel.begin();
    pixel.show();
}

void rgb_led_set_color(uint8_t r, uint8_t g, uint8_t b) {
    pixel.setPixelColor(0, pixel.Color(r, g, b));
    pixel.show();
}

void rgb_led_off() {
    pixel.clear();
    pixel.show();
}

void rgb_led_boot()     { rgb_led_set_color(255, 165,   0); }  // Orange
void rgb_led_cal_ok()   { rgb_led_set_color(  0, 255,   0); delay(2000); rgb_led_off(); }  // Green 2s then off
void rgb_led_defaults() { rgb_led_set_color( 30,   0,   0); }  // Dim red — stays on

void rgb_led_fatal() {
    rgb_led_blink_code(BLINK_FATAL, 255, 0, 0);
    rgb_led_set_color(255, 0, 0);
}

void rgb_led_blink_code(int count, uint8_t r, uint8_t g, uint8_t b) {
    for (int i = 0; i < count; i++) {
        rgb_led_set_color(r, g, b);
        delay(80);
        rgb_led_off();
        delay(80);
    }
}
