using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Declarative registry of all application settings.
/// The Settings page generates its UI dynamically from these descriptors.
/// </summary>
public static class SettingsRegistry
{
    public static IReadOnlyList<SettingDescriptor> All { get; } = Build();

    private static List<SettingDescriptor> Build()
    {
        var list = new List<SettingDescriptor>();

        // ── Connection ──────────────────────────────────────────────

        list.Add(new SettingDescriptor
        {
            Id = "connection.mode",
            Label = "Transport Mode",
            Description = "How PolyPilot connects to the Copilot CLI backend.",
            Category = "Connection",
            Section = "Transport Mode",
            Type = SettingType.CardEnum,
            Order = 10,
            SearchKeywords = "transport mode embedded persistent remote demo connection",
            Options = PlatformHelper.AvailableModes.Select(m => new SettingOption(
                m.ToString(),
                m switch
                {
                    ConnectionMode.Embedded => "Embedded",
                    ConnectionMode.Persistent => "Persistent",
                    ConnectionMode.Remote => "Remote",
                    ConnectionMode.Demo => "Demo",
                    _ => m.ToString()
                })).ToArray(),
            GetValue = ctx => ctx.Settings.Mode.ToString(),
            SetValue = (ctx, v) =>
            {
                if (v is string s && Enum.TryParse<ConnectionMode>(s, out var mode))
                    ctx.Settings.Mode = mode;
            },
            IsVisible = ctx => PlatformHelper.AvailableModes.Length > 1
        });

        list.Add(new SettingDescriptor
        {
            Id = "connection.host",
            Label = "Host",
            Description = "Hostname or IP address for the persistent server.",
            Category = "Connection",
            Section = "Persistent Server",
            Type = SettingType.String,
            Order = 20,
            SearchKeywords = "host server address ip localhost",
            GetValue = ctx => ctx.Settings.Host,
            SetValue = (ctx, v) => ctx.Settings.Host = v as string ?? "localhost",
            IsVisible = ctx => ctx.Settings.Mode == ConnectionMode.Persistent
        });

        list.Add(new SettingDescriptor
        {
            Id = "connection.port",
            Label = "Port",
            Description = "Port number for the persistent server (1024–65535).",
            Category = "Connection",
            Section = "Persistent Server",
            Type = SettingType.Int,
            Order = 30,
            Min = 1024,
            Max = 65535,
            SearchKeywords = "port server number persistent",
            GetValue = ctx => ctx.Settings.Port,
            SetValue = (ctx, v) => { if (v is int i) ctx.Settings.Port = i; },
            IsVisible = ctx => ctx.Settings.Mode == ConnectionMode.Persistent
        });

        // DevTunnel, Direct Sharing, Fiesta — too complex for generic controls
        list.Add(new SettingDescriptor
        {
            Id = "connection.sharing",
            Label = "Sharing",
            Description = "Share your session via DevTunnel or direct LAN connection.",
            Category = "Connection",
            Section = "Sharing",
            Type = SettingType.Custom,
            Order = 40,
            SearchKeywords = "devtunnel share tunnel mobile qr url token connect direct lan tailscale password fiesta workers",
            IsVisible = ctx => ctx.IsDesktop && ctx.Settings.Mode == ConnectionMode.Persistent
        });

        list.Add(new SettingDescriptor
        {
            Id = "connection.remoteUrl",
            Label = "Remote Server URL",
            Description = "URL of the remote PolyPilot server to connect to.",
            Category = "Connection",
            Section = "Remote Server",
            Type = SettingType.String,
            Order = 50,
            SearchKeywords = "remote server url address",
            GetValue = ctx => ctx.Settings.RemoteUrl,
            SetValue = (ctx, v) => ctx.Settings.RemoteUrl = v as string,
            IsVisible = ctx => ctx.Settings.Mode == ConnectionMode.Remote
        });

        list.Add(new SettingDescriptor
        {
            Id = "connection.remoteToken",
            Label = "Remote Token",
            Description = "Authentication token for the remote server.",
            Category = "Connection",
            Section = "Remote Server",
            Type = SettingType.String,
            Order = 60,
            IsSecret = true,
            SearchKeywords = "remote token password auth",
            GetValue = ctx => ctx.Settings.RemoteToken,
            SetValue = (ctx, v) => ctx.Settings.RemoteToken = v as string,
            IsVisible = ctx => ctx.Settings.Mode == ConnectionMode.Remote
        });

        list.Add(new SettingDescriptor
        {
            Id = "connection.reconnect",
            Label = "Save & Reconnect",
            Description = "Apply connection changes and reconnect.",
            Category = "Connection",
            Section = "Actions",
            Type = SettingType.Action,
            Order = 100,
            ActionLabel = "Save & Reconnect",
            SearchKeywords = "save reconnect apply restart",
        });

        // ── Copilot CLI ─────────────────────────────────────────────

        list.Add(new SettingDescriptor
        {
            Id = "cli.source",
            Label = "CLI Source",
            Description = "Use the CLI bundled with the app or one installed on your system.",
            Category = "Copilot CLI",
            Type = SettingType.CardEnum,
            Order = 10,
            SearchKeywords = "cli source built-in system version binary copilot",
            Options = new[]
            {
                new SettingOption("BuiltIn", "📦 Built-in"),
                new SettingOption("System", "💻 System"),
            },
            GetValue = ctx => ctx.Settings.CliSource.ToString(),
            SetValue = (ctx, v) =>
            {
                if (v is string s && Enum.TryParse<CliSourceMode>(s, out var src))
                    ctx.Settings.CliSource = src;
            },
            IsVisible = ctx => ctx.Settings.Mode != ConnectionMode.Remote
                            && ctx.Settings.Mode != ConnectionMode.Demo
        });

        // ── UI ──────────────────────────────────────────────────────

        list.Add(new SettingDescriptor
        {
            Id = "ui.colorScheme",
            Label = "Color Scheme",
            Description = "Choose between system, dark, or light appearance.",
            Category = "UI",
            Section = "Theme",
            Type = SettingType.CardEnum,
            Order = 10,
            SearchKeywords = "theme appearance dark light system color scheme",
            Options = new[]
            {
                new SettingOption("System", "System", "tp-system"),
                new SettingOption("Dark", "Dark", "tp-pp-dark"),
                new SettingOption("Light", "Light", "tp-pp-light"),
            },
            GetValue = ctx => ctx.Settings.Theme switch
            {
                UiTheme.System or UiTheme.SystemSolarized => "System",
                UiTheme.PolyPilotDark or UiTheme.SolarizedDark => "Dark",
                UiTheme.PolyPilotLight or UiTheme.SolarizedLight => "Light",
                _ => "System"
            },
            SetValue = (ctx, v) =>
            {
                var isSolarized = ctx.Settings.Theme is UiTheme.SolarizedDark or UiTheme.SolarizedLight or UiTheme.SystemSolarized;
                ctx.Settings.Theme = (v as string) switch
                {
                    "Dark" => isSolarized ? UiTheme.SolarizedDark : UiTheme.PolyPilotDark,
                    "Light" => isSolarized ? UiTheme.SolarizedLight : UiTheme.PolyPilotLight,
                    _ => isSolarized ? UiTheme.SystemSolarized : UiTheme.System
                };
            }
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.themeStyle",
            Label = "Style",
            Description = "Visual style for the color scheme.",
            Category = "UI",
            Section = "Theme",
            Type = SettingType.CardEnum,
            Order = 20,
            SearchKeywords = "theme style polypilot solarized palette",
            Options = new[]
            {
                new SettingOption("PolyPilot", "PolyPilot", "tp-pp-dark"),
                new SettingOption("Solarized", "Solarized", "tp-sol-dark"),
            },
            GetValue = ctx => ctx.Settings.Theme is UiTheme.SolarizedDark or UiTheme.SolarizedLight or UiTheme.SystemSolarized
                ? "Solarized" : "PolyPilot",
            SetValue = (ctx, v) =>
            {
                var isSystem = ctx.Settings.Theme is UiTheme.System or UiTheme.SystemSolarized;
                var isDark = ctx.Settings.Theme is UiTheme.PolyPilotDark or UiTheme.SolarizedDark or UiTheme.System or UiTheme.SystemSolarized;
                ctx.Settings.Theme = (v as string) switch
                {
                    "Solarized" => isSystem ? UiTheme.SystemSolarized : (isDark ? UiTheme.SolarizedDark : UiTheme.SolarizedLight),
                    _ => isSystem ? UiTheme.System : (isDark ? UiTheme.PolyPilotDark : UiTheme.PolyPilotLight)
                };
            },
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.chatLayout",
            Label = "Message Layout",
            Description = "Position of assistant and user messages.",
            Category = "UI",
            Section = "Chat",
            Type = SettingType.CardEnum,
            Order = 30,
            SearchKeywords = "chat message layout default reversed both left",
            Options = new[]
            {
                new SettingOption("Default", "Default"),
                new SettingOption("Reversed", "Reversed"),
                new SettingOption("BothLeft", "Both Left"),
            },
            GetValue = ctx => ctx.Settings.ChatLayout.ToString(),
            SetValue = (ctx, v) =>
            {
                if (v is string s && Enum.TryParse<ChatLayout>(s, out var layout))
                    ctx.Settings.ChatLayout = layout;
            }
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.chatStyle",
            Label = "Message Style",
            Description = "Visual treatment of chat messages.",
            Category = "UI",
            Section = "Chat",
            Type = SettingType.CardEnum,
            Order = 40,
            SearchKeywords = "chat message style normal minimal bubble",
            Options = new[]
            {
                new SettingOption("Normal", "Normal"),
                new SettingOption("Minimal", "Minimal"),
            },
            GetValue = ctx => ctx.Settings.ChatStyle.ToString(),
            SetValue = (ctx, v) =>
            {
                if (v is string s && Enum.TryParse<ChatStyle>(s, out var style))
                    ctx.Settings.ChatStyle = style;
            }
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.fontSize",
            Label = "Font Size",
            Description = "Base font size for the application (12–24 px).",
            Category = "UI",
            Section = "Font",
            Type = SettingType.Int,
            Order = 50,
            Min = 12,
            Max = 24,
            SearchKeywords = "font size text zoom",
            GetValue = ctx => ctx.FontSize,
            SetValue = (ctx, v) => { if (v is int i) ctx.FontSize = Math.Clamp(i, 12, 24); }
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.notifications",
            Label = "Session Notifications",
            Description = "Get a system notification when an agent finishes responding.",
            Category = "UI",
            Section = "Notifications",
            Type = SettingType.Bool,
            Order = 60,
            SearchKeywords = "notifications alert sound badge",
            GetValue = ctx => ctx.Settings.EnableSessionNotifications,
            SetValue = (ctx, v) =>
            {
                if (v is bool b)
                    ctx.Settings.EnableSessionNotifications = b;
            }
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.muteWorkerNotifications",
            Label = "Mute Worker Notifications",
            Description = "Don't send notifications for worker sessions in multi-agent groups.",
            Category = "UI",
            Section = "Notifications",
            Type = SettingType.Bool,
            Order = 61,
            SearchKeywords = "notifications worker multi-agent mute quiet",
            IsVisible = ctx => ctx.Settings.EnableSessionNotifications,
            GetValue = ctx => ctx.Settings.MuteWorkerNotifications,
            SetValue = (ctx, v) =>
            {
                if (v is bool b)
                    ctx.Settings.MuteWorkerNotifications = b;
            }
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.editor",
            Label = "Editor",
            Description = "Which VS Code variant to launch from session menus.",
            Category = "UI",
            Section = "Editor",
            Type = SettingType.CardEnum,
            Order = 70,
            SearchKeywords = "editor vscode vs code insiders",
            Options = new[]
            {
                new SettingOption("Stable", "VS Code"),
                new SettingOption("Insiders", "VS Code Insiders"),
            },
            GetValue = ctx => ctx.Settings.Editor.ToString(),
            SetValue = (ctx, v) =>
            {
                if (v is string s && Enum.TryParse<VsCodeVariant>(s, out var variant))
                    ctx.Settings.Editor = variant;
            },
            IsVisible = ctx => ctx.IsDesktop
        });

        list.Add(new SettingDescriptor
        {
            Id = "ui.codespaces",
            Label = "Codespaces",
            Description = "⚠️ Alpha — Enable GitHub Codespaces integration. Requires Embedded mode. Adds the ability to connect sessions to running codespaces via SSH tunnels.",
            Category = "UI",
            Section = "Features",
            Type = SettingType.Bool,
            Order = 65,
            SearchKeywords = "codespaces github cloud remote ssh tunnel embedded",
            GetValue = ctx => ctx.Settings.CodespacesEnabled,
            SetValue = (ctx, v) =>
            {
                // Only allow enabling in Embedded mode; always allow disabling
                if (v is bool b && (ctx.InitialMode == ConnectionMode.Embedded || !b))
                    ctx.Settings.CodespacesEnabled = b;
            },
            IsVisible = ctx => ctx.IsDesktop
        });

        // ── Developer ───────────────────────────────────────────────

        list.Add(new SettingDescriptor
        {
            Id = "developer.autoUpdate",
            Label = "Auto-Update from Main",
            Description = "Watches origin/main for new commits every 30s. When changes are detected, automatically pulls, rebuilds, and relaunches.",
            Category = "Developer",
            Type = SettingType.Custom,  // Uses GitAutoUpdateService, not ConnectionSettings
            Order = 10,
            SearchKeywords = "auto update main git watch relaunch rebuild developer",
        });

        return list;
    }

    /// <summary>Returns distinct category names in display order.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        All.Select(s => s.Category).Distinct().ToList();

    /// <summary>Get all visible settings for a category.</summary>
    public static IEnumerable<SettingDescriptor> ForCategory(string category, SettingsContext ctx) =>
        All.Where(s => s.Category == category
                    && (s.IsVisible?.Invoke(ctx) ?? true))
           .OrderBy(s => s.Order);

    /// <summary>Get all visible settings matching a search query.</summary>
    public static IEnumerable<SettingDescriptor> Search(string query, SettingsContext ctx) =>
        All.Where(s => (s.IsVisible?.Invoke(ctx) ?? true)
                    && MatchesSearch(s, query))
           .OrderBy(s => s.Category).ThenBy(s => s.Order);

    private static bool MatchesSearch(SettingDescriptor s, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(q)) return true;
        return s.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (s.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || s.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (s.Section?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || s.SearchKeywords.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
