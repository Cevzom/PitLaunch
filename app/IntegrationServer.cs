using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PitLaunch;

/// <summary>
/// A deliberately small, current-user-only API for local control surfaces such as Stream Deck.
/// Frames are one UTF-8 JSON object per line. Nothing is exposed on TCP or to the local network.
/// </summary>
internal sealed class IntegrationServer : IDisposable
{
    public const string ProtocolName = "PitLaunch.Integration.v1";
    public const int ProtocolVersion = 1;
    public const string DefaultPipeName = ProtocolName;

    private const int MaxFrameCharacters = 64 * 1024;
    private const int MaxClients = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<Task<IntegrationStateSnapshot>> _getState;
    private readonly Func<Guid, Task<IntegrationActivationResult>> _activate;
    private readonly Func<Task<IntegrationActivationResult>> _toggle;
    private readonly Func<Task<IntegrationRestoreResult>> _restoreDisplays;
    private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _pipeName;
    private Task? _acceptTask;
    private int _nextClientId;
    private bool _disposed;

    public IntegrationServer(
        Func<Task<IntegrationStateSnapshot>> getState,
        Func<Guid, Task<IntegrationActivationResult>> activate,
        Func<Task<IntegrationActivationResult>> toggle,
        Func<Task<IntegrationRestoreResult>> restoreDisplays,
        string? pipeName = null)
    {
        _getState = getState;
        _activate = activate;
        _toggle = toggle;
        _restoreDisplays = restoreDisplays;
        _pipeName = ResolvePipeName(pipeName);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acceptTask is not null) return;
        _acceptTask = AcceptLoopAsync(_cancellation.Token);
        AppLog.Info($"Local integration pipe started ({_pipeName}).");
    }

    public void NotifyProfilesChanged() => Broadcast(new
    {
        protocol = ProtocolName,
        version = ProtocolVersion,
        @event = "profiles.changed"
    });

    public void NotifyStatusChanged() => Broadcast(new
    {
        protocol = ProtocolName,
        version = ProtocolVersion,
        @event = "status.changed"
    });

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // CurrentUserOnly is the important security boundary: another signed-in Windows
                // user cannot issue display/audio changes through this local convenience API.
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                int id = Interlocked.Increment(ref _nextClientId);
                ClientConnection connection = new(id, pipe);
                pipe = null; // ownership moved to the client task
                _clients[id] = connection;
                Task clientTask = HandleClientAsync(connection, cancellationToken);
                _clientTasks[id] = clientTask;
                _ = clientTask.ContinueWith(
                    _ => RemoveClientTask(id),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                AppLog.Error("PitLaunch integration pipe failed: " + ex.Message);
                try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleClientAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using StreamReader reader = new(
                connection.Pipe,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested && connection.Pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaxFrameCharacters)
                {
                    await connection.WriteAsync(Error(string.Empty, "INVALID_REQUEST", "The request was too large."), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                object response = await HandleRequestAsync(line).ConfigureAwait(false);
                await connection.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // A Stream Deck restart closes the pipe abruptly; that is normal lifecycle noise.
        }
        catch (Exception ex)
        {
            AppLog.Error("PitLaunch integration client failed: " + ex.Message);
        }
        finally
        {
            _clients.TryRemove(connection.Id, out _);
            connection.Dispose();
        }
    }

    private async Task<object> HandleRequestAsync(string line)
    {
        string id = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(id, "INVALID_REQUEST", "The request must be a JSON object.");
            }

            if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(id) ||
                !root.TryGetProperty("protocol", out JsonElement protocol) ||
                protocol.ValueKind != JsonValueKind.String ||
                protocol.GetString() != ProtocolName ||
                !root.TryGetProperty("version", out JsonElement version) ||
                !version.TryGetInt32(out int protocolVersion) ||
                !root.TryGetProperty("method", out JsonElement methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return Error(id, "INVALID_REQUEST", "protocol, version, id, and method are required.");
            }

            if (protocolVersion != ProtocolVersion)
            {
                return Error(id, "UNSUPPORTED_VERSION", $"PitLaunch supports integration protocol version {ProtocolVersion}.");
            }

            string method = methodElement.GetString() ?? string.Empty;
            return method switch
            {
                "profiles.list" => await ListProfilesAsync(id).ConfigureAwait(false),
                "status.get" => await GetStatusAsync(id).ConfigureAwait(false),
                "profile.activate" => await ActivateAsync(id, root).ConfigureAwait(false),
                "profile.toggle" => await ToggleAsync(id).ConfigureAwait(false),
                "displays.restore" => await RestoreDisplaysAsync(id).ConfigureAwait(false),
                _ => Error(id, "UNSUPPORTED_METHOD", $"Unknown integration method '{method}'.")
            };
        }
        catch (JsonException)
        {
            return Error(id, "INVALID_REQUEST", "The request was not valid JSON.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Integration request failed: " + ex);
            return Error(id, "INTERNAL_ERROR", "PitLaunch could not complete the integration request.");
        }
    }

    private async Task<object> ListProfilesAsync(string id)
    {
        IntegrationStateSnapshot state = await _getState().ConfigureAwait(false);
        return Success(id, new
        {
            profiles = state.Profiles.Select(profile => new
            {
                id = profile.Id.ToString("D").ToLowerInvariant(),
                profile.Name,
                profile.Kind,
                isActive = profile.Id == state.ActiveProfileId
            }).ToArray()
        });
    }

    private async Task<object> GetStatusAsync(string id)
    {
        IntegrationStateSnapshot state = await _getState().ConfigureAwait(false);
        return Success(id, new
        {
            activeProfileId = state.ActiveProfileId?.ToString("D").ToLowerInvariant(),
            appVersion = AppInfo.Version,
            state.Busy
        });
    }

    private async Task<object> ActivateAsync(string id, JsonElement root)
    {
        if (!TryReadProfileId(root, out Guid profileId))
        {
            return Error(id, "INVALID_REQUEST", "params.profileId must be a valid setup id.");
        }

        IntegrationStateSnapshot state = await _getState().ConfigureAwait(false);
        if (state.Busy) return Error(id, "BUSY", "PitLaunch is already changing a setup.");
        if (!state.Profiles.Any(profile => profile.Id == profileId))
        {
            return Error(id, "PROFILE_NOT_FOUND", "That setup no longer exists in PitLaunch.");
        }

        IntegrationActivationResult activated = await _activate(profileId).ConfigureAwait(false);
        return ActivationResponse(id, activated);
    }

    private async Task<object> ToggleAsync(string id)
    {
        IntegrationStateSnapshot state = await _getState().ConfigureAwait(false);
        if (state.Busy) return Error(id, "BUSY", "PitLaunch is already changing a setup.");
        if (state.Profiles.Count == 0)
        {
            return Error(id, "PROFILE_NOT_FOUND", "Create a Desk and Sim Racing setup in PitLaunch first.");
        }

        IntegrationActivationResult activated = await _toggle().ConfigureAwait(false);
        return ActivationResponse(id, activated);
    }

    private async Task<object> RestoreDisplaysAsync(string id)
    {
        IntegrationStateSnapshot state = await _getState().ConfigureAwait(false);
        if (state.Busy) return Error(id, "BUSY", "PitLaunch is already changing a setup.");
        IntegrationRestoreResult restored = await _restoreDisplays().ConfigureAwait(false);
        return Success(id, new { restored.Restored, restored.Message });
    }

    private static object ActivationResponse(string id, IntegrationActivationResult activated)
    {
        if (activated.ProfileId is null)
        {
            return Error(id, "PROFILE_NOT_FOUND", activated.Message);
        }

        return Success(id, new
        {
            profileId = activated.ProfileId.Value.ToString("D").ToLowerInvariant(),
            activated.ProfileName,
            complete = activated.Complete,
            activated.Message
        });
    }

    private static bool TryReadProfileId(JsonElement root, out Guid profileId)
    {
        profileId = Guid.Empty;
        return root.TryGetProperty("params", out JsonElement parameters) &&
               parameters.ValueKind == JsonValueKind.Object &&
               parameters.TryGetProperty("profileId", out JsonElement id) &&
               id.ValueKind == JsonValueKind.String &&
               Guid.TryParse(id.GetString(), out profileId) &&
               profileId != Guid.Empty;
    }

    private static object Success(string id, object result) => new
    {
        protocol = ProtocolName,
        version = ProtocolVersion,
        id,
        ok = true,
        result
    };

    private static object Error(string id, string code, string message) => new
    {
        protocol = ProtocolName,
        version = ProtocolVersion,
        id,
        ok = false,
        error = new { code, message }
    };

    private void Broadcast(object notification)
    {
        if (_disposed) return;
        foreach (ClientConnection connection in _clients.Values)
        {
            _ = BroadcastToClientAsync(connection, notification);
        }
    }

    private static async Task BroadcastToClientAsync(ClientConnection connection, object notification)
    {
        try { await connection.WriteAsync(notification, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }

    private void RemoveClientTask(int id) => _clientTasks.TryRemove(id, out Task? _);

    private static string ResolvePipeName(string? explicitName)
    {
        string? configured = explicitName;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("PITLAUNCH_INTEGRATION_PIPE");
        }

        configured = string.IsNullOrWhiteSpace(configured) ? DefaultPipeName : configured.Trim();
        if (configured.Length > 200 || configured.Contains('\\') || configured.Contains('/'))
        {
            AppLog.Write(OperationSeverity.Warning, "Ignored an invalid PITLAUNCH_INTEGRATION_PIPE value.");
            return DefaultPipeName;
        }
        return configured;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        foreach (ClientConnection client in _clients.Values) client.Dispose();
        _clients.Clear();
        try { _acceptTask?.Wait(1500); } catch { }
        try { Task.WaitAll(_clientTasks.Values.ToArray(), 1500); } catch { }
        _clientTasks.Clear();
        _cancellation.Dispose();
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private int _disposed;

        public int Id { get; }
        public NamedPipeServerStream Pipe { get; }

        public ClientConnection(int id, NamedPipeServerStream pipe)
        {
            Id = id;
            Pipe = pipe;
        }

        public async Task WriteAsync(object frame, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(frame, JsonOptions) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await Pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Pipe.Dispose(); } catch { }
            _writeGate.Dispose();
        }
    }
}

internal sealed record IntegrationProfileSnapshot(Guid Id, string Name, string Kind);

internal sealed record IntegrationStateSnapshot(
    IReadOnlyList<IntegrationProfileSnapshot> Profiles,
    Guid? ActiveProfileId,
    bool Busy);

internal sealed record IntegrationActivationResult(
    Guid? ProfileId,
    string? ProfileName,
    bool Complete,
    string Message);

internal sealed record IntegrationRestoreResult(bool Restored, string Message);
