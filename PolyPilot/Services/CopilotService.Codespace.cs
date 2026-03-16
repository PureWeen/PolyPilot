using System.Collections.Concurrent;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using PolyPilot.Models;

using ChatMessage = PolyPilot.Models.ChatMessage;

namespace PolyPilot.Services;

/// <summary>
/// Codespace lifecycle: group creation, health checks, tunnel management, reconnection.
/// </summary>
public partial class CopilotService : IAsyncDisposable
{
    /// Per-group reconnect locks to prevent concurrent reconnect attempts from leaking tunnel processes.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reconnectLocks = new();

    /// <summary>
    /// Creates a CopilotClient that connects to a remote copilot --headless server (e.g., in a codespace via port-forward tunnel).
    /// </summary>
    private static CopilotClient CreateRemoteClient(string host, int port)
    {
        var options = new CopilotClientOptions
        {
            CliPath = null,
            UseStdio = false,
            AutoStart = false,
            CliUrl = $"http://{host}:{port}"
        };
        return new CopilotClient(options);
    }

    /// <summary>
    /// Returns the CopilotClient to use for a session in the given group.
    /// For codespace groups, returns the dedicated tunnel client.
    /// Throws <see cref="InvalidOperationException"/> if the codespace is not connected yet.
    /// </summary>
    private CopilotClient GetClientForGroup(string? groupId)
    {
        if (groupId != null)
        {
            // Use snapshot for thread safety — may be called after an await continuation
            var group = SnapshotGroups().FirstOrDefault(g => g.Id == groupId);
            if (group?.IsCodespace == true)
            {
                if (_codespaceClients.TryGetValue(groupId, out var csClient))
                    return csClient;
                throw new InvalidOperationException($"Codespace '{group.Name}' is not connected. Wait for the connection to be re-established or check the codespace status.");
            }
        }
        if (!IsInitialized || _client == null)
            throw new InvalidOperationException("Service not initialized. Call InitializeAsync first.");
        return _client;
    }

