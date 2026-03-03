namespace PolyPilot.Services;

public enum CommandType
{
    NewSession,
    ResumeSession,
    ListPersistedSessions,
    SwitchSession,
    ListSessions,
    CloseSession,
    Status,
    Model,
    Clear,
    Help,
    Quit,
    Prompt
}

public record ParsedCommand(CommandType Type, string? Argument = null, string? SecondArgument = null);

public static class CommandParser
{
    public static ParsedCommand Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ParsedCommand(CommandType.Prompt, string.Empty);

        var trimmed = input.Trim();
        
        if (!trimmed.StartsWith('/'))
            return new ParsedCommand(CommandType.Prompt, trimmed);

        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var arg1 = parts.Length > 1 ? parts[1] : null;
        var arg2 = parts.Length > 2 ? parts[2] : null;

        return command switch
        {
            "/new" => new ParsedCommand(CommandType.NewSession, arg1, arg2),
            "/resume" or "/r" => new ParsedCommand(CommandType.ResumeSession, arg1, arg2),
            "/saved" or "/persisted" => new ParsedCommand(CommandType.ListPersistedSessions),
            "/switch" or "/sw" => new ParsedCommand(CommandType.SwitchSession, arg1),
            "/list" or "/ls" => new ParsedCommand(CommandType.ListSessions),
            "/close" => new ParsedCommand(CommandType.CloseSession, arg1),
            "/status" => new ParsedCommand(CommandType.Status),
            "/model" => new ParsedCommand(CommandType.Model),
            "/clear" => new ParsedCommand(CommandType.Clear),
            "/help" or "/?" => new ParsedCommand(CommandType.Help),
            "/quit" or "/exit" or "/q" => new ParsedCommand(CommandType.Quit),
            _ => new ParsedCommand(CommandType.Prompt, trimmed)
        };
    }
}
