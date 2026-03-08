namespace PolyPilot.Models;

public enum StepAction
{
    None,
    Navigate
}

public class TutorialStep
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public StepAction Action { get; set; } = StepAction.None;
    public string? NavigateTo { get; set; }
}

public class TutorialChapter
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<TutorialStep> Steps { get; set; } = new();
}

public static class TutorialContent
{
    public static List<TutorialChapter> Chapters { get; } = new()
    {
        new TutorialChapter
        {
            Id = "getting-started",
            Title = "Getting Started",
            Description = "Learn the basics of PolyPilot and how to connect to Copilot.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "welcome",
                    Title = "Welcome to PolyPilot",
                    Description = "PolyPilot is a native GUI for managing multiple GitHub Copilot CLI sessions. Let's take a quick tour of the key features."
                },
                new TutorialStep
                {
                    Id = "connection-status",
                    Title = "Connection Status",
                    Description = "The indicator in the sidebar shows whether you're connected to the Copilot backend. It displays the current connection mode (Persistent, Embedded, Remote, or Demo)."
                },
                new TutorialStep
                {
                    Id = "settings-nav",
                    Title = "Settings",
                    Description = "Click the gear icon in the sidebar header to open Settings, where you can choose your connection mode, configure remote access, and more."
                }
            }
        },
        new TutorialChapter
        {
            Id = "sessions",
            Title = "Sessions",
            Description = "Create and manage Copilot chat sessions.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "new-session",
                    Title = "Create a Session",
                    Description = "Use the session form at the top of the sidebar to create a new Copilot session. You can optionally set a name, choose a model, and pick a working directory."
                },
                new TutorialStep
                {
                    Id = "send-prompt",
                    Title = "Send a Prompt",
                    Description = "Type your message in the input area at the bottom and press Enter (or click the send button) to send a prompt to Copilot.",
                    Action = StepAction.Navigate,
                    NavigateTo = "/"
                },
                new TutorialStep
                {
                    Id = "response-area",
                    Title = "View Responses",
                    Description = "Copilot's responses stream in real-time. You'll see a status indicator showing the current phase: Sending, Thinking, Working (with tool call counts)."
                }
            }
        },
        new TutorialChapter
        {
            Id = "groups",
            Title = "Groups & Organization",
            Description = "Organize your sessions into groups for better workflow management.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "create-group",
                    Title = "Create a Group",
                    Description = "Use the toolbar buttons below the session form to create a new group, add a multi-agent team, or add a repository. Groups help you organize sessions by project or task.",
                    Action = StepAction.Navigate,
                    NavigateTo = "/"
                },
                new TutorialStep
                {
                    Id = "sort-sessions",
                    Title = "Sort Sessions",
                    Description = "Use the sort dropdown in the sidebar toolbar to order sessions by last activity, creation date, name, or manually drag them."
                }
            }
        },
        new TutorialChapter
        {
            Id = "model-selection",
            Title = "Model Selection",
            Description = "Choose which AI model to use for each session.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "model-picker",
                    Title = "Choose a Model",
                    Description = "The model selector lets you pick which AI model to use. Click '+ New Session' to expand the form and see the model dropdown. Models are set at session creation time.",
                    Action = StepAction.Navigate,
                    NavigateTo = "/"
                },
                new TutorialStep
                {
                    Id = "model-per-session",
                    Title = "Model Per Session",
                    Description = "Each session is tied to its model. To switch models, create a new session with the desired model selected."
                }
            }
        },
        new TutorialChapter
        {
            Id = "multi-agent",
            Title = "Multi-Agent Presets",
            Description = "Use pre-configured agent teams for specialized workflows.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "preset-picker",
                    Title = "Agent Presets",
                    Description = "Presets let you spin up a team of specialized agents. Look for the multi-agent button in the sidebar toolbar -- it shows built-in presets, your saved presets, and repo-level Squad teams."
                },
                new TutorialStep
                {
                    Id = "squad-integration",
                    Title = "Squad Integration",
                    Description = "If your repository has a .squad/ directory, PolyPilot automatically discovers team definitions and shows them as presets in the 'From Repo' section."
                }
            }
        },
        new TutorialChapter
        {
            Id = "remote-mode",
            Title = "Remote Mode",
            Description = "Connect to PolyPilot from mobile devices.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "remote-settings",
                    Title = "Remote Connection",
                    Description = "Remote mode lets you connect from mobile devices to a desktop PolyPilot instance. Configure it in Settings under the Connection section.",
                    Action = StepAction.Navigate,
                    NavigateTo = "/settings"
                },
                new TutorialStep
                {
                    Id = "devtunnel-qr",
                    Title = "DevTunnel & QR Code",
                    Description = "Use DevTunnel to expose your session over the internet, then scan the QR code from the mobile app for easy setup."
                }
            }
        },
        new TutorialChapter
        {
            Id = "repos",
            Title = "Repository Management",
            Description = "Set working directories and manage repo contexts.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "repo-picker",
                    Title = "Working Directory",
                    Description = "Each session can have a working directory. When creating a session, click the folder icon to pick a repository or directory for Copilot to work in.",
                    Action = StepAction.Navigate,
                    NavigateTo = "/"
                },
                new TutorialStep
                {
                    Id = "repo-context",
                    Title = "Repo Context",
                    Description = "The working directory determines which files Copilot can see and modify. Sessions in different directories have different file contexts."
                }
            }
        },
        new TutorialChapter
        {
            Id = "shortcuts",
            Title = "Keyboard Shortcuts",
            Description = "Speed up your workflow with keyboard shortcuts.",
            Steps = new()
            {
                new TutorialStep
                {
                    Id = "session-shortcuts",
                    Title = "Session Navigation",
                    Description = "Use Cmd+1-9 to quickly switch between sessions. Press Cmd+E to focus the session search. Use Up/Down arrows to cycle through your prompt history."
                },
                new TutorialStep
                {
                    Id = "input-shortcuts",
                    Title = "Input Shortcuts",
                    Description = "Press Tab to accept Copilot's suggestion. Press Esc to cancel. Use Cmd+/Cmd- to adjust font size. Press Ctrl+C to abort a running request."
                },
                new TutorialStep
                {
                    Id = "info-popover",
                    Title = "Quick Reference",
                    Description = "You can always find the full shortcut list in Settings by clicking the info icon in the top right corner."
                }
            }
        }
    };
}
