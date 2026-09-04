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
            ("sora:verified wired endpoint suppresses its receiver", SoraVerifiedWiredEndpointSuppressesReceiver),
            ("sora:receiver pairing response decodes AE12", SoraReceiverPairingResponseDecodesAe12),
            ("sora:pair cache rejects receiver serial epoch changes", SoraPairCacheRejectsReceiverSerialEpochChanges),
            ("sora:unresolved or ambiguous endpoints remain independent", SoraUnresolvedOrAmbiguousEndpointsRemainIndependent),
            ("sora:wired transport separates external power from charging bit", SoraWiredTransportSeparatesExternalPowerFromCharging),
            ("provider:compx failure context retains raw identity", CompxFailureContextRetainsRawIdentity),
            ("provider:profile failure context retains raw identity", KnownProfileFailureContextRetainsRawIdentity),
            ("state:sora transport switch remains one logical device", StateSoraTransportSwitchRemainsOneLogicalDevice),
            ("state:sora receiver removal converges endpoint alias", StateSoraReceiverRemovalConvergesEndpointAlias),
            ("state:sora delayed pairing converges endpoint aliases", StateSoraDelayedPairingConvergesEndpointAliases),
            ("state:sora receiver serial degradation keeps history anchor", StateSoraReceiverSerialDegradationKeepsHistoryAnchor),
            ("state:sora pairing mismatch cannot reclaim a combined alias", StateSoraPairingMismatchCannotReclaimCombinedAlias),
            ("state:sora unrelated receiver mismatch preserves valid alias", StateSoraUnrelatedReceiverMismatchPreservesValidAlias),
            ("state:sora reused receiver path separates serial epochs", StateSoraReusedReceiverPathSeparatesSerialEpochs),
            ("state:sora identity-only confirm replaces an old epoch", StateSoraIdentityOnlyConfirmReplacesOldEpoch),
            ("state:sora identity-only serial degradation keeps canonical key", StateSoraIdentityOnlySerialDegradationKeepsCanonicalKey),
            ("state:sora confirm unifies canonical identity across resolved paths", StateSoraConfirmUnifiesCanonicalAcrossResolvedPaths),
            ("state:sora rejected replacement epoch blocks the old alias", StateSoraRejectedReplacementEpochBlocksOldAlias),
            ("state:sora pair rejection keeps the receiver independently present", StateSoraPairRejectionKeepsReceiverPresent),
            ("state:sora pair rejection advances the receiver self epoch", StateSoraPairRejectionAdvancesReceiverSelfEpoch),
            ("state:sora receiver epoch resets runtime policy identity", StateSoraReceiverEpochResetsRuntimePolicyIdentity),
            ("state:sora receiver fallback preserves the log epoch token", StateSoraReceiverFallbackPreservesLogEpochToken),
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
            ("history:sora transports share one logical key", HistorySoraTransportsShareLogicalKey),
            ("history:sora legacy transport keys migrate to receiver key", HistorySoraLegacyTransportKeysMigrate),
            ("history:sora asymmetric serials keep receiver anchor", HistorySoraAsymmetricSerialsKeepReceiverAnchor),
            ("history:sora wired restart recovers receiver key", HistorySoraWiredRestartRecoversReceiverKey),
            ("history:sora pairing mismatch invalidates receiver recovery", HistorySoraPairingMismatchInvalidatesReceiverRecovery),
            ("history:sora unrelated receiver mismatch preserves valid anchor", HistorySoraUnrelatedReceiverMismatchPreservesValidAnchor),
            ("history:sora receiver association survives key form changes", HistorySoraReceiverAssociationSurvivesKeyFormChanges),
            ("history:sora reused receiver path separates serial epochs", HistorySoraReusedReceiverPathSeparatesSerialEpochs),
            ("history:sora rejected replacement epoch blocks cold recovery", HistorySoraRejectedReplacementEpochBlocksColdRecovery),
            ("history:sora receiver serial degradation survives pair rejection", HistorySoraReceiverSerialDegradationSurvivesPairRejection),
            ("history:sora pair rejection persists the replacement receiver epoch", HistorySoraPairRejectionPersistsReplacementReceiverEpoch),
            ("history:sora receiver association recurrence refreshes active relation", HistorySoraAssociationRecurrenceRefreshesActiveRelation),
            ("history:sora prune retains identity relations", HistorySoraPruneRetainsIdentityRelations),
            ("history:sora verified pair supersedes equal-sample tombstone", HistorySoraVerifiedPairSupersedesEqualSampleTombstone),
            ("history:power state uses unified charging semantics", HistoryPowerStateUsesUnifiedChargingSemantics),
            ("inventory:failed snapshot retries after two seconds", InventoryFailedSnapshotRetriesAfterTwoSeconds),
            ("inventory:reliable snapshot caches until invalidated", InventoryReliableSnapshotCachesUntilInvalidated),
            ("flight:flushed lines have valid schema", FlightRecorderFlushedLinesHaveValidSchema),
            ("flight:operation scope crosses Task.Run", FlightRecorderOperationScopeCrossesTaskRun),
            ("flight:raw device identity is readable", FlightRecorderRecordsRawDeviceIdentity),
            ("flight:battery fact validity is explicit", FlightRecorderBatteryFactValidityIsExplicit),
            ("flight:logical transport identity retains both HID paths", FlightRecorderRetainsLogicalTransportIdentity),
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

    private static void SoraVerifiedWiredEndpointSuppressesReceiver()
    {
        var receiver = new NinjutsoSoraEndpoint("receiver-path", 0xAE1C, "Ninjutso Sora V2", "000000000000");
        var wired = new NinjutsoSoraEndpoint("wired-path", 0xAE12, "Ninjutso Sora V2", "000000000000");
        var wirelessPlan = NinjutsoSoraTransportPolicy.CreatePlan(new[] { receiver });
        var pairedProductIds = new Dictionary<string, int?> { [receiver.DeviceId] = 0xAE12 };

        var plan = NinjutsoSoraTransportPolicy.CreatePlan(new[] { receiver, wired }, pairedProductIds);
        Equal(1, plan.SelectedEndpoints.Count, "verified pair should expose one authoritative endpoint");
        Equal("wired-path", plan.SelectedEndpoints[0].Endpoint.DeviceId, "wired endpoint must win while plugged in");
        Equal(DeviceConnectionTransport.WiredUsb, plan.SelectedEndpoints[0].Transport, "selected transport");
        Equal(HistoryAnchorEvidence.ConfirmReceiver, plan.SelectedEndpoints[0].HistoryAnchorEvidence,
            "verified pair must emit plan-level positive identity evidence");
        Equal(1, plan.SuppressedEndpoints.Count, "receiver should be suppressed");
        Equal("receiver-path", plan.SuppressedEndpoints[0].DeviceId, "suppressed receiver path");
        Equal(
            wirelessPlan.SelectedEndpoints[0].LogicalDeviceId,
            plan.SelectedEndpoints[0].LogicalDeviceId,
            "wired endpoint must retain the receiver-anchored logical identity");
        True(plan.SelectedEndpoints[0].ResolvedDeviceIds.Contains("receiver-path"), "logical reading should resolve the receiver path");
        True(plan.SelectedEndpoints[0].ResolvedDeviceIds.Contains("wired-path"), "logical reading should resolve the wired path");
        var identityObservation = NinjutsoSoraOfficialProvider.CreateIdentityObservations(plan).Single();
        Equal(HistoryAnchorEvidence.ConfirmReceiver, identityObservation.HistoryAnchorEvidence,
            "positive identity evidence must not depend on a battery read");
        True(!string.IsNullOrWhiteSpace(identityObservation.AssociationReceiverHistoryKey),
            "positive identity evidence must carry a stable receiver association key");
        True(identityObservation.ResolvedDeviceIds.Contains("receiver-path"), "positive observation receiver path");
        True(identityObservation.ResolvedDeviceIds.Contains("wired-path"), "positive observation wired path");

        var receiverWithSerial = receiver with { DeviceSerial = "REAL-RECEIVER-SERIAL" };
        var serialPlan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiverWithSerial, wired },
            new Dictionary<string, int?> { [receiverWithSerial.DeviceId] = 0xAE12 });
        var serialObservation = NinjutsoSoraOfficialProvider.CreateIdentityObservations(serialPlan).Single();
        NotEqual(identityObservation.HistoryDeviceKey, serialObservation.HistoryDeviceKey,
            "curve key should promote to the receiver serial when it is available");
        Equal(identityObservation.AssociationReceiverHistoryKey, serialObservation.AssociationReceiverHistoryKey,
            "receiver relation key must remain path-stable when serial availability changes");

        var reversed = NinjutsoSoraTransportPolicy.CreatePlan(new[] { wired, receiver }, pairedProductIds);
        Equal("wired-path", reversed.SelectedEndpoints.Single().Endpoint.DeviceId, "enumeration order must not affect arbitration");
        Equal(plan.SelectedEndpoints[0].LogicalDeviceId, reversed.SelectedEndpoints[0].LogicalDeviceId, "enumeration order logical identity");

        var pairedReading = SoraStateReading(
            "wired-path",
            plan.SelectedEndpoints[0].LogicalDeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { "receiver-path", "wired-path" });
        var coverage = new[]
        {
            new ProviderBatchResult
            {
                ProviderName = "SORA V2 Official HID",
                CandidateFound = true,
                CandidateDeviceIds = new[] { "receiver-path", "wired-path" },
                Readings = new[] { pairedReading }
            }
        };
        True(DeviceIdentity.CandidatePathsCoveredBy(coverage), "one logical reading should cover both resolved HID endpoints");
    }

    private static void SoraReceiverPairingResponseDecodesAe12()
    {
        var response = new byte[32];
        response[0] = 0x05;
        response[1] = 0x28;
        response[9] = 0x12;
        response[10] = 0xAE;
        Equal<int?>(0xAE12, NinjutsoSoraOfficialProvider.ParsePairedProductIdResponse(response), "0x28 little-endian paired PID");

        response[9] = 0;
        response[10] = 0;
        Equal<int?>(null, NinjutsoSoraOfficialProvider.ParsePairedProductIdResponse(response), "zero paired PID should be retried");
        response[1] = 0x15;
        Equal<int?>(null, NinjutsoSoraOfficialProvider.ParsePairedProductIdResponse(response), "wrong command must be rejected");
    }

    private static void SoraPairCacheRejectsReceiverSerialEpochChanges()
    {
        True(NinjutsoSoraOfficialProvider.ReceiverSerialMatchesPairCache(
            "RECEIVER-ONE",
            "receiver-one"), "the same normalized receiver serial may reuse a live pairing result");
        False(NinjutsoSoraOfficialProvider.ReceiverSerialMatchesPairCache(
            "RECEIVER-ONE",
            "RECEIVER-TWO"), "a reused HID path must not reuse another receiver's pairing result");
        False(NinjutsoSoraOfficialProvider.ReceiverSerialMatchesPairCache(
            "RECEIVER-ONE",
            "000000000000"), "stable-to-placeholder serial degradation must fail closed");
        True(NinjutsoSoraOfficialProvider.ReceiverSerialMatchesPairCache(
            "000000000000",
            ""), "equivalent placeholder serials may share an unresolved cache epoch");
    }

    private static void SoraUnresolvedOrAmbiguousEndpointsRemainIndependent()
    {
        var receiver = new NinjutsoSoraEndpoint("receiver-a", 0xAE1C, "Ninjutso Sora V2", "");
        var wired = new NinjutsoSoraEndpoint("wired-a", 0xAE12, "Ninjutso Sora V2", "");

        var unresolved = NinjutsoSoraTransportPolicy.CreatePlan(new[] { receiver, wired });
        Equal(2, unresolved.SelectedEndpoints.Count, "missing 0x28 evidence must not merge endpoints");
        Equal(0, unresolved.SuppressedEndpoints.Count, "missing 0x28 evidence must not suppress a receiver");
        Equal(HistoryAnchorEvidence.None, unresolved.SelectedEndpoints.Single(item => item.Transport == DeviceConnectionTransport.WiredUsb).HistoryAnchorEvidence,
            "unresolved pairing must not recover or invalidate persisted identity");

        var mismatch = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiver, wired },
            new Dictionary<string, int?> { [receiver.DeviceId] = 0xAE11 });
        Equal(2, mismatch.SelectedEndpoints.Count, "mismatched paired PID must not merge endpoints");
        Equal(HistoryAnchorEvidence.RejectPersistedReceiver,
            mismatch.SelectedEndpoints.Single(item => item.Transport == DeviceConnectionTransport.WiredUsb).HistoryAnchorEvidence,
            "valid mismatched pairing evidence must invalidate an old receiver association");
        var mismatchObservations = NinjutsoSoraOfficialProvider.CreateIdentityObservations(mismatch);
        Equal(HistoryAnchorEvidence.RejectPersistedReceiver,
            mismatchObservations.Single(observation =>
                observation.HistoryAnchorEvidence == HistoryAnchorEvidence.RejectPersistedReceiver).HistoryAnchorEvidence,
            "negative identity evidence must not depend on a battery read");
        Equal(HistoryAnchorEvidence.ConfirmReceiver,
            mismatchObservations.Single(observation =>
                observation.DeviceId == receiver.DeviceId).HistoryAnchorEvidence,
            "a pairing rejection must retain the receiver's independent self identity");

        var wiredOnly = NinjutsoSoraTransportPolicy.CreatePlan(new[] { wired });
        Equal(HistoryAnchorEvidence.RecoverPersistedReceiver, wiredOnly.SelectedEndpoints.Single().HistoryAnchorEvidence,
            "persisted receiver identity may only be recovered when no receiver is present");
        var secondWired = new NinjutsoSoraEndpoint("wired-b", 0xAE13, "Ninjutso Sora V2", "");
        var twoWired = NinjutsoSoraTransportPolicy.CreatePlan(new[] { wired, secondWired });
        True(twoWired.SelectedEndpoints.All(item => item.HistoryAnchorEvidence == HistoryAnchorEvidence.RecoverPersistedReceiver),
            "every wired endpoint may recover its exact persisted path when no receiver is present");

        var secondReceiver = new NinjutsoSoraEndpoint("receiver-b", 0xAE1C, "Ninjutso Sora V2", "");
        var ambiguous = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiver, secondReceiver, wired },
            new Dictionary<string, int?>
            {
                [receiver.DeviceId] = 0xAE12,
                [secondReceiver.DeviceId] = 0xAE12
            });
        Equal(3, ambiguous.SelectedEndpoints.Count, "ambiguous multi-device topology must stay independent");
        Equal(0, ambiguous.SuppressedEndpoints.Count, "ambiguous topology must not suppress endpoints");

        var alternateReceiver = new NinjutsoSoraEndpoint("receiver-ae8a", 0xAE8A, "Ninjutso Sora", "");
        var alternatePlan = NinjutsoSoraTransportPolicy.CreatePlan(new[] { alternateReceiver });
        Equal(DeviceConnectionTransport.Receiver, alternatePlan.SelectedEndpoints.Single().Transport,
            "AE8A should be classified as a receiver transport");
        True(alternatePlan.SelectedEndpoints.Single().LogicalDeviceId.StartsWith("ninjutso-sora-v2:receiver:", StringComparison.Ordinal),
            "AE8A should receive a receiver logical identity");
    }

    private static void SoraWiredTransportSeparatesExternalPowerFromCharging()
    {
        var pending = NinjutsoSoraTransportPolicy.ResolvePowerFacts(
            75,
            reportedCharging: false,
            DeviceConnectionTransport.WiredUsb);

        False(pending.IsCharging, "provider charging bit must remain factual");
        True(pending.IsCableConnected, "wired mouse should be cable-connected");
        True(pending.ExternalPowerConnected, "wired mouse should be externally powered");
        Equal(DevicePowerState.PendingCharge, pending.PowerState, "wired mouse should wait for a late charging bit");
        False(pending.IsFullyCharged, "75 percent must not be marked full");

        var charging = NinjutsoSoraTransportPolicy.ResolvePowerFacts(
            75,
            reportedCharging: true,
            DeviceConnectionTransport.WiredUsb);
        True(charging.IsCharging, "reported charging bit should be retained");
        Equal(DevicePowerState.Charging, charging.PowerState, "reported charging state");
    }

    private static void CompxFailureContextRetainsRawIdentity()
    {
        const string devicePath = @"\\?\hid#vid_373b&pid_1234#COMPX-FAILURE-CONTEXT";
        const string deviceName = "COMPX SelfTest Mouse";
        const string deviceSerial = "COMPX-SERIAL-123";
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var device = new FakeHidDevice(
            devicePath,
            deviceSerial,
            deviceName,
            vendorId: 0x373B,
            productId: 0x1234);
        var inventory = new HidInventorySnapshot
        {
            Devices = new HidDevice[] { device },
            CreatedUtc = StartUtc,
            IsReliable = true
        };

        var result = new CompxBatteryProvider(recorder)
            .ReadAsync(inventory, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        True(result.CandidateFound, "COMPX fake should reach the device-open failure path");
        True(recorder.Flush(TimeSpan.FromSeconds(5)), "COMPX failure context should flush");
        var loggedDevice = FindEvent(
                ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl")),
                "provider.device_open_failed")
            .GetProperty("device");
        Equal(devicePath, loggedDevice.GetProperty("path").GetString(), "COMPX failure path should remain readable");
        Equal(deviceName, loggedDevice.GetProperty("name").GetString(), "COMPX failure name should remain readable");
        Equal(deviceSerial, loggedDevice.GetProperty("serial").GetString(), "COMPX failure serial should remain readable");
    }

    private static void KnownProfileFailureContextRetainsRawIdentity()
    {
        const string devicePath = @"\\?\hid#vid_1234&pid_5678#PROFILE-FAILURE-CONTEXT";
        const string deviceName = "Profile SelfTest Mouse";
        const string deviceSerial = "PROFILE-SERIAL-456";
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        File.WriteAllText(
            Path.Combine(paths.ProfilesDirectory, "selftest-profile.json"),
            """
            {
              "Name": "SelfTest failure profile",
              "VendorId": "0x1234",
              "ProductIds": ["0x5678"],
              "ReportType": "Feature",
              "ReportId": "0x05",
              "BatteryOffset": 1
            }
            """);
        var device = new FakeHidDevice(
            devicePath,
            deviceSerial,
            deviceName,
            vendorId: 0x1234,
            productId: 0x5678);
        var inventorySource = new HidDeviceInventory(
            enumerateDevices: () => new HidDevice[] { device },
            utcNow: () => StartUtc);
        var inventory = inventorySource.GetSnapshot();
        using var recorder = new FlightRecorder(paths.LogsDirectory);

        var result = new KnownDeviceProfileProvider(paths, inventorySource, recorder)
            .ReadAsync(inventory, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        True(result.CandidateFound, "profile fake should reach the device-open failure path");
        True(recorder.Flush(TimeSpan.FromSeconds(5)), "profile failure context should flush");
        var loggedDevice = FindEvent(
                ReadJsonLinesShared(Path.Combine(paths.LogsDirectory, "flight-current.jsonl")),
                "provider.device_open_failed")
            .GetProperty("device");
        Equal(devicePath, loggedDevice.GetProperty("path").GetString(), "profile failure path should remain readable");
        Equal(deviceName, loggedDevice.GetProperty("name").GetString(), "profile failure name should remain readable");
        Equal(deviceSerial, loggedDevice.GetProperty("serial").GetString(), "profile failure serial should remain readable");
    }

    private static void StateSoraTransportSwitchRemainsOneLogicalDevice()
    {
        const string logicalDeviceId = "ninjutso-sora-v2:receiver:receiver-path";
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var store = new DeviceBatteryStateStore(recorder);
        var wireless = SoraStateReading(
            "receiver-path",
            logicalDeviceId,
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { "receiver-path" });
        var initial = store.Apply(
            StateResult(new[] { wireless }, new[] { "receiver-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));
        Equal(1, initial.CurrentReadings.Count, "wireless baseline logical device count");

        var wired = SoraStateReading(
            "wired-path",
            logicalDeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { "receiver-path", "wired-path" });
        var plugged = store.Apply(
            StateResult(new[] { wired }, new[] { "receiver-path", "wired-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, plugged.CurrentReadings.Count, "plugging in must not create a phantom second mouse");
        Equal(0, plugged.OfflineReadings.Count, "transport switch must not emit offline");
        Equal(0, plugged.Rekeys.Count, "stable logical identity should not need a policy-state rekey");
        Equal("wired-path", plugged.CurrentReadings[0].DeviceId, "wired endpoint should become the physical path");
        Equal(DeviceConnectionTransport.WiredUsb, plugged.CurrentReadings[0].ConnectionTransport, "wired transport state");
        True(DevicePowerSemantics.IsExternallyPowered(plugged.CurrentReadings[0]), "plugged logical mouse should drive charging UI");

        var unplugged = store.Apply(
            StateResult(new[] { SoraStateReading(
                "receiver-path",
                logicalDeviceId,
                74,
                DeviceConnectionTransport.Receiver,
                externallyPowered: false,
                new[] { "receiver-path" }) }, new[] { "receiver-path" }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));
        Equal(1, unplugged.CurrentReadings.Count, "unplugging should keep one logical mouse");
        Equal(0, unplugged.OfflineReadings.Count, "unplug transport switch must not emit offline");
        Equal("receiver-path", unplugged.CurrentReadings[0].DeviceId, "receiver should resume after unplug");
        False(DevicePowerSemantics.IsExternallyPowered(unplugged.CurrentReadings[0]), "unplugged mouse should return to battery state");

        True(recorder.Flush(TimeSpan.FromSeconds(5)), "transport transitions should flush");
        var transportEvents = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"))
            .Where(entry => entry.TryGetProperty("event", out var eventName)
                && string.Equals(eventName.GetString(), "device.transport_changed", StringComparison.Ordinal))
            .ToArray();
        Equal(2, transportEvents.Length, "plug and unplug should each record a transport transition");
        var pluggedData = transportEvents[0].GetProperty("data");
        Equal("Receiver", pluggedData.GetProperty("previous_transport").GetString(), "plug event previous transport");
        Equal("WiredUsb", pluggedData.GetProperty("transport").GetString(), "plug event current transport");
    }

    private static void StateSoraReceiverRemovalConvergesEndpointAlias()
    {
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string receiverHistoryKey = "0X1915:0XAE1C:PATH:RECEIVER-HISTORY";
        const string wiredHistoryKey = "0X1915:0XAE12:PATH:WIRED-HISTORY";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "receiver-path", "wired-path" },
            historyDeviceKey: receiverHistoryKey) },
                new[] { "receiver-path", "wired-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var update = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    wiredLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "wired-path" },
                    historyDeviceKey: wiredHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { "wired-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, update.CurrentReadings.Count, "receiver removal must not leave the combined logical state behind");
        Equal(1, update.Rekeys.Count, "receiver removal should converge the overlapping endpoint alias");
        Equal(0, update.OfflineReadings.Count, "endpoint alias convergence is not an offline event");
        Equal(wiredLogicalId, update.CurrentReadings[0].LogicalDeviceId, "direct-only logical identity");
        Equal(receiverHistoryKey, update.CurrentReadings[0].HistoryDeviceKey,
            "direct-only fallback should retain the known receiver history anchor");

        var repeated = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    wiredLogicalId,
                    76,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "wired-path" },
                    historyDeviceKey: wiredHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { "wired-path" }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));
        Equal(receiverHistoryKey, repeated.CurrentReadings.Single().HistoryDeviceKey,
            "subsequent direct-only polls should not regress to a split wired history key");
    }

    private static void StateSoraDelayedPairingConvergesEndpointAliases()
    {
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        var store = new DeviceBatteryStateStore();
        var unresolved = store.Apply(
            StateResult(
                new[]
                {
                    SoraStateReading(
                        "receiver-path",
                        receiverLogicalId,
                        75,
                        DeviceConnectionTransport.Receiver,
                        externallyPowered: false,
                        new[] { "receiver-path" }),
                    SoraStateReading(
                        "wired-path",
                        wiredLogicalId,
                        75,
                        DeviceConnectionTransport.WiredUsb,
                        externallyPowered: true,
                        new[] { "wired-path" })
                },
                new[] { "receiver-path", "wired-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));
        Equal(2, unresolved.CurrentReadings.Count, "unresolved pairing starts as two independent endpoints");

        var resolved = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "receiver-path", "wired-path" }) },
                new[] { "receiver-path", "wired-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, resolved.CurrentReadings.Count, "late pairing success must remove the old wired logical alias");
        Equal(1, resolved.Rekeys.Count, "late pairing should record the removed endpoint alias");
        Equal(0, resolved.OfflineReadings.Count, "late pairing convergence is not an offline event");
        Equal(receiverLogicalId, resolved.CurrentReadings[0].LogicalDeviceId, "resolved receiver-anchored identity");
    }

    private static void StateSoraReceiverSerialDegradationKeepsHistoryAnchor()
    {
        const string logicalDeviceId = "ninjutso-sora-v2:receiver:receiver-path";
        const string serialKey = "0X1915:0XAE1C:SERIAL:REAL-RECEIVER";
        const string pathKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "receiver-path",
                    logicalDeviceId,
                    75,
                    DeviceConnectionTransport.Receiver,
                    externallyPowered: false,
                    new[] { "receiver-path" },
                    historyDeviceKey: serialKey,
                    deviceSerial: "REAL-RECEIVER") },
                new[] { "receiver-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var degraded = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "receiver-path",
                    logicalDeviceId,
                    74,
                    DeviceConnectionTransport.Receiver,
                    externallyPowered: false,
                    new[] { "receiver-path" },
                    historyDeviceKey: pathKey) },
                new[] { "receiver-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));
        Equal(serialKey, degraded.CurrentReadings.Single().HistoryDeviceKey,
            "temporary loss of receiver serial must not split history");
    }

    private static void StateSoraPairingMismatchCannotReclaimCombinedAlias()
    {
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string receiverHistoryKey = "0X1915:0XAE1C:PATH:RECEIVER-HISTORY";
        const string wiredHistoryKey = "0X1915:0XAE12:PATH:WIRED-HISTORY";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "receiver-path", "wired-path" },
                    historyDeviceKey: receiverHistoryKey,
                    associationReceiverHistoryKey: receiverHistoryKey) },
                new[] { "receiver-path", "wired-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                "wired-path",
                wiredLogicalId,
                wiredHistoryKey,
                HistoryAnchorEvidence.RejectPersistedReceiver,
                new[] { "wired-path" },
                receiverHistoryKey)
        });

        var mismatch = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    wiredLogicalId,
                    76,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "wired-path" },
                    historyDeviceKey: wiredHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { "wired-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, mismatch.CurrentReadings.Count,
            "after a mismatch read failure and receiver removal, wired recovery must stay independent");
        Equal(0, mismatch.Rekeys.Count, "mismatched pairing must not claim the prior combined alias");
        Equal(1, mismatch.OfflineReadings.Count, "the obsolete combined receiver state should go offline");
        Equal(wiredHistoryKey,
            mismatch.CurrentReadings.Single().HistoryDeviceKey,
            "mismatched wired endpoint must retain its independent history key");
        Equal(HistoryAnchorEvidence.RecoverPersistedReceiver,
            mismatch.CurrentReadings.Single().HistoryAnchorEvidence,
            "direct-only plan fact remains recovery while scoped evidence blocks the rejected receiver");
    }

    private static void StateSoraUnrelatedReceiverMismatchPreservesValidAlias()
    {
        const string receiverOneLogicalId = "ninjutso-sora-v2:receiver:receiver-one";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string receiverOneHistoryKey = "0X1915:0XAE1C:PATH:RECEIVER-ONE";
        const string receiverOneAssociationKey = "0X1915:0XAE1C:PATH:ASSOCIATION-ONE";
        const string receiverTwoAssociationKey = "0X1915:0XAE1C:PATH:ASSOCIATION-TWO";
        const string wiredHistoryKey = "0X1915:0XAE12:PATH:WIRED-HISTORY";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    receiverOneLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "receiver-one", "wired-path" },
                    historyDeviceKey: receiverOneHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: receiverOneAssociationKey) },
                new[] { "receiver-one", "wired-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                "wired-path",
                wiredLogicalId,
                wiredHistoryKey,
                HistoryAnchorEvidence.RejectPersistedReceiver,
                new[] { "wired-path" },
                receiverTwoAssociationKey)
        });

        var update = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    wiredLogicalId,
                    76,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "wired-path" },
                    historyDeviceKey: wiredHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { "receiver-two", "wired-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(1, update.CurrentReadings.Count,
            "an unrelated receiver rejection must not create a duplicate wired mouse state");
        Equal(1, update.Rekeys.Count,
            "the still-valid receiver-one relation should converge the wired endpoint alias");
        Equal(receiverOneHistoryKey, update.CurrentReadings.Single().HistoryDeviceKey,
            "unrelated receiver-two evidence must not invalidate receiver-one history");
        Equal(receiverOneAssociationKey, update.CurrentReadings.Single().AssociationReceiverHistoryKey,
            "recovered state must retain the valid receiver-one association scope");
    }

    private static void StateSoraReusedReceiverPathSeparatesSerialEpochs()
    {
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string receiverAssociationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string receiverOneHistoryKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string receiverTwoHistoryKey = "0X1915:0XAE1C:SERIAL:RECEIVER-TWO";
        const string wiredHistoryKey = "0X1915:0XAE12:PATH:WIRED-PATH";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "receiver-path", "wired-path" },
                    historyDeviceKey: receiverOneHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: receiverAssociationKey,
                    associationReceiverSerial: "RECEIVER-ONE") },
                new[] { "receiver-path", "wired-path" }),
            StartUtc,
            TimeSpan.FromMinutes(10));
        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                "wired-path",
                wiredLogicalId,
                wiredHistoryKey,
                HistoryAnchorEvidence.RejectPersistedReceiver,
                new[] { "wired-path" },
                receiverAssociationKey,
                "RECEIVER-ONE")
        });
        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                "wired-path",
                receiverLogicalId,
                receiverTwoHistoryKey,
                HistoryAnchorEvidence.ConfirmReceiver,
                new[] { "receiver-path", "wired-path" },
                receiverAssociationKey,
                "RECEIVER-TWO")
        });
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    receiverLogicalId,
                    80,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "receiver-path", "wired-path" },
                    historyDeviceKey: receiverTwoHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: receiverAssociationKey,
                    associationReceiverSerial: "RECEIVER-TWO") },
                new[] { "receiver-path", "wired-path" }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        var direct = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    "wired-path",
                    wiredLogicalId,
                    81,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { "wired-path" },
                    historyDeviceKey: wiredHistoryKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { "wired-path" }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));

        Equal(1, direct.CurrentReadings.Count,
            "old receiver-one rejection must not duplicate receiver-two's wired state");
        Equal(receiverTwoHistoryKey, direct.CurrentReadings.Single().HistoryDeviceKey,
            "a reused HID path with a different stable serial must keep receiver-two independent");
        Equal("RECEIVER-TWO", direct.CurrentReadings.Single().AssociationReceiverSerial,
            "recovered state must retain the active receiver serial epoch");
    }

    private static void StateSoraIdentityOnlyConfirmReplacesOldEpoch()
    {
        const string receiverPath = "receiver-path";
        const string wiredPath = "wired-path";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string receiverOneKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string receiverTwoKey = "0X1915:0XAE1C:SERIAL:RECEIVER-TWO";
        const string wiredKey = "0X1915:0XAE12:PATH:WIRED-PATH";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { receiverPath, wiredPath },
                    historyDeviceKey: receiverOneKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: associationKey,
                    associationReceiverSerial: "RECEIVER-ONE") },
                new[] { receiverPath, wiredPath }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                wiredPath,
                receiverLogicalId,
                receiverTwoKey,
                HistoryAnchorEvidence.ConfirmReceiver,
                new[] { receiverPath, wiredPath },
                associationKey,
                "RECEIVER-TWO",
                StartUtc.AddMinutes(1))
        });

        var direct = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    wiredLogicalId,
                    80,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { wiredPath },
                    historyDeviceKey: wiredKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { wiredPath }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));

        Equal(1, direct.CurrentReadings.Count, "identity-only confirm must converge to one wired state");
        Equal(receiverTwoKey, direct.CurrentReadings.Single().HistoryDeviceKey,
            "a confirmed replacement receiver must win even when its battery read failed");
        Equal("RECEIVER-TWO", direct.CurrentReadings.Single().AssociationReceiverSerial,
            "wired recovery must carry the latest confirmed receiver epoch");
        Equal(1, direct.OfflineReadings.Count, "the superseded receiver epoch must go offline");
    }

    private static void StateSoraIdentityOnlySerialDegradationKeepsCanonicalKey()
    {
        const string receiverPath = "receiver-path";
        const string wiredPath = "wired-path";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string serialKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string pathKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string wiredKey = "0X1915:0XAE12:PATH:WIRED-PATH";
        var store = new DeviceBatteryStateStore();
        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                wiredPath,
                receiverLogicalId,
                serialKey,
                HistoryAnchorEvidence.ConfirmReceiver,
                new[] { receiverPath, wiredPath },
                associationKey,
                "RECEIVER-ONE",
                StartUtc),
            SoraIdentityObservation(
                wiredPath,
                receiverLogicalId,
                pathKey,
                HistoryAnchorEvidence.ConfirmReceiver,
                new[] { receiverPath, wiredPath },
                associationKey,
                "",
                StartUtc.AddMinutes(1))
        });

        var direct = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    wiredLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { wiredPath },
                    historyDeviceKey: wiredKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { wiredPath }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));

        Equal(serialKey, direct.CurrentReadings.Single().HistoryDeviceKey,
            "an identity-only placeholder serial must not downgrade a known receiver serial curve key");
        Equal("RECEIVER-ONE", direct.CurrentReadings.Single().AssociationReceiverSerial,
            "an identity-only placeholder serial must retain the stable receiver epoch");

        var promotionStore = new DeviceBatteryStateStore();
        promotionStore.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                wiredPath,
                receiverLogicalId,
                pathKey,
                HistoryAnchorEvidence.ConfirmReceiver,
                new[] { receiverPath, wiredPath },
                associationKey,
                "",
                StartUtc)
        });
        var promoted = promotionStore.Apply(
            StateResult(
                new[] { SoraStateReading(
                    receiverPath,
                    receiverLogicalId,
                    74,
                    DeviceConnectionTransport.Receiver,
                    externallyPowered: false,
                    new[] { receiverPath },
                    historyDeviceKey: serialKey,
                    deviceSerial: "RECEIVER-ONE",
                    associationReceiverHistoryKey: associationKey,
                    associationReceiverSerial: "RECEIVER-ONE") },
                new[] { receiverPath }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));
        Equal(serialKey, promoted.CurrentReadings.Single().HistoryDeviceKey,
            "an older placeholder fact must not downgrade a newly observed stable receiver key");
        Equal("RECEIVER-ONE", promoted.CurrentReadings.Single().AssociationReceiverSerial,
            "a live stable receiver serial must take precedence over placeholder identity history");
    }

    private static void StateSoraConfirmUnifiesCanonicalAcrossResolvedPaths()
    {
        const string receiverPath = "receiver-path";
        const string wiredPath = "wired-path";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string receiverOneKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string receiverTwoKey = "0X1915:0XAE1C:SERIAL:RECEIVER-TWO";
        const string wiredKey = "0X1915:0XAE12:PATH:WIRED-PATH";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { receiverPath, wiredPath },
                    historyDeviceKey: receiverOneKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: associationKey,
                    associationReceiverSerial: "RECEIVER-ONE") },
                new[] { receiverPath, wiredPath }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                receiverPath,
                receiverLogicalId,
                receiverTwoKey,
                HistoryAnchorEvidence.ConfirmReceiver,
                new[] { receiverPath },
                associationKey,
                "RECEIVER-TWO",
                StartUtc.AddMinutes(1)),
            SoraIdentityObservation(
                wiredPath,
                wiredLogicalId,
                wiredKey,
                HistoryAnchorEvidence.RejectPersistedReceiver,
                new[] { wiredPath },
                associationKey,
                "RECEIVER-TWO",
                StartUtc.AddMinutes(1))
        });

        var pairedWithoutSerial = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    receiverLogicalId,
                    80,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { receiverPath, wiredPath },
                    historyDeviceKey: associationKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: associationKey) },
                new[] { receiverPath, wiredPath }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));
        Equal(receiverTwoKey, pairedWithoutSerial.CurrentReadings.Single().HistoryDeviceKey,
            "a degraded confirm must take the replacement epoch canonical key from either resolved path");

        var wiredOnly = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    wiredLogicalId,
                    81,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { wiredPath },
                    historyDeviceKey: wiredKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { wiredPath }),
            StartUtc.AddMinutes(3),
            TimeSpan.FromMinutes(10));

        Equal(receiverTwoKey, wiredOnly.CurrentReadings.Single().HistoryDeviceKey,
            "every path in one confirm must retain the same replacement canonical key for later recovery");
        Equal("RECEIVER-TWO", wiredOnly.CurrentReadings.Single().AssociationReceiverSerial,
            "wired-only recovery must retain the replacement receiver epoch on every resolved path");
    }

    private static void StateSoraRejectedReplacementEpochBlocksOldAlias()
    {
        const string receiverPath = "receiver-path";
        const string wiredPath = "wired-path";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:wired-path";
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string receiverOneKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string wiredKey = "0X1915:0XAE12:PATH:WIRED-PATH";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    receiverLogicalId,
                    75,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { receiverPath, wiredPath },
                    historyDeviceKey: receiverOneKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: associationKey,
                    associationReceiverSerial: "RECEIVER-ONE") },
                new[] { receiverPath, wiredPath }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(new[]
        {
            SoraIdentityObservation(
                wiredPath,
                wiredLogicalId,
                wiredKey,
                HistoryAnchorEvidence.RejectPersistedReceiver,
                new[] { receiverPath, wiredPath },
                associationKey,
                "RECEIVER-TWO",
                StartUtc.AddMinutes(1))
        });

        var mismatch = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    wiredLogicalId,
                    80,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { wiredPath },
                    historyDeviceKey: wiredKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { receiverPath, wiredPath }),
            StartUtc.AddMinutes(2),
            TimeSpan.FromMinutes(10));

        Equal(1, mismatch.CurrentReadings.Count, "a rejected replacement epoch must not leave a duplicate state");
        Equal(wiredKey, mismatch.CurrentReadings.Single().HistoryDeviceKey,
            "a new rejected receiver epoch must block recovery of the old receiver curve");
        Equal(1, mismatch.OfflineReadings.Count,
            "the old combined state must not remain present through a reused receiver HID path");
    }

    private static void StateSoraPairRejectionKeepsReceiverPresent()
    {
        var receiver = new NinjutsoSoraEndpoint(
            "receiver-path",
            0xAE1C,
            "Ninjutso Sora V2 Receiver",
            "RECEIVER-ONE");
        var wired = new NinjutsoSoraEndpoint(
            "wired-path",
            0xAE12,
            "Ninjutso Sora V2",
            "RECEIVER-ONE");
        var mismatchPlan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiver, wired },
            new Dictionary<string, int?> { [receiver.DeviceId] = 0xAE11 });
        var observations = NinjutsoSoraOfficialProvider.CreateIdentityObservations(mismatchPlan);
        var receiverObservation = observations.Single(observation => observation.DeviceId == receiver.DeviceId);
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    receiver.DeviceId,
                    receiverObservation.LogicalDeviceId,
                    70,
                    DeviceConnectionTransport.Receiver,
                    externallyPowered: false,
                    new[] { receiver.DeviceId },
                    historyDeviceKey: receiverObservation.HistoryDeviceKey,
                    deviceSerial: receiver.DeviceSerial,
                    associationReceiverHistoryKey: receiverObservation.AssociationReceiverHistoryKey,
                    associationReceiverSerial: receiver.DeviceSerial) },
                new[] { receiver.DeviceId }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(observations);
        var update = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wired.DeviceId,
                    mismatchPlan.SelectedEndpoints.Single(selection =>
                        selection.Transport == DeviceConnectionTransport.WiredUsb).LogicalDeviceId,
                    80,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { wired.DeviceId },
                    historyDeviceKey: "0X1915:0XAE12:PATH:WIRED-PATH",
                    historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver) },
                new[] { receiver.DeviceId, wired.DeviceId }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(2, update.CurrentReadings.Count,
            "a pairing mismatch leaves the receiver and wired mouse as independent present devices");
        Equal(0, update.OfflineReadings.Count,
            "a receiver battery read failure during pair rejection must not mark the present receiver offline");
        Equal(1, update.CurrentReadings.Single(reading => reading.DeviceId == receiver.DeviceId).ConsecutiveFailures,
            "the unread but present receiver should become a failed sample, not disappear");
    }

    private static void StateSoraPairRejectionAdvancesReceiverSelfEpoch()
    {
        var receiverOne = new NinjutsoSoraEndpoint(
            "receiver-path",
            0xAE1C,
            "Ninjutso Sora V2 Receiver",
            "RECEIVER-ONE");
        var receiverTwo = receiverOne with { DeviceSerial = "RECEIVER-TWO" };
        var wiredTwo = new NinjutsoSoraEndpoint(
            "wired-path",
            0xAE12,
            "Ninjutso Sora V2",
            "RECEIVER-TWO");
        var receiverOnePlan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiverOne, wiredTwo with { DeviceSerial = "RECEIVER-ONE" } },
            new Dictionary<string, int?> { [receiverOne.DeviceId] = 0xAE12 });
        var receiverOneObservation = NinjutsoSoraOfficialProvider.CreateIdentityObservations(receiverOnePlan).Single();
        var mismatchPlan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiverTwo, wiredTwo },
            new Dictionary<string, int?> { [receiverTwo.DeviceId] = 0xAE11 });
        var replacementObservations = NinjutsoSoraOfficialProvider.CreateIdentityObservations(mismatchPlan);
        var receiverTwoObservation = replacementObservations.Single(observation =>
            observation.DeviceId == receiverTwo.DeviceId);
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredTwo.DeviceId,
                    receiverOneObservation.LogicalDeviceId,
                    70,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { receiverOne.DeviceId, wiredTwo.DeviceId },
                    historyDeviceKey: receiverOneObservation.HistoryDeviceKey,
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: receiverOneObservation.AssociationReceiverHistoryKey,
                    associationReceiverSerial: receiverOne.DeviceSerial) },
                new[] { receiverOne.DeviceId, wiredTwo.DeviceId }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        store.ObserveIdentityEvidence(replacementObservations);
        var receiverOnly = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    receiverTwo.DeviceId,
                    receiverTwoObservation.LogicalDeviceId,
                    80,
                    DeviceConnectionTransport.Receiver,
                    externallyPowered: false,
                    new[] { receiverTwo.DeviceId },
                    historyDeviceKey: receiverOneObservation.AssociationReceiverHistoryKey,
                    deviceSerial: "000000000000",
                    associationReceiverHistoryKey: receiverTwoObservation.AssociationReceiverHistoryKey) },
                new[] { receiverTwo.DeviceId }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(receiverTwoObservation.HistoryDeviceKey, receiverOnly.CurrentReadings.Single().HistoryDeviceKey,
            "a receiver self observation must prevent serial degradation from reviving the prior receiver curve");
        Equal("RECEIVER-TWO", receiverOnly.CurrentReadings.Single().AssociationReceiverSerial,
            "receiver-only fallback must retain the replacement receiver epoch");
        True(receiverOnly.Rekeys.Any(rekey => !rekey.PreservePolicyState),
            "the replacement receiver must reset old runtime policy state even when its live serial is unavailable");
    }

    private static void StateSoraReceiverEpochResetsRuntimePolicyIdentity()
    {
        const string receiverPath = "receiver-path";
        const string wiredPath = "wired-path";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:receiver-path";
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        var store = new DeviceBatteryStateStore();
        store.Apply(
            StateResult(
                new[]
                {
                    SoraStateReading(
                        wiredPath,
                        receiverLogicalId,
                        75,
                        DeviceConnectionTransport.WiredUsb,
                        externallyPowered: true,
                        new[] { receiverPath, wiredPath },
                        historyDeviceKey: "0X1915:0XAE1C:SERIAL:RECEIVER-ONE",
                        historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                        associationReceiverHistoryKey: associationKey,
                        associationReceiverSerial: "RECEIVER-ONE"),
                    SoraStateReading(
                        wiredPath,
                        "ninjutso-sora-v2:wired:wired-path",
                        74,
                        DeviceConnectionTransport.WiredUsb,
                        externallyPowered: true,
                        new[] { wiredPath },
                        historyDeviceKey: "0X1915:0XAE12:PATH:WIRED-PATH")
                },
                new[] { receiverPath, wiredPath }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var replacement = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    wiredPath,
                    receiverLogicalId,
                    80,
                    DeviceConnectionTransport.WiredUsb,
                    externallyPowered: true,
                    new[] { receiverPath, wiredPath },
                    historyDeviceKey: "0X1915:0XAE1C:SERIAL:RECEIVER-TWO",
                    historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                    associationReceiverHistoryKey: associationKey,
                    associationReceiverSerial: "RECEIVER-TWO") },
                new[] { receiverPath, wiredPath }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10));

        Equal(2, replacement.Rekeys.Count,
            "replacement must account for both the same-key old epoch and the independent wired alias");
        True(replacement.Rekeys.All(rekey => !rekey.PreservePolicyState),
            "no alias may carry alert or polling state into a different stable receiver epoch");
        var epochRekey = replacement.Rekeys.Single(rekey =>
            DeviceIdentity.NormalizeSerial(rekey.PreviousReading.AssociationReceiverSerial) == "RECEIVER-ONE");
        NotEqual(DeviceIdentity.CreateRuntimeKey(epochRekey.PreviousReading),
            DeviceIdentity.CreateRuntimeKey(epochRekey.CurrentReading),
            "receiver serial epochs must have distinct runtime policy identities");
    }

    private static void StateSoraReceiverFallbackPreservesLogEpochToken()
    {
        const string receiverPath = "receiver-path";
        const string wiredPath = "wired-path";
        const string logicalDeviceId = "ninjutso-sora-v2:receiver:receiver-path";
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER-PATH";
        const string serialKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var store = new DeviceBatteryStateStore();
        var paired = SoraStateReading(
            wiredPath,
            logicalDeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: serialKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: associationKey,
            associationReceiverSerial: "RECEIVER-ONE");
        store.Apply(
            StateResult(new[] { paired }, new[] { receiverPath, wiredPath }),
            StartUtc,
            TimeSpan.FromMinutes(10));

        var receiverOnly = store.Apply(
            StateResult(
                new[] { SoraStateReading(
                    receiverPath,
                    logicalDeviceId,
                    74,
                    DeviceConnectionTransport.Receiver,
                    externallyPowered: false,
                    new[] { receiverPath },
                    historyDeviceKey: serialKey,
                    deviceSerial: "RECEIVER-ONE") },
                new[] { receiverPath }),
            StartUtc.AddMinutes(1),
            TimeSpan.FromMinutes(10)).CurrentReadings.Single();

        Equal(associationKey, receiverOnly.AssociationReceiverHistoryKey,
            "same-logical receiver fallback must retain the receiver association");
        Equal("RECEIVER-ONE", receiverOnly.AssociationReceiverSerial,
            "same-logical receiver fallback must retain the receiver serial epoch");
        Equal(recorder.DeviceToken(paired), recorder.DeviceToken(receiverOnly),
            "one physical receiver epoch must keep a stable flight-recorder token across transports");
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

    private static void HistorySoraTransportsShareLogicalKey()
    {
        const string logicalDeviceId = "ninjutso-sora-v2:receiver:receiver-path";
        var legacyReceiver = SoraStateReading(
            "receiver-path",
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { "receiver-path" });
        var legacyReceiverKey = CreateHistoryDeviceKey(legacyReceiver);
        var receiver = SoraStateReading(
            "receiver-path",
            logicalDeviceId,
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { "receiver-path" },
            historyDeviceKey: legacyReceiverKey);
        var wired = SoraStateReading(
            "wired-path",
            logicalDeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { "receiver-path", "wired-path" },
            historyDeviceKey: legacyReceiverKey);

        var receiverKey = CreateHistoryDeviceKey(receiver);
        var wiredKey = CreateHistoryDeviceKey(wired);
        Equal(receiverKey, wiredKey, "receiver and wired samples must share one history key");
        Equal(legacyReceiverKey, receiverKey, "logical identity must retain the pre-upgrade receiver history key");
        True(receiverKey.Contains(":0XAE1C:PATH:", StringComparison.Ordinal), "receiver history key shape");

        var wiredOnly = SoraStateReading(
            "wired-path",
            "ninjutso-sora-v2:wired:wired-path",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { "wired-path" });
        var legacyWired = SoraStateReading(
            "wired-path",
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { "wired-path" });
        Equal(CreateHistoryDeviceKey(legacyWired), CreateHistoryDeviceKey(wiredOnly),
            "wired-only logical identity must retain the pre-upgrade wired key");
    }

    private static void HistorySoraLegacyTransportKeysMigrate()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string logicalDeviceId = "ninjutso-sora-v2:receiver:" + receiverPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        var legacyReceiver = SoraStateReading(
            receiverPath,
            "",
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath });
        var legacyWired = SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath });
        var legacyReceiverKey = CreateHistoryDeviceKey(legacyReceiver);
        var legacyWiredKey = CreateHistoryDeviceKey(legacyWired);
        var legacyStore = new BatteryHistoryStore(paths);
        legacyStore.Append(legacyReceiver);
        legacyStore.Append(legacyWired);

        var currentStore = new BatteryHistoryStore(paths);
        currentStore.Append(SoraStateReading(
            receiverPath,
            logicalDeviceId,
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            historyDeviceKey: legacyReceiverKey));
        True(File.ReadLines(paths.HistoryPath)
                .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line))
                .Any(entry => string.Equals(entry?.DeviceKey, legacyWiredKey, StringComparison.OrdinalIgnoreCase)),
            "receiver-only startup must leave an unseen wired alias available for later migration");

        var paired = SoraStateReading(
            wiredPath,
            logicalDeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: legacyReceiverKey,
            deviceSerial: "REAL-WIRED-SERIAL");
        var canonicalKey = CreateHistoryDeviceKey(paired);
        currentStore.Append(paired);

        var entries = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("history migration entry was not readable"))
            .ToArray();
        Equal(legacyReceiverKey, canonicalKey, "receiver key should remain canonical after upgrade");
        NotEqual(legacyWiredKey, canonicalKey, "test setup must start with two old transport keys");
        True(entries.All(entry => string.Equals(entry.DeviceKey, canonicalKey, StringComparison.OrdinalIgnoreCase)),
            "legacy receiver and wired samples should converge to the receiver key");
        True(entries.Any(entry => entry.ProductId == "0XAE12"), "migrated wired samples should remain available");
        Equal("", entries.Single(entry => entry.ProductId == "0XAE12" && string.IsNullOrWhiteSpace(entry.LogicalDeviceId)).DeviceSerial,
            "migration must not rewrite the original serial fact on an old sample");
    }

    private static void HistorySoraAsymmetricSerialsKeepReceiverAnchor()
    {
        var receiver = new NinjutsoSoraEndpoint("receiver-path", 0xAE1C, "Ninjutso Sora V2", "REAL-RECEIVER-SERIAL");
        var wired = new NinjutsoSoraEndpoint("wired-path", 0xAE12, "Ninjutso Sora V2", "000000000000");
        var plan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiver, wired },
            new Dictionary<string, int?> { [receiver.DeviceId] = wired.ProductId });
        Equal(receiver.DeviceId, plan.SelectedEndpoints.Single().HistoryEndpoint.DeviceId,
            "paired history identity must be sourced from the receiver endpoint");

        var receiverHistoryKey = CreateHistoryDeviceKey(new BatteryReading
        {
            DeviceId = receiver.DeviceId,
            DeviceSerial = receiver.DeviceSerial,
            DeviceName = receiver.DeviceName,
            VendorId = "0x1915",
            ProductId = "0xAE1C",
            Source = "SORA V2 Official HID"
        });
        var wireless = SoraStateReading(
            receiver.DeviceId,
            "ninjutso-sora-v2:receiver:" + receiver.DeviceId,
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiver.DeviceId },
            historyDeviceKey: receiverHistoryKey);
        var pairedWired = SoraStateReading(
            wired.DeviceId,
            "ninjutso-sora-v2:receiver:" + receiver.DeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiver.DeviceId, wired.DeviceId },
            historyDeviceKey: receiverHistoryKey);
        Equal(CreateHistoryDeviceKey(wireless), CreateHistoryDeviceKey(pairedWired),
            "wired placeholder serial must not replace the receiver's stable history serial");
        True(receiverHistoryKey.Contains(":SERIAL:REAL-RECEIVER-SERIAL", StringComparison.Ordinal),
            "test setup should use the receiver serial key");
    }

    private static void HistorySoraWiredRestartRecoversReceiverKey()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        var receiverAnchor = SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath });
        var receiverKey = CreateHistoryDeviceKey(receiverAnchor);
        var wiredAnchor = SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath });
        var wiredKey = CreateHistoryDeviceKey(wiredAnchor);

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverKey,
            associationReceiverHistoryKey: receiverKey));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            receiverPath,
            receiverLogicalId,
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            historyDeviceKey: receiverKey));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            76,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            77,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));

        var entries = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("persisted history identity entry was not readable"))
            .ToArray();
        Equal(1, entries.Select(entry => entry.DeviceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "wired-only cold starts should reuse the persisted receiver key");
        True(entries.All(entry => string.Equals(entry.DeviceKey, receiverKey, StringComparison.OrdinalIgnoreCase)),
            "persisted resolved endpoint mapping should keep the receiver curve continuous");
        Equal(77, entries[^1].BatteryPercentage, "second wired-only cold start should also recover the mapping");
    }

    private static void HistorySoraPairingMismatchInvalidatesReceiverRecovery()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        var receiverKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath }));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverKey,
            associationReceiverHistoryKey: receiverKey));

        var mismatchStore = new BatteryHistoryStore(paths);
        mismatchStore.RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            wiredLogicalId,
            wiredKey,
            HistoryAnchorEvidence.RejectPersistedReceiver,
            new[] { wiredPath },
            receiverKey));

        var afterMismatch = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("history mismatch entry was not readable"))
            .ToArray();
        Equal(wiredKey, afterMismatch[^1].DeviceKey,
            "explicit pairing mismatch must keep the wired endpoint independent");
        False(afterMismatch[^1].HasBatteryPercentage,
            "plan-level mismatch evidence must not invent a battery sample when the wired read failed");
        Equal(0, mismatchStore.ReadLast(TimeSpan.FromDays(30), wiredKey).Count,
            "identity-only evidence must not appear as a zero-percent point in battery history");
        Equal(2, afterMismatch.Select(entry => entry.DeviceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "explicit mismatch must not migrate the independent wired curve into the receiver curve");

        mismatchStore.Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            76,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));

        mismatchStore.MarkOffline(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverKey));
        var afterOffline = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("history offline entry was not readable"))
            .ToArray();
        Equal(wiredKey, afterOffline.Single(entry => entry.HasBatteryPercentage && entry.BatteryPercentage == 76).DeviceKey,
            "offline bookkeeping must not migrate a successful independent wired sample into the rejected receiver curve");

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            77,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var afterRestart = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("history restart entry was not readable"))
            .ToArray();
        Equal(wiredKey, afterRestart[^1].DeviceKey,
            "a persisted mismatch tombstone must survive stale combined-state offline records and prevent wired-only recovery");

        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverKey));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            79,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var afterVerifiedPair = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("history repaired pairing entry was not readable"))
            .ToArray();
        Equal(receiverKey, afterVerifiedPair[^1].DeviceKey,
            "new plan-level verified pairing evidence must supersede the older mismatch tombstone even when its battery read fails");
    }

    private static void HistorySoraUnrelatedReceiverMismatchPreservesValidAnchor()
    {
        const string receiverOnePath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER-ONE";
        const string receiverTwoPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER-TWO";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverOneLogicalId = "ninjutso-sora-v2:receiver:" + receiverOnePath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        var receiverOneKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverOnePath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverOnePath }));
        var receiverTwoAssociationKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverTwoPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverTwoPath }));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            receiverOneLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverOnePath, wiredPath },
            historyDeviceKey: receiverOneKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverOneKey));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            wiredLogicalId,
            wiredKey,
            HistoryAnchorEvidence.RejectPersistedReceiver,
            new[] { wiredPath },
            receiverTwoAssociationKey));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            76,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));

        var lastBattery = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("scoped receiver history entry was not readable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(receiverOneKey, lastBattery.DeviceKey,
            "receiver-two mismatch must not invalidate receiver-one's confirmed wired relation");
        Equal(receiverOneKey, lastBattery.AssociationReceiverHistoryKey,
            "recovered battery sample must retain receiver-one's association scope");
    }

    private static void HistorySoraReceiverAssociationSurvivesKeyFormChanges()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        var receiverPathKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath }));
        var receiverSerialKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "REAL-RECEIVER-SERIAL"));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));
        NotEqual(receiverPathKey, receiverSerialKey, "test setup requires path and serial curve keys");

        void AssertMismatchBlocks(string initialReceiverKey, string label)
        {
            using var temporary = new TemporaryDirectory();
            var paths = new AppPaths(temporary.Path);
            paths.Ensure();
            new BatteryHistoryStore(paths).Append(SoraStateReading(
                wiredPath,
                receiverLogicalId,
                75,
                DeviceConnectionTransport.WiredUsb,
                externallyPowered: true,
                new[] { receiverPath, wiredPath },
                historyDeviceKey: initialReceiverKey,
                historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
                associationReceiverHistoryKey: receiverPathKey,
                associationReceiverSerial: string.Equals(initialReceiverKey, receiverSerialKey, StringComparison.OrdinalIgnoreCase)
                    ? "REAL-RECEIVER-SERIAL"
                    : ""));
            new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
                wiredPath,
                wiredLogicalId,
                wiredKey,
                HistoryAnchorEvidence.RejectPersistedReceiver,
                new[] { wiredPath },
                receiverPathKey,
                string.Equals(initialReceiverKey, receiverSerialKey, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : "REAL-RECEIVER-SERIAL"));
            new BatteryHistoryStore(paths).Append(SoraStateReading(
                wiredPath,
                wiredLogicalId,
                76,
                DeviceConnectionTransport.WiredUsb,
                externallyPowered: true,
                new[] { wiredPath },
                historyDeviceKey: wiredKey,
                historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
            var last = File.ReadLines(paths.HistoryPath)
                .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                    ?? throw new InvalidOperationException("receiver key-form mismatch entry was not readable"))
                .Last(entry => entry.HasBatteryPercentage);
            Equal(wiredKey, last.DeviceKey, label);
        }

        AssertMismatchBlocks(receiverSerialKey,
            "serial-confirmed relation must be rejected by the same receiver's path-scoped mismatch");
        AssertMismatchBlocks(receiverPathKey,
            "path-confirmed relation must be rejected after the same receiver later exposes a serial");

        using var promotionTemporary = new TemporaryDirectory();
        var promotionPaths = new AppPaths(promotionTemporary.Path);
        promotionPaths.Ensure();
        new BatteryHistoryStore(promotionPaths).Append(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverPathKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverPathKey));
        new BatteryHistoryStore(promotionPaths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverSerialKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverPathKey,
            "REAL-RECEIVER-SERIAL"));
        new BatteryHistoryStore(promotionPaths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            76,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var promoted = File.ReadLines(promotionPaths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("receiver serial promotion entry was not readable"))
            .Where(entry => entry.HasBatteryPercentage)
            .ToArray();
        True(promoted.All(entry => string.Equals(entry.DeviceKey, receiverSerialKey, StringComparison.OrdinalIgnoreCase)),
            "plan-level serial promotion must migrate the existing curve even without a battery read");

        new BatteryHistoryStore(promotionPaths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverPathKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverPathKey));
        new BatteryHistoryStore(promotionPaths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            77,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var afterSerialLoss = File.ReadLines(promotionPaths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("receiver serial-loss entry was not readable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(receiverSerialKey, afterSerialLoss.DeviceKey,
            "a later placeholder serial must not demote the canonical receiver curve key");

        using var identityOnlyTemporary = new TemporaryDirectory();
        var identityOnlyPaths = new AppPaths(identityOnlyTemporary.Path);
        identityOnlyPaths.Ensure();
        new BatteryHistoryStore(identityOnlyPaths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverPathKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverPathKey));
        new BatteryHistoryStore(identityOnlyPaths).Append(SoraStateReading(
            receiverPath,
            receiverLogicalId,
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            historyDeviceKey: receiverSerialKey,
            deviceSerial: "REAL-RECEIVER-SERIAL"));
        new BatteryHistoryStore(identityOnlyPaths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var promotedAfterReceiverOnlyRead = File.ReadLines(identityOnlyPaths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("receiver-only serial promotion entry was not readable"))
            .Where(entry => entry.HasBatteryPercentage)
            .ToArray();
        True(promotedAfterReceiverOnlyRead.All(entry => string.Equals(
                entry.DeviceKey,
                receiverSerialKey,
                StringComparison.OrdinalIgnoreCase)),
            "receiver-only serial recovery must promote an earlier identity-only pair before wired fallback");
    }

    private static void HistorySoraReusedReceiverPathSeparatesSerialEpochs()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        var receiverAssociationKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath }));
        var receiverOneKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "RECEIVER-ONE"));
        var receiverTwoKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            80,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "RECEIVER-TWO"));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverOneKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverAssociationKey,
            associationReceiverSerial: "RECEIVER-ONE"));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            wiredLogicalId,
            wiredKey,
            HistoryAnchorEvidence.RejectPersistedReceiver,
            new[] { wiredPath },
            receiverAssociationKey,
            "RECEIVER-ONE"));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverTwoKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverAssociationKey,
            "RECEIVER-TWO"));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            80,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverTwoKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverAssociationKey,
            associationReceiverSerial: "RECEIVER-TWO"));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            81,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));

        var batteries = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("receiver serial-epoch entry was not readable"))
            .Where(entry => entry.HasBatteryPercentage)
            .ToArray();
        Equal(receiverOneKey, batteries.Single(entry => entry.BatteryPercentage == 75).DeviceKey,
            "receiver-one history must not be rewritten when the HID path is reused");
        True(batteries.Where(entry => entry.BatteryPercentage >= 80)
                .All(entry => string.Equals(entry.DeviceKey, receiverTwoKey, StringComparison.OrdinalIgnoreCase)),
            "receiver-two and its wired-only continuation must use a separate serial epoch");

        using var recurrenceTemporary = new TemporaryDirectory();
        var recurrencePaths = new AppPaths(recurrenceTemporary.Path);
        recurrencePaths.Ensure();
        new BatteryHistoryStore(recurrencePaths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverOneKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverAssociationKey,
            "RECEIVER-ONE",
            StartUtc));
        new BatteryHistoryStore(recurrencePaths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverTwoKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverAssociationKey,
            "RECEIVER-TWO",
            StartUtc.AddMinutes(1)));
        new BatteryHistoryStore(recurrencePaths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverOneKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            receiverAssociationKey,
            "RECEIVER-ONE",
            StartUtc.AddMinutes(2)));
        new BatteryHistoryStore(recurrencePaths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            82,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var recurrenceBattery = File.ReadLines(recurrencePaths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("receiver recurrence entry was not readable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(receiverOneKey, recurrenceBattery.DeviceKey,
            "R1 to R2 to R1 must refresh R1 as the active serial epoch for wired-only recovery");
    }

    private static void HistorySoraRejectedReplacementEpochBlocksColdRecovery()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        const string associationKey = "0X1915:0XAE1C:PATH:RECEIVER";
        const string receiverOneKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string wiredKey = "0X1915:0XAE12:PATH:WIRED";
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverOneKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            associationKey,
            "RECEIVER-ONE",
            StartUtc));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            wiredLogicalId,
            wiredKey,
            HistoryAnchorEvidence.RejectPersistedReceiver,
            new[] { receiverPath, wiredPath },
            associationKey,
            "RECEIVER-TWO",
            StartUtc.AddMinutes(1)));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            80,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));

        var battery = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("replacement rejection history was unreadable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(wiredKey, battery.DeviceKey,
            "a newer rejected serial epoch on the same receiver association must block old cold-start recovery");
    }

    private static void HistorySoraReceiverSerialDegradationSurvivesPairRejection()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        var associationKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath }));
        var receiverSerialKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "RECEIVER-ONE"));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));

        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            receiverLogicalId,
            receiverSerialKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverPath, wiredPath },
            associationKey,
            "RECEIVER-ONE",
            StartUtc));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            wiredLogicalId,
            wiredKey,
            HistoryAnchorEvidence.RejectPersistedReceiver,
            new[] { receiverPath, wiredPath },
            associationKey,
            "",
            StartUtc.AddMinutes(1)));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            receiverPath,
            receiverLogicalId,
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            historyDeviceKey: associationKey,
            deviceSerial: "000000000000",
            associationReceiverHistoryKey: associationKey));

        var battery = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("receiver degradation history was unreadable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(receiverSerialKey, battery.DeviceKey,
            "rejecting the receiver-to-wired pair must not split the receiver's own serial-degraded curve");
    }

    private static void HistorySoraPairRejectionPersistsReplacementReceiverEpoch()
    {
        var receiverOne = new NinjutsoSoraEndpoint(
            @"\\?\hid#vid_1915&pid_ae1c#RECEIVER",
            0xAE1C,
            "Ninjutso Sora V2 Receiver",
            "RECEIVER-ONE");
        var receiverTwo = receiverOne with { DeviceSerial = "RECEIVER-TWO" };
        var wired = new NinjutsoSoraEndpoint(
            @"\\?\hid#vid_1915&pid_ae12#WIRED",
            0xAE12,
            "Ninjutso Sora V2",
            "RECEIVER-TWO");
        var firstPlan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiverOne, wired with { DeviceSerial = "RECEIVER-ONE" } },
            new Dictionary<string, int?> { [receiverOne.DeviceId] = 0xAE12 });
        var firstObservation = NinjutsoSoraOfficialProvider.CreateIdentityObservations(firstPlan).Single();
        var mismatchPlan = NinjutsoSoraTransportPolicy.CreatePlan(
            new[] { receiverTwo, wired },
            new Dictionary<string, int?> { [receiverTwo.DeviceId] = 0xAE11 });
        var replacementObservations = NinjutsoSoraOfficialProvider.CreateIdentityObservations(mismatchPlan);
        var receiverTwoObservation = replacementObservations.Single(observation =>
            observation.DeviceId == receiverTwo.DeviceId);
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        var store = new BatteryHistoryStore(paths);
        store.RecordIdentityEvidence(firstObservation);
        foreach (var observation in replacementObservations)
            store.RecordIdentityEvidence(observation);

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            receiverTwo.DeviceId,
            receiverTwoObservation.LogicalDeviceId,
            80,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverTwo.DeviceId },
            historyDeviceKey: firstObservation.AssociationReceiverHistoryKey,
            deviceSerial: "000000000000",
            associationReceiverHistoryKey: receiverTwoObservation.AssociationReceiverHistoryKey));

        var battery = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("replacement receiver self history was unreadable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(receiverTwoObservation.HistoryDeviceKey, battery.DeviceKey,
            "the receiver self observation must preserve the replacement epoch across restart and serial loss");
    }

    private static void HistorySoraAssociationRecurrenceRefreshesActiveRelation()
    {
        const string receiverOnePath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER-ONE";
        const string receiverTwoPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER-TWO";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        const string receiverOneKey = "0X1915:0XAE1C:SERIAL:RECEIVER-ONE";
        const string receiverTwoKey = "0X1915:0XAE1C:SERIAL:RECEIVER-TWO";
        const string receiverOneAssociation = "0X1915:0XAE1C:PATH:ASSOCIATION-ONE";
        const string receiverTwoAssociation = "0X1915:0XAE1C:PATH:ASSOCIATION-TWO";
        const string wiredKey = "0X1915:0XAE12:PATH:WIRED";
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            "ninjutso-sora-v2:receiver:" + receiverOnePath,
            receiverOneKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverOnePath, wiredPath },
            receiverOneAssociation,
            "RECEIVER-ONE",
            StartUtc));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            "ninjutso-sora-v2:receiver:" + receiverTwoPath,
            receiverTwoKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverTwoPath, wiredPath },
            receiverTwoAssociation,
            "RECEIVER-TWO",
            StartUtc.AddMinutes(1)));
        new BatteryHistoryStore(paths).RecordIdentityEvidence(SoraIdentityObservation(
            wiredPath,
            "ninjutso-sora-v2:receiver:" + receiverOnePath,
            receiverOneKey,
            HistoryAnchorEvidence.ConfirmReceiver,
            new[] { receiverOnePath, wiredPath },
            receiverOneAssociation,
            "RECEIVER-ONE",
            StartUtc.AddMinutes(2)));
        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            82,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));

        var entries = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("association recurrence history was unreadable"))
            .ToArray();
        Equal(3, entries.Count(entry => !entry.HasBatteryPercentage),
            "R1 to R2 to R1 must persist the third identity-only activation");
        Equal(receiverOneKey, entries.Last(entry => entry.HasBatteryPercentage).DeviceKey,
            "wired-only recovery must follow the most recently reactivated receiver association");
    }

    private static void HistorySoraPruneRetainsIdentityRelations()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();
        var receiverAssociationKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            70,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath }));
        var receiverSerialKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            70,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "REAL-RECEIVER-SERIAL"));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            70,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));
        var nowUtc = DateTime.UtcNow;
        var oldPair = new BatteryHistoryEntry
        {
            TimestampUtc = nowUtc.AddDays(-40),
            DeviceKey = receiverSerialKey,
            DeviceName = "Ninjutso Sora V2",
            LogicalDeviceId = receiverLogicalId,
            AssociationReceiverHistoryKey = receiverAssociationKey,
            AssociationReceiverSerial = "REAL-RECEIVER-SERIAL",
            ReceiverCanonicalHistoryKey = receiverSerialKey,
            ConnectionTransport = DeviceConnectionTransport.WiredUsb.ToString(),
            HistoryAnchorEvidence = HistoryAnchorEvidence.ConfirmReceiver.ToString(),
            ResolvedDeviceIds = new[] { receiverPath, wiredPath },
            VendorId = "0X1915",
            ProductId = "0XAE12",
            HasBatteryPercentage = true,
            BatteryPercentage = 70,
            IsCharging = true,
            IsCableConnected = true,
            State = "sample",
            Source = "SORA V2 Official HID"
        };
        var recentReceiver = new BatteryHistoryEntry
        {
            TimestampUtc = nowUtc.AddDays(-1),
            DeviceKey = receiverSerialKey,
            DeviceName = "Ninjutso Sora V2",
            LogicalDeviceId = receiverLogicalId,
            AssociationReceiverHistoryKey = receiverAssociationKey,
            ReceiverCanonicalHistoryKey = receiverSerialKey,
            ConnectionTransport = DeviceConnectionTransport.Receiver.ToString(),
            HistoryAnchorEvidence = HistoryAnchorEvidence.None.ToString(),
            ResolvedDeviceIds = new[] { receiverPath },
            VendorId = "0X1915",
            ProductId = "0XAE1C",
            HasBatteryPercentage = true,
            BatteryPercentage = 69,
            State = "sample",
            Source = "SORA V2 Official HID"
        };
        File.WriteAllLines(paths.HistoryPath, new[]
        {
            JsonSerializer.Serialize(oldPair),
            JsonSerializer.Serialize(recentReceiver)
        });

        _ = new BatteryHistoryStore(paths).ReadDevices(TimeSpan.FromDays(30));
        var pruned = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("pruned identity entry was not readable"))
            .ToArray();
        False(pruned.Any(entry => entry.HasBatteryPercentage && entry.BatteryPercentage == 70),
            "battery samples older than the retention window must still be pruned");
        True(pruned.Any(entry => !entry.HasBatteryPercentage
                && entry.State == "identity_anchor_retained"
                && entry.ResolvedDeviceIds.SequenceEqual(new[] { wiredPath })
                && string.Equals(entry.ReceiverCanonicalHistoryKey, receiverSerialKey, StringComparison.OrdinalIgnoreCase)),
            "pruning must project the latest wired identity relation without retaining its old battery fact");
        Equal("REAL-RECEIVER-SERIAL",
            pruned.Single(entry => entry.HasBatteryPercentage && entry.BatteryPercentage == 69).AssociationReceiverSerial,
            "pruning must materialize an inherited receiver serial on a retained receiver-only row");

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            71,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var recovered = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("post-prune recovery entry was not readable"))
            .Last(entry => entry.HasBatteryPercentage);
        Equal(receiverSerialKey, recovered.DeviceKey,
            "wired-only recovery must retain the receiver curve after old battery samples are pruned");
    }

    private static void HistorySoraVerifiedPairSupersedesEqualSampleTombstone()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string receiverLogicalId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string wiredLogicalId = "ninjutso-sora-v2:wired:" + wiredPath;
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        paths.Ensure();

        var receiverKey = CreateHistoryDeviceKey(SoraStateReading(
            receiverPath,
            "",
            75,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath }));
        var wiredKey = CreateHistoryDeviceKey(SoraStateReading(
            wiredPath,
            "",
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath }));
        var baseline = new BatteryHistoryEntry
        {
            TimestampUtc = StartUtc,
            DeviceKey = receiverKey,
            DeviceName = "Ninjutso Sora V2",
            LogicalDeviceId = receiverLogicalId,
            ConnectionTransport = DeviceConnectionTransport.WiredUsb.ToString(),
            HistoryAnchorEvidence = HistoryAnchorEvidence.None.ToString(),
            ResolvedDeviceIds = new[] { receiverPath, wiredPath },
            VendorId = "0X1915",
            ProductId = "0XAE12",
            BatteryPercentage = 75,
            IsCharging = true,
            IsCableConnected = true,
            State = "sample",
            Source = "SORA V2 Official HID"
        };
        var tombstone = new BatteryHistoryEntry
        {
            TimestampUtc = StartUtc.AddMinutes(1),
            DeviceKey = "0X1915:0XAE12:PATH:NONALIAS-TOMBSTONE",
            DeviceName = "Ninjutso Sora V2",
            LogicalDeviceId = wiredLogicalId,
            ConnectionTransport = DeviceConnectionTransport.WiredUsb.ToString(),
            HistoryAnchorEvidence = HistoryAnchorEvidence.RejectPersistedReceiver.ToString(),
            AssociationReceiverHistoryKey = receiverKey,
            ResolvedDeviceIds = new[] { wiredPath },
            VendorId = "0X1915",
            ProductId = "0XAE12",
            BatteryPercentage = 75,
            IsCharging = true,
            IsCableConnected = true,
            State = "transport_change",
            Source = "SORA V2 Official HID"
        };
        File.WriteAllLines(paths.HistoryPath, new[]
        {
            JsonSerializer.Serialize(baseline),
            JsonSerializer.Serialize(tombstone)
        });

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            receiverLogicalId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: receiverKey,
            associationReceiverHistoryKey: receiverKey));
        var repaired = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("equal-sample repaired entry was not readable"))
            .ToArray();
        Equal(3, repaired.Length,
            "verified pairing must be persisted even when its battery and transport equal the old receiver sample");
        Equal(receiverKey, repaired[^1].DeviceKey, "verified pairing should restore the receiver anchor");
        Equal("transport_change", repaired[^1].State, "equal-sample anchor restoration event");

        new BatteryHistoryStore(paths).Append(SoraStateReading(
            wiredPath,
            wiredLogicalId,
            76,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { wiredPath },
            historyDeviceKey: wiredKey,
            historyAnchorEvidence: HistoryAnchorEvidence.RecoverPersistedReceiver));
        var recovered = File.ReadLines(paths.HistoryPath)
            .Select(line => JsonSerializer.Deserialize<BatteryHistoryEntry>(line)
                ?? throw new InvalidOperationException("equal-sample recovery entry was not readable"))
            .ToArray();
        Equal(receiverKey, recovered[^1].DeviceKey,
            "a later cold-start wired sample must recover the newly verified receiver anchor");
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

    private static void FlightRecorderBatteryFactValidityIsExplicit()
    {
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var contextOnly = new BatteryReading
        {
            HasBatteryPercentage = false,
            BatteryPercentage = 0,
            IsOnline = true,
            ExternalPowerConnected = null,
            DeviceId = "identity-only-path",
            DeviceName = "Ninjutso Sora V2",
            VendorId = "0x1915",
            ProductId = "0xAE12",
            Source = "selftest"
        };

        recorder.Write("warn", "selftest.identity_only", nameof(Program), "degraded", reading: contextOnly);
        recorder.Write(
            "warn",
            "selftest.read_failure_without_battery",
            nameof(Program),
            "failure",
            exception: new IOException("selftest read failure"),
            reading: contextOnly);
        True(recorder.Flush(TimeSpan.FromSeconds(5)), "identity-only event should flush");
        var liveEntries = ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl"));
        var liveDevice = FindEvent(
            liveEntries,
            "selftest.identity_only").GetProperty("device");
        False(liveDevice.GetProperty("has_battery_percentage").GetBoolean(),
            "zero must be explicitly identified as no battery fact");
        True(liveDevice.GetProperty("is_online").GetBoolean(), "online identity context should remain explicit");
        Equal(JsonValueKind.Null, liveDevice.GetProperty("external_power_connected").ValueKind,
            "unknown external-power state must remain unknown rather than false");
        Equal(JsonValueKind.Null, liveDevice.GetProperty("last_successful_read_utc").ValueKind,
            "identity-only and failed reads must not invent a successful battery-read timestamp");
        var liveFailureDevice = FindEvent(liveEntries, "selftest.read_failure_without_battery")
            .GetProperty("device");
        Equal(JsonValueKind.Null, liveFailureDevice.GetProperty("last_successful_read_utc").ValueKind,
            "a failed read without a battery fact must not invent a successful-read timestamp");

        var snapshotPath = Path.Combine(temporary.Path, "identity-only-snapshot.jsonl");
        True(recorder.SnapshotRecentLogs(snapshotPath, 1024 * 1024) > 0,
            "identity-only snapshot should be created");
        var projectedEntries = ReadJsonLinesShared(snapshotPath);
        var projectedDevice = FindEvent(projectedEntries, "selftest.identity_only")
            .GetProperty("device");
        False(projectedDevice.GetProperty("has_battery_percentage").GetBoolean(),
            "snapshot projection must preserve battery-fact validity");
        Equal(JsonValueKind.Null, projectedDevice.GetProperty("external_power_connected").ValueKind,
            "snapshot projection must preserve unknown external power");
        Equal(JsonValueKind.Null, projectedDevice.GetProperty("last_successful_read_utc").ValueKind,
            "snapshot projection must preserve the absence of a successful battery-read timestamp");
        var projectedFailureDevice = FindEvent(projectedEntries, "selftest.read_failure_without_battery")
            .GetProperty("device");
        Equal(JsonValueKind.Null, projectedFailureDevice.GetProperty("last_successful_read_utc").ValueKind,
            "snapshot projection must preserve a failed read's missing successful-read timestamp");
    }

    private static void FlightRecorderRetainsLogicalTransportIdentity()
    {
        const string receiverPath = @"\\?\hid#vid_1915&pid_ae1c#RECEIVER";
        const string wiredPath = @"\\?\hid#vid_1915&pid_ae12#WIRED";
        const string logicalDeviceId = "ninjutso-sora-v2:receiver:" + receiverPath;
        const string historyDeviceKey = "0X1915:0XAE1C:PATH:TEST-HISTORY-KEY";
        const string receiverAssociationKey = "0X1915:0XAE1C:PATH:TEST-ASSOCIATION-KEY";
        using var temporary = new TemporaryDirectory();
        using var recorder = new FlightRecorder(temporary.Path);
        var reading = SoraStateReading(
            wiredPath,
            logicalDeviceId,
            75,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: historyDeviceKey,
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverAssociationKey,
            associationReceiverSerial: "REAL-RECEIVER-SERIAL");
        var receiverTransport = SoraStateReading(
            receiverPath,
            logicalDeviceId,
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            historyDeviceKey: historyDeviceKey,
            deviceSerial: "REAL-RECEIVER-SERIAL",
            associationReceiverHistoryKey: receiverAssociationKey,
            associationReceiverSerial: "REAL-RECEIVER-SERIAL");
        var replacementEpoch = SoraStateReading(
            wiredPath,
            logicalDeviceId,
            80,
            DeviceConnectionTransport.WiredUsb,
            externallyPowered: true,
            new[] { receiverPath, wiredPath },
            historyDeviceKey: "0X1915:0XAE1C:SERIAL:REPLACEMENT-RECEIVER",
            historyAnchorEvidence: HistoryAnchorEvidence.ConfirmReceiver,
            associationReceiverHistoryKey: receiverAssociationKey,
            associationReceiverSerial: "REPLACEMENT-RECEIVER");
        Equal(recorder.DeviceToken(reading), recorder.DeviceToken(receiverTransport),
            "the same receiver epoch must keep one log token across wired and receiver transports");
        NotEqual(recorder.DeviceToken(reading), recorder.DeviceToken(replacementEpoch),
            "a reused receiver path with a new stable serial must get a distinct log token");
        var unscopedReceiverOne = SoraStateReading(
            receiverPath,
            logicalDeviceId,
            74,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "RECEIVER-ONE");
        var unscopedReceiverTwo = SoraStateReading(
            receiverPath,
            logicalDeviceId,
            80,
            DeviceConnectionTransport.Receiver,
            externallyPowered: false,
            new[] { receiverPath },
            deviceSerial: "RECEIVER-TWO");
        NotEqual(recorder.DeviceToken(unscopedReceiverOne), recorder.DeviceToken(unscopedReceiverTwo),
            "receiver-only fallback tokens must separate stable serial epochs even without an association field");

        recorder.Write("info", "selftest.logical_transport", nameof(Program), "success", reading: reading);
        True(recorder.Flush(TimeSpan.FromSeconds(5)), "logical transport event should flush");

        var entry = FindEvent(ReadJsonLinesShared(Path.Combine(temporary.Path, "flight-current.jsonl")), "selftest.logical_transport");
        var device = entry.GetProperty("device");
        Equal(logicalDeviceId, device.GetProperty("logical_id").GetString(), "logical id should remain readable");
        Equal(historyDeviceKey, device.GetProperty("history_key").GetString(), "history key should remain readable");
        Equal(receiverAssociationKey, device.GetProperty("receiver_association_key").GetString(),
            "receiver association key should remain readable");
        Equal("REAL-RECEIVER-SERIAL", device.GetProperty("receiver_association_serial").GetString(),
            "receiver association serial should remain readable");
        Equal("confirmreceiver", device.GetProperty("history_anchor_evidence").GetString(),
            "history anchor evidence should remain readable");
        Equal("wiredusb", device.GetProperty("transport").GetString(), "transport should be recorded");
        Equal(wiredPath, device.GetProperty("path").GetString(), "active physical path should be recorded");
        var resolvedPaths = device.GetProperty("resolved_paths").EnumerateArray().Select(item => item.GetString()).ToArray();
        True(resolvedPaths.Contains(receiverPath), "receiver path should be retained");
        True(resolvedPaths.Contains(wiredPath), "wired path should be retained");

        var snapshotPath = Path.Combine(temporary.Path, "logical-transport-snapshot.jsonl");
        True(recorder.SnapshotRecentLogs(snapshotPath, 1024 * 1024) > 0, "logical transport snapshot should be created");
        var projectedDevice = FindEvent(ReadJsonLinesShared(snapshotPath), "selftest.logical_transport").GetProperty("device");
        Equal(logicalDeviceId, projectedDevice.GetProperty("logical_id").GetString(), "snapshot should retain logical id");
        Equal(historyDeviceKey, projectedDevice.GetProperty("history_key").GetString(), "snapshot should retain history key");
        Equal(receiverAssociationKey, projectedDevice.GetProperty("receiver_association_key").GetString(),
            "snapshot should retain receiver association key");
        Equal("REAL-RECEIVER-SERIAL", projectedDevice.GetProperty("receiver_association_serial").GetString(),
            "snapshot should retain receiver association serial");
        Equal("confirmreceiver", projectedDevice.GetProperty("history_anchor_evidence").GetString(),
            "snapshot should retain history anchor evidence");
        Equal("wiredusb", projectedDevice.GetProperty("transport").GetString(), "snapshot should retain transport");
        var projectedPaths = projectedDevice.GetProperty("resolved_paths").EnumerateArray().Select(item => item.GetString()).ToArray();
        True(projectedPaths.Contains(receiverPath), "snapshot should retain receiver path");
        True(projectedPaths.Contains(wiredPath), "snapshot should retain wired path");
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
        var forgedWithoutBatteryFact = new
        {
            schema = "sora.flight.v1",
            ts_utc = StartUtc.AddSeconds(1).ToString("O"),
            ts_local = StartUtc.AddSeconds(1).ToString("O"),
            mono_ms = 11,
            seq = 100,
            level = "warn",
            @event = "selftest.forged_legacy_no_battery",
            run_id = Guid.NewGuid(),
            op_id = (string?)null,
            component = "Legacy",
            thread_id = 4,
            app_version = "0.1",
            outcome = "degraded",
            device = new { id = rawSerial, vendor_id = "0x1915", product_id = "0xAE12", battery_percentage = 0 },
            data = (object?)null,
            error = (object?)null
        };
        File.WriteAllLines(
            Path.Combine(temporary.Path, "flight-forged.jsonl"),
            new[]
            {
                JsonSerializer.Serialize(forged),
                JsonSerializer.Serialize(forgedWithoutBatteryFact)
            },
            new UTF8Encoding(false));

        var snapshotPath = Path.Combine(temporary.Path, "snapshot.jsonl");
        True(recorder.SnapshotRecentLogs(snapshotPath, 1024 * 1024) > 0, "snapshot should contain projected records");
        var entries = ReadJsonLinesShared(snapshotPath);
        var projected = FindEvent(entries, "selftest.forged_legacy");
        var projectedWithoutBatteryFact = FindEvent(entries, "selftest.forged_legacy_no_battery");
        True(entries.Any(entry => EnumerateJsonText(entry).Any(text => text.Contains(rawPath, StringComparison.Ordinal))),
            "snapshot should preserve the raw device path fact");
        True(entries.Any(entry => EnumerateJsonText(entry).Any(text => text.Contains(rawSerial, StringComparison.Ordinal))),
            "snapshot should preserve the legacy identity fact");
        True(projected.GetProperty("device").GetProperty("id").GetString()?.StartsWith("dev_", StringComparison.Ordinal) == true,
            "legacy device id should also gain a stable correlation token");
        True(projected.GetProperty("device").GetProperty("has_battery_percentage").GetBoolean(),
            "a valid legacy 50-percent value should remain a battery fact");
        False(projectedWithoutBatteryFact.GetProperty("device").GetProperty("has_battery_percentage").GetBoolean(),
            "a legacy context-only zero must not become a fabricated battery fact");
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

    private static BatteryReading SoraStateReading(
        string deviceId,
        string logicalDeviceId,
        int battery,
        DeviceConnectionTransport transport,
        bool externallyPowered,
        IReadOnlyList<string> resolvedDeviceIds,
        string historyDeviceKey = "",
        string deviceSerial = "000000000000",
        HistoryAnchorEvidence historyAnchorEvidence = HistoryAnchorEvidence.None,
        string associationReceiverHistoryKey = "",
        string associationReceiverSerial = "")
    {
        return new BatteryReading
        {
            BatteryPercentage = battery,
            HasBatteryPercentage = true,
            IsOnline = true,
            IsCableConnected = externallyPowered,
            IsCharging = externallyPowered,
            IsFullyCharged = externallyPowered && battery >= 100,
            ExternalPowerConnected = externallyPowered,
            PowerState = externallyPowered
                ? battery >= 100 ? DevicePowerState.FullyCharged : DevicePowerState.Charging
                : DevicePowerState.Discharging,
            LogicalDeviceId = logicalDeviceId,
            HistoryDeviceKey = historyDeviceKey,
            AssociationReceiverHistoryKey = associationReceiverHistoryKey,
            AssociationReceiverSerial = associationReceiverSerial,
            ConnectionTransport = transport,
            HistoryAnchorEvidence = historyAnchorEvidence,
            ResolvedDeviceIds = resolvedDeviceIds,
            DeviceId = deviceId,
            DeviceSerial = deviceSerial,
            DeviceName = "Ninjutso Sora V2",
            VendorId = "0x1915",
            ProductId = transport == DeviceConnectionTransport.Receiver ? "0xAE1C" : "0xAE12",
            Source = "SORA V2 Official HID"
        };
    }

    private static DeviceIdentityObservation SoraIdentityObservation(
        string deviceId,
        string logicalDeviceId,
        string historyDeviceKey,
        HistoryAnchorEvidence historyAnchorEvidence,
        IReadOnlyList<string> resolvedDeviceIds,
        string associationReceiverHistoryKey,
        string associationReceiverSerial = "",
        DateTime? timestampUtc = null)
    {
        return new DeviceIdentityObservation
        {
            DeviceId = deviceId,
            DeviceName = "Ninjutso Sora V2",
            LogicalDeviceId = logicalDeviceId,
            HistoryDeviceKey = historyDeviceKey,
            AssociationReceiverHistoryKey = associationReceiverHistoryKey,
            AssociationReceiverSerial = associationReceiverSerial,
            HistoryAnchorEvidence = historyAnchorEvidence,
            ResolvedDeviceIds = resolvedDeviceIds,
            DeviceSerial = "000000000000",
            VendorId = "0x1915",
            ProductId = "0xAE12",
            Source = "SORA V2 Official HID",
            TimestampUtc = timestampUtc ?? DateTime.UtcNow
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
        private readonly int _vendorId;
        private readonly int _productId;

        public FakeHidDevice(
            string path,
            string serial,
            string productName = "SelfTest Mouse",
            string manufacturer = "SelfTest Manufacturer",
            int vendorId = 0x1915,
            int productId = 0xAE12)
        {
            _path = path;
            _serial = serial;
            _productName = productName;
            _manufacturer = manufacturer;
            _vendorId = vendorId;
            _productId = productId;
        }

        public override int ProductID => _productId;
        public override int ReleaseNumberBcd => 0x0100;
        public override int VendorID => _vendorId;
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
