using System.Xml.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using VitaMoonlight.Host;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TestVddConfigurationNormalization()
{
    const string original = """
        <vdd_settings>
          <monitors>
            <count>3</count>
            <vendor_extension>preserved</vendor_extension>
          </monitors>
          <monitors><count>2</count></monitors>
          <gpu><friendlyname>user-selected-gpu</friendlyname></gpu>
          <resolutions>
            <resolution>
              <width>1111</width>
              <height>777</height>
              <refresh_rate>73</refresh_rate>
            </resolution>
          </resolutions>
          <options>
            <HardwareCursor>false</HardwareCursor>
            <Logging>true</Logging>
            <logging>true</logging>
          </options>
          <OPTIONS>
            <CustomEdid>true</CustomEdid>
            <debuglogging>TRUE</debuglogging>
            <DebugLogging>true</DebugLogging>
          </OPTIONS>
        </vdd_settings>
        """;

    var normalized =
        VddConfigurationNormalizer.NormalizeForVitaRuntime(original);
    var document = XDocument.Parse(normalized);
    var root = document.Root!;
    var monitors = root.Elements("monitors").ToArray();
    var options = root.Elements("options").ToArray();
    Require(monitors.Length == 1, "Repair retained multiple monitor containers.");
    Require(
        monitors[0].Elements("count").Single().Value == "1",
        "Repair did not configure exactly one virtual monitor.");
    Require(
        monitors[0].Element("vendor_extension")?.Value == "preserved",
        "Repair removed an unrelated monitor option.");
    Require(options.Length == 1, "Repair retained multiple option containers.");
    Require(
        options[0].Elements("logging").Single().Value == "false" &&
        options[0].Elements("debuglogging").Single().Value == "false",
        "Repair did not disable both driver logging modes exactly once.");
    Require(
        options[0].Element("HardwareCursor")?.Value == "false" &&
        options[0].Element("CustomEdid")?.Value == "true",
        "Repair changed or removed an unrelated driver option.");
    Require(
        root.Element("gpu")?.Element("friendlyname")?.Value ==
            "user-selected-gpu" &&
        root.Element("resolutions")?.Element("resolution")?
            .Element("width")?.Value == "1111",
        "Repair changed a user-selected GPU or custom resolution.");
    Require(
        VddConfigurationNormalizer.IsNormalizedForVitaRuntime(normalized),
        "The normalized configuration was not recognized as safe.");
    Require(
        VddConfigurationNormalizer.NormalizeForVitaRuntime(normalized) ==
            normalized,
        "VDD normalization is not idempotent.");
}

