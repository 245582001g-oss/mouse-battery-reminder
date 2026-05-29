# Sora V2 Battery Tip

English | [简体中文](README.zh-CN.md)

A tiny Windows tray utility for Ninjutso SORA V2 wireless mouse battery status and low-battery alerts.

The original goal is simple: while gaming, do not discover that the mouse is out of power only after it suddenly stops working.

## Current status

- Stable target device: **Ninjutso SORA V2**.
- Current build detects SORA V2 wired / wireless state through Windows HID device-interface notifications.
- Battery percentage is read through the SORA V2 HID feature-report path discovered during development.
- This project is **not yet a universal wireless mouse battery reader**.

## Why this project exists

Windows does not provide a reliable generic battery API for many 2.4 GHz gaming mice. Different vendors expose battery data through private HID reports, vendor-defined report pages, web drivers, desktop drivers, or proprietary protocols.

This project documents one practical path that worked for SORA V2 and keeps the code public so others can study, verify, and extend it.

## Core behavior

- Runs as a tray app.
- Left-click tray icon: run an immediate battery check.
- Right-click tray icon: adjust threshold, polling interval, alert cooldown, language, alert sound, volume, startup, and uninstall.
- Wired state: tray icon switches to a static charging icon.
- Wireless state: tray icon switches back to a battery icon based on the cached battery bucket.
- Battery icon redraw is bucketed by 5 percent to avoid unnecessary icon churn.
- Low battery alert uses user-selectable WAV sounds.
- Alert volume defaults to 15 percent unless the user changes it.
- Settings and sounds live in `Documents\SoraV2BatteryTip`.

## The important implementation lesson

The final smooth version avoids querying HID during USB plug/unplug transitions.

Earlier builds tried this:

```text
USB change
-> enumerate HID devices
-> open HID interfaces
-> send feature reports
-> parse battery
```

That caused mouse input to freeze briefly during cable unplug / reconnect because the receiver and Windows HID stack were still settling.

The stable version does this instead:

```text
Windows HID interface notification
-> parse device path string only
-> VID_1915 PID_AE12 means SORA V2 wired interface
-> VID_1915 PID_AE1C means SORA V2 wireless interface
-> update tray state immediately
-> do not open HID during plug/unplug
```

Battery reads are only done on:

- app startup
- timer polling
- manual immediate check

This separation is the key to keeping mouse input smooth.

## SORA V2 battery read path

The SORA V2 battery path uses HID feature reports:

1. Find SORA V2 HID devices by VID/PID.
2. Read the profile index with report `0x05`.
3. Request status with report `0x04` and the profile index.
4. Parse battery, charging, full, mode, and online bytes from the response payload.
5. Stabilize suspicious transient values such as `1%` during transitions.

The current known IDs are:

```text
VID: 0x1915
Wired PID:    0xAE12
Wireless PID: 0xAE1C
```

## Future work

The long-term goal is broader wireless mouse support. Help is welcome, especially from people familiar with HID, USB, WebHID, vendor-defined report pages, and gaming mouse firmware protocols.

Possible future directions:

- Provider-based architecture for multiple device families.
- Known device profiles for common gaming mice.
- Safe HID diagnostic mode.
- Learning mode that compares official driver / web driver battery percentage with raw HID report candidates.
- Community-submitted JSON profiles.
- Better generic detection of battery percentage, charging state, online state, and wired / wireless state.

## Build

Requires Windows and .NET 8 Desktop Runtime / SDK.

```powershell
dotnet publish .\src\SoraV2BatteryTip\SoraV2BatteryTip.csproj -c Release -r win-x64 --self-contained false -o .\releases\latest
```

Run:

```powershell
.\releases\latest\SoraV2BatteryTip.exe
```

## Data directory

```text
%USERPROFILE%\Documents\SoraV2BatteryTip
```

Contains:

- `settings.json`
- `status.json`
- `sounds\*.wav`

## License

MIT
