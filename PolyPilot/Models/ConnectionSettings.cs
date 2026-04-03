using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public enum ConnectionMode
{
    Embedded,   // SDK spawns copilot via stdio (dies with app)
    Persistent, // App spawns detached copilot server; survives app restarts
    Remote,     // Connect to a remote server via URL (e.g. DevTunnel)
    Demo        // Local mock mode for testing chat UI without a real connection
}

public enum ChatLayout
{
    Default,      // Copilot left, User right
    Reversed,     // User left, Copilot right
    BothLeft      // Both on left
}

public enum ChatStyle
{
    Normal,       // Colored bubbles with borders
    Minimal       // Full-width, no bubbles (Claude.ai style)
}

public enum UiTheme
{
    System,          // Follow OS light/dark preference (PolyPilot palette)
    PolyPilotDark,   // Default dark theme
    PolyPilotLight,  // Light variant
    SolarizedDark,   // Solarized dark
    SolarizedLight,  // Solarized light
    SystemSolarized, // Follow OS light/dark preference (Solarized palette)
    InternationalWomensDay,  // Purple/violet theme for March 8
    AmberDark,       // Warm amber dark theme
    AmberLight,      // Warm amber light theme
    SystemAmber      // Follow OS light/dark preference (Amber palette)
}

public enum CliSourceMode
{
    BuiltIn,   // Use the CLI bundled with the app
    System     // Use the CLI installed on the system (PATH, homebrew, npm)
}

public enum VsCodeVariant
{
    Stable,    // Use 'code' command
    Insiders   // Use 'code-insiders' command
}

public static class VsCodeVariantExtensions
{
    public static string Command(this VsCodeVariant v) => v switch
    {
        VsCodeVariant.Insiders => "code-insiders",
        _ => "code"
    };

    public static string DisplayName(this VsCodeVariant v) => v switch
    {
        VsCodeVariant.Insiders => "VS Code Insiders",
        _ => "VS Code"
    };
}

public class ConnectionSettings
{
    public ConnectionMode Mode { get; set; } = PlatformHelper.DefaultMode;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4321;
    public bool AutoStartServer { get; set; } = false;
    public string? RemoteUrl { get; set; }

    // Secrets: stored in SecureStorage on iOS/Android; plain JSON on desktop (incl. Mac Catalyst).
    // Mac Catalyst runs without app sandbox, making Keychain unreliable for SecureStorage.
#if IOS || ANDROID
    private string? _remoteToken;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RemoteToken
    {
        get => _remoteToken;
        set { if (_remoteToken != value) { _remoteToken = value; _secretsDirty = true; } }
    }

    private string? _lanToken;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LanToken
    {
        get => _lanToken;
        set { if (_lanToken != value) { _lanToken = value; _secretsDirty = true; } }
    }

    private string? _serverPassword;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ServerPassword
    {
        get => _serverPassword;
        set { if (_serverPassword != value) { _serverPassword = value; _secretsDirty = true; } }
    }

    private bool _secretsDirty;
#else
    public string? RemoteToken { get; set; }
    public string? LanToken { get; set; }
    public string? ServerPassword { get; set; }
#endif