static void TestVersionedExactResidueCleanup()
{
    string[] expectedRootPayloadV1 =
    [
        "COMPATIBILITY.md",
        "END_TO_END_TEST.md",
        "FINAL_RELEASE_CHECKLIST.md",
        "THIRD_PARTY_NOTICES.md",
        "VITA_SETTINGS_GUIDE.md",
    ];
    string[] expectedLegacyStateV1 =
    [
        "display-recovery.json",
        "host-settings.json",
        "session.lock",
        "last-command-error.txt",
        "display-driver-verification.json",
        "display-driver-directory-identity.json",
        "backend-lifecycle.json",
        "backend-lifecycle.backup.json",
        "backend-lifecycle.lock",
        "backend-disabled.intent",
        "deferred-host-setup.json",
        "display-suspend.intent",
        "display-suspend.lock",
        "installer-maintenance.json",
        "installer-maintenance.backup.json",
        "installer-maintenance.lock",
        "stream-rescue-status.json",
        "stream-rescue.log",
    ];
    Require(
        InstallResidueCleanup.ObsoleteRootPayloadFilesV1.SequenceEqual(
            expectedRootPayloadV1) &&
        InstallResidueCleanup.LegacyProgramDataStateFilesV1.SequenceEqual(
            expectedLegacyStateV1),
        "A version-one exact cleanup allowlist drifted.");

    var testRoot = Path.Combine(
        Path.GetTempPath(),
        $"vita-moonlight-residue-test-{Guid.NewGuid():N}");
    var installRoot = Path.Combine(testRoot, "install");
    var legacyRoot = Path.Combine(testRoot, "legacy");
    var diagnosticsRoot = Path.Combine(legacyRoot, "Diagnostics");
    var unknownTree = Path.Combine(legacyRoot, "community-data");
    try
    {
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(diagnosticsRoot);
        Directory.CreateDirectory(unknownTree);
        File.WriteAllText(
            Path.Combine(installRoot, "END_TO_END_TEST.md"),
            "obsolete");
        File.WriteAllText(
            Path.Combine(installRoot, "THIRD_PARTY_NOTICES.md"),
            "obsolete");
        File.WriteAllText(
            Path.Combine(installRoot, "README.md"),
            "current-owned-sentinel");
        File.WriteAllText(
            Path.Combine(legacyRoot, "host-settings.json"),
            "inert legacy state");
        File.WriteAllText(
            Path.Combine(diagnosticsRoot, "stream-rescue.log"),
            "inert legacy diagnostic");
        File.WriteAllText(
            Path.Combine(legacyRoot, "community-notes.txt"),
            "unknown sentinel");
        File.WriteAllText(
            Path.Combine(unknownTree, "keep.txt"),
            "nested unknown sentinel");

        var first = InstallResidueCleanup.CleanupVersions(
            completedVersion: 0,
            installRoot,
            legacyRoot);
        Require(
            first.Completed && first.RemovedFiles == 4,
            "Version-one cleanup did not remove its four exact fixture files.");
        Require(
            !File.Exists(Path.Combine(installRoot, "END_TO_END_TEST.md")) &&
            !File.Exists(Path.Combine(installRoot, "THIRD_PARTY_NOTICES.md")) &&
            !File.Exists(Path.Combine(legacyRoot, "host-settings.json")) &&
            !Directory.Exists(diagnosticsRoot),
            "Version-one cleanup retained a known obsolete entry.");
        Require(
            File.Exists(Path.Combine(installRoot, "README.md")) &&
            File.Exists(Path.Combine(legacyRoot, "community-notes.txt")) &&
            File.Exists(Path.Combine(unknownTree, "keep.txt")),
            "Exact cleanup removed a current or unknown entry.");

        var alreadyCompleted = Path.Combine(
            installRoot,
            "END_TO_END_TEST.md");
        File.WriteAllText(alreadyCompleted, "must remain after v1");
        var skipped = InstallResidueCleanup.CleanupVersions(
            InstallResidueCleanup.CurrentCleanupVersion,
            installRoot,
            legacyRoot);
        Require(
            skipped.RemovedFiles == 0 && File.Exists(alreadyCompleted),
            "A completed cleanup generation ran again.");
        File.Delete(alreadyCompleted);

        var busyPath = Path.Combine(legacyRoot, "session.lock");
        File.WriteAllText(busyPath, "busy legacy state");
        using (var busy = new FileStream(
                   busyPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var retained = InstallResidueCleanup.CleanupVersions(
                completedVersion: 0,
                installRoot,
                legacyRoot);
            Require(
                !retained.Completed &&
                retained.RetainedOwnedEntries.Any(path =>
                    string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(busyPath),
                        StringComparison.OrdinalIgnoreCase)),
                "A busy exact legacy entry was not retained for retry.");
        }
        var retry = InstallResidueCleanup.CleanupVersions(
            completedVersion: 0,
            installRoot,
            legacyRoot);
        Require(
            retry.Completed && !File.Exists(busyPath),
            "Exact cleanup did not complete after a busy file was released.");

        var driveRoot = Path.GetPathRoot(testRoot)!;
        var rootRejected = false;
        try
        {
            InstallResidueCleanup.CleanupVersions(
                completedVersion: 0,
                driveRoot,
                legacyRoot);
        }
        catch (InvalidDataException)
        {
            rootRejected = true;
        }
        Require(rootRejected, "Cleanup accepted a filesystem root as scope.");
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}

static void TestStreamBoundaryProtocolParsing()
{
    var prepare = StreamBoundaryRequest.Parse(
        "GET /v1/prepare?fps=60&width=960&height=544 HTTP/1.1\r\n" +
        "Host: test\r\n\r\n");
    Require(
        prepare.Path == StreamBoundaryBridgeServer.PreparePath &&
        prepare.Parameters.Count == 3 &&
        prepare.TryGetInt("width", out var width) && width == 960 &&
        prepare.TryGetInt("height", out var height) && height == 544 &&
        prepare.TryGetInt("fps", out var fps) && fps == 60,
        "Host stream-boundary prepare parsing drifted.");

    var stop = StreamBoundaryRequest.Parse(
        "GET /v1/stop?generation=0123456789abcdef0123456789abcdef " +
        "HTTP/1.1\r\nHost: test\r\n\r\n");
    Require(
        stop.Path == StreamBoundaryBridgeServer.StopPath &&
        StreamBoundaryBridgeServer.IsGeneration(
            stop.Parameters["generation"]),
        "Host stream-boundary generation parsing drifted.");

    var started = StreamBoundaryRequest.Parse(
        "GET /v1/started?generation=0123456789abcdef0123456789abcdef " +
        "HTTP/1.1\r\nHost: test\r\n\r\n");
    var heartbeat = StreamBoundaryRequest.Parse(
        "GET /v1/heartbeat?generation=0123456789abcdef0123456789abcdef " +
        "HTTP/1.1\r\nHost: test\r\n\r\n");
    Require(
        started.Path == StreamBoundaryBridgeServer.StartedPath &&
        heartbeat.Path == StreamBoundaryBridgeServer.HeartbeatPath &&
        started.Parameters.Keys.SequenceEqual(["generation"]) &&
        heartbeat.Parameters.Keys.SequenceEqual(["generation"]),
        "Host stream-boundary lifecycle endpoint parsing drifted.");
    Require(
        !StreamBoundaryBridgeServer.IsGeneration(
            "0123456789ABCDEF0123456789ABCDEF") &&
        !StreamBoundaryBridgeServer.IsGeneration("short"),
        "Host accepted a non-canonical stream-boundary generation.");

    var rejectedDuplicate = false;
    try
    {
        _ = StreamBoundaryRequest.Parse(
            "GET /v1/stop?generation=0123456789abcdef0123456789abcdef" +
            "&generation=ffffffffffffffffffffffffffffffff HTTP/1.1\r\n" +
            "Host: test\r\n\r\n");
    }
    catch (InvalidDataException)
    {
        rejectedDuplicate = true;
    }
    Require(rejectedDuplicate, "Host accepted a duplicate generation value.");

    var rejectedBody = false;
    try
    {
        _ = StreamBoundaryRequest.Parse(
            "GET /v1/stop?generation=0123456789abcdef0123456789abcdef " +
            "HTTP/1.1\r\nContent-Length: 1\r\n\r\n");
    }
    catch (InvalidDataException)
    {
        rejectedBody = true;
    }
    Require(rejectedBody, "Host accepted a stream-boundary request body.");
}

static void TestStreamBoundaryOperationalFailures()
{
    Require(
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new TimeoutException()) &&
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new OperationCanceledException()) &&
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new HostRestartRequiredException("restart")) &&
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new System.Security.SecurityException()) &&
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new System.ComponentModel.Win32Exception(5)) &&
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new IOException()),
        "A bounded host operational failure could terminate the bridge listener.");
    Require(
        StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new AggregateException(
                new TimeoutException(),
                new IOException())) &&
        !StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new AggregateException(
                new TimeoutException(),
                new NullReferenceException())) &&
        !StreamBoundaryBridgeServer.IsOperationalRequestFailure(
            new NullReferenceException()),
        "Aggregate bridge failure classification hid a programming failure or rejected operational inners.");
}

