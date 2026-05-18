# PXN V12 Lite SimHub LED Controller

A SimHub plugin that controls the built-in LED bar on the **PXN V12 Lite** racing wheel using live game RPM telemetry.

This project was created by reverse-engineering the PXN V12 Lite USB HID LED packet and integrating it with SimHub's telemetry system to provide proper RPM shift lights, limiter flashing, idle effects, and configurable LED behaviour.

> Unofficial community project. Not affiliated with, endorsed by, or supported by PXN or SimHub.

---

## Features

- RPM-based LED bar for the PXN V12 Lite
- Green, orange, and red RPM zones
- Hard limiter override mode
- Limiter flashing without RPM colour overlap
- Configurable limiter colour:
  - Red
  - Purple
  - Blue
  - White
  - Orange
  - Cyan
  - Pink
  - Yellow
  - Custom RGB
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
- Individual LED control for idle mode
- HID scan tool for finding VID/PID
- Live RPM diagnostics
- Save/reset settings
- Single-interface HID writing for lower resource usage

---

## Tested device

Tested on:

```text
Wheel: PXN V12 Lite
VID: 0x11FF
PID: 0x1112
LED packet header: 64 41 E0 02
Internal packet slots: 19 RGB slots
Visible LED bar: 10 LEDs
```

Other PXN V12 Lite firmware versions may expose a different HID interface or PID. The plugin includes a HID scan feature to help users find their own device details.

---

## Requirements

- Windows
- SimHub
- PXN V12 Lite racing wheel
- Visual Studio 2022, for building from source
- HidSharp.dll

---

## Installation

### Option 1: From release build

1. Download the release package.
2. Copy the plugin DLL into your SimHub installation folder:

```text
C:\Program Files (x86)\SimHub
```

3. Copy `HidSharp.dll` into the same folder.
4. Restart SimHub.
5. Go to:

```text
SimHub → Settings → Plugins
```

6. Enable:

```text
PXN V12 Lite LED Controller
```

---

### Option 2: Build from source

1. Open the project in Visual Studio 2022.
2. Make sure the project references `HidSharp`.
3. Build the project.
4. Copy the generated plugin DLL into:

```text
C:\Program Files (x86)\SimHub
```

5. Copy `HidSharp.dll` into the same SimHub folder.
6. Restart SimHub.
7. Enable the plugin in SimHub settings.

---

## Recommended starting settings

```text
HID target: Interface 1
USB update rate: 50 ms
RPM smoothing: 20%
Visible LEDs: 10
Brightness: 80–90%
RPM mode: Bar zones
Orange starts: 55%
Red starts: 80%
Limiter starts: 95%
Limiter mode: Flash limiter LEDs
Limiter colour: Purple
Limiter LED count: 2
Clear once when entering limiter: On
Clear once when exiting limiter: On
```

---

## How the RPM LED logic works

### Normal RPM mode

In normal RPM mode, the LED bar follows RPM percentage:

```text
Low RPM:    Green
Mid RPM:    Orange
High RPM:   Red
```

The plugin supports two RPM display modes:

| Mode | Behaviour |
|---|---|
| Bar zones | Individual zones show green, orange, and red across the LED bar |
| Whole bar stage | The active RPM bar changes as one colour based on RPM stage |

---

## Limiter behaviour

The limiter is designed as a **hard override**.

When RPM reaches the limiter threshold:

```text
Normal RPM LEDs stop.
Limiter-only LEDs activate.
No green/orange/red RPM colours are rendered underneath.
```

This prevents visual overlap such as:

```text
Red limiter flash → old green/orange RPM LEDs → red limiter flash
```

Available limiter modes:

| Mode | Behaviour |
|---|---|
| Flash limiter LEDs | Only selected edge LEDs flash |
| Solid limiter LEDs | Only selected edge LEDs stay solid |
| Flash full bar | Whole bar flashes |
| Solid full bar | Whole bar stays solid |
| Off | Limiter effect disabled |

---

## Idle modes

When no game telemetry is active, the plugin can display idle lighting.

Available idle modes:

| Mode | Behaviour |
|---|---|
| Off | All LEDs off |
| Dim green | Low brightness green |
| Rainbow idle | Moving rainbow effect |
| Custom individual LEDs | Choose each idle LED colour manually |
| Keep last colour | Leaves the previous LED state unchanged |

---

## HID / USB protocol notes

The tested wheel accepts a 63-byte HID output packet.

Basic packet structure:

```text
64 41 E0 02 [RGB data] [00 00]
```

The packet contains:

```text
4-byte header
19 RGB slots = 57 bytes
2-byte tail
Total = 63 bytes
```

Only the first 10 RGB slots appear to control the visible LED bar on the tested PXN V12 Lite unit.

---

## Troubleshooting

### LEDs do not respond

Try the following:

1. Close the PXN official app.
2. Restart SimHub.
3. Make sure the plugin is enabled.
4. Use the plugin's HID scan button.
5. Check the VID/PID fields.
6. Try HID target `Interface 1`.
7. If that fails, try:
   - `Interface 0`
   - `Interface 2`
   - `Interface 3`
   - `All interfaces`

---

### LEDs flicker or lag

Try:

```text
USB update rate: 50–70 ms
RPM smoothing: 20–35%
HID target: Interface 1
Brightness: 80–90%
```

Avoid using `All interfaces` unless needed.

---

Recommended settings:

```text
Limiter mode: Flash limiter LEDs
Clear once when entering limiter: On
Clear once when exiting limiter: On
Clear-frame delay: 5–8 ms
```

---

### Game RPM does not show

Check that SimHub is receiving telemetry for the game.

In SimHub:

```text
Games → Select your game → Configure game
```

Then launch the game and confirm SimHub shows live telemetry.

If the game does not provide max RPM, disable game max RPM or use fallback max RPM.

---

## Known limitations

- Tested on one PXN V12 Lite unit so far.
- Other firmware versions may use different HID interface ordering.
- The PXN official app may overwrite LED colours if open at the same time.
- Console mode is not supported.
- SimHub must receive game telemetry for RPM-based behaviour.
- This plugin uses an unofficial reverse-engineered HID packet.

---

## Development notes

Recommended Visual Studio setup:

```text
Visual Studio 2022
.NET desktop development workload
HidSharp NuGet package
SimHub PluginSdk demo project as base
```

## Roadmap ideas

- Better automatic HID interface detection
- Profile presets per game
- Gear-based LED effects
- Pit limiter effect
- Flag warning effects
- ABS/TC flash effects if telemetry is available
- Import/export settings
- Packaged release DLL and dependency

---

## Disclaimer

This is an unofficial community plugin.

It is not made, endorsed, or supported by PXN or SimHub. Use it at your own risk.

---
