using VitaMoonlight.Host;

internal static class PendingAudioRecoveryAgentContractTests
{
    internal static void Run()
    {
        FileNotificationsAreLimitedToDurableRecoveryRecords();
        CircuitBreakerSuppressesSelfWriteNotifications();
        DeviceNotificationsCanRearmAfterTheCircuitBreaker();
        RetryWindowIsStrictlyBounded();
    }

    private static void FileNotificationsAreLimitedToDurableRecoveryRecords()
    {
        var primary = Path.GetFileName(HostStatePaths.AudioRecoveryFile);
        var backup = Path.GetFileName(
            HostStatePaths.AudioRecoveryBackupFile);
        var lockFile = Path.GetFileName(
            HostStatePaths.AudioRecoveryLockFile);
        Require(
            HostRecoveryHotkeyWindow
                .IsPendingAudioRecoveryFileNameForContractTest(primary) &&
            HostRecoveryHotkeyWindow
                .IsPendingAudioRecoveryFileNameForContractTest(backup),
            "The audio watcher did not accept both durable pending records.");
        Require(
            !HostRecoveryHotkeyWindow
                .IsPendingAudioRecoveryFileNameForContractTest(lockFile) &&
            !HostRecoveryHotkeyWindow
                .IsPendingAudioRecoveryFileNameForContractTest(
                    "unrelated.json"),
            "The audio watcher reacted to its serialization lock or an unrelated file.");
        Require(
            HostRecoveryHotkeyWindow
                .ShouldSchedulePendingAudioRecoveryForRename(
                    "already-restored.json",
                    primary),
            "Renaming a pending record away did not schedule reconciliation.");
    }

    private static void CircuitBreakerSuppressesSelfWriteNotifications()
    {
        const string stoppedFingerprint = "revision-2-after-partial-progress";
        Require(
            !HostRecoveryHotkeyWindow.ShouldArmPendingAudioRecoveryRetry(
                currentFingerprint: null,
                circuitBreakerFingerprint: null),
            "The audio worker armed without a primary or backup record.");
        Require(
            !HostRecoveryHotkeyWindow.ShouldArmPendingAudioRecoveryRetry(
                stoppedFingerprint,
                stoppedFingerprint),
            "A FileSystemWatcher event from the worker's own partial-progress write bypassed the circuit breaker.");
        Require(
            HostRecoveryHotkeyWindow.ShouldArmPendingAudioRecoveryRetry(
                "revision-3-new-obligation",
                stoppedFingerprint),
            "A genuinely newer pending audio obligation could not re-arm recovery.");
    }

    private static void DeviceNotificationsCanRearmAfterTheCircuitBreaker()
    {
        var cooldown = HostRecoveryHotkeyWindow
            .PendingAudioRecoveryDeviceEventCooldownForContractTest;
        var cooldownMilliseconds = checked((long)cooldown.TotalMilliseconds);
        Require(
            !HostRecoveryHotkeyWindow
                .ShouldRearmPendingAudioRecoveryForDeviceEvent(
                    hasPendingRecord: false,
                    circuitBreakerFingerprint: "pending",
                    nowMilliseconds: cooldownMilliseconds,
                    lastRearmMilliseconds: null) &&
            !HostRecoveryHotkeyWindow
                .ShouldRearmPendingAudioRecoveryForDeviceEvent(
                    hasPendingRecord: true,
                    circuitBreakerFingerprint: null,
                    nowMilliseconds: cooldownMilliseconds,
                    lastRearmMilliseconds: null),
            "A device event armed recovery without both a record and an open circuit breaker.");
        Require(
            HostRecoveryHotkeyWindow
                .ShouldRearmPendingAudioRecoveryForDeviceEvent(
                    hasPendingRecord: true,
                    circuitBreakerFingerprint: "pending",
                    nowMilliseconds: 0,
                    lastRearmMilliseconds: null),
            "The first real endpoint/topology event could not re-arm an unchanged pending obligation.");
        Require(
            !HostRecoveryHotkeyWindow
                .ShouldRearmPendingAudioRecoveryForDeviceEvent(
                    hasPendingRecord: true,
                    circuitBreakerFingerprint: "pending",
                    nowMilliseconds: cooldownMilliseconds - 1,
                    lastRearmMilliseconds: 0) &&
            HostRecoveryHotkeyWindow
                .ShouldRearmPendingAudioRecoveryForDeviceEvent(
                    hasPendingRecord: true,
                    circuitBreakerFingerprint: "pending",
                    nowMilliseconds: cooldownMilliseconds,
                    lastRearmMilliseconds: 0),
            "Rapid device notifications were not coalesced at the cooldown boundary.");
    }

    private static void RetryWindowIsStrictlyBounded()
    {
        var maximumAttempts = HostRecoveryHotkeyWindow
            .MaximumPendingAudioRecoveryAttemptsForContractTest;
        var budget = HostRecoveryHotkeyWindow
            .PendingAudioRecoveryBudgetForContractTest;
        Require(
            maximumAttempts == 30 && budget == TimeSpan.FromSeconds(60),
            "The pending audio retry circuit drifted from its bounded 60-second policy.");
        Require(
            HostRecoveryHotkeyWindow.ShouldContinuePendingAudioRecoveryRetry(
                maximumAttempts - 1,
                budget - TimeSpan.FromMilliseconds(1),
                succeeded: false,
                hasPendingRecord: true,
                cancellationRequested: false),
            "The pending audio worker stopped before its bounded retry window.");
        Require(
            !HostRecoveryHotkeyWindow.ShouldContinuePendingAudioRecoveryRetry(
                maximumAttempts,
                TimeSpan.Zero,
                succeeded: false,
                hasPendingRecord: true,
                cancellationRequested: false) &&
            !HostRecoveryHotkeyWindow.ShouldContinuePendingAudioRecoveryRetry(
                0,
                budget,
                succeeded: false,
                hasPendingRecord: true,
                cancellationRequested: false) &&
            !HostRecoveryHotkeyWindow.ShouldContinuePendingAudioRecoveryRetry(
                0,
                TimeSpan.Zero,
                succeeded: true,
                hasPendingRecord: true,
                cancellationRequested: false) &&
            !HostRecoveryHotkeyWindow.ShouldContinuePendingAudioRecoveryRetry(
                0,
                TimeSpan.Zero,
                succeeded: false,
                hasPendingRecord: false,
                cancellationRequested: false) &&
            !HostRecoveryHotkeyWindow.ShouldContinuePendingAudioRecoveryRetry(
                0,
                TimeSpan.Zero,
                succeeded: false,
                hasPendingRecord: true,
                cancellationRequested: true),
            "The pending audio circuit did not stop on success, removal, disposal, attempt limit, or time limit.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
