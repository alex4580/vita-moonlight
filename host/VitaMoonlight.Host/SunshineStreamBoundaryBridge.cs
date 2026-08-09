using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record SunshineStreamBridgeConfiguration(
    string ConfigurationDirectory,
    string StatePath,
    string CertificatePath,
    string PrivateKeyPath,
    int Port)
{
    internal const int DefaultSunshineHttpPort = 47989;
    internal const int BridgePortOffset = 23;
    private const int MaximumConfigurationBytes = 1024 * 1024;

    internal static SunshineStreamBridgeConfiguration? LoadIfEnabled()
    {
        var settings = HostSettings.Load();
        if (!IsEnabledForSettings(settings))
        {
            return null;
        }

        var directory =
            InstallationTrust.RequireTrustedConfigurationDirectory(
                StreamingHostLocator.ResolveConfigurationDirectory(
                    settings.SunshineConfigDirectory,
                    "sunshine"),
                "Vita stream-boundary setup");
        return LoadFromConfigurationDirectory(directory);
    }

    internal static bool IsEnabledForSettings(HostSettings settings) =>
        settings.HostMode.Equals(
            "sunshine",
            StringComparison.OrdinalIgnoreCase) &&
        settings.IntegrateAllSunshineApps;

    internal static SunshineStreamBridgeConfiguration
        LoadFromConfigurationDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        var values = ReadConfigurationValues(
            Path.Combine(directory, "sunshine.conf"));
        var httpPort = DefaultSunshineHttpPort;
        if (values.TryGetValue("port", out var configuredPort) &&
            (!int.TryParse(
                configuredPort,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out httpPort) ||
             httpPort is < 1 or > ushort.MaxValue - BridgePortOffset))
        {
            throw new InvalidDataException(
                "Sunshine's configured HTTP port cannot produce a valid Vita stream-boundary port.");
        }

        var credentialsDirectory = Path.Combine(directory, "credentials");
        var certificatePath = ResolveConfiguredPath(
            directory,
            values.GetValueOrDefault("cert"),
            Path.Combine(credentialsDirectory, "cacert.pem"));
        var privateKeyPath = ResolveConfiguredPath(
            directory,
            values.GetValueOrDefault("pkey"),
            Path.Combine(credentialsDirectory, "cakey.pem"));
        return new SunshineStreamBridgeConfiguration(
            directory,
            ResolveConfiguredPath(
                directory,
                values.GetValueOrDefault("file_state"),
                Path.Combine(directory, "sunshine_state.json")),
            certificatePath,
            privateKeyPath,
            checked(httpPort + BridgePortOffset));
    }

    private static Dictionary<string, string> ReadConfigurationValues(
        string path)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                "Sunshine's configuration is unexpectedly large.");
        }
        var text = new UTF8Encoding(false, true).GetString(bytes);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals < 1) continue;
            var key = line[..equals].Trim();
            if (!key.Equals("port", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("cert", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("pkey", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("file_state", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = line[(equals + 1)..].Trim();
            var comment = value.IndexOf('#');
            if (comment >= 0) value = value[..comment].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            if (value.Length == 0 || value.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException(
                    $"Sunshine's {key} setting is empty or invalid.");
            }
            if (!result.TryAdd(key, value))
            {
                throw new InvalidDataException(
                    $"Sunshine's {key} setting is defined more than once; Vita Moonlight cannot choose a secure stream-boundary configuration.");
            }
        }
        return result;
    }

    private static string ResolveConfiguredPath(
        string configurationDirectory,
        string? configuredPath,
        string defaultPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? defaultPath
            : configuredPath;
        var combined = Path.IsPathFullyQualified(candidate)
            ? candidate
            : Path.Combine(configurationDirectory, candidate);
        return Path.GetFullPath(combined);
    }
}

/// <summary>
/// Loads Sunshine's PEM identity into a key representation that Windows
/// Schannel can actually use for server authentication. CreateFromPemFile()
/// attaches an ephemeral CNG key. Some supported Windows builds report
/// HasPrivateKey for that certificate but reject it later from SslStream with
/// SEC_E_NO_CREDENTIALS. A password-protected, in-memory PKCS#12 round trip
/// without PersistKeySet gives Schannel a temporary current-user key container;
/// disposing the returned certificate removes that container.
/// </summary>
internal static class SchannelServerCertificate
{
    private static readonly TimeSpan PreflightTimeout =
        TimeSpan.FromSeconds(5);

    internal static X509Certificate2 LoadFromPemFiles(
        string certificatePath,
        string privateKeyPath)
    {
        using var pemCertificate = X509Certificate2.CreateFromPemFile(
            certificatePath,
            privateKeyPath);
        return PrepareForSchannel(pemCertificate);
    }

    private static X509Certificate2 PrepareForSchannel(
        X509Certificate2 pemCertificate)
    {
        if (!pemCertificate.HasPrivateKey)
        {
            throw new InvalidDataException(
                "Sunshine's server certificate has no matching private key.");
        }

        Span<byte> passwordEntropy = stackalloc byte[32];
        RandomNumberGenerator.Fill(passwordEntropy);
        var password = Convert.ToHexString(passwordEntropy);
        CryptographicOperations.ZeroMemory(passwordEntropy);

        byte[]? pkcs12 = null;
        X509Certificate2? schannelCertificate = null;
        try
        {
            pkcs12 = pemCertificate.Export(
                X509ContentType.Pkcs12,
                password);
            schannelCertificate = new X509Certificate2(
                pkcs12,
                password,
                X509KeyStorageFlags.UserKeySet);
            if (!schannelCertificate.HasPrivateKey ||
                !CryptographicOperations.FixedTimeEquals(
                    pemCertificate.RawData,
                    schannelCertificate.RawData))
            {
                throw new InvalidDataException(
                    "Sunshine's server certificate could not be prepared for Windows TLS.");
            }
            var result = schannelCertificate;
            schannelCertificate = null;
            return result;
        }
        finally
        {
            schannelCertificate?.Dispose();
            if (pkcs12 is not null)
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
    }

    internal static void VerifyRuntimeForSelfTest()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Vita Moonlight Schannel self-test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var pemCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        using var schannelCertificate = PrepareForSchannel(pemCertificate);
        VerifyUsableForServerAuthentication(schannelCertificate);
    }

    internal static void VerifyUsableForServerAuthentication(
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var timeout = new CancellationTokenSource(PreflightTimeout);
        try
        {
            VerifyUsableForServerAuthenticationAsync(
                    certificate,
                    timeout.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception error) when (
            error is AuthenticationException or
                IOException or
                SocketException or
                OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Sunshine's server certificate could not be activated by Windows TLS. Run Set up or repair this PC before streaming.",
                error);
        }
    }

    private static async Task VerifyUsableForServerAuthenticationAsync(
        X509Certificate2 certificate,
        CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 1);
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var acceptTask = listener.AcceptTcpClientAsync(token).AsTask();
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(
                    IPAddress.Loopback,
                    endpoint.Port,
                    token)
                .ConfigureAwait(false);
            using var serverClient = await acceptTask.ConfigureAwait(false);
            var expectedHash = SHA256.HashData(certificate.RawData);
            var exactCertificateObserved = false;
            using var serverTls = new SslStream(
                serverClient.GetStream(),
                leaveInnerStreamOpen: false);
            using var clientTls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, presented, _, _) =>
                {
                    if (presented is null) return false;
                    var presentedHash = SHA256.HashData(
                        presented.GetRawCertData());
                    exactCertificateObserved =
                        CryptographicOperations.FixedTimeEquals(
                            expectedHash,
                            presentedHash);
                    return exactCertificateObserved;
                });
            var protocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            var serverHandshake = serverTls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                token);
            var clientHandshake = clientTls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = protocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                token);
            await Task.WhenAll(serverHandshake, clientHandshake)
                .ConfigureAwait(false);
            if (!exactCertificateObserved ||
                !serverTls.IsAuthenticated ||
                !serverTls.IsServer ||
                !clientTls.IsAuthenticated ||
                clientTls.IsServer)
            {
                throw new AuthenticationException(
                    "Windows TLS did not activate the exact Sunshine server identity.");
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal static class SunshinePairedClientCertificates
{
    private const int MaximumStateBytes = 4 * 1024 * 1024;
    private const int MaximumCertificateCharacters = 32 * 1024;
    private const int MaximumNamedDevices = 256;

    internal static IReadOnlyList<byte[]> LoadEnabledCertificateHashes(
        string statePath)
    {
        var bytes = File.ReadAllBytes(statePath);
        if (bytes.Length > MaximumStateBytes)
        {
            throw new InvalidDataException(
                "Sunshine's paired-device state is unexpectedly large.");
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = RequireUniqueObject(document.RootElement, "Sunshine state");
        if (!root.TryGetProperty("root", out var innerElement))
        {
            throw new InvalidDataException(
                "Sunshine's paired-device state has no root object.");
        }
        var inner = RequireUniqueObject(innerElement, "Sunshine state root");
        if (!inner.TryGetProperty("named_devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Sunshine's paired-device state has no named_devices array.");
        }
        if (devices.GetArrayLength() > MaximumNamedDevices)
        {
            throw new InvalidDataException(
                "Sunshine's paired-device state contains an unreasonable number of named devices.");
        }

        var hashes = new List<byte[]>();
        foreach (var deviceElement in devices.EnumerateArray())
        {
            var device = RequireUniqueObject(
                deviceElement,
                "Sunshine named device");
            if (!device.TryGetProperty("enabled", out var enabled) ||
                !IsEnabled(enabled))
            {
                continue;
            }
            if (!device.TryGetProperty("cert", out var certificateElement) ||
                certificateElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "An enabled Sunshine device has no certificate.");
            }
            var pem = certificateElement.GetString();
            if (string.IsNullOrWhiteSpace(pem) ||
                pem.Length > MaximumCertificateCharacters)
            {
                throw new InvalidDataException(
                    "An enabled Sunshine device has an invalid certificate value.");
            }
            using var certificate = X509Certificate2.CreateFromPem(pem);
            hashes.Add(SHA256.HashData(certificate.RawData));
        }
        return hashes;
    }

    internal static bool IsEnabledClient(
        string statePath,
        X509Certificate certificate)
    {
        var presentedHash = SHA256.HashData(certificate.GetRawCertData());
        return LoadEnabledCertificateHashes(statePath).Any(allowed =>
            CryptographicOperations.FixedTimeEquals(allowed, presentedHash));
    }

    private static JsonElement RequireUniqueObject(
        JsonElement element,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} is not an object.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"{description} contains a duplicate property.");
            }
        }
        return element;
    }

    private static bool IsEnabled(JsonElement element) =>
        element.ValueKind == JsonValueKind.True ||
        (element.ValueKind == JsonValueKind.String &&
         string.Equals(
             element.GetString(),
             "true",
             StringComparison.OrdinalIgnoreCase)) ||
        (element.ValueKind == JsonValueKind.Number &&
         element.TryGetInt32(out var numeric) &&
         numeric == 1);
}

