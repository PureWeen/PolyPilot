using System.Text.Json;

namespace PolyPilot.Models;

public static class ChatCopyPayloads
{
    public static string? GetPrimaryCopyText(ChatMessage message)
    {
        return message.MessageType switch
        {
            ChatMessageType.User => FirstNonEmpty(message.OriginalContent, message.Content),
            ChatMessageType.Assistant or ChatMessageType.Reasoning or ChatMessageType.Error or ChatMessageType.System or ChatMessageType.ShellOutput or ChatMessageType.Reflection or ChatMessageType.Diff
                => NullIfEmpty(message.Content),
            ChatMessageType.ToolCall when message.ToolName == "task_complete" && !IsUnusableToolResult(message.Content)
                => NullIfEmpty(message.Content),
            _ => null
        };
    }

    public static string? GetOrchestrationPromptCopyText(ChatMessage message)
    {
        if (message.MessageType != ChatMessageType.User || string.IsNullOrEmpty(message.OriginalContent))
        {
            return null;
        }

        return NullIfEmpty(message.Content);
    }

    public static string? GetToolInputCopyText(ChatMessage message) => GetToolInputCopyText(message.ToolName, message.ToolInput);

    public static string? GetToolInputCopyText(string? toolName, string? toolInput)
    {
        if (string.IsNullOrWhiteSpace(toolInput))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolInput);
            var root = doc.RootElement;

            return toolName switch
            {
                "bash" => FirstNonEmpty(ExtractJsonPropFull(root, "command"), toolInput),
                "sql" => FirstNonEmpty(ExtractJsonPropFull(root, "query"), toolInput),
                "edit" => FirstNonEmpty(ExtractJsonPropFull(root, "path"), toolInput),
                "create" => FirstNonEmpty(ExtractJsonPropFull(root, "path"), toolInput),
                "view" => FormatViewCopy(root, toolInput),
                "grep" => FormatGrepCopy(root, toolInput),
                "glob" => FirstNonEmpty(ExtractJsonPropFull(root, "pattern"), toolInput),
                "web_fetch" => FirstNonEmpty(ExtractJsonPropFull(root, "url"), toolInput),
                "web_search" => FirstNonEmpty(ExtractJsonPropFull(root, "query"), toolInput),
                "task" => FirstNonEmpty(ExtractJsonPropFull(root, "description"), toolInput),
                "ask_user" => FirstNonEmpty(ExtractJsonPropFull(root, "message"), toolInput),
                _ => toolInput
            };
        }
        catch
        {
            return toolInput;
        }
    }

    public static string? GetToolOutputCopyText(ChatMessage message)
    {
        if (message.MessageType != ChatMessageType.ToolCall || IsUnusableToolResult(message.Content))
        {
            return null;
        }

        return NullIfEmpty(message.Content);
    }

    public static bool IsUnusableToolResult(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return true;
        }

        if (content.StartsWith("GitHub.Copilot.SDK.", StringComparison.Ordinal))
        {
            return true;
        }

        return content is "(no result)" or "Intent logged";
    }

    private static string? FormatViewCopy(JsonElement root, string fallback)
    {
        var path = ExtractJsonPropFull(root, "path");
        if (string.IsNullOrEmpty(path))
        {
            return fallback;
        }

        if (root.TryGetProperty("view_range", out var range))
        {
            return $"{path} lines {range}";
        }

        return path;
    }

    private static string? FormatGrepCopy(JsonElement root, string fallback)
    {
        var pattern = ExtractJsonPropFull(root, "pattern");
        if (string.IsNullOrEmpty(pattern))
        {
            return fallback;
        }

        var target = ExtractJsonPropFull(root, "glob")
            ?? ExtractJsonPropFull(root, "path")
            ?? ".";

        return $"grep '{ShellEscape(pattern)}' '{ShellEscape(target)}'";
    }

    private static string ShellEscape(string s) => s.Replace("'", @"'\''", StringComparison.Ordinal);

    private static string? ExtractJsonPropFull(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
