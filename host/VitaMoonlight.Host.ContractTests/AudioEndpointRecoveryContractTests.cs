using System.Text.Json;
using VitaMoonlight.Host;

internal static class AudioEndpointRecoveryContractTests
{
    internal static void Run()
    {
        CaptureKeepsExactRoleDefaults();
        RestoreWaitsForTheCapturedEndpoint();
        RestoreNeverSubstitutesAnotherEndpoint();
        PendingRecoveryPreservesTheOldestExactDefaults();
        PartialRecoveryReleasesRestoredRoles();
        PendingRecoverySurvivesATornPrimaryCopy();
        LegacyDisplayRecoveryRemainsReadable();
        LiveWindowsBackendCanReadWithoutMutation();
    }

    private static void CaptureKeepsExactRoleDefaults()
    {
        using var backend = new FakeAudioEndpointBackend();
        backend.Defaults[AudioEndpointRole.Console] = "monitor-audio";
        backend.Defaults[AudioEndpointRole.Multimedia] = "speakers";
        backend.Defaults[AudioEndpointRole.Communications] = "headset";

        var captured = AudioEndpointRecoveryService.Capture(backend);
        Require(
            captured is
            {
                ConsoleDeviceId: "monitor-audio",
                MultimediaDeviceId: "speakers",
                CommunicationsDeviceId: "headset",
            },
            "Audio recovery did not capture each exact pre-stream default role.");
    }

    private static void RestoreWaitsForTheCapturedEndpoint()
    {
        using var backend = new FakeAudioEndpointBackend
        {
            ActiveAfterProbeCount = 2,
        };
        backend.Defaults[AudioEndpointRole.Console] = "temporary-output";
        backend.Defaults[AudioEndpointRole.Multimedia] = "temporary-output";
        backend.Defaults[AudioEndpointRole.Communications] = "headset";
        backend.Active.Add("headset");
        backend.BecomesActive.Add("monitor-audio");
        var waits = 0;

        var result = AudioEndpointRecoveryService.Restore(
            new AudioEndpointRecoveryRecord(
                "monitor-audio",
                "monitor-audio",
                "headset"),
            backend,
            attempts: 4,
            () => waits++);

        Require(
            result.Succeeded &&
            waits == 1 &&
            backend.Defaults[AudioEndpointRole.Console] == "monitor-audio" &&
            backend.Defaults[AudioEndpointRole.Multimedia] == "monitor-audio" &&
            backend.Defaults[AudioEndpointRole.Communications] == "headset" &&
            backend.SetOperations.All(operation =>
                operation.DeviceId is "monitor-audio" or "headset"),
            "Audio recovery did not wait boundedly and restore only the captured endpoint IDs.");
    }

    private static void RestoreNeverSubstitutesAnotherEndpoint()
    {
        using var backend = new FakeAudioEndpointBackend();
        backend.Active.Add("unrelated-speakers");
        backend.Defaults[AudioEndpointRole.Console] = "unrelated-speakers";
        var waits = 0;

        var result = AudioEndpointRecoveryService.Restore(
            new AudioEndpointRecoveryRecord(
                "missing-monitor-audio",
                null,
                null),
            backend,
            attempts: 3,
            () => waits++);

        Require(
            !result.Succeeded &&
            waits == 2 &&
            result.UnavailableRoles.SequenceEqual(
                new[] { AudioEndpointRole.Console }) &&
            backend.SetOperations.Count == 0 &&
            backend.Defaults[AudioEndpointRole.Console] ==
                "unrelated-speakers",
            "Audio recovery substituted or changed an unrelated render endpoint.");
    }

