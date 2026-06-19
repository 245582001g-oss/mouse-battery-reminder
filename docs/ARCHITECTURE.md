# 鼠标电量提醒 (Mouse Battery Reminder)

English | [简体中文](README.zh-CN.md)

鼠标电量提醒 (Mouse Battery Reminder) is a lightweight Windows tray utility for wireless gaming mouse battery status and low-battery alerts.

The original goal is simple: while gaming, do not discover that the mouse is out of power only after it suddenly stops working.

## Current status

This project is **not a universal wireless mouse battery reader yet**. The current release focuses on two verified device families and keeps a fallback learning/profile path for other devices.

Verified / partially supported:

- **Ninjutso SORA V2 family** through the official HID feature-report protocol found in the vendor web driver.
- **ATK / COMPX-based 2.4G mice** through the COMPX HID battery command used by some 8K NANO dongles.
- Multiple connected supported mice can be shown together in the tray menu.
- Unknown devices can still use the local profile / automatic candidate-learning flow, but this is best-effort and requires user confirmation.

Known limitation:

- Many wireless mice use private HID reports, local driver bridges, vendor services, or encrypted/undocumented protocols. Without the actual mouse, a protocol can often be extracted from public web-driver code, but it cannot be fully verified.

## Supported protocols in this release

### Ninjutso SORA V2 official HID

The SORA V2 path now uses the official-style HID feature report observed in the NinjaForce web driver.

Known VID/PID family:

```text
VID: 0x1915
Known / expected product IDs include: 0xAE11-0xAE16, 0xAE1C, 0xAE8A, 0xAE8C
```

Battery query:

```text
Feature report ID: 0x05
Command byte:       0x15
Request payload:    15 00 00 01 00 00 04 ...
Battery byte:       response[9]
Charging byte:      response[10] == 1
```

The older learned `draft-1915-*` profiles are intentionally ignored when this built-in provider matches, so SORA V2 does not depend on a guessed offset anymore.

### ATK / COMPX HID

Some ATK / COMPX 2.4G dongles expose battery through a vendor HID command.

Known VID family:

```text
VID: 0x373B
Example product: Wireless mouse 8k NANO dongle-L
```

Implementation notes:

```text
Report ID:      0x08
Command:        0x04
Payload length: 16 bytes
Battery byte:   parsed from the command response payload
Charging byte:  parsed from the command response payload
```

This provider is verified only on the devices available during development. It should be treated as partial support for ATK/COMPX-style devices, not a promise that every ATK mouse works.

## Core behavior

- Runs as a Windows tray app.
- Left-click tray icon: run an immediate battery check.
- Right-click tray icon: adjust threshold, polling interval, alert cooldown, language, alert sound, volume, startup, profile tools, and uninstall.
- Shows one or more supported mice when multiple devices are connected.
- Wired / charging state uses a static charging tray icon.
- Wireless state uses a battery tray icon bucketed by 5 percent to avoid unnecessary icon churn.
- Low-battery alerts use user-selectable WAV sounds.
- Alert volume defaults to 15 percent unless the user changes it.
- Settings, profiles, sounds, logs, and history live in `Documents\SoraV2BatteryTip` (kept as a stable compatibility directory).

## Stability principle

The smooth version avoids querying HID during USB plug/unplug transitions.

Earlier builds tried this:

```text
USB change
-> enumerate HID devices
-> open HID interfaces
-> send feature reports
-> parse battery
```

That can briefly disturb mouse input while Windows and the receiver are still settling.

The current design separates the two jobs:

```text
USB/HID notification
-> schedule/update state cheaply
-> avoid heavy HID probing during the transition

Startup / timer / manual check
-> query battery through providers
-> update tray state and history
```

This is the key implementation lesson from the project.

## Architecture

Battery reading is provider-based:

```text
NinjutsoSoraOfficialProvider
-> CompxBatteryProvider
-> KnownDeviceProfileProvider / learned local JSON profiles
```

The app prefers verified built-in protocols first. Local learned profiles are useful for unsupported devices, but they are lower priority and can be replaced by built-in providers later.

## Data directory

```text
%USERPROFILE%\Documents\SoraV2BatteryTip
```

Contains:

- `settings.json`
- `status.json`
- `sounds\*.wav`
- `profiles\*.json`
- battery history and diagnostics exports

## Build

Requires Windows and .NET 8 Desktop Runtime / SDK.

```powershell
dotnet publish .\src\SoraV2BatteryTip\SoraV2BatteryTip.csproj -c Release -r win-x64 --self-contained false -o .\releases\latest
```

Run:

```powershell
.\releases\latest\SoraV2BatteryTip.exe
```

## Future work

The long-term goal is broader wireless mouse support. Help is welcome, especially from people familiar with HID, USB, WebHID, vendor-defined reports, and gaming mouse firmware protocols.

Useful contributions:

- Verified protocols for more mouse brands.
- Safe diagnostic captures with VID/PID, report lengths, and anonymized raw responses.
- JSON profiles for devices that do not need a built-in provider yet.
- Better charging/full/online-state detection.
- Review of vendor web-driver HID logic.

## License

MIT