static void TestStreamBoundaryLeaseContract()
{
    var now = new DateTimeOffset(
        2026,
        8,
        8,
        12,
        0,
        0,
        TimeSpan.Zero);
    var captured = now.AddSeconds(-1);
    const string generation =
        "0123456789abcdef0123456789abcdef";
    var owner = Enumerable.Range(0, 32)
        .Select(value => (byte)value)
        .ToArray();
    var prepared = StreamBoundaryLeaseJournal.CreatePreparedForTest(
        generation,
        owner,
        960,
        544,
        60,
        captured,
        now);
    Require(
        prepared.Phase == StreamBoundaryLeasePhase.Prepared &&
        prepared.LeaseExpiresAtUtc ==
            now.Add(StreamBoundaryLeaseJournal.PreparedLifetime) &&
        StreamBoundaryLeaseJournal.PreparedLifetime >=
            TimeSpan.FromSeconds(210),
        "The durable Prepared lease cannot cover host setup plus Sunshine launch negotiation.");
    var livePrepared = StreamBoundaryLeaseJournal.ClassifyForTest(
        prepared,
        captured,
        now.AddSeconds(30));
    Require(
        livePrepared.Disposition ==
            StreamBoundaryLeaseDisposition.LivePrepared &&
        livePrepared.AuthorizesActiveHandoff &&
        !livePrepared.RequiresRecovery &&
        HostRecoveryActions.ShouldDeferRecoveryForLease(
            livePrepared,
            hasNoSessionProof: false) &&
        !HostRecoveryActions.ShouldDeferRecoveryForLease(
            livePrepared,
            hasNoSessionProof: true),
        "An exact unexpired Prepared lease did not authorize its handoff.");
    Require(
        StreamBoundaryLeaseJournal.ClassifyForTest(
                null,
                captured,
                now).Disposition ==
            StreamBoundaryLeaseDisposition.OrphanedRecovery &&
        StreamBoundaryLeaseJournal.ClassifyForTest(
                prepared,
                null,
                now).Disposition ==
            StreamBoundaryLeaseDisposition.StaleLease &&
        StreamBoundaryLeaseJournal.ClassifyForTest(
                prepared,
                captured.AddTicks(1),
                now).Disposition ==
            StreamBoundaryLeaseDisposition.MismatchedRecovery &&
        StreamBoundaryLeaseJournal.ClassifyForTest(
                prepared,
                captured,
                prepared.LeaseExpiresAtUtc).Disposition ==
            StreamBoundaryLeaseDisposition.Expired,
        "Lease assessment did not fail closed for orphaned, stale, mismatched, or expired state.");

    var startedAt = now.AddSeconds(100);
    var started = StreamBoundaryLeaseJournal.MarkStartedForTest(
        prepared,
        startedAt);
    Require(
        started.Phase == StreamBoundaryLeasePhase.Started &&
        started.StartedAtUtc == startedAt &&
        started.LastHeartbeatAtUtc == startedAt &&
        started.LeaseExpiresAtUtc ==
            startedAt.Add(StreamBoundaryLeaseJournal.StartedLifetime),
        "Started did not establish the bounded active heartbeat lease.");
    var heartbeatAt = startedAt.AddSeconds(10);
    var renewed = StreamBoundaryLeaseJournal.RenewHeartbeatForTest(
        started,
        heartbeatAt);
    Require(
        renewed.LastHeartbeatAtUtc == heartbeatAt &&
        renewed.LeaseExpiresAtUtc ==
            heartbeatAt.Add(StreamBoundaryLeaseJournal.StartedLifetime) &&
        StreamBoundaryLeaseJournal.ClassifyForTest(
                renewed,
                captured,
                heartbeatAt).Disposition ==
            StreamBoundaryLeaseDisposition.LiveStarted,
        "An authenticated heartbeat did not renew exact active authority.");

    // A real stream commonly outlives several 45-second lease windows. Every
    // exact heartbeat must move the observer's deadline without consuming its
    // later 30-second display-recovery retry budget.
    var longStream = started;
    for (var seconds = 10; seconds <= 100; seconds += 10)
    {
        var beat = startedAt.AddSeconds(seconds);
        longStream = StreamBoundaryLeaseJournal.RenewHeartbeatForTest(
            longStream,
            beat);
        var assessment = StreamBoundaryLeaseJournal.ClassifyForTest(
            longStream,
            captured,
            beat);
        Require(
            assessment.Disposition ==
                StreamBoundaryLeaseDisposition.LiveStarted &&
            HostRecoveryActions
                .GetStreamBoundaryRecoveryDelayForAssessment(
                    assessment,
                    beat) ==
                StreamBoundaryLeaseJournal.StartedLifetime,
            "A stream longer than two lease lifetimes lost its renewed observer deadline.");
    }
    Require(
        StreamBoundaryLeaseJournal.MatchesAuthority(
            renewed,
            generation,
            owner,
            captured) &&
        !StreamBoundaryLeaseJournal.MatchesAuthority(
            renewed,
            "ffffffffffffffffffffffffffffffff",
            owner,
            captured) &&
        !StreamBoundaryLeaseJournal.MatchesAuthority(
            renewed,
            generation,
            owner.Select(value => (byte)(value ^ 0xff)).ToArray(),
            captured) &&
        !StreamBoundaryLeaseJournal.MatchesAuthority(
            renewed,
            generation,
            owner,
            captured.AddTicks(1)),
        "Lease authority was not bound to generation, paired certificate, and recovery CapturedAt.");
    Require(
        !StreamBoundaryLeaseJournal.ShouldCheckpointHeartbeatForTest(
            heartbeatAt,
            heartbeatAt.Add(
                StreamBoundaryLeaseJournal.HeartbeatCheckpointInterval)
                .AddTicks(-1)) &&
        StreamBoundaryLeaseJournal.ShouldCheckpointHeartbeatForTest(
            heartbeatAt,
            heartbeatAt.Add(
                StreamBoundaryLeaseJournal.HeartbeatCheckpointInterval)) &&
        StreamBoundaryLeaseJournal.HeartbeatCheckpointInterval <=
            TimeSpan.FromSeconds(30) &&
        StreamBoundaryLeaseJournal.StartedLifetime -
            StreamBoundaryLeaseJournal.HeartbeatCheckpointInterval >=
            TimeSpan.FromSeconds(15),
        "Heartbeat durability checkpointing is too frequent or leaves no restart grace.");
    Require(
        StreamBoundaryLeaseJournal
            .CanRemoveAssessedForProvenNoSessionForTest(
                renewed,
                StreamBoundaryLeaseJournal.ClassifyForTest(
                    renewed,
                    captured,
                    heartbeatAt)) &&
        !StreamBoundaryLeaseJournal
            .CanRemoveAssessedForProvenNoSessionForTest(
                renewed with { Revision = renewed.Revision + 1 },
                StreamBoundaryLeaseJournal.ClassifyForTest(
                    renewed,
                    captured,
                    heartbeatAt)) &&
        !StreamBoundaryLeaseJournal
            .CanRemoveAssessedForProvenNoSessionForTest(
                renewed,
                StreamBoundaryLeaseJournal.ClassifyForTest(
                    renewed,
                    captured,
                    renewed.LeaseExpiresAtUtc)),
        "Proven-no-session invalidation was not limited to the exact live assessed revision.");

    var rejectedPreparedHeartbeat = false;
    try
    {
        _ = StreamBoundaryLeaseJournal.RenewHeartbeatForTest(
            prepared,
            now.AddSeconds(1));
    }
    catch (InvalidOperationException)
    {
        rejectedPreparedHeartbeat = true;
    }
    Require(
        rejectedPreparedHeartbeat,
        "The host accepted a heartbeat before the client reported Started.");
}

