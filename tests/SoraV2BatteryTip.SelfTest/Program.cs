using System.Text;
using System.Text.Json;
using HidSharp;
using SoraV2BatteryTip;

namespace SoraV2BatteryTip.SelfTest;

internal static class Program
{
    private static readonly DateTime StartUtc = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("alert:first low sample plays", AlertFirstLowSamplePlays),
            ("alert:batch coalesces and arms every eligible device", AlertBatchCoalescesAndArmsEveryDevice),
            ("alert:cooldown delays a lower bucket", AlertCooldownDelaysLowerBucket),
            ("alert:external power resets device state", AlertExternalPowerResetsState),
            ("alert:device state is independent", AlertDeviceStateIsIndependent),
            ("alert:invalid inputs are suppressed", AlertInvalidInputsAreSuppressed),
            ("alert:explicit settings change the threshold", AlertExplicitSettingsChangeThreshold),
            ("alert:explicit reset permits same battery again", AlertExplicitResetPermitsSameBatteryAgain),
            ("polling:no charging uses configured normal interval", PollingNoChargingUsesNormalInterval),
            ("polling:first charging device starts fast window", PollingFirstChargingStartsFastWindow),
            ("polling:stable keys ignore ordering", PollingStableKeysIgnoreOrdering),
            ("polling:fast window expires to steady", PollingFastWindowExpiresToSteady),
            ("polling:new charging device extends fast window", PollingNewDeviceExtendsFastWindow),
            ("polling:display off and full rules win", PollingDisplayOffAndFullRulesWin),
            ("polling:stopping and re-entering charging restarts fast", PollingReentryRestartsFastWindow),
            ("state:same-name devices survive one-sided read failure", StateSameNameDevicesSurviveOneSidedFailure),
            ("state:unique missing-serial device rekeys old path to new", StateUniqueMissingSerialDeviceRekeys),
            ("state:real serial rekeys across paths", StateRealSerialRekeysAcrossPaths),
            ("state:serial promotion emits runtime identity transition", StateSerialPromotionEmitsRuntimeIdentityTransition),
            ("state:provider offline keeps last-known facts in an offline snapshot", StateProviderOfflineKeepsLastKnownFacts),
            ("state:reliable candidate absence keeps last-known facts in an offline snapshot", StateCandidateAbsenceKeepsLastKnownFacts),
            ("coverage:same-name devices require one-to-one readings", CoverageSameNameDevicesRequireDistinctReadings),
            ("coverage:unread candidate keeps device refresh retrying", CoverageUnreadCandidateKeepsRefreshRetrying),
            ("refresh:arrival cannot succeed on old device sample", RefreshArrivalCannotSucceedOnOldDeviceSample),
            ("refresh:completion waits until state is applied", RefreshCompletionWaitsUntilStateApplied),
            ("refresh:forced read failure retries then degrades", RefreshForcedReadFailureRetriesThenDegrades),
            ("refresh:two arrivals require both to resolve", RefreshTwoArrivalsRequireBothToResolve),
            ("refresh:ambiguous fresh paths cannot replace an unresolved arrival", RefreshAmbiguousFreshPathsCannotReplaceUnresolvedArrival),
            ("refresh:removal succeeds without a fresh sample", RefreshRemovalSucceedsWithoutFreshSample),
            ("refresh:candidate arrival succeeds after target read", RefreshCandidateArrivalSucceedsAfterTargetRead),
            ("refresh:unrelated HID arrival is reported as skipped", RefreshUnrelatedHidArrivalIsSkipped),
            ("refresh:forced no-device poll completes normally", RefreshForcedNoDevicePollCompletesNormally),
            ("power:power-state charging suppresses low-battery alert", PowerStateChargingSuppressesLowBatteryAlert),
            ("alert:identity transition preserves suppression", AlertIdentityTransitionPreservesSuppression),
            ("polling:identity transition preserves fast deadline", PollingIdentityTransitionPreservesFastDeadline),
            ("history:placeholder serials use distinct path keys", HistoryPlaceholderSerialsUseDistinctPathKeys),
            ("history:power state uses unified charging semantics", HistoryPowerStateUsesUnifiedChargingSemantics),
            ("inventory:failed snapshot retries after two seconds", InventoryFailedSnapshotRetriesAfterTwoSeconds),
            ("inventory:reliable snapshot caches until invalidated", InventoryReliableSnapshotCachesUntilInvalidated),
            ("flight:flushed lines have valid schema", FlightRecorderFlushedLinesHaveValidSchema),
            ("flight:operation scope crosses Task.Run", FlightRecorderOperationScopeCrossesTaskRun),
            ("flight:raw device identity is readable", FlightRecorderRecordsRawDeviceIdentity),
            ("flight:power transitions retain complete device facts", FlightRecorderPowerTransitionsRetainCompleteDeviceFacts),
            ("flight:sensitive field variants are classified", FlightRecorderClassifiesSensitiveFieldVariants),
            ("flight:concurrent file sequence is monotonic", FlightRecorderConcurrentSequenceIsMonotonic),
            ("flight:snapshot reprojects legacy records without losing facts", FlightRecorderSnapshotReprojectsLegacyFacts),
            ("flight:key migrates outside public logs", FlightRecorderKeyMigratesOutsidePublicLogs),
            ("flight:crash facts are write-through before history", FlightRecorderCrashFactsPrecedeHistory),
            ("flight:clean shutdown marker controls recovery warning", FlightRecorderCleanMarkerControlsRecoveryWarning),
            ("flight:dispose ends with logger.stopping", FlightRecorderDisposeEndsWithStopping),
            ("flight:active file supports shared reads", FlightRecorderActiveFileSupportsSharedReads),
            ("diagnostics:local facts are retained", DiagnosticsLocalFactsAreRetained)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"RESULT passed={tests.Length - failures} failed={failures} total={tests.Length}");
        return failures == 0 ? 0 : 1;
    }

    private static void AlertFirstLowSamplePlays()
    {
        var result = new LowBatteryAlertTracker().Update(
            new[] { AlertDevice("mouse-a", 10) },
            StartUtc,
            AlertSettings());

        True(result.ShouldPlaySound, "first low sample should request sound");
        Decision(result, "mouse-a", LowBatteryAlertDisposition.Played, "low_battery");
    }

    private static void AlertBatchCoalescesAndArmsEveryDevice()
    {
        var tracker = new LowBatteryAlertTracker();
        var settings = AlertSettings();
        var first = tracker.Update(
            new[] { AlertDevice("mouse-a", 8), AlertDevice("mouse-b", 4) },
            StartUtc,
            settings);

        True(first.ShouldPlaySound, "eligible batch should request one sound");
        Equal(1, first.Decisions.Count(item => item.Disposition == LowBatteryAlertDisposition.Played), "batch must have exactly one played decision");
        Decision(first, "mouse-b", LowBatteryAlertDisposition.Played, "low_battery");
        Decision(first, "mouse-a", LowBatteryAlertDisposition.Suppressed, "batch_coalesced");

        var second = tracker.Update(
            new[] { AlertDevice("mouse-a", 8), AlertDevice("mouse-b", 4) },
            StartUtc.AddMinutes(30),
            settings);

        False(second.ShouldPlaySound, "coalesced devices must both be armed for later rounds");
        Decision(second, "mouse-a", LowBatteryAlertDisposition.Suppressed, "same_or_higher_bucket");
        Decision(second, "mouse-b", LowBatteryAlertDisposition.Suppressed, "same_or_higher_bucket");
    }

    private static void AlertCooldownDelaysLowerBucket()
    {
        var tracker = new LowBatteryAlertTracker();
        var settings = AlertSettings(cooldownMinutes: 10);
        tracker.Update(new[] { AlertDevice("mouse-a", 10) }, StartUtc, settings);

        var duringCooldown = tracker.Update(new[] { AlertDevice("mouse-a", 4) }, StartUtc.AddMinutes(5), settings);
        False(duringCooldown.ShouldPlaySound, "lower bucket must wait for cooldown");
        Decision(duringCooldown, "mouse-a", LowBatteryAlertDisposition.Suppressed, "cooldown");

        var afterCooldown = tracker.Update(new[] { AlertDevice("mouse-a", 4) }, StartUtc.AddMinutes(10), settings);
        True(afterCooldown.ShouldPlaySound, "lower bucket should play when cooldown expires");
        Decision(afterCooldown, "mouse-a", LowBatteryAlertDisposition.Played, "low_battery");
    }

    private static void AlertExternalPowerResetsState()
    {
        var tracker = new LowBatteryAlertTracker();
        var settings = AlertSettings();
        tracker.Update(new[] { AlertDevice("mouse-a", 5) }, StartUtc, settings);

        var powered = tracker.Update(
            new[] { AlertDevice("mouse-a", 5, isCharging: true) },
            StartUtc.AddMinutes(1),
            settings);
        False(powered.ShouldPlaySound, "powered device should not alert");
        Decision(powered, "mouse-a", LowBatteryAlertDisposition.Reset, "external_power");

        var unplugged = tracker.Update(new[] { AlertDevice("mouse-a", 5) }, StartUtc.AddMinutes(2), settings);
        True(unplugged.ShouldPlaySound, "reset device should alert after external power is removed");
    }

    private static void AlertDeviceStateIsIndependent()
    {
        var tracker = new LowBatteryAlertTracker();
        var settings = AlertSettings();
        tracker.Update(new[] { AlertDevice("mouse-a", 5) }, StartUtc, settings);

        var otherDevice = tracker.Update(new[] { AlertDevice("mouse-b", 5) }, StartUtc.AddMinutes(1), settings);
        True(otherDevice.ShouldPlaySound, "one device must not suppress another device");
        Decision(otherDevice, "mouse-b", LowBatteryAlertDisposition.Played, "low_battery");
    }

    private static void AlertInvalidInputsAreSuppressed()
    {
        var result = new LowBatteryAlertTracker().Update(
            new[]
            {
                AlertDevice("", 5),
                new LowBatteryAlertInput("mouse-a", 0),
                new LowBatteryAlertInput("mouse-b", 50, HasBatteryPercentage: false)
            },
            StartUtc,
            AlertSettings());

        False(result.ShouldPlaySound, "invalid inputs must never request sound");
        Equal("invalid_device_key", result.Decisions[0].Reason, "blank key reason");
        Equal("invalid_battery", result.Decisions[1].Reason, "out of range reason");
        Equal("invalid_battery", result.Decisions[2].Reason, "missing battery reason");
    }

    private static void AlertExplicitSettingsChangeThreshold()
    {
        var tracker = new LowBatteryAlertTracker();
        var above = tracker.Update(new[] { AlertDevice("mouse-a", 12) }, StartUtc, AlertSettings(threshold: 10));
        Decision(above, "mouse-a", LowBatteryAlertDisposition.Reset, "above_threshold");

        var low = tracker.Update(new[] { AlertDevice("mouse-a", 12) }, StartUtc, AlertSettings(threshold: 15));
        True(low.ShouldPlaySound, "explicit threshold should be used for this update");
    }

    private static void AlertExplicitResetPermitsSameBatteryAgain()
    {
        var tracker = new LowBatteryAlertTracker();
        var settings = AlertSettings(cooldownMinutes: 30);
        var first = tracker.Update(new[] { AlertDevice("mouse-a", 5) }, StartUtc, settings);
        True(first.ShouldPlaySound, "first low sample should play");

        True(tracker.Reset(" mouse-a "), "explicit reset should remove the tracked device state");
        var repeated = tracker.Update(new[] { AlertDevice("mouse-a", 5) }, StartUtc.AddMinutes(1), settings);

        True(repeated.ShouldPlaySound, "same battery should play again after explicit reset, even within cooldown");
        Decision(repeated, "mouse-a", LowBatteryAlertDisposition.Played, "low_battery");
        False(tracker.Reset("missing-device"), "resetting an unknown device should report no change");
    }

    private static void PollingNoChargingUsesNormalInterval()
    {
        var result = new ChargingPollingPolicy().Evaluate(
            Array.Empty<ChargingPollingDevice>(),
            displayActive: false,
            StartUtc,
            PollingSettings(7));

        Equal(ChargingPollingReason.Normal, result.Reason, "no charging reason");
        Equal(TimeSpan.FromMinutes(7), result.Interval, "normal interval");
        Equal(DateTime.MinValue, result.FastUntilUtc, "normal state should not retain a fast window");
    }

    private static void PollingFirstChargingStartsFastWindow()
    {
        var result = new ChargingPollingPolicy().Evaluate(
            new[] { ChargingDevice("mouse-a") },
            displayActive: true,
            StartUtc,
            PollingSettings());

        Equal(ChargingPollingReason.FastCharging, result.Reason, "new charging reason");
        Equal(TimeSpan.FromSeconds(10), result.Interval, "fast interval");
        Equal(StartUtc.AddMinutes(1), result.FastUntilUtc, "fast deadline");
        SequenceEqual(new[] { "mouse-a" }, result.NewlyChargingDeviceKeys, "new charging keys");
    }

    private static void PollingStableKeysIgnoreOrdering()
    {
        var policy = new ChargingPollingPolicy();
        policy.Evaluate(
            new[] { ChargingDevice("mouse-a"), ChargingDevice("mouse-b") },
            true,
            StartUtc,
            PollingSettings());

        var reordered = policy.Evaluate(
            new[] { ChargingDevice("mouse-b"), ChargingDevice("mouse-a") },
            true,
            StartUtc.AddSeconds(20),
            PollingSettings());

        Equal(0, reordered.NewlyChargingDeviceKeys.Count, "reordering stable keys must not look like a new device");
        Equal(StartUtc.AddMinutes(1), reordered.FastUntilUtc, "reordering must not extend the deadline");
    }

    private static void PollingFastWindowExpiresToSteady()
    {
        var policy = new ChargingPollingPolicy();
        policy.Evaluate(new[] { ChargingDevice("mouse-a") }, true, StartUtc, PollingSettings());

        var result = policy.Evaluate(
            new[] { ChargingDevice("mouse-a") },
            true,
            StartUtc.AddMinutes(1),
            PollingSettings());

        Equal(ChargingPollingReason.SteadyCharging, result.Reason, "deadline should switch to steady");
        Equal(TimeSpan.FromMinutes(1), result.Interval, "steady interval");
    }

    private static void PollingNewDeviceExtendsFastWindow()
    {
        var policy = new ChargingPollingPolicy();
        policy.Evaluate(new[] { ChargingDevice("mouse-a") }, true, StartUtc, PollingSettings());

        var extended = policy.Evaluate(
            new[] { ChargingDevice("mouse-a"), ChargingDevice("mouse-b") },
            true,
            StartUtc.AddSeconds(50),
            PollingSettings());

        SequenceEqual(new[] { "mouse-b" }, extended.NewlyChargingDeviceKeys, "second device should be new");
        Equal(StartUtc.AddSeconds(110), extended.FastUntilUtc, "second device should extend fast window from its entry time");

        var stillFast = policy.Evaluate(
            new[] { ChargingDevice("mouse-a"), ChargingDevice("mouse-b") },
            true,
            StartUtc.AddSeconds(100),
            PollingSettings());
        Equal(ChargingPollingReason.FastCharging, stillFast.Reason, "extended window should remain fast");
    }

    private static void PollingDisplayOffAndFullRulesWin()
    {
        var policy = new ChargingPollingPolicy();
        var displayOff = policy.Evaluate(
            new[] { ChargingDevice("mouse-a") },
            displayActive: false,
            StartUtc,
            PollingSettings());
        Equal(ChargingPollingReason.DisplayOff, displayOff.Reason, "display off should override active fast polling");
        Equal(TimeSpan.FromMinutes(1), displayOff.Interval, "display off interval");

        var full = policy.Evaluate(
            new[] { ChargingDevice("mouse-a", isFullyCharged: true) },
            displayActive: false,
            StartUtc.AddSeconds(5),
            PollingSettings());
        Equal(ChargingPollingReason.FullyCharged, full.Reason, "full should override display off");
        Equal(TimeSpan.FromMinutes(5), full.Interval, "full interval");
    }

    private static void PollingReentryRestartsFastWindow()
    {
        var policy = new ChargingPollingPolicy();
        policy.Evaluate(new[] { ChargingDevice("mouse-a") }, true, StartUtc, PollingSettings());

        var stopped = policy.Evaluate(
            new[] { ChargingDevice("mouse-a", isCharging: false) },
            true,
            StartUtc.AddSeconds(30),
            PollingSettings());
        Equal(ChargingPollingReason.Normal, stopped.Reason, "stopped charging should restore normal interval");
        Equal(DateTime.MinValue, stopped.FastUntilUtc, "stopped charging should clear fast deadline");

        var reentered = policy.Evaluate(
            new[] { ChargingDevice("mouse-a") },
            true,
            StartUtc.AddMinutes(5),
            PollingSettings());
        Equal(ChargingPollingReason.FastCharging, reentered.Reason, "re-entry should restart fast polling");
        Equal(StartUtc.AddMinutes(6), reentered.FastUntilUtc, "re-entry deadline");
    }

    private static void StateSameNameDevicesSurviveOneSidedFailure()
    {
        var store = new DeviceBatteryStateStore();
        var first = StateResult(
            new[]
            {
                StateReading("path-a", 80, deviceName: "Same Mouse"),
                StateReading("path-b", 60, deviceName: "Same Mouse")
            },
            new[] { "path-a", "path-b" });
        var initial = store.Apply(first, StartUtc, TimeSpan.FromMinutes(10));
        Equal(2, initial.CurrentReadings.Count, "initial device count");

        var oneSided = StateResult(
            new[] { StateReading("path-a", 79, deviceName: "Same Mouse") },
            new[] { "path-a", "path-b" });
        var update = store.Apply(oneSided, StartUtc.AddMinutes(1), TimeSpan.FromMinutes(10));

        Equal(2, update.CurrentReadings.Count, "missing sibling must remain visible");
        Equal(0, update.OfflineReadings.Count, "present candidate must not be marked offline");
        Equal(0, update.Rekeys.Count, "same-name siblings must not be merged");
        var failedSibling = update.CurrentReadings.Single(reading => reading.DeviceId == "path-b");
        Equal(1, failedSibling.ConsecutiveFailures, "missing sibling failure count");
        Equal(BatteryDataFreshness.Fresh, failedSibling.Freshness, "one failure should not yet be stale");
    }

    private static void StateUniqueMissingSerialDeviceRekeys()
    {
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { StateReading("path-old", 70, deviceName: "Solo Mouse") },
                new[] { "path-old" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var update = store.Apply(
            StateResult(
                new[] { StateReading("path-new", 69, deviceName: "Solo Mouse") },
                new[] { "path-new" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, update.CurrentReadings.Count, "rekey should leave one current device");
        Equal("path-new", update.CurrentReadings[0].DeviceId, "current path after rekey");
        Equal(1, update.Rekeys.Count, "unique old path should rekey exactly once");
        Equal("test-provider|path-old", update.Rekeys[0].PreviousKey, "previous runtime key");
        Equal("test-provider|path-new", update.Rekeys[0].CurrentKey, "current runtime key");
        Equal(0, update.OfflineReadings.Count, "rekey is not an offline transition");
    }

    private static void StateRealSerialRekeysAcrossPaths()
    {
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { StateReading("path-old", 55, serial: " Serial-123 ", deviceName: "Old Label") },
                new[] { "path-old" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var update = store.Apply(
            StateResult(
                new[] { StateReading("path-new", 54, serial: "serial-123", deviceName: "New Label") },
                new[] { "path-old", "path-new" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, update.CurrentReadings.Count, "serial rekey should leave one current device");
        Equal("path-new", update.CurrentReadings[0].DeviceId, "serial rekey current path");
        Equal(1, update.Rekeys.Count, "normalized real serial should rekey across paths");
        Equal("path-old", update.Rekeys[0].PreviousReading.DeviceId, "serial rekey previous reading");
        Equal("path-new", update.Rekeys[0].CurrentReading.DeviceId, "serial rekey current reading");
        Equal(0, update.OfflineReadings.Count, "serial rekey is not offline");
    }

    private static void StateSerialPromotionEmitsRuntimeIdentityTransition()
    {
        var store = new DeviceBatteryStateStore();
        var previous = StateReading("path-a", 42, serial: "0000", deviceName: "Solo Mouse");
        store.Apply(
            StateResult(new[] { previous }, new[] { "path-a" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var current = StateReading("path-a", 41, serial: "real-serial", deviceName: "Solo Mouse");
        var update = store.Apply(
            StateResult(new[] { current }, new[] { "path-a" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, update.Rekeys.Count, "serial promotion on a stable path should emit one identity transition");
        NotEqual(
            DeviceIdentity.CreateRuntimeKey(update.Rekeys[0].PreviousReading),
            DeviceIdentity.CreateRuntimeKey(update.Rekeys[0].CurrentReading),
            "runtime identity should move from path to stable serial");
    }

    private static void StateProviderOfflineKeepsLastKnownFacts()
    {
        const string rawPath = @"\\?\hid#vid_1234&pid_5678#PROVIDER-OFFLINE-PATH#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string rawSerial = "PROVIDER-OFFLINE-SERIAL";
        using var temporary = new TemporaryDirectory();
        var logs = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logs);
        using var recorder = new FlightRecorder(logs, Path.Combine(temporary.Path, ".flight-recorder.key"));
        var store = new DeviceBatteryStateStore(recorder);
        var online = OfflineTransitionReading(
            rawPath,
            rawSerial,
            battery: 67,
            online: true,
            powerState: DevicePowerState.Charging,
            hasBatteryPercentage: true,
            externalPower: true,
            timestampUtc: StartUtc);
        store.Apply(StateResult(new[] { online }, new[] { rawPath }), StartUtc, TimeSpan.FromMinutes(10));

        var providerOffline = OfflineTransitionReading(
            rawPath,
            rawSerial,
            battery: 2,
            online: false,
            powerState: DevicePowerState.Offline,
            hasBatteryPercentage: false,
            externalPower: true,
            timestampUtc: StartUtc.AddMinutes(1));
        var update = store.Apply(
            StateResult(new[] { providerOffline }, new[] { rawPath }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        var offline = AssertSingleOfflineUpdate(update, rawPath, rawSerial, 67, StartUtc, "provider offline");
        Equal("Offline Test Mouse", offline.DeviceName, "provider offline device name");

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "provider-offline event should flush");
        AssertOfflineEvent(
            FindEvent(ReadJsonLinesShared(Path.Combine(logs, "flight-current.jsonl")), "device.state_offline"),
            rawPath,
            rawSerial,
            67,
            "provider_reported_offline",
            "provider_offline_reading",
            "Charging");
    }

    private static void StateCandidateAbsenceKeepsLastKnownFacts()
    {
        const string rawPath = @"\\?\hid#vid_1234&pid_5678#CANDIDATE-ABSENCE-PATH#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string rawSerial = "CANDIDATE-ABSENCE-SERIAL";
        using var temporary = new TemporaryDirectory();
        var logs = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logs);
        using var recorder = new FlightRecorder(logs, Path.Combine(temporary.Path, ".flight-recorder.key"));
        var store = new DeviceBatteryStateStore(recorder);
        var online = OfflineTransitionReading(
            rawPath,
            rawSerial,
            battery: 72,
            online: true,
            powerState: DevicePowerState.FullyCharged,
            hasBatteryPercentage: true,
            externalPower: true,
            timestampUtc: StartUtc);
        store.Apply(StateResult(new[] { online }, new[] { rawPath }), StartUtc, TimeSpan.FromMinutes(10));

        var update = store.Apply(
            StateResult(Array.Empty<BatteryReading>(), new[] { "different-present-candidate" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        var offline = AssertSingleOfflineUpdate(update, rawPath, rawSerial, 72, StartUtc, "candidate absence");
        Equal("Offline Test Mouse", offline.DeviceName, "candidate absence device name");

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "candidate-absence event should flush");
        AssertOfflineEvent(
            FindEvent(ReadJsonLinesShared(Path.Combine(logs, "flight-current.jsonl")), "device.state_offline"),
            rawPath,
            rawSerial,
            72,
            "candidate_removed",
            "inventory_candidate_absence",
            "FullyCharged");
    }

    private static BatteryReading AssertSingleOfflineUpdate(
        DeviceStateUpdate update,
        string expectedPath,
        string expectedSerial,
        int expectedBattery,
        DateTime expectedLastSuccessfulReadUtc,
        string message)
    {
        Equal(0, update.CurrentReadings.Count, $"{message} current readings");
        Equal(0, update.FreshReadings.Count, $"{message} fresh readings");
        False(update.HasFreshSamples, $"{message} must not be a fresh sample");
        Equal(1, update.OfflineReadings.Count, $"{message} offline reading count");
        var offline = update.OfflineReadings[0];
        False(offline.IsOnline, $"{message} IsOnline");
        Equal(DevicePowerState.Offline, offline.PowerState, $"{message} power state");
        False(offline.IsCharging, $"{message} charging flag");
        False(offline.IsFullyCharged, $"{message} fully-charged flag");
        False(offline.IsCableConnected, $"{message} cable flag");
        Equal<bool?>(false, offline.ExternalPowerConnected, $"{message} external-power flag");
        Equal(BatteryDataFreshness.Stale, offline.Freshness, $"{message} freshness");
        Equal(expectedBattery, offline.BatteryPercentage, $"{message} last-known battery");
        True(offline.HasBatteryPercentage, $"{message} must retain last-known battery validity");
        Equal(expectedLastSuccessfulReadUtc, offline.LastSuccessfulReadUtc, $"{message} last-success timestamp");
        Equal(expectedPath, offline.DeviceId, $"{message} raw path");
        Equal(expectedSerial, offline.DeviceSerial, $"{message} raw serial");
        return offline;
    }

    private static void AssertOfflineEvent(
        JsonElement entry,
        string expectedPath,
        string expectedSerial,
        int expectedBattery,
        string expectedReason,
        string expectedFactBasis,
        string expectedPreviousPowerState)
    {
        Equal("success", RequiredProperty(entry, "outcome", JsonValueKind.String).GetString(), "offline event outcome");
        var device = RequiredProperty(entry, "device", JsonValueKind.Object);
        Equal("offline", RequiredProperty(device, "power_state", JsonValueKind.String).GetString(), "offline event envelope power state");
        Equal(expectedBattery, RequiredProperty(device, "battery_percentage", JsonValueKind.Number).GetInt32(), "offline event last-known battery");
        Equal(expectedPath, RequiredProperty(device, "path", JsonValueKind.String).GetString(), "offline event raw path");
        Equal(expectedSerial, RequiredProperty(device, "serial", JsonValueKind.String).GetString(), "offline event raw serial");

        var data = RequiredProperty(entry, "data", JsonValueKind.Object);
        Equal(expectedReason, RequiredProperty(data, "reason", JsonValueKind.String).GetString(), "offline event reason");
        Equal(expectedBattery, RequiredProperty(data, "previous_battery_percentage", JsonValueKind.Number).GetInt32(), "offline event previous battery");
        Equal(expectedPreviousPowerState, RequiredProperty(data, "previous_power_state", JsonValueKind.String).GetString(), "offline event previous power state");
        Equal(expectedFactBasis, RequiredProperty(data, "fact_basis", JsonValueKind.String).GetString(), "offline event fact basis");
        Equal("last_known_online_sample", RequiredProperty(data, "battery_fact", JsonValueKind.String).GetString(), "offline event battery fact");
    }

    private static void CoverageSameNameDevicesRequireDistinctReadings()
    {
        var previous = new[]
        {
            StateReading("path-a", 80, deviceName: "Same Mouse"),
            StateReading("path-b", 60, deviceName: "Same Mouse")
        };

        False(
            DeviceIdentity.PreviousReadingsCoveredBy(previous, new[] { StateReading("path-a", 79, deviceName: "Same Mouse") }),
            "one current reading must not cover two identical prior devices");
        True(
            DeviceIdentity.PreviousReadingsCoveredBy(previous, new[]
            {
                StateReading("path-b", 59, deviceName: "Same Mouse"),
                StateReading("path-a", 79, deviceName: "Same Mouse")
            }),
            "two distinct current readings should cover both prior devices regardless of order");
    }

    private static void CoverageUnreadCandidateKeepsRefreshRetrying()
    {
        var oneReading = StateReading("path-a", 79);
        var partial = new[]
        {
            new ProviderBatchResult
            {
                ProviderName = "test-provider",
                CandidateFound = true,
                CandidateDeviceIds = new[] { "path-a", "path-b" },
                Readings = new[] { oneReading }
            }
        };
        False(DeviceIdentity.CandidatePathsCoveredBy(partial), "unread path-b candidate should keep refresh retries active");

        var complete = new[]
        {
            new ProviderBatchResult
            {
                ProviderName = "test-provider",
                CandidateFound = true,
                CandidateDeviceIds = new[] { "path-a", "path-b" },
                Readings = new[] { oneReading, StateReading("path-b", 61) }
            }
        };
        True(DeviceIdentity.CandidatePathsCoveredBy(complete), "every candidate path has a returned reading");
    }

    private static void RefreshArrivalCannotSucceedOnOldDeviceSample()
    {
        var events = new[] { new DeviceRefreshEvent("path-b", Arrived: true, WasTracked: false) };
        var baseline = new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase);
        var inventory = new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase);
        var poll = RefreshPoll(new[] { "path-a" }, new[] { "path-a" });

        var early = DeviceRefreshPolicy.Evaluate(events, baseline, inventory, true, poll, finalAttempt: false);
        False(early.IsComplete, "an old mouse sample must not complete a new arrival refresh");

        var final = DeviceRefreshPolicy.Evaluate(events, baseline, inventory, true, poll, finalAttempt: true);
        True(final.IsComplete, "the bounded settling window should terminate");
        Equal("degraded", final.Outcome, "missing arrival outcome");
        Equal("arrival_not_present_after_settling", final.Reason, "missing arrival reason");
    }

    private static void RefreshCompletionWaitsUntilStateApplied()
    {
        var events = new[] { new DeviceRefreshEvent("path-b", Arrived: true, WasTracked: false) };
        var baseline = new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase);
        var inventory = new HashSet<string>(new[] { "path-a", "path-b" }, StringComparer.OrdinalIgnoreCase);
        var poll = RefreshPoll(new[] { "path-b" }, new[] { "path-b" }, stateApplied: false);

        var early = DeviceRefreshPolicy.Evaluate(events, baseline, inventory, true, poll, finalAttempt: false);
        False(early.IsComplete, "a read that was deliberately preserved must not complete refresh");
        Equal("retry", early.Outcome, "unapplied state should retry");
        Equal("state_not_applied", early.Reason, "unapplied state retry reason");

        var final = DeviceRefreshPolicy.Evaluate(events, baseline, inventory, true, poll, finalAttempt: true);
        True(final.IsComplete, "the final unapplied attempt should terminate the bounded refresh");
        Equal("degraded", final.Outcome, "unapplied final state should be degraded");
        Equal("state_not_applied", final.Reason, "unapplied final state reason");
    }

    private static void RefreshForcedReadFailureRetriesThenDegrades()
    {
        var poll = RefreshPoll(
            new[] { "path-a" },
            Array.Empty<string>(),
            stateApplied: true,
            hasFreshSamples: false,
            failureReason: "read_failed");
        var inventory = new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase);

        var early = DeviceRefreshPolicy.Evaluate(
            Array.Empty<DeviceRefreshEvent>(),
            inventory,
            inventory,
            true,
            poll,
            finalAttempt: false);
        False(early.IsComplete, "forced read_failed must keep retrying before the final attempt");
        Equal("retry", early.Outcome, "forced read_failed retry outcome");
        Equal("forced_read_incomplete", early.Reason, "forced read_failed retry reason");

        var final = DeviceRefreshPolicy.Evaluate(
            Array.Empty<DeviceRefreshEvent>(),
            inventory,
            inventory,
            true,
            poll,
            finalAttempt: true);
        True(final.IsComplete, "forced read_failed should terminate on the final attempt");
        Equal("degraded", final.Outcome, "forced read_failed final outcome");
        Equal("forced_read_incomplete", final.Reason, "forced read_failed final reason");
    }

    private static void RefreshTwoArrivalsRequireBothToResolve()
    {
        var events = new[]
        {
            new DeviceRefreshEvent("path-b", Arrived: true, WasTracked: false),
            new DeviceRefreshEvent("path-c", Arrived: true, WasTracked: false)
        };
        var baseline = new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase);
        var inventory = new HashSet<string>(new[] { "path-a", "path-b", "path-c" }, StringComparer.OrdinalIgnoreCase);
        var onlyBRead = RefreshPoll(new[] { "path-b" }, new[] { "path-b" }, stateApplied: true);

        var early = DeviceRefreshPolicy.Evaluate(events, baseline, inventory, true, onlyBRead, finalAttempt: false);
        False(early.IsComplete, "reading only B must not complete the B+C arrival batch");
        Equal("retry", early.Outcome, "partial arrival batch should retry");

        var final = DeviceRefreshPolicy.Evaluate(events, baseline, inventory, true, onlyBRead, finalAttempt: true);
        True(final.IsComplete, "partial arrival batch should terminate at the retry bound");
        Equal("degraded", final.Outcome, "reading only B must never report the B+C batch as success");
    }

    private static void RefreshAmbiguousFreshPathsCannotReplaceUnresolvedArrival()
    {
        var events = new[] { new DeviceRefreshEvent("path-b", Arrived: true, WasTracked: false) };
        var baseline = new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase);
        var ambiguousInventory = new HashSet<string>(new[] { "path-a", "path-d", "path-e" }, StringComparer.OrdinalIgnoreCase);
        var ambiguousFresh = RefreshPoll(
            new[] { "path-d", "path-e" },
            new[] { "path-d", "path-e" },
            stateApplied: true);

        var early = DeviceRefreshPolicy.Evaluate(events, baseline, ambiguousInventory, true, ambiguousFresh, finalAttempt: false);
        False(early.IsComplete, "multiple unrelated fresh paths cannot stand in for unresolved path-b");
        Equal("retry", early.Outcome, "ambiguous path substitution should retry");

        var final = DeviceRefreshPolicy.Evaluate(events, baseline, ambiguousInventory, true, ambiguousFresh, finalAttempt: true);
        True(final.IsComplete, "ambiguous substitution should terminate at the retry bound");
        Equal("degraded", final.Outcome, "ambiguous path substitution must not become success");

        var uniqueInventory = new HashSet<string>(new[] { "path-a", "path-d" }, StringComparer.OrdinalIgnoreCase);
        var uniqueFresh = RefreshPoll(new[] { "path-d" }, new[] { "path-d" }, stateApplied: true);
        var uniqueFallback = DeviceRefreshPolicy.Evaluate(events, baseline, uniqueInventory, true, uniqueFresh, finalAttempt: false);
        True(uniqueFallback.IsComplete, "one arrival plus one unique new battery path may use the composite-device fallback");
        Equal("success", uniqueFallback.Outcome, "unique composite-device fallback outcome");
        Equal("single_arrival_composite_read", uniqueFallback.Reason, "unique composite-device fallback reason");
    }

    private static void RefreshRemovalSucceedsWithoutFreshSample()
    {
        var decision = DeviceRefreshPolicy.Evaluate(
            new[] { new DeviceRefreshEvent("path-a", Arrived: false, WasTracked: true) },
            new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            true,
            RefreshPoll(Array.Empty<string>(), Array.Empty<string>(), hasFreshSamples: false, failureReason: "not_detected"),
            finalAttempt: false);

        True(decision.IsComplete, "reliable absence should complete removal immediately");
        Equal("success", decision.Outcome, "removal outcome");
        Equal("removal_confirmed", decision.Reason, "removal reason");
    }

    private static void RefreshCandidateArrivalSucceedsAfterTargetRead()
    {
        var decision = DeviceRefreshPolicy.Evaluate(
            new[] { new DeviceRefreshEvent("path-b", Arrived: true, WasTracked: false) },
            new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(new[] { "path-a", "path-b" }, StringComparer.OrdinalIgnoreCase),
            true,
            RefreshPoll(new[] { "path-a", "path-b" }, new[] { "path-a", "path-b" }),
            finalAttempt: false);

        True(decision.IsComplete, "the exact arriving candidate was read");
        Equal("success", decision.Outcome, "arrival outcome");
        Equal("all_arrival_candidates_read", decision.Reason, "arrival reason");
    }

    private static void RefreshUnrelatedHidArrivalIsSkipped()
    {
        var decision = DeviceRefreshPolicy.Evaluate(
            new[] { new DeviceRefreshEvent("keyboard-hid", Arrived: true, WasTracked: false) },
            new HashSet<string>(new[] { "path-a" }, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(new[] { "path-a", "keyboard-hid" }, StringComparer.OrdinalIgnoreCase),
            true,
            RefreshPoll(new[] { "path-a" }, new[] { "path-a" }),
            finalAttempt: true);

        True(decision.IsComplete, "unrelated HID should settle after the bounded window");
        Equal("skipped", decision.Outcome, "unrelated HID outcome");
        Equal("non_battery_hid_settled", decision.Reason, "unrelated HID reason");
    }

    private static void RefreshForcedNoDevicePollCompletesNormally()
    {
        var decision = DeviceRefreshPolicy.Evaluate(
            Array.Empty<DeviceRefreshEvent>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            true,
            RefreshPoll(Array.Empty<string>(), Array.Empty<string>(), hasFreshSamples: false, failureReason: "not_detected"),
            finalAttempt: false);

        True(decision.IsComplete, "a completed forced poll does not require a mouse to exist");
        Equal("success", decision.Outcome, "forced refresh outcome");
        Equal("forced_absence_applied", decision.Reason, "forced refresh reason");
    }

    private static PollAttemptOutcome RefreshPoll(
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> fresh,
        bool stateApplied = true,
        bool hasFreshSamples = true,
        string failureReason = "")
    {
        return new PollAttemptOutcome(
            PollCompleted: true,
            InventoryReliable: true,
            StateApplied: stateApplied,
            HasFreshSamples: hasFreshSamples,
            CandidateCoverageComplete: candidates.All(candidate => fresh.Contains(candidate, StringComparer.OrdinalIgnoreCase)),
            CandidateDevicePaths: candidates,
            FreshDevicePaths: fresh,
            FailureReason: failureReason);
    }

    private static void PowerStateChargingSuppressesLowBatteryAlert()
    {
        var reading = new BatteryReading
        {
            BatteryPercentage = 5,
            HasBatteryPercentage = true,
            PowerState = DevicePowerState.Charging
        };
        True(DevicePowerSemantics.IsExternallyPowered(reading), "PowerState.Charging must be treated as external power");

        var result = new LowBatteryAlertTracker().Update(
            new[]
            {
                new LowBatteryAlertInput(
                    "mouse-a",
                    reading.BatteryPercentage,
                    reading.HasBatteryPercentage,
                    DevicePowerSemantics.IsExternallyPowered(reading),
                    DevicePowerSemantics.IsFullyCharged(reading),
                    DevicePowerSemantics.IsExternallyPowered(reading))
            },
            StartUtc,
            AlertSettings());
        False(result.ShouldPlaySound, "a charging device must not play the low-battery sound");
        Decision(result, "mouse-a", LowBatteryAlertDisposition.Reset, "external_power");
    }

    private static void AlertIdentityTransitionPreservesSuppression()
    {
        var tracker = new LowBatteryAlertTracker();
        var settings = AlertSettings();
        var first = tracker.Update(new[] { AlertDevice("path-old", 8) }, StartUtc, settings);
        True(first.ShouldPlaySound, "first low reading should arm the old identity");
        True(tracker.MoveState("path-old", "serial:stable"), "alert state should move to the promoted identity");

        var afterMove = tracker.Update(new[] { AlertDevice("serial:stable", 8) }, StartUtc.AddMinutes(1), settings);
        False(afterMove.ShouldPlaySound, "identity promotion must not repeat the same low-battery alert");
        Decision(afterMove, "serial:stable", LowBatteryAlertDisposition.Suppressed, "same_or_higher_bucket");
    }

    private static void PollingIdentityTransitionPreservesFastDeadline()
    {
        var policy = new ChargingPollingPolicy();
        var settings = PollingSettings();
        var first = policy.Evaluate(new[] { ChargingDevice("path-old") }, true, StartUtc, settings);
        Equal(StartUtc.Add(ChargingPollingPolicy.FastChargingWindow), first.FastUntilUtc, "initial fast deadline");
        True(policy.MoveState("path-old", "serial:stable"), "charging policy state should move to the promoted identity");

        var afterMove = policy.Evaluate(new[] { ChargingDevice("serial:stable") }, true, StartUtc.AddSeconds(30), settings);
        Equal(0, afterMove.NewlyChargingDeviceKeys.Count, "identity promotion must not look like a newly charging device");
        Equal(first.FastUntilUtc, afterMove.FastUntilUtc, "identity promotion must not extend the fast deadline");
    }

    private static void HistoryPlaceholderSerialsUseDistinctPathKeys()
    {
        var first = StateReading("path-a", 50, serial: "0000", deviceName: "Same Mouse");
        var second = StateReading("path-b", 50, serial: "0-0 0", deviceName: "Same Mouse");

        var firstKey = CreateHistoryDeviceKey(first);
        var secondKey = CreateHistoryDeviceKey(second);

        True(firstKey.Contains(":PATH:", StringComparison.Ordinal), "placeholder serial should fall back to path key");
        True(secondKey.Contains(":PATH:", StringComparison.Ordinal), "placeholder serial should fall back to path key");
        NotEqual(firstKey, secondKey, "different paths must not collide when serials are placeholders");
    }

    private static void HistoryPowerStateUsesUnifiedChargingSemantics()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        var store = new BatteryHistoryStore(paths);
        store.Append(new BatteryReading
        {
            BatteryPercentage = 5,
            HasBatteryPercentage = true,
            PowerState = DevicePowerState.Charging,
            DeviceId = "path-a",
            DeviceName = "Test Mouse",
            VendorId = "0x1234",
            ProductId = "0x5678",
            Source = "test-source"
        });

        var line = File.ReadLines(paths.HistoryPath).Single();
        var entry = JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
            ?? throw new InvalidOperationException("history entry was not readable");
        True(entry.IsCharging, "PowerState.Charging should be stored as charging");
        True(entry.IsCableConnected, "PowerState.Charging should be stored as external power");
    }

    private static void InventoryFailedSnapshotRetriesAfterTwoSeconds()
    {
        var nowUtc = StartUtc;
        var enumerationCount = 0;
        var inventory = new HidDeviceInventory(
            enumerateDevices: () =>
            {
                enumerationCount++;
                if (enumerationCount == 1)
                    throw new InvalidOperationException("simulated enumeration failure");
                return [];
            },
            utcNow: () => nowUtc);

        var failed = inventory.GetSnapshot();
        False(failed.IsReliable, "first failed enumeration should be unreliable");
        Equal(nameof(InvalidOperationException), failed.Error, "failed snapshot error type");
        Equal(1, enumerationCount, "first enumeration count");

        nowUtc = StartUtc.AddSeconds(1);
        var cachedFailure = inventory.GetSnapshot();
        True(ReferenceEquals(failed, cachedFailure), "failed snapshot should be cached before two-second TTL");
        Equal(1, enumerationCount, "failed snapshot should not retry before TTL");

        nowUtc = StartUtc.AddSeconds(2);
        var recovered = inventory.GetSnapshot();
        True(recovered.IsReliable, "enumeration should retry and recover at two-second TTL");
        Equal(2, enumerationCount, "enumeration should retry exactly once at TTL");
        Equal(StartUtc.AddSeconds(2), recovered.CreatedUtc, "recovered snapshot timestamp");
        Equal(0, recovered.Generation, "TTL retry should preserve generation");
    }

    private static void InventoryReliableSnapshotCachesUntilInvalidated()
    {
        var nowUtc = StartUtc;
        var enumerationCount = 0;
        var inventory = new HidDeviceInventory(
            enumerateDevices: () =>
            {
                enumerationCount++;
                return [];
            },
            utcNow: () => nowUtc);

        var first = inventory.GetSnapshot();
        True(first.IsReliable, "successful enumeration should be reliable");
        Equal(1, enumerationCount, "initial enumeration count");

        nowUtc = StartUtc.AddMinutes(9).AddSeconds(59);
        var cached = inventory.GetSnapshot();
        True(ReferenceEquals(first, cached), "reliable snapshot should remain cached within ten minutes");
        Equal(1, enumerationCount, "reliable cache should avoid re-enumeration");

        inventory.Invalidate();
        var refreshed = inventory.GetSnapshot();
        False(ReferenceEquals(first, refreshed), "invalidate should discard cached snapshot immediately");
        Equal(2, enumerationCount, "invalidate should force immediate enumeration");
        Equal(1, refreshed.Generation, "invalidate should advance generation");
        Equal(nowUtc, refreshed.CreatedUtc, "invalidated snapshot timestamp");
    }

    private static void FlightRecorderFlushedLinesHaveValidSchema()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        recorder.Write("debug", "selftest.schema", nameof(Program), "success", new { count = 1, label = "safe" });

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "flight recorder should flush queued events");
        var entries = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"));

        True(entries.Count >= 2, "active log should include logger.started and the self-test event");
        foreach (var entry in entries)
            ValidateFlightSchema(entry);
        Equal("selftest.schema", FindEvent(entries, "selftest.schema").GetProperty("event").GetString(), "self-test event should be persisted");
    }

    private static void FlightRecorderOperationScopeCrossesTaskRun()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);

        using (recorder.BeginOperation("scope-test"))
        {
            recorder.Write("info", "selftest.scope.parent", nameof(Program), "success");
            Task.Run(() => recorder.Write("info", "selftest.scope.child", nameof(Program), "success"))
                .GetAwaiter()
                .GetResult();
        }
        recorder.Write("info", "selftest.scope.outside", nameof(Program), "success");

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "operation-scope events should flush");
        var entries = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"));
        var parentOperation = FindEvent(entries, "selftest.scope.parent").GetProperty("op_id").GetString();
        var childOperation = FindEvent(entries, "selftest.scope.child").GetProperty("op_id").GetString();
        var outsideOperation = FindEvent(entries, "selftest.scope.outside").GetProperty("op_id");

        True(!string.IsNullOrWhiteSpace(parentOperation), "operation scope should assign an operation id");
        Equal(parentOperation, childOperation, "Task.Run should inherit the operation id");
        Equal(JsonValueKind.Null, outsideOperation.ValueKind, "disposed operation scope should restore the previous context");
    }

    private static void FlightRecorderRecordsRawDeviceIdentity()
    {
        const string rawPath = @"\\?\hid#vid_1915&pid_ae12#RAW-HID-PATH-1A2B#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string rawSerial = "RAW-SERIAL-7E6D4C3B";
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var reading = new BatteryReading
        {
            BatteryPercentage = 42,
            IsOnline = true,
            PowerState = DevicePowerState.Discharging,
            DeviceId = rawPath,
            DeviceSerial = rawSerial,
            DeviceName = "Test Mouse",
            VendorId = "0x1915",
            ProductId = "0xAE12",
            Source = "selftest"
        };

        recorder.Write(
            "debug",
            "selftest.redaction",
            nameof(Program),
            "success",
            new
            {
                devicePath = rawPath,
                serialNumber = rawSerial,
                candidateDeviceIds = new[] { rawPath },
                rawReport = new byte[] { 0x01, 0x02, 0x03 }
            },
            reading: reading);

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "redaction event should flush");
        var entries = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"));
        var recorded = FindEvent(entries, "selftest.redaction");
        var device = recorded.GetProperty("device");
        True(device.GetProperty("id").GetString()?.StartsWith("dev_", StringComparison.Ordinal) == true,
            "BatteryReading should retain a stable correlation token");
        Equal(rawPath, device.GetProperty("path").GetString(), "raw HID path should remain readable for local diagnosis");
        Equal(rawSerial, device.GetProperty("serial").GetString(), "raw serial should remain readable for local diagnosis");
        Equal("Test Mouse", device.GetProperty("name").GetString(), "device name should remain readable");
        var data = recorded.GetProperty("data");
        Equal(rawPath, data.GetProperty("devicePath").GetString(), "path-valued event fact should remain readable");
        Equal(rawSerial, data.GetProperty("serialNumber").GetString(), "serial-valued event fact should remain readable");
        Equal("[binary-redacted]", data.GetProperty("rawReport").GetString(), "raw HID report should be redacted");
    }

    private static void FlightRecorderPowerTransitionsRetainCompleteDeviceFacts()
    {
        const string rawPath = @"\\?\hid#vid_1234&pid_5678#POWER-TRANSITION-PATH#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        using var temporary = new TemporaryDirectory();
        var logs = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logs);
        using var recorder = new FlightRecorder(logs, Path.Combine(temporary.Path, ".flight-recorder.key"));
        var store = new DeviceBatteryStateStore(recorder);

        var discharging50 = TransitionReading(rawPath, 50, DevicePowerState.Discharging, cableConnected: false, charging: false);
        var charging49 = TransitionReading(rawPath, 49, DevicePowerState.Charging, cableConnected: true, charging: true);
        var discharging48 = TransitionReading(rawPath, 48, DevicePowerState.Discharging, cableConnected: false, charging: false);

        store.Apply(StateResult(new[] { discharging50 }, new[] { rawPath }), StartUtc, TimeSpan.FromMinutes(30));
        store.Apply(StateResult(new[] { charging49 }, new[] { rawPath }), StartUtc.AddMinutes(1), TimeSpan.FromMinutes(30));
        store.Apply(StateResult(new[] { discharging48 }, new[] { rawPath }), StartUtc.AddMinutes(2), TimeSpan.FromMinutes(30));

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "power transition events should flush");
        var entries = ReadJsonLinesShared(Path.Combine(logs, "flight-current.jsonl"));

        AssertTransitionFacts(FindEvent(entries, "device.cable_connected"), rawPath, 50, 49, "cable connected");
        AssertTransitionFacts(FindEvent(entries, "device.charging_started"), rawPath, 50, 49, "charging started");
        AssertTransitionFacts(FindEvent(entries, "device.cable_disconnected"), rawPath, 49, 48, "cable disconnected");
        AssertTransitionFacts(FindEvent(entries, "device.charging_stopped"), rawPath, 49, 48, "charging stopped");

        var batteryChanges = entries
            .Where(entry => entry.TryGetProperty("event", out var eventName)
                && string.Equals(eventName.GetString(), "device.battery_changed", StringComparison.Ordinal))
            .ToArray();
        Equal(2, batteryChanges.Length, "both battery changes should be recorded");
        AssertTransitionFacts(batteryChanges[0], rawPath, 50, 49, "battery change while connecting");
        AssertTransitionFacts(batteryChanges[1], rawPath, 49, 48, "battery change while disconnecting");
    }

    private static void AssertTransitionFacts(
        JsonElement entry,
        string expectedPath,
        int expectedPreviousBattery,
        int expectedBattery,
        string message)
    {
        var device = RequiredProperty(entry, "device", JsonValueKind.Object);
        Equal(expectedPath, RequiredProperty(device, "path", JsonValueKind.String).GetString(), $"{message} raw path");
        Equal(expectedBattery, RequiredProperty(device, "battery_percentage", JsonValueKind.Number).GetInt32(), $"{message} device battery");

        var data = RequiredProperty(entry, "data", JsonValueKind.Object);
        Equal(expectedPreviousBattery, RequiredProperty(data, "previous_battery_percentage", JsonValueKind.Number).GetInt32(), $"{message} previous battery");
        Equal(expectedBattery, RequiredProperty(data, "battery_percentage", JsonValueKind.Number).GetInt32(), $"{message} current battery");
        Equal(
            "derived_from_consecutive_provider_readings",
            RequiredProperty(data, "fact_basis", JsonValueKind.String).GetString(),
            $"{message} fact basis");
    }

    private static void FlightRecorderClassifiesSensitiveFieldVariants()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var deviceToken = recorder.TokenFor("stable-device", "dev");
        recorder.Write(
            "info",
            "selftest.classification",
            nameof(Program),
            "success",
            new
            {
                device_token = deviceToken,
                tokenValue = "SECRET-TOKEN-VALUE",
                responseHex = "A1B2C3D4",
                payloadBytes = "RAW-PAYLOAD-BYTES"
            });

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "classification event should flush");
        var data = FindEvent(
            ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl")),
            "selftest.classification").GetProperty("data");
        Equal(deviceToken, data.GetProperty("device_token").GetString(), "opaque device token should remain correlatable");
        Equal("[redacted]", data.GetProperty("tokenValue").GetString(), "tokenValue should be redacted");
        Equal("[binary-redacted]", data.GetProperty("responseHex").GetString(), "responseHex should be redacted");
        Equal("[binary-redacted]", data.GetProperty("payloadBytes").GetString(), "payloadBytes should be redacted");
    }

    private static void FlightRecorderConcurrentSequenceIsMonotonic()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        Parallel.For(
            0,
            1000,
            index => recorder.Write("debug", "selftest.concurrent", nameof(Program), "success", new { index }));

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "concurrent events should flush");
        var entries = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"));
        Equal(1000, entries.Count(entry => entry.GetProperty("event").GetString() == "selftest.concurrent"),
            "all bounded concurrent events should persist");
        var sequences = entries.Select(entry => entry.GetProperty("seq").GetInt64()).ToArray();
        for (var index = 1; index < sequences.Length; index++)
            True(sequences[index] > sequences[index - 1], "file sequence should be strictly increasing");
    }

    private static void FlightRecorderSnapshotReprojectsLegacyFacts()
    {
        const string rawPath = @"\\?\hid#vid_1915&pid_ae12#FORGED-RAW-PATH";
        const string rawSerial = "FORGED-RAW-SERIAL";
        const string rawSecret = "FORGED-TOKEN-SECRET";
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        True(recorder.Flush(TimeSpan.FromSeconds(5)), "recorder startup should flush");

        var forged = new
        {
            schema = "sora.flight.v1",
            ts_utc = StartUtc.ToString("O"),
            ts_local = StartUtc.ToString("O"),
            mono_ms = 10,
            seq = 99,
            level = "info",
            @event = "selftest.forged_legacy",
            run_id = Guid.NewGuid(),
            op_id = (string?)null,
            component = "Legacy",
            thread_id = 4,
            app_version = "0.1",
            outcome = "success",
            device = new { id = rawSerial, vendor_id = "0x1915", product_id = "0xAE12", battery_percentage = 50 },
            data = new { devicePath = rawPath, serialNumber = rawSerial, tokenValue = rawSecret, responseHex = "DEADBEEF" },
            error = new { message = rawSecret, type = "System.Exception" }
        };
        File.WriteAllText(
            Path.Combine(temporary.Path, "flight-forged.jsonl"),
            JsonSerializer.Serialize(forged) + "\n",
            new UTF8Encoding(false));

        var snapshotPath = Path.Combine(temporary.Path, "snapshot.jsonl");
        True(recorder.SnapshotRecentLogs(snapshotPath, 1024 * 1024) > 0, "snapshot should contain projected records");
        var entries = ReadJsonLinesShared(snapshotPath);
        var projected = FindEvent(entries, "selftest.forged_legacy");
        True(entries.Any(entry => EnumerateJsonText(entry).Any(text => text.Contains(rawPath, StringComparison.Ordinal))),
            "snapshot should preserve the raw device path fact");
        True(entries.Any(entry => EnumerateJsonText(entry).Any(text => text.Contains(rawSerial, StringComparison.Ordinal))),
            "snapshot should preserve the legacy identity fact");
        True(projected.GetProperty("device").GetProperty("id").GetString()?.StartsWith("dev_", StringComparison.Ordinal) == true,
            "legacy device id should also gain a stable correlation token");
        Equal(rawSecret, projected.GetProperty("error").GetProperty("message").GetString(),
            "legacy exception message should remain readable");
        Equal("[redacted]", projected.GetProperty("data").GetProperty("tokenValue").GetString(),
            "unrelated credential-shaped data can remain redacted");
    }

    private static void FlightRecorderKeyMigratesOutsidePublicLogs()
    {
        using var temporary = new TemporaryDirectory();
        var logs = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logs);
        var legacyPath = Path.Combine(logs, ".flight-recorder.key");
        var privatePath = Path.Combine(temporary.Path, ".flight-recorder.key");
        var legacyKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        File.WriteAllBytes(legacyPath, legacyKey);

        using (var recorder = new FlightRecorder(logs, privatePath))
            True(recorder.Flush(TimeSpan.FromSeconds(5)), "migrated-key recorder should flush");

        True(File.Exists(privatePath), "private key should exist beside, not inside, logs");
        False(File.Exists(legacyPath), "legacy key should be removed from public logs after migration");
        SequenceEqual(legacyKey, File.ReadAllBytes(privatePath), "migration should preserve stable HMAC identity");
    }

    private static void FlightRecorderCrashFactsPrecedeHistory()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        recorder.Write("info", "selftest.before_crash", nameof(Program), "success");
        var crashPath = recorder.CrashSnapshot(
            new InvalidOperationException("must not be copied"),
            nameof(Program),
            "selftest.fatal");

        True(!string.IsNullOrWhiteSpace(crashPath), "crash snapshot should be created");
        var entries = ReadJsonLinesShared(crashPath!);
        True(entries.Count >= 2, "crash snapshot should begin with direct facts");
        Equal("selftest.fatal", entries[0].GetProperty("event").GetString(), "fatal fact should be write-through first");
        Equal("must not be copied", entries[0].GetProperty("error").GetProperty("message").GetString(),
            "crash exception message should remain readable");
        Equal("logger.crash_snapshot", entries[1].GetProperty("event").GetString(), "snapshot marker should be write-through second");
    }

    private static void FlightRecorderCleanMarkerControlsRecoveryWarning()
    {
        using var cleanDirectory = new TemporaryDirectory();
        WritePreviousRun(cleanDirectory.Path, "app.clean_shutdown", "logger.stopping");
        using (var recorder = new FlightRecorder(cleanDirectory.Path))
        {
            True(recorder.Flush(TimeSpan.FromSeconds(5)), "clean previous-run check should flush");
            var entries = ReadJsonLinesShared(Path.Combine(cleanDirectory.Path, "flight-current.jsonl"));
            False(entries.Any(entry => entry.GetProperty("event").GetString() == "app.previous_run_unclean"),
                "logger.stopping after an explicit clean marker should remain clean");
        }

        using var failedDirectory = new TemporaryDirectory();
        WritePreviousRun(failedDirectory.Path, "selftest.fatal", "logger.stopping");
        using (var recorder = new FlightRecorder(failedDirectory.Path))
        {
            True(recorder.Flush(TimeSpan.FromSeconds(5)), "unclean previous-run check should flush");
            var entries = ReadJsonLinesShared(Path.Combine(failedDirectory.Path, "flight-current.jsonl"));
            True(entries.Any(entry => entry.GetProperty("event").GetString() == "app.previous_run_unclean"),
                "logger.stopping alone must not hide a crash");
        }
    }

    private static void WritePreviousRun(string logsDirectory, params string[] events)
    {
        var runId = Guid.NewGuid();
        var lines = events.Select(eventName => JsonSerializer.Serialize(new
        {
            schema = "sora.flight.v1",
            run_id = runId,
            @event = eventName
        }));
        File.WriteAllText(
            Path.Combine(logsDirectory, "flight-current.jsonl"),
            string.Join("\n", lines) + "\n",
            new UTF8Encoding(false));
    }

    private static void FlightRecorderDisposeEndsWithStopping()
    {
        using var temporary = new TemporaryDirectory();
        var recorder = new FlightRecorder(temporary.Path);
        try
        {
            recorder.Write("info", "selftest.before_dispose", nameof(Program), "success");
            True(recorder.Flush(TimeSpan.FromSeconds(5)), "pre-dispose event should flush");
        }
        finally
        {
            recorder.Dispose();
        }

        var entries = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"));
        True(entries.Count > 0, "disposed recorder should leave a readable log");
        Equal("logger.stopping", entries[^1].GetProperty("event").GetString(), "dispose should persist logger.stopping last");
        foreach (var entry in entries)
            ValidateFlightSchema(entry);
    }

    private static void FlightRecorderActiveFileSupportsSharedReads()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        recorder.Write("info", "selftest.shared_read", nameof(Program), "success");
        True(recorder.Flush(TimeSpan.FromSeconds(5)), "shared-read event should flush");

        var activePath = Path.Combine(temporary.Path, "flight-current.jsonl");
        using var stream = new FileStream(
            activePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var activeText = reader.ReadToEnd();

        True(activeText.Contains("selftest.shared_read", StringComparison.Ordinal),
            "active flight log should be readable while its writer remains open");
    }

    private static void DiagnosticsLocalFactsAreRetained()
    {
        const string rawHidPath = @"\\?\hid#vid_1915&pid_ae12#RAW-DIAGNOSTIC-HID-PATH#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string rawHidSerial = "RAW-DIAGNOSTIC-HID-SERIAL-4F7A";
        const string rawHistoryKey = "SERIAL:RAW-DIAGNOSTIC-HISTORY-KEY-6B2C";
        const string rawHistorySerial = "RAW-DIAGNOSTIC-HISTORY-SERIAL-8D1E";
        const string rawStateDevicePath = @"\\?\hid#vid_1915&pid_ae12#RAW-STATE-DEVICE-PATH";
        const string rawStateSerial = "RAW-STATE-SERIAL-5C0E";
        const string rawStateHistoryKey = "PATH:RAW-STATE-HISTORY-KEY-4D3B";
        const string rawStatePassword = "RAW-STATE-PASSWORD-A96F";
        const string rawStateToken = "RAW-STATE-ACCESS-TOKEN-C87E";
        const string rawStatePath = @"C:\Users\PrivateUser\AppData\RAW-STATE-PATH\trace.log";
        const string rawProfileNote = "RAW-PROFILE-NOTE-47AB";
        const string rawProfilePassword = "RAW-PROFILE-PASSWORD-49C1";
        const string rawProfileExtensionPath = @"C:\Users\PrivateUser\RAW-PROFILE-EXTENSION\secret.bin";
        const string rawAlertSoundPath = @"C:\Users\PrivateUser\RAW-ALERT-SOUND\private-alert.wav";
        const string safeDeviceToken = "dev_0123456789abcdef01234567";
        const string safeHistoryToken = "hist_89abcdef0123456701234567";
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        File.WriteAllText(
            paths.HistoryPath,
            JsonSerializer.Serialize(new BatteryHistoryEntry
            {
                TimestampUtc = StartUtc,
                DeviceKey = rawHistoryKey,
                DeviceName = $"SelfTest Mouse {rawHistorySerial}",
                DeviceSerial = rawHistorySerial,
                VendorId = "0x1915",
                ProductId = "0xAE12",
                BatteryPercentage = 67,
                IsCharging = false,
                IsCableConnected = false,
                State = "sample",
                Source = "selftest"
            }) + "\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(paths.ProfilesDirectory, "malicious-profile.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Name"] = "SelfTest profile",
                ["Enabled"] = true,
                ["Priority"] = 175,
                ["VendorId"] = "0x1915",
                ["ProductIds"] = new[] { "0xAE12" },
                ["CableProductIds"] = new[] { "0xAE13" },
                ["ProductNameContains"] = new[] { "SelfTest Mouse" },
                ["ReportType"] = "Feature",
                ["ReportId"] = "0x08",
                ["ResponseReportId"] = "0x09",
                ["RequestBytes"] = new[] { "0x08", "0x01", "0x02" },
                ["SendRequest"] = true,
                ["MinFeatureLength"] = 64,
                ["DelayMs"] = 80,
                ["PayloadStarts"] = new[] { 0, 1 },
                ["BatteryOffset"] = 6,
                ["ChargingOffset"] = 7,
                ["FullOffset"] = 8,
                ["OnlineOffset"] = 9,
                ["Notes"] = $"must not be copied: {rawProfileNote}",
                ["Password"] = rawProfilePassword,
                ["Extension"] = new { rawPath = rawProfileExtensionPath }
            }),
            new UTF8Encoding(false));

        var maliciousState = new Dictionary<string, object?>
        {
            ["status"] = "selftest",
            ["devicePath"] = rawStateDevicePath,
            ["serialNumber"] = rawStateSerial,
            ["historyKey"] = rawStateHistoryKey,
            ["password"] = rawStatePassword,
            ["accessToken"] = rawStateToken,
            ["logPath"] = rawStatePath,
            ["deviceToken"] = safeDeviceToken,
            ["historyToken"] = safeHistoryToken,
            ["nested"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["deviceId"] = rawStateDevicePath,
                    [rawStatePath] = rawStateToken
                }
            }
        };
        var enumerationCount = 0;
        var inventory = new HidDeviceInventory(
            enumerateDevices: () =>
            {
                enumerationCount++;
                return new HidDevice[]
                {
                    new FakeHidDevice(
                        rawHidPath,
                        rawHidSerial,
                        $"SelfTest Mouse {rawHidSerial}",
                        $"SelfTest Manufacturer {rawHidPath}")
                };
            },
            utcNow: () => StartUtc);

        using var recorder = new FlightRecorder(paths.LogsDirectory);
        var exporter = new DiagnosticsExporter(
            paths,
            () => new AppSettings
            {
                AlertThreshold = 11,
                PollingIntervalMinutes = 3,
                AlertSoundFile = rawAlertSoundPath
            },
            () => maliciousState,
            inventory,
            recorder);
        var exportDirectory = exporter.Export();

        Equal(1, enumerationCount, "diagnostics should use the injected HID inventory exactly once");
        var historyEntries = ReadJsonLinesShared(Path.Combine(exportDirectory, "battery-history.jsonl"));
        Equal(1, historyEntries.Count, "diagnostics should export one valid history row");
        var history = historyEntries[0];
        Equal(rawHistoryKey, history.GetProperty("DeviceKey").GetString(), "history key should be retained exactly");
        Equal(rawHistorySerial, history.GetProperty("DeviceSerial").GetString(), "history serial should be retained exactly");

        var hidDevices = ReadJsonFile(Path.Combine(exportDirectory, "hid-devices.json"));
        Equal(JsonValueKind.Array, hidDevices.ValueKind, "HID diagnostics should be a JSON array");
        Equal(1, hidDevices.GetArrayLength(), "fake HID device should be exported without real hardware");
        Equal(rawHidPath, hidDevices[0].GetProperty("devicePath").GetString(), "HID path should be retained exactly");
        Equal(rawHidSerial, hidDevices[0].GetProperty("serialNumber").GetString(), "HID serial should be retained exactly");
        True(hidDevices[0].GetProperty("productName").GetString()?.Contains(rawHidSerial, StringComparison.Ordinal) == true,
            "the original HID product descriptor should be retained");
        True(hidDevices[0].GetProperty("manufacturer").GetString()?.Contains(rawHidPath, StringComparison.Ordinal) == true,
            "the original HID manufacturer descriptor should be retained");

        var settings = ReadJsonFile(Path.Combine(exportDirectory, "settings.json"));
        Equal(rawAlertSoundPath, settings.GetProperty("AlertSoundFile").GetString(),
            "settings should retain the complete alert sound path");

        var state = ReadJsonFile(Path.Combine(exportDirectory, "app-state.json"));
        Equal(rawStateDevicePath, state.GetProperty("devicePath").GetString(), "state device paths should be retained");
        Equal(rawStateSerial, state.GetProperty("serialNumber").GetString(), "state serials should be retained");
        Equal(rawStateHistoryKey, state.GetProperty("historyKey").GetString(), "state history keys should be retained");
        Equal(rawStatePassword, state.GetProperty("password").GetString(), "state password fields should be retained locally");
        Equal(rawStateToken, state.GetProperty("accessToken").GetString(), "state token fields should be retained locally");
        Equal(rawStatePath, state.GetProperty("logPath").GetString(), "state absolute paths should be retained");
        Equal(safeDeviceToken, state.GetProperty("deviceToken").GetString(), "existing device tokens must remain stable");
        Equal(safeHistoryToken, state.GetProperty("historyToken").GetString(), "existing history tokens must remain stable");

        var profile = ReadJsonFile(Path.Combine(exportDirectory, "profiles", "malicious-profile.json"));
        Equal("Feature", profile.GetProperty("ReportType").GetString(), "profile report type should be retained");
        Equal("0x08", profile.GetProperty("ReportId").GetString(), "profile report id should be retained");
        Equal("0x09", profile.GetProperty("ResponseReportId").GetString(), "profile response report id should be retained");
        Equal(6, profile.GetProperty("BatteryOffset").GetInt32(), "profile battery offset should be retained");
        Equal(7, profile.GetProperty("ChargingOffset").GetInt32(), "profile charging offset should be retained");
        Equal(8, profile.GetProperty("FullOffset").GetInt32(), "profile full offset should be retained");
        Equal(9, profile.GetProperty("OnlineOffset").GetInt32(), "profile online offset should be retained");
        True(profile.GetProperty("SendRequest").GetBoolean(), "profile request flag should be retained");
        Equal(3, profile.GetProperty("RequestBytes").GetArrayLength(), "profile request bytes should be retained");
        True(profile.GetProperty("Notes").GetString()?.Contains(rawProfileNote, StringComparison.Ordinal) == true,
            "profile Notes should be retained");
        Equal(rawProfilePassword, profile.GetProperty("Password").GetString(), "unknown profile fields should be retained");
        Equal(rawProfileExtensionPath, profile.GetProperty("Extension").GetProperty("rawPath").GetString(),
            "nested profile extension fields should be retained");

        var diagnosticJson = Directory.EnumerateFiles(exportDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? ReadJsonLinesShared(path)
                : new[] { ReadJsonFile(path) })
            .ToArray();
        AssertJsonTextContains(diagnosticJson, rawHidPath, "diagnostics should contain the raw HID path");
        AssertJsonTextContains(diagnosticJson, rawHidSerial, "diagnostics should contain the raw HID serial");
        AssertJsonTextContains(diagnosticJson, rawHistoryKey, "diagnostics should contain the raw history key");
        AssertJsonTextContains(diagnosticJson, rawHistorySerial, "diagnostics should contain the history serial");
        AssertJsonTextContains(diagnosticJson, rawStateDevicePath, "diagnostics should contain a state device path");
        AssertJsonTextContains(diagnosticJson, rawStateSerial, "diagnostics should contain a state serial");
        AssertJsonTextContains(diagnosticJson, rawStateHistoryKey, "diagnostics should contain a state history key");
        AssertJsonTextContains(diagnosticJson, rawStatePassword, "diagnostics should contain a state password field");
        AssertJsonTextContains(diagnosticJson, rawStateToken, "diagnostics should contain a state access token field");
        AssertJsonTextContains(diagnosticJson, rawStatePath, "diagnostics should contain a state absolute path");
        AssertJsonTextContains(diagnosticJson, rawProfileNote, "diagnostics should contain profile notes");
        AssertJsonTextContains(diagnosticJson, rawProfilePassword, "diagnostics should contain profile extension fields");
        AssertJsonTextContains(diagnosticJson, rawProfileExtensionPath, "diagnostics should contain profile extension paths");
        AssertJsonTextContains(diagnosticJson, rawAlertSoundPath, "diagnostics should contain the complete alert sound path");
    }

    private static BatteryReadAllResult StateResult(
        IReadOnlyList<BatteryReading> readings,
        IReadOnlyList<string> candidateDeviceIds)
    {
        return new BatteryReadAllResult
        {
            Readings = readings,
            InventoryReliable = true,
            HasCandidate = candidateDeviceIds.Count > 0,
            ProviderResults = new[]
            {
                new ProviderBatchResult
                {
                    ProviderName = "test-provider",
                    Readings = readings,
                    CandidateFound = candidateDeviceIds.Count > 0,
                    CandidateDeviceIds = candidateDeviceIds
                }
            }
        };
    }

    private static BatteryReading StateReading(
        string deviceId,
        int battery,
        string serial = "",
        string deviceName = "Test Mouse")
    {
        return new BatteryReading
        {
            BatteryPercentage = battery,
            IsOnline = true,
            PowerState = DevicePowerState.Discharging,
            DeviceId = deviceId,
            DeviceSerial = serial,
            DeviceName = deviceName,
            VendorId = "0x1234",
            ProductId = "0x5678",
            Source = "test-source"
        };
    }

    private static BatteryReading TransitionReading(
        string deviceId,
        int battery,
        DevicePowerState powerState,
        bool cableConnected,
        bool charging)
    {
        return new BatteryReading
        {
            BatteryPercentage = battery,
            HasBatteryPercentage = true,
            IsOnline = true,
            IsCableConnected = cableConnected,
            IsCharging = charging,
            ExternalPowerConnected = cableConnected,
            PowerState = powerState,
            DeviceId = deviceId,
            DeviceName = "Transition Test Mouse",
            VendorId = "0x1234",
            ProductId = "0x5678",
            Source = "test-source"
        };
    }

    private static BatteryReading OfflineTransitionReading(
        string deviceId,
        string serial,
        int battery,
        bool online,
        DevicePowerState powerState,
        bool hasBatteryPercentage,
        bool externalPower,
        DateTime timestampUtc)
    {
        return new BatteryReading
        {
            BatteryPercentage = battery,
            HasBatteryPercentage = hasBatteryPercentage,
            IsOnline = online,
            IsCharging = externalPower,
            IsFullyCharged = powerState == DevicePowerState.FullyCharged,
            IsCableConnected = externalPower,
            ExternalPowerConnected = externalPower,
            PowerState = powerState,
            DeviceId = deviceId,
            DeviceSerial = serial,
            DeviceName = "Offline Test Mouse",
            VendorId = "0x1234",
            ProductId = "0x5678",
            Source = "test-source",
            TimestampUtc = timestampUtc
        };
    }

    private static string CreateHistoryDeviceKey(BatteryReading reading)
    {
        var method = typeof(BatteryHistoryStore).GetMethod(
            "CreateDeviceKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("BatteryHistoryStore.CreateDeviceKey was not found");
        return method.Invoke(null, new object[] { reading }) as string
            ?? throw new InvalidOperationException("BatteryHistoryStore.CreateDeviceKey returned no key");
    }

    private static LowBatteryAlertInput AlertDevice(string key, int battery, bool isCharging = false)
    {
        return new LowBatteryAlertInput(key, battery, IsCharging: isCharging);
    }

    private static ChargingPollingDevice ChargingDevice(string key, bool isCharging = true, bool isFullyCharged = false)
    {
        return new ChargingPollingDevice(key, isCharging, isFullyCharged);
    }

    private static AppSettings AlertSettings(int threshold = 10, int cooldownMinutes = 10)
    {
        return new AppSettings
        {
            AlertThreshold = threshold,
            AlertCooldownMinutes = cooldownMinutes
        };
    }

    private static AppSettings PollingSettings(int intervalMinutes = 10)
    {
        return new AppSettings { PollingIntervalMinutes = intervalMinutes };
    }

    private static void Decision(
        LowBatteryAlertBatchResult result,
        string deviceKey,
        LowBatteryAlertDisposition disposition,
        string reason)
    {
        var decision = result.Decisions.Single(item => string.Equals(item.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase));
        Equal(disposition, decision.Disposition, $"{deviceKey} disposition");
        Equal(reason, decision.Reason, $"{deviceKey} reason");
    }

    private static IReadOnlyList<JsonElement> ReadJsonLinesShared(string path)
    {
        var entries = new List<JsonElement>();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = JsonDocument.Parse(line);
            entries.Add(document.RootElement.Clone());
        }
        return entries;
    }

    private static JsonElement ReadJsonFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonElement FindEvent(IEnumerable<JsonElement> entries, string eventName)
    {
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("event", out var value)
                && string.Equals(value.GetString(), eventName, StringComparison.Ordinal))
                return entry;
        }
        throw new InvalidOperationException($"flight event was not found: {eventName}");
    }

    private static void ValidateFlightSchema(JsonElement entry)
    {
        Equal(JsonValueKind.Object, entry.ValueKind, "flight entry kind");
        Equal("sora.flight.v1", RequiredProperty(entry, "schema", JsonValueKind.String).GetString(), "flight schema");
        True(DateTimeOffset.TryParse(RequiredProperty(entry, "ts_utc", JsonValueKind.String).GetString(), out _), "ts_utc should be an ISO timestamp");
        True(DateTimeOffset.TryParse(RequiredProperty(entry, "ts_local", JsonValueKind.String).GetString(), out _), "ts_local should be an ISO timestamp");
        True(RequiredProperty(entry, "mono_ms", JsonValueKind.Number).GetInt64() >= 0, "mono_ms should be non-negative");
        True(RequiredProperty(entry, "seq", JsonValueKind.Number).GetInt64() > 0, "seq should be positive");
        var level = RequiredProperty(entry, "level", JsonValueKind.String).GetString();
        True(level is "debug" or "info" or "warn" or "error" or "fatal", "level should be normalized");
        True(!string.IsNullOrWhiteSpace(RequiredProperty(entry, "event", JsonValueKind.String).GetString()), "event should be present");
        True(Guid.TryParse(RequiredProperty(entry, "run_id", JsonValueKind.String).GetString(), out _), "run_id should be a GUID");
        var operation = RequiredProperty(entry, "op_id");
        True(operation.ValueKind is JsonValueKind.String or JsonValueKind.Null, "op_id should be a string or null");
        True(!string.IsNullOrWhiteSpace(RequiredProperty(entry, "component", JsonValueKind.String).GetString()), "component should be present");
        True(RequiredProperty(entry, "thread_id", JsonValueKind.Number).GetInt32() > 0, "thread_id should be positive");
        RequiredProperty(entry, "app_version", JsonValueKind.String);
        True(!string.IsNullOrWhiteSpace(RequiredProperty(entry, "outcome", JsonValueKind.String).GetString()), "outcome should be present");
        RequiredProperty(entry, "device");
        RequiredProperty(entry, "data");
        RequiredProperty(entry, "error");
    }

    private static JsonElement RequiredProperty(JsonElement element, string name, JsonValueKind? kind = null)
    {
        if (!element.TryGetProperty(name, out var property))
            throw new InvalidOperationException($"required JSON property is missing: {name}");
        if (kind.HasValue && property.ValueKind != kind.Value)
            throw new InvalidOperationException($"JSON property {name} should be {kind.Value}, actual={property.ValueKind}");
        return property;
    }

    private static void AssertJsonTextDoesNotContain(IEnumerable<JsonElement> entries, string forbidden, string message)
    {
        foreach (var entry in entries)
        {
            foreach (var text in EnumerateJsonText(entry))
            {
                if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{message}: leaked={text}");
            }
        }
    }

    private static void AssertJsonTextContains(IEnumerable<JsonElement> entries, string expected, string message)
    {
        foreach (var entry in entries)
        {
            if (EnumerateJsonText(entry).Any(text => text.Contains(expected, StringComparison.Ordinal)))
                return;
        }
        throw new InvalidOperationException($"{message}: missing={expected}");
    }

    private static IEnumerable<string> EnumerateJsonText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in EnumerateJsonText(property.Value))
                        yield return value;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in EnumerateJsonText(item))
                        yield return value;
                }
                break;

            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }

    private static void NotEqual<T>(T first, T second, string message)
    {
        if (EqualityComparer<T>.Default.Equals(first, second))
            throw new InvalidOperationException($"{message}: both={first}");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"{message}: expected=[{string.Join(",", expected)}], actual=[{string.Join(",", actual)}]");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SoraV2BatteryTip.SelfTest",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private sealed class FakeHidDevice : HidDevice
    {
        private readonly string _path;
        private readonly string _serial;
        private readonly string _productName;
        private readonly string _manufacturer;

        public FakeHidDevice(
            string path,
            string serial,
            string productName = "SelfTest Mouse",
            string manufacturer = "SelfTest Manufacturer")
        {
            _path = path;
            _serial = serial;
            _productName = productName;
            _manufacturer = manufacturer;
        }

        public override int ProductID => 0xAE12;
        public override int ReleaseNumberBcd => 0x0100;
        public override int VendorID => 0x1915;
        public override string DevicePath => _path;
        public override string GetManufacturer() => _manufacturer;
        public override string GetProductName() => _productName;
        public override string GetSerialNumber() => _serial;
        public override int GetMaxInputReportLength() => 64;
        public override int GetMaxOutputReportLength() => 64;
        public override int GetMaxFeatureReportLength() => 64;
        public override string GetFileSystemName() => _path;

        protected override DeviceStream OpenDeviceDirectly(OpenConfiguration openConfig)
        {
            throw new NotSupportedException("Self-test fake devices cannot be opened.");
        }
    }
}
