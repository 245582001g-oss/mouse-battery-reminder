# Profile contribution guide

Sora V2 Battery Tip supports local JSON profiles in:

`Documents\SoraV2BatteryTip\profiles`

Profiles are loaded by `KnownDeviceProfileProvider`. A profile must be explicit. The app does not automatically send commands to unknown devices.

## Safe read-only profile

Use this shape when Learning Mode finds a battery-like byte from a Feature Report without sending any command:

```json
{
  "name": "draft-example",
  "enabled": true,
  "priority": 50,
  "vendorId": "0x0000",
  "productIds": ["0x0000"],
  "productNameContains": ["Mouse Name"],
  "reportType": "Feature",
  "reportId": "0x00",
  "responseReportId": "0x00",
  "requestBytes": [],
  "sendRequest": false,
  "minFeatureLength": 64,
  "delayMs": 0,
  "payloadStarts": [0],
  "batteryOffset": 0,
  "chargingOffset": null,
  "fullOffset": null,
  "onlineOffset": null,
  "notes": "Generated from safe read-only Learning Mode. Verify before sharing."
}
```

## Active Feature Report profile

Only use `sendRequest: true` when the command is known to be safe for that device.

```json
{
  "name": "known-device-example",
  "enabled": true,
  "priority": 100,
  "vendorId": "0x0000",
  "productIds": ["0x0000"],
  "reportType": "Feature",
  "reportId": "0x04",
  "responseReportId": "0x04",
  "requestBytes": ["0x04", "0x26", "0x00", "0x01"],
  "sendRequest": true,
  "minFeatureLength": 73,
  "delayMs": 120,
  "payloadStarts": [9, 10],
  "batteryOffset": 7,
  "chargingOffset": 8,
  "fullOffset": 9,
  "onlineOffset": 11
}
```

## Validation expectations

Before submitting a profile:

1. Run `Device Profiles -> Collect Battery Candidates`.
2. Run `Device Profiles -> Import Latest Profile Drafts`.
3. Confirm this app matches the official driver within 1%.
4. Export diagnostics and include the ZIP in the issue.

## Safety rules

- Do not target standard boot mouse interfaces.
- Prefer `sendRequest: false` when possible.
- Use active requests only when the command is known.
- Keep polling intervals conservative.