static void TestStreamBoundaryLifecycleContract()
{
    Require(
        SunshineStreamBridgeConfiguration.IsEnabledForSettings(
            HostSettings.Default) &&
        !SunshineStreamBridgeConfiguration.IsEnabledForSettings(
            HostSettings.Default with { IntegrateAllSunshineApps = false }),
        "The authenticated boundary conflicts with explicit legacy hook mode.");

    var directory = Path.Combine(
        Path.GetTempPath(),
        $"vita-moonlight-bridge-config-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "sunshine.conf"),
            "port = 50000\n" +
            "file_state = state/custom-state.json\n" +
            "cert = identity/server.pem\n" +
            "pkey = identity/server-key.pem\n");
        var resolved = SunshineStreamBridgeConfiguration
            .LoadFromConfigurationDirectory(directory);
        Require(
            resolved.Port == 50023 &&
            resolved.StatePath == Path.GetFullPath(
                Path.Combine(directory, "state", "custom-state.json")) &&
            resolved.CertificatePath == Path.GetFullPath(
                Path.Combine(directory, "identity", "server.pem")) &&
            resolved.PrivateKeyPath == Path.GetFullPath(
                Path.Combine(directory, "identity", "server-key.pem")),
            "The bridge did not honor Sunshine port/file_state/cert/pkey settings.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestStreamBoundaryPairedCertificateAuthority()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"vita-moonlight-bridge-state-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "sunshine_state.json");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Vita Moonlight contract test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        var pem = certificate.ExportCertificatePem();
        File.WriteAllText(
            statePath,
            "{\"root\":{\"named_devices\":[{" +
            "\"enabled\":\"true\",\"cert\":" +
            JsonSerializer.Serialize(pem) + "}]}}");
        var hashes = SunshinePairedClientCertificates
            .LoadEnabledCertificateHashes(statePath);
        Require(
            hashes.Count == 1 &&
            CryptographicOperations.FixedTimeEquals(
                hashes[0],
                SHA256.HashData(certificate.RawData)),
            "The bridge did not authorize the exact enabled Sunshine certificate.");

        File.WriteAllText(
            statePath,
            "{\"root\":{\"named_devices\":[{" +
            "\"enabled\":\"false\",\"cert\":" +
            JsonSerializer.Serialize(pem) + "}]}}");
        Require(
            SunshinePairedClientCertificates
                .LoadEnabledCertificateHashes(statePath).Count == 0,
            "The bridge authorized a disabled Sunshine device.");

        File.WriteAllText(
            statePath,
            "{\"root\":{\"named_devices\":[]}," +
            "\"root\":{\"named_devices\":[]}}");
        var duplicateRejected = false;
        try
        {
            _ = SunshinePairedClientCertificates
                .LoadEnabledCertificateHashes(statePath);
        }
        catch (InvalidDataException)
        {
            duplicateRejected = true;
        }
        Require(
            duplicateRejected,
            "The bridge accepted ambiguous duplicate Sunshine authority state.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestRecoveryLifecycleDecisions()
{
    Require(
        ResumeTopologyClassifier.Classify(
            activePhysicalPaths: 1,
            activeManagedVirtualPaths: 0,
            pendingTransactionAtWake: null,
            currentPendingTransaction: null,
            stableSamples: ResumeTopologyClassifier.HealthySamplesRequired,
            minimumRecoveryAgeReached: false,
            managedVirtualPnpDisabled: true) ==
        ResumeTopologyDecision.Healthy,
        "A physical-only, PnP-disabled resume topology was not healthy.");
    Require(
        ResumeTopologyClassifier.Classify(
            activePhysicalPaths: 1,
            activeManagedVirtualPaths: 0,
            pendingTransactionAtWake: null,
            currentPendingTransaction: null,
            stableSamples: ResumeTopologyClassifier.HealthySamplesRequired,
            minimumRecoveryAgeReached: false,
            managedVirtualPnpDisabled: false) ==
        ResumeTopologyDecision.ReconcileIdle,
        "A physical-only resume topology with an enabled managed VDD skipped idle reconciliation.");
    Require(
        ResumeTopologyClassifier.Classify(
            activePhysicalPaths: 0,
            activeManagedVirtualPaths: 1,
            pendingTransactionAtWake: null,
            currentPendingTransaction: "new-session",
            stableSamples: ResumeTopologyClassifier.RecoverySamplesRequired,
            minimumRecoveryAgeReached: true,
            managedVirtualPnpDisabled: false) ==
        ResumeTopologyDecision.Healthy,
        "A new post-resume session was mistaken for unhealthy idle.");

    Require(
        HostRecoveryActions.ShouldRetrySunshineRecovery(
            recoverySucceeded: false,
            expectedTransaction: "same-generation",
            currentTransaction: "same-generation"),
        "A failed Sunshine cleanup with the same pending marker was not retried.");
    Require(
        !HostRecoveryActions.ShouldRetrySunshineRecovery(
            recoverySucceeded: false,
            expectedTransaction: "old-generation",
            currentTransaction: "new-generation") &&
        !HostRecoveryActions.ShouldRetrySunshineRecovery(
            recoverySucceeded: true,
            expectedTransaction: "same-generation",
            currentTransaction: "same-generation"),
        "Sunshine cleanup retried after supersession or success.");
    Require(
        HostRecoveryHotkeyWindow
            .ShouldInspectSunshineRecoveryForTopologyEvent(
                resumeObservationOpen: false,
                pendingTransaction: "same-generation") &&
        !HostRecoveryHotkeyWindow
            .ShouldInspectSunshineRecoveryForTopologyEvent(
                resumeObservationOpen: true,
                pendingTransaction: "same-generation") &&
        !HostRecoveryHotkeyWindow
            .ShouldInspectSunshineRecoveryForTopologyEvent(
                resumeObservationOpen: false,
                pendingTransaction: null),
        "Topology notifications did not preserve resume priority and exact pending-marker gating.");
}

static void TestSunshineInfoLoggingRetirement()
{
    var restoredLines = new List<string>
    {
        "unrelated = preserved",
        "min_log_level = info",
    };
    var restoredOwnership = new SunshineOwnedLocation
    {
        ConfigurationDirectory = Path.GetTempPath(),
    };
    restoredOwnership.Values["min_log_level"] =
        new SunshineOwnedValue(
            OriginalPresent: true,
            OriginalValue: "warning",
            AppliedValue: "info");
    Require(
        SunshineConfigurator.RetireOwnedLifecycleConfigurationValue(
            restoredLines,
            restoredOwnership,
            "min_log_level") &&
        restoredLines.SequenceEqual(
            new[] { "unrelated = preserved", "min_log_level = warning" }) &&
        !restoredOwnership.Values.ContainsKey("min_log_level"),
        "Upgrade did not exactly restore and retire Vita-owned Sunshine INFO logging.");

    var divergentLines = new List<string> { "min_log_level = debug" };
    var divergentOwnership = new SunshineOwnedLocation
    {
        ConfigurationDirectory = Path.GetTempPath(),
    };
    divergentOwnership.Values["min_log_level"] =
        new SunshineOwnedValue(
            OriginalPresent: true,
            OriginalValue: "warning",
            AppliedValue: "info");
    Require(
        !SunshineConfigurator.RetireOwnedLifecycleConfigurationValue(
            divergentLines,
            divergentOwnership,
            "min_log_level") &&
        divergentLines.SequenceEqual(new[] { "min_log_level = debug" }) &&
        !divergentOwnership.Values.ContainsKey("min_log_level"),
        "INFO logging retirement replaced or retained ownership of a newer external log level.");
}

TestVddConfigurationNormalization();
TestVersionedExactResidueCleanup();
TestStreamBoundaryProtocolParsing();
TestStreamBoundaryOperationalFailures();
TestStreamBoundaryLeaseContract();
TestStreamBoundaryLifecycleContract();
TestStreamBoundaryPairedCertificateAuthority();
TestRecoveryLifecycleDecisions();
TestSunshineInfoLoggingRetirement();
ManagedVddOwnershipContractTests.Run();
AudioEndpointRecoveryContractTests.Run();
PendingAudioRecoveryAgentContractTests.Run();
Console.WriteLine("Install/upgrade cleanup contract tests passed.");