    private static void LegacyDisplayRecoveryRemainsReadable()
    {
        var legacy = JsonSerializer.Deserialize<DisplayRecoveryRecord>("""
            {
              "formatVersion": 1,
              "capturedAt": "2026-08-08T12:00:00Z",
              "pathStructureSize": 72,
              "modeStructureSize": 64,
              "pathCount": 1,
              "modeCount": 1,
              "pathsBase64": "AA==",
              "modesBase64": "AA==",
              "selectedDisplay": null,
              "requestedWidth": 960,
              "requestedHeight": 544,
              "requestedFps": 60
            }
            """, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        Require(
            legacy is { FormatVersion: 1, AudioDefaults: null },
            "Adding audio recovery made a pre-update display transaction unreadable.");
    }

    private static void PendingRecoveryPreservesTheOldestExactDefaults()
    {
        var merged = AudioEndpointRecoveryService.MergePreferExisting(
            new AudioEndpointRecoveryRecord(
                "original-monitor",
                null,
                "original-headset"),
            new AudioEndpointRecoveryRecord(
                "temporary-speakers",
                "original-monitor",
                "temporary-headset"));
        Require(
            merged is
            {
                ConsoleDeviceId: "original-monitor",
                MultimediaDeviceId: "original-monitor",
                CommunicationsDeviceId: "original-headset",
            },
            "A later stream replaced an older unresolved exact audio endpoint obligation.");
    }

    private static void PartialRecoveryReleasesRestoredRoles()
    {
        var remaining = AudioEndpointRecoveryService.RetainOnlyRoles(
            new AudioEndpointRecoveryRecord(
                "monitor",
                "monitor",
                "unplugged-headset"),
            [AudioEndpointRole.Communications]);
        Require(
            remaining is
            {
                ConsoleDeviceId: null,
                MultimediaDeviceId: null,
                CommunicationsDeviceId: "unplugged-headset",
            },
            "Audio recovery retained authority over roles that had already been restored.");
    }

    private static void PendingRecoverySurvivesATornPrimaryCopy()
    {
        var olderBackup = new PendingAudioEndpointRecoveryRecord(
            FormatVersion: 1,
            Revision: 7,
            CapturedAt: DateTimeOffset.Parse("2026-08-08T12:00:00Z"),
            Defaults: new AudioEndpointRecoveryRecord(
                "original-monitor",
                null,
                null));
        var recovered = AudioEndpointRecoveryService
            .SelectPendingForContractTest(
                primary: null,
                backup: olderBackup);
        Require(
            ReferenceEquals(recovered, olderBackup),
            "A torn primary copy did not fall back to the complete redundant audio obligation.");

        var newerPrimary = olderBackup with
        {
            Revision = 8,
            Defaults = new AudioEndpointRecoveryRecord(
                "original-monitor",
                "original-monitor",
                null),
        };
        recovered = AudioEndpointRecoveryService
            .SelectPendingForContractTest(newerPrimary, olderBackup);
        Require(
            ReferenceEquals(recovered, newerPrimary),
            "The redundant audio journal did not select the newest complete revision.");
    }

    private static void LiveWindowsBackendCanReadWithoutMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using IAudioEndpointBackend backend =
            new WindowsCoreAudioEndpointBackend();
        var direct = AudioEndpointRecoveryService.Capture(backend);
        var isolated = AudioEndpointRecoveryService
            .CaptureCurrentDefaults();
        Require(
            WindowsCoreAudioEndpointBackend
                .IsDefaultEndpointPolicyAvailable(),
            "Windows did not expose a compatible exact default-audio endpoint policy interface.");
        Require(
            direct is null || isolated is not null,
            "The isolated Core Audio capture worker lost defaults that the verified in-process backend could read.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeAudioEndpointBackend : IAudioEndpointBackend
    {
        internal Dictionary<AudioEndpointRole, string?> Defaults { get; } = [];
        internal HashSet<string> Active { get; } = [];
        internal HashSet<string> BecomesActive { get; } = [];
        internal List<(AudioEndpointRole Role, string DeviceId)>
            SetOperations { get; } = [];
        internal int ActiveAfterProbeCount { get; init; } = int.MaxValue;
        private int probes;

        public string? GetDefaultRenderDeviceId(AudioEndpointRole role) =>
            Defaults.TryGetValue(role, out var value) ? value : null;

        public bool IsRenderDeviceActive(string deviceId)
        {
            probes++;
            if (probes >= ActiveAfterProbeCount &&
                BecomesActive.Contains(deviceId))
            {
                Active.Add(deviceId);
            }
            return Active.Contains(deviceId);
        }

        public void SetDefaultRenderDevice(
            string deviceId,
            AudioEndpointRole role)
        {
            if (!Active.Contains(deviceId))
            {
                throw new InvalidOperationException(
                    "The fake endpoint is not active.");
            }
            Defaults[role] = deviceId;
            SetOperations.Add((role, deviceId));
        }

        public void Dispose()
        {
        }
    }
}
