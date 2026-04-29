# Build & Flash

## Compile

```
arduino-cli compile \
  --fqbn rp2040:rp2040:rpipico \
  --board-options "flash=2097152_1048576,usbstack=tinyusb" \
  --build-path firmware_cpp/build \
  firmware_cpp
```

## Flash (BOOTSEL mode)

Copy `firmware_cpp/build/firmware_cpp.ino.uf2` to the RPI-RP2 drive.

## Flash (serial — no button press)

```
REBOOT BOOTSEL\n
```

Send via any serial terminal at 115200 baud with DTR asserted. The Pico will
reboot into BOOTSEL mode and appear as RPI-RP2. Then copy the .uf2.

## Serial interface notes

- Baud: 115200, DTR must be asserted (pyserial default)
- Commands are newline-terminated (`\n`)
- Responses arrive within ~1 second

## Key partition decisions

| Setting | Value | Reason |
|---------|-------|--------|
| FS size | 1MB (`2097152_1048576`) | f_mkfs requires ≥128 logical sectors; 64KB only gave 16 |
| useFTL  | false | SPIFTL cannot parse pre-existing non-SPIFTL flash content |