    public string? LanUrl { get; set; }
    public string? TunnelId { get; set; }
    public bool AutoStartTunnel { get; set; } = false;
    public bool DirectSharingEnabled { get; set; } = false;
    public ChatLayout ChatLayout { get; set; } = ChatLayout.Default;
    public ChatStyle ChatStyle { get; set; } = ChatStyle.Normal;
    public UiTheme Theme { get; set; } = UiTheme.System;
    public bool AutoUpdateFromMain { get; set; } = false;
    public CliSourceMode CliSource { get; set; } = CliSourceMode.BuiltIn;
    public VsCodeVariant Editor { get; set; } = VsCodeVariant.Stable;
    public string? RepositoryStorageRoot { get; set; }
    public List<string> DisabledMcpServers { get; set; } = new();
    public List<string> DisabledPlugins { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();
    public bool EnableSessionNotifications { get; set; } = false;
    public bool MuteWorkerNotifications { get; set; } = false;
    public bool CodespacesEnabled { get; set; } = false;
    /// <summary>
    /// When true, logs every SDK event type to event-diagnostics.log (not just lifecycle events).
    /// Useful for investigating zero-idle sessions (#299) — reveals the exact last event before silence.
    /// </summary>
    public bool EnableVerboseEventTracing { get; set; } = false;

    /// <summary>
    /// Normalizes a remote URL by ensuring it has an http(s):// scheme.
    /// Plain IPs/hostnames get http://, devtunnels/known TLS hosts get https://.
    /// Already-schemed URLs pass through unchanged. Returns null for null/empty input.
    /// </summary>
    public static string? NormalizeRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        var trimmed = url.Trim().TrimEnd('/');

        // Already has any scheme — return as-is (prevents double-scheme like http://ftp://host)
        if (trimmed.Contains("://"))
            return trimmed;

        // Heuristic: known tunnel/proxy hosts always use TLS — match exact suffixes to avoid
        // false-positives from hostnames that merely contain ".ngrok" or ".cloudflare"
        if (trimmed.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ngrok.io", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ngrok-free.app", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".ngrok.app", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
            return "https://" + trimmed;

        // Everything else (bare IP, localhost, LAN hostname) → http
        return "http://" + trimmed;
    }

    internal static Dictionary<string, string> BuildTunnelQrPayload(
        string url,
        string? token,
        string? lanHost,
        int bridgePort,
        string? serverPassword,
        bool allowLan)
    {
        var payload = new Dictionary<string, string> { ["url"] = url };
        if (!string.IsNullOrEmpty(token))
            payload["token"] = token;

        if (allowLan && !string.IsNullOrEmpty(lanHost) && !string.IsNullOrEmpty(serverPassword))
        {
            payload["lanUrl"] = $"http://{lanHost}:{bridgePort}";
            payload["lanToken"] = serverPassword;
        }

        return payload;
    }

    internal static Dictionary<string, string> BuildDirectQrPayload(
        string host,
        int bridgePort,
        string? serverPassword,
        bool allowLan,
        string? tunnelUrl = null,
        string? tunnelToken = null)
    {
        var payload = new Dictionary<string, string>();

        if (allowLan)
        {
            payload["lanUrl"] = $"http://{host}:{bridgePort}";
            if (!string.IsNullOrEmpty(serverPassword))
                payload["lanToken"] = serverPassword;
        }

        if (!string.IsNullOrEmpty(tunnelUrl))
        {
            payload["url"] = tunnelUrl;
            if (!string.IsNullOrEmpty(tunnelToken))
                payload["token"] = tunnelToken;
        }
        else if (allowLan)
        {
            payload["url"] = $"http://{host}:{bridgePort}";
            if (!string.IsNullOrEmpty(serverPassword))
                payload["token"] = serverPassword;
        }

        return payload;
    }

    [JsonIgnore]
    public string CliUrl => Mode == ConnectionMode.Remote && !string.IsNullOrEmpty(RemoteUrl)
        ? RemoteUrl
        : $"{Host}:{Port}";

    private static string? _settingsPath;
    private static string SettingsPath => _settingsPath ??= Path.Combine(
        GetPolyPilotDir(), "settings.json");

    /// <summary>For test isolation only — redirects Load()/Save() to a temp file.</summary>
    internal static void SetSettingsFilePathForTesting(string? path) => _settingsPath = path;

    private static string GetPolyPilotDir()
    {
#if IOS || ANDROID
        try
        {
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
        }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(fallback))
                fallback = Path.GetTempPath();
            return Path.Combine(fallback, ".polypilot");
        }
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".polypilot");
#endif
    }

    public static ConnectionSettings Load()
    {
        ConnectionSettings settings;
        string? rawJson = null;
        try
        {
            if (File.Exists(SettingsPath))
            {
                rawJson = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<ConnectionSettings>(rawJson) ?? DefaultSettings();
            }
            else
            {
                settings = DefaultSettings();
            }
        }
        catch { settings = DefaultSettings(); }

        // Ensure loaded mode is valid for this platform
        if (!PlatformHelper.AvailableModes.Contains(settings.Mode))
            settings.Mode = PlatformHelper.DefaultMode;
        settings.RepositoryStorageRoot = NormalizeRepositoryStorageRoot(settings.RepositoryStorageRoot);

        NormalizeEnumFields(settings);

        // InternationalWomensDay is ephemeral — never persist it; revert to System on load
        if (settings.Theme == UiTheme.InternationalWomensDay)
            settings.Theme = UiTheme.System;

#if MACCATALYST
        // Reverse migration: PR 341 moved secrets to SecureStorage on Mac Catalyst,
        // but Keychain is unreliable without app sandboxing. Recover secrets to plain JSON.
        RecoverSecretsFromSecureStorage(settings);
#elif IOS || ANDROID
        settings.MigrateAndLoadMobileSecrets(rawJson);
#endif

        return settings;
    }

    public static string? NormalizeRepositoryStorageRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return path.Trim();
    }

    /// <summary>Normalize invalid enum values to safe defaults. Testable separately from Load().</summary>
    internal static void NormalizeEnumFields(ConnectionSettings settings)
    {
        if (!Enum.IsDefined(settings.CliSource))
            settings.CliSource = CliSourceMode.BuiltIn;
        if (!Enum.IsDefined(settings.Editor))
            settings.Editor = VsCodeVariant.Stable;
    }

    private static ConnectionSettings DefaultSettings()
    {
#if ANDROID
        // Android can't run Copilot locally — default to persistent mode
        // User must configure the host IP in Settings to point to their Mac
        return new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };
