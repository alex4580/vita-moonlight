namespace VitaMoonlight.Host;

internal sealed record SunshineBackendObservation(
    SunshineBackendKind Kind,
    bool Exists,
    bool Running,
    uint? ServiceStartType,
    bool? DelayedAutoStart,
    string? Error);

/// <summary>
/// Defines the trust boundary between Vita-owned lifecycle components and a
/// shared streaming host. Sunshine credentials/configuration can contain
/// Vita-owned values, but that does not make the process or Windows service
/// itself Vita-owned. Backend pause/enable and uninstall therefore never stop,
/// start, disable, or execute Sunshine.
/// </summary>
internal static class SunshineBackendController
{
    // Retained for deserializing lifecycle records written by an unreleased
    // preview and for pure compatibility tests. They are never applied.
    internal const uint ServiceAutoStart = 2;
    internal const uint ServiceDemandStart = 3;
    internal const uint ServiceDisabled = 4;

    internal static SunshineBackendSnapshot CaptureManagedState() => None();

    internal static void Pause(SunshineBackendSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        // Shared Sunshine is intentionally outside this lifecycle boundary.
    }

    internal static void Resume(SunshineBackendSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        // Shared Sunshine is intentionally outside this lifecycle boundary.
    }

    internal static SunshineBackendObservation Inspect(
        SunshineBackendSnapshot snapshot)
    {
        try
        {
            ValidateSnapshot(snapshot);
            return new SunshineBackendObservation(
                snapshot.Kind,
                Exists: false,
                Running: false,
                ServiceStartType: null,
                DelayedAutoStart: null,
                Error: null);
        }
        catch (InvalidDataException error)
        {
            return new SunshineBackendObservation(
                snapshot.Kind,
                Exists: false,
                Running: false,
                ServiceStartType: null,
                DelayedAutoStart: null,
                Error: error.Message);
        }
    }

    internal static void ValidateSnapshot(SunshineBackendSnapshot snapshot)
    {
        switch (snapshot.Kind)
        {
            case SunshineBackendKind.None:
                if (snapshot.ServiceName is not null ||
                    snapshot.ServiceStartType is not null ||
                    snapshot.DelayedAutoStart is not null ||
                    snapshot.ExecutablePath is not null ||
                    snapshot.WasRunning)
                {
                    throw new InvalidDataException(
                        "An unmanaged Sunshine snapshot contains control state.");
                }
                return;
            case SunshineBackendKind.Service:
                ValidateServiceName(snapshot.ServiceName);
                if (snapshot.ServiceStartType is not (
                        ServiceAutoStart or
                        ServiceDemandStart or
                        ServiceDisabled) ||
                    snapshot.DelayedAutoStart is null ||
                    snapshot.ExecutablePath is not null)
                {
                    throw new InvalidDataException(
                        "The legacy Sunshine service snapshot is incomplete or invalid.");
                }
                return;
            case SunshineBackendKind.Process:
                if (snapshot.ServiceName is not null ||
                    snapshot.ServiceStartType is not null ||
                    snapshot.DelayedAutoStart is not null ||
                    !IsValidExecutablePath(snapshot.ExecutablePath))
                {
                    throw new InvalidDataException(
                        "The legacy Sunshine process snapshot is incomplete or invalid.");
                }
                return;
            default:
                throw new InvalidDataException(
                    "The Sunshine backend snapshot has an unknown kind.");
        }
    }

    internal static bool MatchesDesiredState(
        BackendDesiredState desiredState,
        SunshineBackendSnapshot snapshot,
        SunshineBackendObservation observation)
    {
        _ = desiredState;
        _ = snapshot;
        // No Sunshine state is part of the Vita backend desired state. A
        // malformed legacy record is still surfaced through observation.Error.
        return observation.Error is null;
    }

    private static bool IsValidExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            return Path.GetFullPath(path).Equals(
                path,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (
            error is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidateServiceName(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) ||
            serviceName.Length > 256 ||
            serviceName.Any(character =>
                char.IsControl(character) ||
                character is '\\' or '/'))
        {
            throw new InvalidDataException(
                "The recorded Sunshine service name is invalid.");
        }
    }

    private static SunshineBackendSnapshot None() => new(
        SunshineBackendKind.None,
        null,
        null,
        null,
        null,
        false);
}