    /// <summary>
    /// Creates a new group backed by a GitHub Codespace.
    /// Starts copilot --headless in the codespace, opens a port-forward tunnel, and connects a CopilotClient.
    /// <paramref name="onProgress"/> receives status messages during connection (for UI feedback).
    /// </summary>
    public async Task<SessionGroup> CreateCodespaceGroupAsync(string codespaceName, string repository, int remotePort = 4321, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var groupName = !string.IsNullOrEmpty(repository) ? repository.Split('/').Last() : codespaceName;
        var svc = _codespaceService;

        // If a group for this codespace already exists, reuse its ID (sessions are linked to it) and
        // dispose old tunnel/client to avoid accumulating orphaned port-forward processes.
        var existingGroup = Organization.Groups.FirstOrDefault(g => g.CodespaceName == codespaceName);
        if (existingGroup != null)
        {
            if (_tunnelHandles.TryRemove(existingGroup.Id, out var oldTunnel))
                _ = Task.Run(async () => { try { await oldTunnel.DisposeAsync(); } catch { } });
            if (_codespaceClients.TryRemove(existingGroup.Id, out var oldClient))
                _ = Task.Run(async () => { try { await oldClient.DisposeAsync(); } catch { } });
        }

        CodespaceService.TunnelHandle? tunnel = null;
        bool sshAvailable = false;

        // Strategy 1: SSH tunnel — combines SSH + port forwarding + copilot start in one process
        onProgress?.Invoke("Connecting via SSH tunnel...");
        Debug($"Attempting SSH tunnel to codespace '{codespaceName}'...");
        try
        {
            var sshTunnel = await svc.OpenSshTunnelAsync(codespaceName, remotePort, connectTimeoutSeconds: 45);
            if (sshTunnel != null)
            {
                tunnel = sshTunnel;
                sshAvailable = true;
                Debug($"SSH tunnel established for '{codespaceName}' on local port {tunnel.LocalPort}");
            }
            else
            {
                // SSH unavailable — fall back to strategy 2
                tunnel = null;
            }
        }
        catch (TimeoutException ex)
        {
            // SSH worked but copilot didn't start — still no SSH available for fallback
            Debug($"SSH tunnel timeout for '{codespaceName}': {ex.Message}");
            tunnel = null;
            sshAvailable = true; // SSH itself works, just copilot didn't start
        }

        // Strategy 2: Separate SSH (for copilot start) + gh cs ports forward (for tunnel)
        // Note: Without SSH, there is no way to start copilot remotely. The port-forward
        // tunnel opens but will find nothing listening — the group enters WaitingForCopilot
        // and eventually SetupRequired. This path exists as a graceful degradation, not a
        // viable alternative to SSH.
        if (tunnel == null)
        {
            if (!sshAvailable)
            {
                // Try starting copilot via separate SSH invocations (also requires SSH)
                onProgress?.Invoke("SSH tunnel unavailable. Trying port-forward...");
                Debug($"SSH unavailable for '{codespaceName}', trying StartCopilotHeadlessAsync + ports forward");
                sshAvailable = await svc.StartCopilotHeadlessAsync(codespaceName, remotePort);
            }

            int tunnelTimeoutSeconds;
            if (!sshAvailable)
            {
                // No SSH means copilot can't be started remotely — quick probe only
                onProgress?.Invoke("No SSH — opening port-forward tunnel...");
                Debug($"SSH unavailable for '{codespaceName}' — opening tunnel (copilot unlikely to be running).");
                tunnelTimeoutSeconds = 15; // Quick probe — don't block user for 90s
            }
            else
            {
                onProgress?.Invoke("Waiting for copilot to start...");
                await Task.Delay(2000, cancellationToken);
                tunnelTimeoutSeconds = 30;
            }

            onProgress?.Invoke("Opening port-forward tunnel...");
            Debug($"Opening gh cs ports forward tunnel to '{codespaceName}'...");
            bool copilotReady;
            try
            {
                (tunnel, copilotReady) = await svc.OpenTunnelAsync(codespaceName, remotePort,
                    connectTimeoutSeconds: tunnelTimeoutSeconds, requireCopilot: false);
            }
            catch (Exception ex)
            {
                // Port-forward failed — codespace running but no SSH and tunnel can't connect.
                // Create group in SetupRequired state so the user sees it in sidebar with guidance.
                Debug($"Port-forward tunnel failed for '{codespaceName}': {ex.Message}");
                var setupGroup = existingGroup ?? new SessionGroup
                {
                    Name = groupName,
                    CodespaceName = codespaceName,
                    CodespaceRepository = repository,
                    CodespacePort = remotePort,
                };
                setupGroup.Name = groupName;
                setupGroup.CodespacePort = remotePort;
                setupGroup.SshAvailable = false;
                setupGroup.ConnectionState = CodespaceConnectionState.SetupRequired;

                if (existingGroup == null)
                    AddGroup(setupGroup);
                InvalidateOrganizedSessionsCache();
                SaveOrganization();
                StartCodespaceHealthCheck();
                OnStateChanged?.Invoke();

                // Open the codespace in the browser so the user can run the setup commands
#if MACCATALYST || IOS || ANDROID || WINDOWS
                try { await Launcher.Default.OpenAsync(new Uri($"https://{codespaceName}.github.dev/")); }
                catch { }
#endif

                // Check dotfiles config in background (cached) for UI guidance
                _ = CheckDotfilesStatusAsync();

                Debug($"Codespace group '{setupGroup.Name}' created in SetupRequired state — opened in browser for setup");
                return setupGroup;
            }

            if (!copilotReady)
            {
                // Tunnel works but copilot not running — create group in WaitingForCopilot state
                var waitGroup = existingGroup ?? new SessionGroup
                {
                    Name = groupName,
                    CodespaceName = codespaceName,
                    CodespaceRepository = repository,
                    CodespacePort = remotePort,
                };
                waitGroup.Name = groupName;
                waitGroup.CodespacePort = remotePort;
                waitGroup.SshAvailable = sshAvailable;
                waitGroup.ConnectionState = CodespaceConnectionState.WaitingForCopilot;

                _tunnelHandles[waitGroup.Id] = tunnel;
                if (existingGroup == null)
                    AddGroup(waitGroup);
                InvalidateOrganizedSessionsCache();
                SaveOrganization();
                StartCodespaceHealthCheck();
                OnStateChanged?.Invoke();
                Debug($"Codespace group '{waitGroup.Name}' created in WaitingForCopilot state — tunnel alive, copilot not on port {remotePort}");
                return waitGroup;
            }
        }

        onProgress?.Invoke("Connecting...");
        var group = existingGroup ?? new SessionGroup
        {
            Name = groupName,
            CodespaceName = codespaceName,
            CodespaceRepository = repository,
            CodespacePort = remotePort,
        };
        group.Name = groupName;
        group.CodespacePort = remotePort;
        group.SshAvailable = sshAvailable;

        _tunnelHandles[group.Id] = tunnel;

        var client = CreateRemoteClient("127.0.0.1", tunnel.LocalPort);
        try
        {
            await client.StartAsync(cancellationToken);
        }
        catch (Exception)
        {
            try { await client.DisposeAsync(); } catch { }
            if (tunnel != null) await tunnel.DisposeAsync();
            _tunnelHandles.TryRemove(group.Id, out _);

            // Copilot isn't running — create/update group in WaitingForCopilot state
            // so the health check keeps retrying and the user sees the group in sidebar.
            group.ConnectionState = CodespaceConnectionState.WaitingForCopilot;
            if (existingGroup == null)
                AddGroup(group);
            InvalidateOrganizedSessionsCache();
            SaveOrganization();
            StartCodespaceHealthCheck();
            OnStateChanged?.Invoke();
            Debug($"Codespace group '{group.Name}' created in WaitingForCopilot state — copilot not running on port {remotePort}");
            return group;
        }

        _codespaceClients[group.Id] = client;
        group.ConnectionState = CodespaceConnectionState.Connected;
        if (existingGroup == null)
            AddGroup(group);
        InvalidateOrganizedSessionsCache();
        SaveOrganization();
        StartCodespaceHealthCheck();
        OnStateChanged?.Invoke();
        Debug($"{(existingGroup != null ? "Reconnected" : "Created")} codespace group '{group.Name}' ({codespaceName} via {(tunnel.IsSshTunnel ? "SSH" : "port-forward")} tunnel port {tunnel.LocalPort})");
        return group;
    }