#else
        return new();
#endif
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
#if IOS || ANDROID
            SaveMobileSecretsIfDirty();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
#else
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
#endif
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

#if MACCATALYST
    /// <summary>
    /// One-time reverse migration: PR 341 moved ServerPassword/RemoteToken/LanToken to
    /// SecureStorage on Mac Catalyst. Since Mac Catalyst runs without sandbox, Keychain
    /// is unreliable. This recovers any values and writes them back to plain JSON.
    /// Only removes each Keychain entry after confirming that specific value was recovered.
    /// </summary>
    private static void RecoverSecretsFromSecureStorage(ConnectionSettings settings)
    {
        try
        {
            bool needsSave = false;
            bool recoveredRemote = false, recoveredLan = false, recoveredPass = false;

            if (string.IsNullOrEmpty(settings.RemoteToken))
            {
                var val = ReadSecureStorage("polypilot.connection.remoteToken");
                if (!string.IsNullOrEmpty(val)) { settings.RemoteToken = val; needsSave = true; recoveredRemote = true; }
            }
            if (string.IsNullOrEmpty(settings.LanToken))
            {
                var val = ReadSecureStorage("polypilot.connection.lanToken");
                if (!string.IsNullOrEmpty(val)) { settings.LanToken = val; needsSave = true; recoveredLan = true; }
            }
            if (string.IsNullOrEmpty(settings.ServerPassword))
            {
                var val = ReadSecureStorage("polypilot.connection.serverPassword");
                if (!string.IsNullOrEmpty(val)) { settings.ServerPassword = val; needsSave = true; recoveredPass = true; }
            }

            if (needsSave)
            {
                settings.Save();

                // Per-key cleanup: only remove a Keychain entry if that specific value was recovered
                // and Save() wrote the file. Prevents data loss if Keychain read fails transiently
                // for one secret but succeeds for another.
                if (File.Exists(SettingsPath))
                {
                    if (recoveredRemote)
                        try { SecureStorage.Default.Remove("polypilot.connection.remoteToken"); } catch { }
                    if (recoveredLan)
                        try { SecureStorage.Default.Remove("polypilot.connection.lanToken"); } catch { }
                    if (recoveredPass)
                        try { SecureStorage.Default.Remove("polypilot.connection.serverPassword"); } catch { }
                }
            }
        }
        catch { }
    }

    private static string? ReadSecureStorage(string key)
    {
        try { return Task.Run(() => SecureStorage.Default.GetAsync(key)).GetAwaiter().GetResult(); }
        catch { return null; }
    }
#endif