internal sealed class StreamBoundaryBridgeServer : IDisposable
{
    internal const string ProtocolIdentifier =
        "vita-moonlight-stream-boundary/1";
    internal const string PreparePath = "/v1/prepare";
    internal const string StartedPath = "/v1/started";
    internal const string HeartbeatPath = "/v1/heartbeat";
    internal const string StopPath = "/v1/stop";
    internal const int GenerationHexCharacters = 32;
    private const int MaximumConcurrentClients = 4;
    private const int MaximumRequestHeaderBytes = 4096;
    private static readonly TimeSpan PreAuthenticationTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout =
        TimeSpan.FromSeconds(60);
    private readonly SunshineStreamBridgeConfiguration configuration;
    private readonly X509Certificate2 serverCertificate;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly SemaphoreSlim clientGate = new(
        MaximumConcurrentClients,
        MaximumConcurrentClients);
    private readonly object clientTasksLock = new();
    private readonly HashSet<Task> clientTasks = [];
    private readonly Task acceptTask;
    private readonly Action listenerFaulted;
    private int listenerFaultSignaled;
    private bool disposed;

    internal static StreamBoundaryBridgeServer? CreateIfEnabled(
        Action? listenerFaulted = null)
    {
        var configuration = SunshineStreamBridgeConfiguration.LoadIfEnabled();
        return configuration is null
            ? null
            : new StreamBoundaryBridgeServer(configuration, listenerFaulted);
    }