    /// <summary>
    /// Creates a codespace group in CodespaceStopped state and kicks off StartAndReconnectCodespaceAsync in the background.
    /// The group appears immediately in the sidebar (disabled) while the codespace starts.
    /// </summary>
    public SessionGroup AddStoppedCodespaceGroup(string codespaceName, string repository)
    {
        var groupName = !string.IsNullOrEmpty(repository) ? repository.Split('/').Last() : codespaceName;
        var existing = Organization.Groups.FirstOrDefault(g => g.CodespaceName == codespaceName);
        if (existing != null)
        {
            existing.ConnectionState = CodespaceConnectionState.StartingCodespace;
            if (string.IsNullOrEmpty(existing.CodespaceRepository))
                existing.CodespaceRepository = repository;
            InvalidateOrganizedSessionsCache();
            OnStateChanged?.Invoke();
            return existing;
        }

        var group = new SessionGroup
        {
            Name = groupName,
            CodespaceName = codespaceName,
            CodespaceRepository = repository,
            ConnectionState = CodespaceConnectionState.StartingCodespace,
        };
        AddGroup(group);
        InvalidateOrganizedSessionsCache();
        SaveOrganization();
        OnStateChanged?.Invoke();
        Debug($"Added stopped codespace group '{groupName}' ({codespaceName}) — starting in background");

        var groupId = group.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAndReconnectCodespaceAsync(groupId);
            }
            catch (Exception ex)
            {
                Debug($"Background codespace start failed for '{codespaceName}': {ex.Message}");
                // Re-fetch group via snapshot — runs on background thread
                var g = SnapshotGroups().FirstOrDefault(g => g.Id == groupId);
                if (g == null) return; // Group was deleted, nothing to update
                // Preserve more specific states set by StartAndReconnectCodespaceAsync
                InvokeOnUI(() =>
                {
                    // Re-read on UI thread for safe mutation
                    var uiGroup = Organization.Groups.FirstOrDefault(gr => gr.Id == groupId);
                    if (uiGroup == null) return;
                    if (uiGroup.ConnectionState != CodespaceConnectionState.WaitingForCopilot
                        && uiGroup.ConnectionState != CodespaceConnectionState.SetupRequired)
                        uiGroup.ConnectionState = CodespaceConnectionState.CodespaceStopped;
                    OnStateChanged?.Invoke();
                });
            }
        });

        return group;
    }

    /// <summary>
    /// Starts the background codespace health-check loop if not already running.
    /// Checks every 30s whether codespace tunnels are alive and auto-reconnects.
    /// </summary>
    private readonly object _healthCheckLock = new();

    private void StartCodespaceHealthCheck()
    {
        lock (_healthCheckLock)
        {
            if (_codespaceHealthCts != null) return;
            if (!SnapshotGroups().Any(g => g.IsCodespace)) return;

            _codespaceHealthCts = new CancellationTokenSource();
            var ct = _codespaceHealthCts.Token;
            _codespaceHealthTask = Task.Run(async () =>
            {
                var svc = _codespaceService;
                // Run immediately on first start so disconnected groups reconnect without waiting 30s
                await RunCodespaceHealthCheckAsync(svc, ct);
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
                    await RunCodespaceHealthCheckAsync(svc, ct);
                }
            }, ct);
        }
    }

    private async Task StopCodespaceHealthCheckAsync()
    {
        Task? taskToWait;
        CancellationTokenSource? ctsToDispose;
        lock (_healthCheckLock)
        {
            _codespaceHealthCts?.Cancel();
            ctsToDispose = _codespaceHealthCts;
            taskToWait = _codespaceHealthTask;
            _codespaceHealthCts = null;
            _codespaceHealthTask = null;
        }
        if (taskToWait != null)
        {
            try { await taskToWait.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        ctsToDispose?.Dispose();
    }

    private const int MaxConsecutiveFailures = 5;

    private async Task RunCodespaceHealthCheckAsync(CodespaceService svc, CancellationToken ct)
    {
        // Thread-safe snapshot — health check runs on background thread
        var codespaceGroups = SnapshotGroups().Where(g => g.IsCodespace).ToList();
        if (codespaceGroups.Count == 0) return;

        foreach (var group in codespaceGroups)
        {
            if (ct.IsCancellationRequested) break;

            // Check if tunnel is still alive
            bool tunnelAlive = _tunnelHandles.TryGetValue(group.Id, out var tunnel) && tunnel.IsAlive;
            bool clientExists = _codespaceClients.ContainsKey(group.Id);

            // Even when tunnel + client exist, verify the remote end is still reachable
            // by probing the tunnel port. The CopilotClient can become stale if the remote
            // copilot process died while the SSH tunnel stayed open.
            // Note: A simple TCP connect suffices here because SSH tunnels refuse connections
            // outright when the remote end isn't listening (unlike `gh cs ports forward` which
            // accepts locally). See CodespaceService.IsCopilotListeningAsync for the more
            // sophisticated probe needed for non-SSH tunnels.
            if (tunnelAlive && clientExists && group.ConnectionState == CodespaceConnectionState.Connected)
            {
                bool portReachable = false;
                try
                {
                    using var probe = new System.Net.Sockets.TcpClient();
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(3));
                    await probe.ConnectAsync("127.0.0.1", tunnel.LocalPort, probeCts.Token);
                    portReachable = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { /* per-probe timeout — treat as unreachable */ }
                catch (OperationCanceledException) { throw; }
                catch { /* TCP connect failed — remote end not listening */ }

                if (portReachable)
                    continue;

                // Client is stale — remove it so the reconnect path kicks in
                Debug($"[HEALTH] Codespace '{group.Name}' tunnel alive but remote port {tunnel.LocalPort} not reachable — marking for reconnect");
                if (_codespaceClients.TryRemove(group.Id, out var staleClient))
                {
                    try { await staleClient.DisposeAsync(); } catch { }
                }
                clientExists = false;
            }

            // Skip groups where SSH is known-unavailable — user must configure dotfiles first
            if (group.ConnectionState == CodespaceConnectionState.SetupRequired && group.SshAvailable == false)
            {
                Debug($"[HEALTH] Skipping '{group.Name}' — SSH unavailable, waiting for user to configure dotfiles");
                continue;
            }

            // All group state mutations are marshaled to the UI thread via InvokeOnUI
            // to avoid data races with Blazor render cycles reading the same properties.
            InvokeOnUI(() =>
            {
                group.ReconnectAttempts++;
                group.LastReconnectAttempt = DateTime.UtcNow;
            });

            // For WaitingForCopilot: lightweight tunnel-only probe (no SSH, fast)
            if (group.ConnectionState == CodespaceConnectionState.WaitingForCopilot)
            {
                // If SSH is unavailable, there's no way to start copilot remotely — upgrade to SetupRequired
                if (group.SshAvailable == false)
                {
                    Debug($"[HEALTH] Upgrading '{group.Name}' from WaitingForCopilot to SetupRequired (no SSH)");
                    InvokeOnUI(() =>
                    {
                        group.ConnectionState = CodespaceConnectionState.SetupRequired;
                        group.SetupMessage = "SSH not available. Configure dotfiles to enable codespace integration.";
                        OnStateChanged?.Invoke();
                    });
                    continue;
                }

                Debug($"[HEALTH] Probing tunnel for '{group.Name}' (waiting for copilot, attempt {group.ReconnectAttempts})...");
                try
                {
                    await ReconnectCodespaceGroupAsync(group, svc, ct);
                    Debug($"[HEALTH] Codespace group '{group.Name}' connected (copilot appeared)");
                    await ResumeCodespaceSessionsAsync(group, ct);
                    InvokeOnUI(() =>
                    {
                        group.ConnectionState = CodespaceConnectionState.Connected;
                        group.ReconnectAttempts = 0;
                        group.SetupMessage = null;
                        OnStateChanged?.Invoke();
                    });
                    NotifyCodespaceConnected(group);
                }
                catch
                {
                    // Still not listening — stay in WaitingForCopilot
                    InvokeOnUI(() => OnStateChanged?.Invoke());
                }
                continue;
            }

            // For SetupRequired: try a full reconnect in case user installed SSHD
            if (group.ConnectionState == CodespaceConnectionState.SetupRequired)
            {
                Debug($"[HEALTH] Probing '{group.Name}' (setup required, attempt {group.ReconnectAttempts})...");
                try
                {
                    InvokeOnUI(() => group.SshAvailable = null); // Re-probe SSH availability
                    await ReconnectCodespaceGroupAsync(group, svc, ct);
                    Debug($"[HEALTH] Codespace group '{group.Name}' connected (setup completed by user)");
                    await ResumeCodespaceSessionsAsync(group, ct);
                    InvokeOnUI(() =>
                    {
                        group.ConnectionState = CodespaceConnectionState.Connected;
                        group.ReconnectAttempts = 0;
                        group.SetupMessage = null;
                        OnStateChanged?.Invoke();
                    });
                    NotifyCodespaceConnected(group);
                }
                catch
                {
                    // Still not reachable — stay in SetupRequired
                    InvokeOnUI(() =>
                    {
                        group.ConnectionState = CodespaceConnectionState.SetupRequired;
                        OnStateChanged?.Invoke();
                    });
                }
                continue;
            }

            // Tunnel or client is down — check codespace state
            Debug($"[HEALTH] Codespace group '{group.Name}' unhealthy (tunnel={tunnelAlive}, client={clientExists}, attempt {group.ReconnectAttempts})");
            InvokeOnUI(() =>
            {
                group.ConnectionState = CodespaceConnectionState.Reconnecting;
                OnStateChanged?.Invoke();
            });

            var state = await svc.GetCodespaceStateAsync(group.CodespaceName!);
            if (state != null && state != "Available")
            {
                Debug($"[HEALTH] Codespace '{group.CodespaceName}' is {state}");
                InvokeOnUI(() =>
                {
                    group.ConnectionState = CodespaceConnectionState.CodespaceStopped;
                    OnStateChanged?.Invoke();
                });
                continue;
            }

            // Codespace is available — try to reconnect
            try
            {
                await ReconnectCodespaceGroupAsync(group, svc, ct);
                Debug($"[HEALTH] Codespace group '{group.Name}' reconnected");
                await ResumeCodespaceSessionsAsync(group, ct);
                InvokeOnUI(() =>
                {
                    group.ConnectionState = CodespaceConnectionState.Connected;
                    group.ReconnectAttempts = 0;
                    group.SetupMessage = null;
                    OnStateChanged?.Invoke();
                });
                NotifyCodespaceConnected(group);
            }
            catch (Exception ex)
            {
                Debug($"[HEALTH] Failed to reconnect '{group.Name}': [{ex.GetType().Name}] {ex.Message}");
                InvokeOnUI(() =>
                {
                    // ReconnectCodespaceGroupAsync already sets WaitingForCopilot if appropriate
                    if (group.ConnectionState != CodespaceConnectionState.WaitingForCopilot)
                        group.ConnectionState = CodespaceConnectionState.Reconnecting;

                    // After too many consecutive failures, stop retrying automatically
                    if (group.ReconnectAttempts >= MaxConsecutiveFailures)
                    {
                        group.ConnectionState = CodespaceConnectionState.SetupRequired;
                        group.SetupMessage = $"Connection failed repeatedly: {ex.Message}. Check codespace compatibility.";
                        Debug($"[HEALTH] '{group.Name}' exceeded {MaxConsecutiveFailures} failures — moving to SetupRequired");
                    }
                    OnStateChanged?.Invoke();
                });
            }
        }
    }

    /// <summary>
    /// Notifies the UI that a codespace group has auto-connected (from health check).
    /// Adds a system message to the group's first session for visibility.
    /// </summary>
    private void NotifyCodespaceConnected(SessionGroup group)
    {
        // Snapshot for thread safety — this runs on health check background thread
        var firstSession = SnapshotSessionMetas().FirstOrDefault(m => m.GroupId == group.Id);
        if (firstSession != null && _sessions.TryGetValue(firstSession.SessionName, out var state))
        {
            InvokeOnUI(() =>
            {
                state.Info.History.Add(ChatMessage.SystemMessage($"✅ Codespace '{group.Name}' connected."));
                OnStateChanged?.Invoke();
            });
        }
    }

    /// <summary>
    /// Gets the cached dotfiles configuration status. Null if not yet checked.
    /// </summary>
    public CodespaceService.DotfilesStatus? DotfilesStatus => _dotfilesStatus;

    /// <summary>
    /// Checks whether the user has dotfiles configured for codespaces (cached after first call).
    /// </summary>
    public async Task<CodespaceService.DotfilesStatus> CheckDotfilesStatusAsync()
    {
        if (_dotfilesStatus != null)
            return _dotfilesStatus;
        var svc = _codespaceService;
        _dotfilesStatus = await svc.CheckDotfilesConfiguredAsync();
        return _dotfilesStatus;
    }

    /// <summary>
    /// Manually triggers a reconnect attempt for a codespace group.
    /// Called from the UI "Retry Now" button.
    /// </summary>
    public async Task RetryCodespaceConnectionAsync(string groupId)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null || !group.IsCodespace) return;

        var svc = _codespaceService;
        var previousState = group.ConnectionState;

        group.LastReconnectAttempt = DateTime.UtcNow;
        group.ReconnectAttempts = 0; // Reset backoff counter on manual retry
        group.SetupMessage = null;

        // For SetupRequired, re-probe SSH
        if (group.ConnectionState == CodespaceConnectionState.SetupRequired)
            group.SshAvailable = null;

        group.ConnectionState = CodespaceConnectionState.Reconnecting;
        OnStateChanged?.Invoke();

        try
        {
            await ReconnectCodespaceGroupAsync(group, svc, CancellationToken.None);
            group.ConnectionState = CodespaceConnectionState.Connected;
            group.ReconnectAttempts = 0;
            Debug($"Manual retry: codespace '{group.Name}' connected");
            await ResumeCodespaceSessionsAsync(group, CancellationToken.None);
            NotifyCodespaceConnected(group);
        }
        catch
        {
            // Restore the previous state if reconnect failed (not Reconnecting)
            if (group.ConnectionState == CodespaceConnectionState.Reconnecting)
                group.ConnectionState = previousState;
            Debug($"Manual retry: codespace '{group.Name}' still not reachable");
        }
        finally
        {
            OnStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Reconnects a codespace group: disposes old tunnel/client, starts copilot, opens tunnel, creates client.
    /// </summary>
    private async Task ReconnectCodespaceGroupAsync(SessionGroup group, CodespaceService svc, CancellationToken ct)
    {
        // Serialize reconnect attempts per-group to prevent concurrent calls from
        // spawning duplicate SSH tunnels (loser's tunnel would be orphaned).
        var groupLock = _reconnectLocks.GetOrAdd(group.Id, _ => new SemaphoreSlim(1, 1));
        await groupLock.WaitAsync(ct);
        try
        {
            // Clean up old resources
            if (_tunnelHandles.TryRemove(group.Id, out var oldTunnel))
                try { await oldTunnel.DisposeAsync(); } catch { }
            if (_codespaceClients.TryRemove(group.Id, out var oldClient))
                try { await oldClient.DisposeAsync(); } catch { }

        CodespaceService.TunnelHandle? tunnel = null;

        // Strategy 1: SSH tunnel (combines SSH + port forwarding + copilot start)
        if (group.SshAvailable != false)
        {
            try
            {
                tunnel = await svc.OpenSshTunnelAsync(group.CodespaceName!, group.CodespacePort, connectTimeoutSeconds: 30);
                if (tunnel != null)
                {
                    InvokeOnUI(() => group.SshAvailable = true);
                    Debug($"[HEALTH] SSH tunnel re-established for '{group.CodespaceName}'");
                }
                else
                {
                    InvokeOnUI(() => group.SshAvailable = false);
                    Debug($"[HEALTH] SSH unavailable for '{group.CodespaceName}' — falling back to ports forward");
                }
            }
            catch (TimeoutException)
            {
                InvokeOnUI(() => group.SshAvailable = true); // SSH itself works
                Debug($"[HEALTH] SSH tunnel timeout for '{group.CodespaceName}' — copilot not listening");
            }
        }

        // Strategy 2: Separate SSH (copilot start) + ports forward (tunnel)
        if (tunnel == null)
        {
            if (group.SshAvailable == true)
            {
                var sshWorked = await svc.StartCopilotHeadlessAsync(group.CodespaceName!, group.CodespacePort, sshTimeoutSeconds: 45);
                if (sshWorked)
                    await Task.Delay(3000, ct);
            }

            try
            {
                bool copilotReady;
                (tunnel, copilotReady) = await svc.OpenTunnelAsync(group.CodespaceName!, group.CodespacePort,
                    connectTimeoutSeconds: group.SshAvailable == true ? 30 : 10, requireCopilot: false);
                if (!copilotReady)
                {
                    // Tunnel alive but copilot not running
                    InvokeOnUI(() => group.ConnectionState = CodespaceConnectionState.WaitingForCopilot);
                    _tunnelHandles[group.Id] = tunnel;
                    InvokeOnUI(() => OnStateChanged?.Invoke());
                    throw new TimeoutException("Copilot not listening");
                }
            }
            catch (InvalidOperationException) when (group.SshAvailable == false)
            {
                // Port forward process died — codespace not reachable
                InvokeOnUI(() => group.ConnectionState = CodespaceConnectionState.WaitingForCopilot);
                InvokeOnUI(() => OnStateChanged?.Invoke());
                throw;
            }
        }

        _tunnelHandles[group.Id] = tunnel;

        // Create client — dispose on failure to prevent leaking the CopilotClient
        var client = CreateRemoteClient("127.0.0.1", tunnel.LocalPort);
        try
        {
            await client.StartAsync(ct);
        }
        catch
        {
            try { await client.DisposeAsync(); } catch { }
            throw;
        }
        _codespaceClients[group.Id] = client;
        }
        finally
        {
            groupLock.Release();
        }
    }

    /// <summary>
    /// Resumes placeholder codespace sessions after the tunnel/client has been established.
    /// Placeholder sessions have Session=null! and were created during restore before the codespace was connected.
    /// Also re-wires sessions that had stale SDK sessions from a previous connection.
    /// </summary>
    private async Task ResumeCodespaceSessionsAsync(SessionGroup group, CancellationToken ct)
    {
        if (!_codespaceClients.TryGetValue(group.Id, out var client)) return;

        // Snapshot for thread safety — this runs on health check background thread
        var groupSessions = SnapshotSessionMetas()
            .Where(m => m.GroupId == group.Id)
            .ToList();

        foreach (var meta in groupSessions)
        {
            if (!_sessions.TryGetValue(meta.SessionName, out var state)) continue;
            if (string.IsNullOrEmpty(state.Info.SessionId)) continue;

            try
            {
                var resumeModel = Models.ModelHelper.NormalizeToSlug(state.Info.Model ?? DefaultModel);
                var codespaceWorkDir = group.CodespaceWorkingDirectory ?? state.Info.WorkingDirectory;
                var resumeConfig = new ResumeSessionConfig
                {
                    Model = resumeModel,
                    WorkingDirectory = codespaceWorkDir,
                    Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                    OnPermissionRequest = AutoApprovePermissions,
                };

                CopilotSession newSession;
                try
                {
                    newSession = await client.ResumeSessionAsync(state.Info.SessionId, resumeConfig, ct);
                }
                catch (Exception ex) when (ex.Message.Contains("Session not found", StringComparison.OrdinalIgnoreCase))
                {
                    Debug($"[HEALTH] Session '{meta.SessionName}' expired on server, creating fresh session...");
                    var freshConfig = new SessionConfig
                    {
                        Model = resumeModel ?? DefaultModel,
                        WorkingDirectory = codespaceWorkDir,
                        Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                        OnPermissionRequest = AutoApprovePermissions,
                    };
                    newSession = await client.CreateSessionAsync(freshConfig, ct);
                    state.Info.SessionId = newSession.SessionId;
                    FlushSaveActiveSessionsToDisk();
                }

                // Update the stored working directory so persistence saves the codespace path
                state.Info.WorkingDirectory = codespaceWorkDir;

                CancelProcessingWatchdog(state);
                CancelToolHealthCheck(state);
                var oldState = state;
                var newState = new SessionState
                {
                    Session = newSession,
                    Info = state.Info
                };
                newSession.On(evt => HandleSessionEvent(newState, evt));
                _sessions[meta.SessionName] = newState;
                DisposePrematureIdleSignal(oldState);

                // Remove the placeholder system message and add connected notification (on UI thread)
                var infoRef = state.Info;
                InvokeOnUI(() =>
                {
                    var waitingMsg = infoRef.History.LastOrDefault(m => m.Content == "🔄 Waiting for codespace connection...");
                    if (waitingMsg != null)
                        infoRef.History.Remove(waitingMsg);
                    infoRef.History.Add(ChatMessage.SystemMessage("✅ Connected to codespace."));
                });

                Debug($"[HEALTH] Resumed codespace session '{meta.SessionName}'");
            }
            catch (Exception ex)
            {
                Debug($"[HEALTH] Failed to resume session '{meta.SessionName}': {ex.Message}");
            }
        }
        InvokeOnUI(() => OnStateChanged?.Invoke());
    }

    /// <summary>
    /// Starts a stopped codespace and reconnects the group. Called from the UI "Start Codespace" button.
    /// </summary>
    public async Task StartAndReconnectCodespaceAsync(string groupId, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var group = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null || !group.IsCodespace) return;

        var svc = _codespaceService;

        InvokeOnUI(() =>
        {
            group.ConnectionState = CodespaceConnectionState.StartingCodespace;
            OnStateChanged?.Invoke();
        });

        onProgress?.Invoke("Starting codespace...");
        Debug($"Starting codespace '{group.CodespaceName}'...");
        var started = await svc.StartCodespaceAsync(group.CodespaceName!, timeoutSeconds: 180, ct);
        if (!started)
        {
            InvokeOnUI(() =>
            {
                group.ConnectionState = CodespaceConnectionState.CodespaceStopped;
                OnStateChanged?.Invoke();
            });
            throw new InvalidOperationException(
                $"Could not start codespace '{group.CodespaceName}'. It may be suspended or deleted. " +
                $"Try starting it from github.com or `gh cs start -c {group.CodespaceName}`.");
        }

        onProgress?.Invoke("Codespace started. Connecting...");
        // Re-check group still exists (could have been deleted while we waited)
        if (!Organization.Groups.Any(g => g.Id == groupId)) return;
        // Reset SSH availability — it was likely cached as false from when the codespace
        // was stopped. Now that it's running, SSH should work.
        InvokeOnUI(() => group.SshAvailable = null);
        try
        {
            await ReconnectCodespaceGroupAsync(group, svc, ct);
            InvokeOnUI(() => group.ConnectionState = CodespaceConnectionState.Connected);
            Debug($"Codespace '{group.CodespaceName}' started and reconnected");
            StartCodespaceHealthCheck();
            await ResumeCodespaceSessionsAsync(group, ct);
        }
        catch (Exception ex)
        {
            InvokeOnUI(() =>
            {
                // If SSH is unavailable, this is a SetupRequired situation
                if (group.SshAvailable == false)
                    group.ConnectionState = CodespaceConnectionState.SetupRequired;
                else if (group.ConnectionState != CodespaceConnectionState.WaitingForCopilot)
                    group.ConnectionState = CodespaceConnectionState.Reconnecting;
            });
            if (group.SshAvailable == false)
            {
#if MACCATALYST || IOS || ANDROID || WINDOWS
                try { await Launcher.Default.OpenAsync(new Uri($"https://{group.CodespaceName}.github.dev/")); }
                catch { }
#endif
                Debug($"Codespace '{group.CodespaceName}' started but has no SSH — SetupRequired, opened browser");
            }
            throw new InvalidOperationException(
                $"Codespace '{group.CodespaceName}' is running but copilot is not responding. " +
                $"Open a terminal in the codespace and run: copilot --headless --port {group.CodespacePort}", ex);
        }
        finally
        {
            InvokeOnUI(() => OnStateChanged?.Invoke());
        }
    }
}
