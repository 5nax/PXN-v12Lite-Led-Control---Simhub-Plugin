# PXN V12 Lite SimHub LED Controller

A SimHub plugin that controls the built-in LED bar on the PXN V12 Lite racing wheel using live game RPM telemetry.

This plugin was created by reverse-engineering the PXN V12 Lite USB HID LED packet and integrating it with SimHub's game telemetry system.

## Features

- RPM-based LED bar
- Green, orange, and red RPM zones
- Hard limiter override mode
- Limiter flashing with custom colour
- Purple, red, blue, white, orange, cyan, pink, yellow, and custom RGB limiter colours
- Configurable limiter LED count
- Configurable brightness
- Reverse LED order option
- Adjustable USB update rate
- RPM smoothing
- Fallback max RPM setting
- Use game max RPM when available
- Idle modes:
  - Off
  - Dim green
  - Rainbow idle
  - Custom individual LEDs
  - Keep last colour
- HID scan tool for finding VID/PID
- Live RPM diagnostics
- Save/reset settings

## Tested device

Tested on:

```text
Wheel: PXN V12 Lite
VID: 0x11FF
PID: 0x1112
LED packet header: 64 41 E0 02
Internal packet slots: 19 RGB slots
Visible LED bar: 10 LEDs