    internal StreamBoundaryBridgeServer(
        SunshineStreamBridgeConfiguration configuration,
        Action? listenerFaulted = null)
    {
        this.configuration = configuration;
        this.listenerFaulted = listenerFaulted ?? (() => { });
        // Establish and verify the complete protected state container once.
        // Lease heartbeats thereafter use a narrow exact-file fast path.
        MachineStateSecurity.Secure();
        ReconcileDisplayStateBeforeTlsReadiness();
        _ = SunshinePairedClientCertificates.LoadEnabledCertificateHashes(
            configuration.StatePath);
        serverCertificate = SchannelServerCertificate.LoadFromPemFiles(
            configuration.CertificatePath,
            configuration.PrivateKeyPath);
        TcpListener? startedListener = null;
        try
        {
            SchannelServerCertificate.VerifyUsableForServerAuthentication(
                serverCertificate);
            var executable = Environment.ProcessPath ?? Path.Combine(
                AppContext.BaseDirectory,
                "VitaMoonlight.Host.exe");
            ManagedStreamBridgeFirewall.RequireReady(
                configuration.Port,
                executable);
            startedListener = CreateStartedListener(configuration.Port);
            listener = startedListener;
            acceptTask = Task.Run(
                () => AcceptLoopAsync(cancellation.Token));
        }
        catch
        {
            startedListener?.Stop();
            serverCertificate.Dispose();
            throw;
        }
    }