#if IOS || ANDROID
    private const string RemoteTokenKey = "polypilot.connection.remoteToken";
    private const string LanTokenKey = "polypilot.connection.lanToken";
    private const string ServerPasswordKey = "polypilot.connection.serverPassword";

    private void MigrateAndLoadMobileSecrets(string? loadedJson)
    {
        try
        {
            // Check if the legacy JSON had secret values (before they were [JsonIgnore])
            string? legacyRemote = null, legacyLan = null, legacyPass = null;
            if (!string.IsNullOrEmpty(loadedJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(loadedJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("RemoteToken", out var rt)) legacyRemote = rt.GetString();
                    if (root.TryGetProperty("LanToken", out var lt)) legacyLan = lt.GetString();
                    if (root.TryGetProperty("ServerPassword", out var sp)) legacyPass = sp.GetString();
                }
                catch { }
            }

            var storedRemote = GetSecureStorageSync(RemoteTokenKey);
            var storedLan    = GetSecureStorageSync(LanTokenKey);
            var storedPass   = GetSecureStorageSync(ServerPasswordKey);

            // Migrate legacy plaintext values to SecureStorage on first run.
            // Per-field tracking: only scrub a field from JSON after confirming its write succeeded,
            // so a partial Keychain failure (full, locked, missing entitlements) cannot lose tokens.
            if (string.IsNullOrEmpty(storedRemote) && !string.IsNullOrEmpty(legacyRemote))
            {
                if (SetSecureStorageSync(RemoteTokenKey, legacyRemote))
                    storedRemote = legacyRemote;
            }
            if (string.IsNullOrEmpty(storedLan) && !string.IsNullOrEmpty(legacyLan))
            {
                if (SetSecureStorageSync(LanTokenKey, legacyLan))
                    storedLan = legacyLan;
            }
            if (string.IsNullOrEmpty(storedPass) && !string.IsNullOrEmpty(legacyPass))
            {
                if (SetSecureStorageSync(ServerPasswordKey, legacyPass))
                    storedPass = legacyPass;
            }

            _remoteToken = storedRemote;
            _lanToken    = storedLan;
            _serverPassword = storedPass;
            _secretsDirty = false;

            // Only scrub legacy JSON if every field that existed was successfully migrated.
            // If any write failed, leave the JSON untouched so migration retries next launch.
            // Treat "already in SecureStorage" as success so crash-recovery cases (where SecureStorage
            // was written but Save() crashed before scrubbing JSON) can still reach Save() next launch.
            bool allSucceeded = (string.IsNullOrEmpty(legacyRemote) || !string.IsNullOrEmpty(storedRemote))
                             && (string.IsNullOrEmpty(legacyLan)    || !string.IsNullOrEmpty(storedLan))
                             && (string.IsNullOrEmpty(legacyPass)   || !string.IsNullOrEmpty(storedPass));
            bool anyAttempted = !string.IsNullOrEmpty(legacyRemote)
                             || !string.IsNullOrEmpty(legacyLan)
                             || !string.IsNullOrEmpty(legacyPass);
            if (anyAttempted && allSucceeded)
                Save();
        }
        catch { }
    }

    private void SaveMobileSecretsIfDirty()
    {
        if (!_secretsDirty) return;
        try
        {
            bool ok = SetSecureStorageSync(RemoteTokenKey, _remoteToken)
                    & SetSecureStorageSync(LanTokenKey, _lanToken)
                    & SetSecureStorageSync(ServerPasswordKey, _serverPassword);
            // Only clear dirty flag if all writes succeeded; leave true to retry on next save
            if (ok) _secretsDirty = false;
        }
        catch { }
    }

    private static string? GetSecureStorageSync(string key)
    {
        try { return Task.Run(() => SecureStorage.Default.GetAsync(key)).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private static bool SetSecureStorageSync(string key, string? value)
    {
        try
        {
            Task.Run(async () =>
            {
                if (string.IsNullOrEmpty(value)) { SecureStorage.Default.Remove(key); return; }
                await SecureStorage.Default.SetAsync(key, value);
            }).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }
#endif
}
