using AutoPilot.Services;
using Spectre.Console;

namespace AutoPilot;

public class Program
{
    private static SessionManager _sessionManager = null!;
    private static readonly CancellationTokenSource _cts = new();

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
            AnsiConsole.MarkupLine("\n[yellow]Shutting down...[/]");
        };

        AnsiConsole.Write(new FigletText("AutoPilot").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]Multi-Agent Copilot Session Manager[/]\n");

        _sessionManager = new SessionManager();

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Connecting to Copilot...", async ctx =>
                {
                    await _sessionManager.InitializeAsync(_cts.Token);
                });

            AnsiConsole.MarkupLine("[green]✓ Connected to Copilot[/]");
            AnsiConsole.MarkupLine("[grey]Type /help for commands or start chatting[/]\n");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to connect to Copilot: {ex.Message}[/]");
            AnsiConsole.MarkupLine("[grey]Make sure 'copilot' CLI is installed and authenticated.[/]");
            return 1;
        }

        await RunReplAsync();
        return 0;
    }

    private static async Task RunReplAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            var prompt = GetPrompt();
            AnsiConsole.Markup(prompt);

            string? input;
            try
            {
                input = Console.ReadLine();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (input == null || _cts.Token.IsCancellationRequested)
                break;

            var command = CommandParser.Parse(input);

            try
            {
                var shouldExit = await HandleCommandAsync(command);
                if (shouldExit) break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }
        }

        await ShutdownAsync();
    }

    private static string GetPrompt()
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null)
            return "[grey]no session>[/] ";
        
        return $"[cyan]{session.Name}[/][grey]>[/] ";
    }

    private static async Task<bool> HandleCommandAsync(ParsedCommand command)
    {
        switch (command.Type)
        {
            case CommandType.NewSession:
                await HandleNewSessionAsync(command.Argument, command.SecondArgument);
                break;

            case CommandType.ResumeSession:
                await HandleResumeSessionAsync(command.Argument, command.SecondArgument);
                break;

            case CommandType.ListPersistedSessions:
                HandleListPersistedSessions();
                break;

            case CommandType.SwitchSession:
                HandleSwitchSession(command.Argument);
                break;

            case CommandType.ListSessions:
                HandleListSessions();
                break;

            case CommandType.CloseSession:
                await HandleCloseSessionAsync(command.Argument);
                break;

            case CommandType.Status:
                HandleStatus();
                break;

            case CommandType.Clear:
                HandleClear();
                break;

            case CommandType.Help:
                PrintCommandHelp();
                break;

            case CommandType.Quit:
                return true;

            case CommandType.Prompt:
                if (!string.IsNullOrWhiteSpace(command.Argument))
                    await HandlePromptAsync(command.Argument);
                break;
        }

        return false;
    }

    private static void HandleListPersistedSessions()
    {
        var sessions = _sessionManager.GetPersistedSessions().ToList();
        
        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No saved sessions found in ~/.copilot/session-state[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Session ID")
            .AddColumn("Last Modified")
            .AddColumn("Path");

        foreach (var session in sessions.Take(15))
        {
            table.AddRow(
                $"[cyan]{session.SessionId[..8]}...[/]",
                session.LastModified.ToString("yyyy-MM-dd HH:mm"),
                session.Path.Length > 40 ? "..." + session.Path[^37..] : session.Path
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Use /resume <session-id> <name> to resume a session[/]");
    }

    private static async Task HandleResumeSessionAsync(string? sessionId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            AnsiConsole.MarkupLine("[red]Usage: /resume <session-id> [name][/]");
            AnsiConsole.MarkupLine("[grey]Use /saved to list available sessions[/]");
            return;
        }

        // Find matching session (allow partial GUID match)
        var persisted = _sessionManager.GetPersistedSessions()
            .FirstOrDefault(s => s.SessionId.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase));

        if (persisted == null)
        {
            AnsiConsole.MarkupLine($"[red]Session starting with '{sessionId}' not found[/]");
            return;
        }

        var name = displayName ?? $"resumed-{persisted.SessionId[..8]}";

        try
        {
            var session = await _sessionManager.ResumeSessionAsync(persisted.SessionId, name, _cts.Token);
            AnsiConsole.MarkupLine($"[green]✓ Resumed session '[cyan]{session.Name}[/]' (ID: {persisted.SessionId[..8]}...)[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to resume session: {ex.Message}[/]");
        }
    }

    private static async Task HandleNewSessionAsync(string? name, string? model)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]Usage: /new <name> [model][/]");
            return;
        }

        try
        {
            var session = await _sessionManager.CreateSessionAsync(name, model, _cts.Token);
            AnsiConsole.MarkupLine($"[green]✓ Created session '[cyan]{session.Name}[/]' with model [yellow]{session.Model}[/][/]");
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
        }
    }

    private static void HandleSwitchSession(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]Usage: /switch <name>[/]");
            return;
        }

        if (_sessionManager.SwitchSession(name))
        {
            AnsiConsole.MarkupLine($"[green]✓ Switched to session '[cyan]{name}[/]'[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Session '{name}' not found[/]");
        }
    }

    private static void HandleListSessions()
    {
        var sessions = _sessionManager.GetAllSessions().ToList();
        
        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No active sessions. Use /new <name> to create one.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Model")
            .AddColumn("Messages")
            .AddColumn("Created")
            .AddColumn("Status");

        var activeName = _sessionManager.ActiveSessionName;
        
        foreach (var session in sessions)
        {
            var isActive = session.Name == activeName;
            var name = isActive ? $"[cyan]{session.Name}[/] *" : session.Name;
            var status = session.IsProcessing ? "[yellow]processing[/]" : "[green]idle[/]";
            
            table.AddRow(
                name,
                session.Model,
                session.MessageCount.ToString(),
                session.CreatedAt.ToString("HH:mm:ss"),
                status
            );
        }

        AnsiConsole.Write(table);
    }

    private static async Task HandleCloseSessionAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]Usage: /close <name>[/]");
            return;
        }

        if (await _sessionManager.CloseSessionAsync(name))
        {
            AnsiConsole.MarkupLine($"[green]✓ Closed session '[cyan]{name}[/]'[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Session '{name}' not found[/]");
        }
    }

    private static void HandleStatus()
    {
        var session = _sessionManager.GetActiveSession();
        
        if (session == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active session. Use /new <name> to create one.[/]");
            return;
        }

        var panel = new Panel(new Rows(
            new Markup($"[bold]Name:[/] [cyan]{session.Name}[/]"),
            new Markup($"[bold]Model:[/] [yellow]{session.Model}[/]"),
            new Markup($"[bold]Messages:[/] {session.MessageCount}"),
            new Markup($"[bold]Created:[/] {session.CreatedAt:yyyy-MM-dd HH:mm:ss}"),
            new Markup($"[bold]Status:[/] {(session.IsProcessing ? "[yellow]processing[/]" : "[green]idle[/]")}")
        ))
        {
            Header = new PanelHeader("Session Status"),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);

        if (session.History.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Recent History:[/]");
            foreach (var msg in session.History.TakeLast(4))
            {
                var role = msg.Role == "user" ? "[blue]You[/]" : "[green]Copilot[/]";
                var content = msg.Content.Length > 80 
                    ? msg.Content[..77] + "..." 
                    : msg.Content;
                content = Markup.Escape(content.ReplaceLineEndings(" "));
                AnsiConsole.MarkupLine($"  {role}: {content}");
            }
        }
    }

    private static void HandleClear()
    {
        var session = _sessionManager.GetActiveSession();
        
        if (session == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active session[/]");
            return;
        }

        session.ClearHistory();
        AnsiConsole.MarkupLine($"[green]✓ Cleared history for '[cyan]{session.Name}[/]'[/]");
    }

    private static async Task HandlePromptAsync(string prompt)
    {
        var session = _sessionManager.GetActiveSession();
        
        if (session == null)
        {
            AnsiConsole.MarkupLine("[yellow]No active session. Use /new <name> to create one first.[/]");
            return;
        }

        if (session.IsProcessing)
        {
            AnsiConsole.MarkupLine("[yellow]Session is already processing a request. Please wait.[/]");
            return;
        }

        session.OnContentReceived += OnContent;
        session.OnError += OnError;

        AnsiConsole.Markup("[green]");
        
        try
        {
            await session.SendPromptAsync(prompt, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]Cancelled[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]Error: {ex.Message}[/]");
        }
        finally
        {
            AnsiConsole.Markup("[/]");
            Console.WriteLine();
            session.OnContentReceived -= OnContent;
            session.OnError -= OnError;
        }

        static void OnContent(string content) => Console.Write(content);
        static void OnError(string error) => AnsiConsole.MarkupLine($"\n[red]Error: {Markup.Escape(error)}[/]");
    }

    private static void PrintCommandHelp()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Command")
            .AddColumn("Description");

        table.AddRow("/new <name> [model]", "Create a new agent session (default model: gpt-5.2)");
        table.AddRow("/resume <id> [name]", "Resume a saved session by GUID (partial match OK)");
        table.AddRow("/saved", "List saved sessions from ~/.copilot/session-state");
        table.AddRow("/switch <name>", "Switch to an existing session");
        table.AddRow("/list", "List all active sessions");
        table.AddRow("/close <name>", "Close a session");
        table.AddRow("/status", "Show current session info");
        table.AddRow("/clear", "Clear current session history");
        table.AddRow("/help", "Show this help");
        table.AddRow("/quit", "Exit application");
        table.AddRow("<text>", "Send prompt to current session");

        AnsiConsole.Write(table);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AutoPilot - Multi-Agent Copilot Session Manager");
        Console.WriteLine();
        Console.WriteLine("Usage: autopilot [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help    Show this help message");
        Console.WriteLine();
        Console.WriteLine("Interactive commands:");
        Console.WriteLine("  /new <name> [model]  Create a new agent session");
        Console.WriteLine("  /switch <name>       Switch to an existing session");
        Console.WriteLine("  /list                List all sessions");
        Console.WriteLine("  /close <name>        Close a session");
        Console.WriteLine("  /status              Show current session info");
        Console.WriteLine("  /clear               Clear current session history");
        Console.WriteLine("  /quit                Exit application");
    }

    private static async Task ShutdownAsync()
    {
        AnsiConsole.MarkupLine("\n[yellow]Closing all sessions...[/]");
        await _sessionManager.DisposeAsync();
        AnsiConsole.MarkupLine("[green]Goodbye![/]");
    }
}