    private static void ReconcileDisplayStateBeforeTlsReadiness()
    {
        // A protected lease can safely survive an agent restart because it is
        // bound to the exact paired-client certificate, random generation,
        // and durable display-recovery CapturedAt. Preserve a live lease so a
        // reconnecting Vita can resume heartbeats. Anything else must be
        // restored before external Sunshine state or TLS credentials are read:
        // a malformed credential must never strand a virtual-only desktop by
        // making every scheduled-agent restart fail before recovery.
        var durableCapturedAt = ReadDurableRecoveryCapturedAt();
        var assessment = StreamBoundaryLeaseJournal.Assess(
            durableCapturedAt);
        if (!assessment.AuthorizesActiveHandoff)
        {
            if (durableCapturedAt is { } expected)
            {
                _ = new SessionManager().RestoreIfPending(expected);
            }
            _ = new SessionManager().RecoverToIdle();
            if (assessment.CanDiscardLeaseAfterRecovery)
            {
                _ = StreamBoundaryLeaseJournal.RemoveAssessed(
                    assessment);
            }
        }
        if (!assessment.AuthorizesActiveHandoff &&
            File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                "An orphaned Vita display handoff could not be restored before bridge startup.");
        }
    }

    private static TcpListener CreateStartedListener(int port)
    {
        TcpListener? candidate = null;
        try
        {
            candidate = new TcpListener(IPAddress.IPv6Any, port);
            candidate.Server.DualMode = true;
            candidate.Start(backlog: 4);
            return candidate;
        }
        catch (SocketException error) when (error.SocketErrorCode is
            SocketError.AddressFamilyNotSupported or
            SocketError.ProtocolNotSupported or
            SocketError.OperationNotSupported)
        {
            candidate?.Stop();
            candidate = new TcpListener(IPAddress.Any, port);
            try
            {
                candidate.Start(backlog: 4);
                return candidate;
            }
            catch
            {
                candidate.Stop();
                throw;
            }
        }
        catch
        {
            candidate?.Stop();
            throw;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await clientGate.WaitAsync(token).ConfigureAwait(false);
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    clientGate.Release();
                    return;
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested)
                {
                    clientGate.Release();
                    return;
                }
                catch
                {
                    clientGate.Release();
                    throw;
                }

                var task = HandleAcceptedClientAsync(client!, token);
                lock (clientTasksLock) clientTasks.Add(task);
                _ = task.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        lock (clientTasksLock) clientTasks.Remove(completed);
                        clientGate.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception error) when (
            token.IsCancellationRequested &&
            error is OperationCanceledException or
                ObjectDisposedException or
                SocketException)
        {
            return;
        }
        catch
        {
            SignalListenerFault();
            throw;
        }
    }

    private async Task HandleAcceptedClientAsync(
        TcpClient client,
        CancellationToken token)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, token).ConfigureAwait(false);
            }
            catch (Exception error) when (
                IsConnectionLocalFailure(error) ||
                IsOperationalRequestFailure(error))
            {
                // Authentication, malformed input, client disconnects, and
                // bounded host-operation failures are connection-local. They
                // never terminate the listener or emit credentials.
            }
            catch
            {
                SignalListenerFault();
            }
        }
    }

    private void SignalListenerFault()
    {
        if (Interlocked.Exchange(ref listenerFaultSignaled, 1) != 0) return;
        listenerFaulted();
        cancellation.Cancel();
        listener.Stop();
    }

    private static bool IsConnectionLocalFailure(Exception error) =>
        error is SocketException or
            AuthenticationException or
            OperationCanceledException;

    internal static bool IsOperationalRequestFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is AggregateException aggregate)
        {
            var inner = aggregate.Flatten().InnerExceptions;
            return inner.Count > 0 &&
                inner.All(IsOperationalRequestFailure);
        }
        return error is
            TimeoutException or
            OperationCanceledException or
            HostRestartRequiredException or
            IOException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            System.Security.SecurityException or
            CryptographicException or
            JsonException or
            InvalidDataException or
            InvalidOperationException or
            NotSupportedException;
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken serverToken)
    {
        using var preAuthentication = CancellationTokenSource.CreateLinkedTokenSource(
            serverToken);
        preAuthentication.CancelAfter(PreAuthenticationTimeout);
        using var tls = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            ValidateClientCertificate);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            preAuthentication.Token).ConfigureAwait(false);
        if (tls.RemoteCertificate is null)
        {
            throw new AuthenticationException(
                "A paired client certificate is required.");
        }
        var ownerHash = SHA256.HashData(
            tls.RemoteCertificate.GetRawCertData());
        var request = await ReadRequestAsync(tls, preAuthentication.Token)
            .ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            serverToken);
        operation.CancelAfter(OperationTimeout);
        await requestGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            await DispatchAsync(tls, request, ownerHash, operation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            requestGate.Release();
        }
    }

    private bool ValidateClientCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        _ = sender;
        _ = chain;
        _ = errors;
        if (certificate is null) return false;
        try
        {
            return SunshinePairedClientCertificates.IsEnabledClient(
                configuration.StatePath,
                certificate);
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                CryptographicException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    private async Task DispatchAsync(
        SslStream tls,
        StreamBoundaryRequest request,
        byte[] ownerHash,
        CancellationToken token)
    {
        try
        {
            await DispatchCoreAsync(
                tls,
                request,
                ownerHash,
                token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsOperationalRequestFailure(error))
        {
            using var response = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));
            await WriteResponseAsync(
                    tls,
                    503,
                    "host display operation unavailable",
                    response.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task DispatchCoreAsync(
        SslStream tls,
        StreamBoundaryRequest request,
        byte[] ownerHash,
        CancellationToken token)
    {
        var durableCapturedAt = ReadDurableRecoveryCapturedAt();
        var assessment = StreamBoundaryLeaseJournal.Assess(
            durableCapturedAt);

        if (request.Path.Equals(PreparePath, StringComparison.Ordinal))
        {
            if (!request.TryGetInt("width", out var width) ||
                !request.TryGetInt("height", out var height) ||
                !request.TryGetInt("fps", out var fps) ||
                request.Parameters.Count != 3)
            {
                await WriteResponseAsync(
                    tls,
                    400,
                    "invalid prepare request",
                    token).ConfigureAwait(false);
                return;
            }
            try
            {
                _ = VitaDisplayModes.RequireSupportedStreamMode(
                    width,
                    height,
                    fps);
            }
            catch (ArgumentException)
            {
                await WriteResponseAsync(
                    tls,
                    422,
                    "unsupported stream mode",
                    token).ConfigureAwait(false);
                return;
            }

            if (assessment.Disposition ==
                StreamBoundaryLeaseDisposition.StaleLease)
            {
                _ = StreamBoundaryLeaseJournal.RemoveAssessed(assessment);
                assessment = StreamBoundaryLeaseJournal.Assess(null);
            }
            if (assessment.RequiresRecovery)
            {
                if (assessment.DurableRecoveryCapturedAtUtc is
                    { } orphanCapturedAt)
                {
                    _ = new SessionManager().RestoreIfPending(
                        orphanCapturedAt);
                }
                _ = new SessionManager().RecoverToIdle();
                if (File.Exists(HostStatePaths.RecoveryFile))
                {
                    throw new InvalidOperationException(
                        "The orphaned display handoff was retained.");
                }
                if (assessment.CanDiscardLeaseAfterRecovery)
                {
                    _ = StreamBoundaryLeaseJournal.RemoveAssessed(
                        assessment);
                }
                assessment = StreamBoundaryLeaseJournal.Assess(null);
            }

            if (assessment.AuthorizesActiveHandoff &&
                assessment.Lease is { } current)
            {
                if (StreamBoundaryLeaseJournal.MatchesAuthority(
                        current,
                        current.Generation,
                        ownerHash,
                        current.RecoveryCapturedAtUtc) &&
                    current.Width == width &&
                    current.Height == height &&
                    current.Fps == fps)
                {
                    await WriteResponseAsync(
                        tls,
                        200,
                        $"prepared generation={current.Generation}",
                        token).ConfigureAwait(false);
                }
                else
                {
                    await WriteResponseAsync(
                        tls,
                        409,
                        "another generation is active",
                        token).ConfigureAwait(false);
                }
                return;
            }

            StreamBoundaryLeaseState? created = null;
            _ = new SessionManager().Start(
                width,
                height,
                fps,
                started => created =
                    StreamBoundaryLeaseJournal.PublishPrepared(
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                        .ToLowerInvariant(),
                    ownerHash,
                    width,
                    height,
                    fps,
                    started.RecoveryCapturedAt));
            var published = created ?? throw new InvalidOperationException(
                "The authenticated Prepared lease was not published before the display transaction ended.");
            try
            {
                await WriteResponseAsync(
                    tls,
                    200,
                    $"prepared generation={published.Generation}",
                    token).ConfigureAwait(false);
            }
            catch
            {
                RollBackUndeliveredGeneration(published, ownerHash);
                throw;
            }
            return;
        }

        if (request.Path.Equals(StartedPath, StringComparison.Ordinal) ||
            request.Path.Equals(HeartbeatPath, StringComparison.Ordinal))
        {
            var isStarted = request.Path.Equals(
                StartedPath,
                StringComparison.Ordinal);
            if (!TryGetGeneration(request, out var generation))
            {
                await WriteResponseAsync(
                    tls,
                    400,
                    isStarted
                        ? "invalid started request"
                        : "invalid heartbeat request",
                    token).ConfigureAwait(false);
                return;
            }
            if (!assessment.AuthorizesActiveHandoff ||
                assessment.Lease is not { } current ||
                !StreamBoundaryLeaseJournal.MatchesAuthority(
                    current,
                    generation,
                    ownerHash,
                    current.RecoveryCapturedAtUtc))
            {
                await WriteResponseAsync(
                    tls,
                    409,
                    "generation does not own a live display handoff",
                    token).ConfigureAwait(false);
                return;
            }
            if (isStarted)
            {
                _ = StreamBoundaryLeaseJournal.MarkStarted(
                    generation,
                    ownerHash,
                    current.RecoveryCapturedAtUtc);
            }
            else
            {
                _ = StreamBoundaryLeaseJournal.RenewHeartbeat(
                    generation,
                    ownerHash,
                    current.RecoveryCapturedAtUtc);
            }
            await WriteResponseAsync(
                tls,
                200,
                isStarted ? "started" : "heartbeat",
                token).ConfigureAwait(false);
            return;
        }

        if (request.Path.Equals(StopPath, StringComparison.Ordinal))
        {
            if (!TryGetGeneration(request, out var generation))
            {
                await WriteResponseAsync(
                    tls,
                    400,
                    "invalid stop request",
                    token).ConfigureAwait(false);
                return;
            }

            if (durableCapturedAt is null)
            {
                if (assessment.Lease is { } stale &&
                    !StreamBoundaryLeaseJournal.MatchesAuthority(
                        stale,
                        generation,
                        ownerHash,
                        stale.RecoveryCapturedAtUtc))
                {
                    await WriteResponseAsync(
                        tls,
                        409,
                        "generation does not own the stale handoff lease",
                        token).ConfigureAwait(false);
                    return;
                }
                if (assessment.CanDiscardLeaseAfterRecovery)
                {
                    _ = StreamBoundaryLeaseJournal.RemoveAssessed(
                        assessment);
                }
                await WriteResponseAsync(
                    tls,
                    200,
                    "stopped",
                    token).ConfigureAwait(false);
                return;
            }

            if (assessment.Lease is not { } currentLease ||
                currentLease.RecoveryCapturedAtUtc.UtcTicks !=
                    durableCapturedAt.Value.UtcTicks ||
                !StreamBoundaryLeaseJournal.MatchesAuthority(
                    currentLease,
                    generation,
                    ownerHash,
                    durableCapturedAt.Value))
            {
                await WriteResponseAsync(
                    tls,
                    409,
                    "generation does not own the active handoff",
                    token).ConfigureAwait(false);
                return;
            }
            _ = new SessionManager().RestoreIfPending(
                currentLease.RecoveryCapturedAtUtc);
            StreamBoundaryLeaseJournal.RemoveExact(
                generation,
                ownerHash,
                currentLease.RecoveryCapturedAtUtc);
            await WriteResponseAsync(
                tls,
                200,
                "stopped",
                token).ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(tls, 404, "unknown endpoint", token)
            .ConfigureAwait(false);
    }

    private static bool TryGetGeneration(
        StreamBoundaryRequest request,
        out string generation)
    {
        generation = string.Empty;
        if (!request.Parameters.TryGetValue(
                "generation",
                out var candidate) ||
            request.Parameters.Count != 1 ||
            !IsGeneration(candidate))
        {
            return false;
        }
        generation = candidate;
        return true;
    }

    private static async Task<StreamBoundaryRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken token)
    {
        var buffer = new byte[MaximumRequestHeaderBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(count, buffer.Length - count),
                token).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Incomplete request.");
            count += read;
            var headerEnd = FindHeaderEnd(buffer.AsSpan(0, count));
            if (headerEnd >= 0)
            {
                return StreamBoundaryRequest.Parse(
                    Encoding.ASCII.GetString(buffer, 0, headerEnd));
            }
        }
        throw new InvalidDataException("Request header is too large.");
    }

    private static DateTimeOffset? ReadDurableRecoveryCapturedAt()
    {
        if (!File.Exists(HostStatePaths.RecoveryFile)) return null;
        try
        {
            return new DisplayTopologyService().LoadRecovery().CapturedAt;
        }
        catch (FileNotFoundException)
        {
            // Atomic observer cleanup won the race after File.Exists().
            return null;
        }
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 3; index < bytes.Length; index++)
        {
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' &&
                bytes[index - 1] == '\r' && bytes[index] == '\n')
            {
                return index + 1;
            }
        }
        return -1;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int status,
        string message,
        CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            409 => "Conflict",
            422 => "Unprocessable Content",
            503 => "Service Unavailable",
            _ => "Error",
        };
        var body = Encoding.ASCII.GetBytes(
            $"{ProtocolIdentifier} {message}\n");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/plain; charset=us-ascii\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void RollBackUndeliveredGeneration(
        StreamBoundaryLeaseState generation,
        byte[] ownerHash)
    {
        try
        {
            _ = new SessionManager().RestoreIfPending(
                generation.RecoveryCapturedAtUtc);
            StreamBoundaryLeaseJournal.RemoveExact(
                generation.Generation,
                ownerHash,
                generation.RecoveryCapturedAtUtc);
        }
        catch
        {
            // Preserve both the generation and durable recovery record. The
            // rescue agent can still restore this exact interrupted handoff.
        }
    }

    internal static bool IsGeneration(string? value) =>
        value is { Length: GenerationHexCharacters } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        listener.Stop();
        try
        {
            acceptTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Process shutdown retains the durable recovery record.
        }
        Task[] clients;
        lock (clientTasksLock) clients = [.. clientTasks];
        try
        {
            if (clients.Length > 0)
            {
                _ = Task.WaitAll(clients, TimeSpan.FromSeconds(2));
            }
        }
        catch (AggregateException)
        {
            // Process shutdown deliberately preserves the durable lease and
            // exact recovery record for reconnection or expiry recovery.
        }
        serverCertificate.Dispose();
        cancellation.Dispose();
    }
}

