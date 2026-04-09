namespace PolyPilot.Provider;

/// <summary>
/// Optional extension interface for providers that support permission approval flows.
/// When a provider implements this, the host UI will show permission requests to the user
/// and allow them to approve or deny tool execution.
///
/// Example: Squad's RemoteBridge emits RCPermissionEvent when an agent requests
/// tool approval — the host should show a card with the tool name, description,
/// and approve/deny buttons.
///
/// Backward-compatible: providers that don't implement this interface work as before.
/// The host checks <c>provider is IPermissionAwareProvider</c> at registration time.
/// </summary>
public interface IPermissionAwareProvider : ISessionProvider
{
    /// <summary>
    /// Fires when an agent requests permission to execute a tool.
    /// The host should display this request and call ApprovePermissionAsync or DenyPermissionAsync.
    /// </summary>
    event Action<ProviderPermissionRequest>? OnPermissionRequested;

    /// <summary>
    /// Fires when a previously pending permission request is resolved (approved, denied, or timed out).
    /// Args: (permissionId). The host should remove the request from the UI.
    /// </summary>
    event Action<string>? OnPermissionResolved;

    /// <summary>
    /// Returns the currently pending permission requests that have not yet been approved or denied.
    /// </summary>
    IReadOnlyList<ProviderPermissionRequest> GetPendingPermissions();

    /// <summary>
    /// Approve a pending permission request, allowing the agent to proceed with tool execution.
    /// </summary>
    Task ApprovePermissionAsync(string permissionId, CancellationToken ct = default);

    /// <summary>
    /// Deny a pending permission request, blocking the agent from executing the tool.
    /// </summary>
    Task DenyPermissionAsync(string permissionId, CancellationToken ct = default);
}