internal sealed record StreamBoundaryRequest(
    string Path,
    IReadOnlyDictionary<string, string> Parameters)
{
    internal static StreamBoundaryRequest Parse(string header)
    {
        if (header.Any(character =>
                character is not ('\r' or '\n' or >= ' ' and <= '~')))
        {
            throw new InvalidDataException("Request contains invalid bytes.");
        }
        var lines = header.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 3 ||
            !lines[0].StartsWith("GET ", StringComparison.Ordinal) ||
            !lines[0].EndsWith(" HTTP/1.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only HTTP/1.1 GET is supported.");
        }
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                !line["Content-Length:".Length..].Trim().Equals(
                    "0",
                    StringComparison.Ordinal) ||
                line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Request bodies are not supported.");
            }
        }
        var target = lines[0][4..^9];
        if (target.Length == 0 || target.Length > 1024 ||
            target[0] != '/' || target.Contains('%') ||
            target.Contains('#'))
        {
            throw new InvalidDataException("Request target is invalid.");
        }
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? string.Empty : target[(question + 1)..];
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length > 0)
        {
            foreach (var pair in query.Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals < 1 || equals == pair.Length - 1 ||
                    !parameters.TryAdd(pair[..equals], pair[(equals + 1)..]))
                {
                    throw new InvalidDataException(
                        "Request query contains a duplicate or invalid value.");
                }
            }
        }
        return new StreamBoundaryRequest(path, parameters);
    }

    internal bool TryGetInt(string name, out int value)
    {
        value = 0;
        return Parameters.TryGetValue(name, out var raw) &&
            raw.Length is > 0 and <= 5 &&
            int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }
}
