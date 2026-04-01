using System.Text.RegularExpressions;

namespace PolyPilot.Models;

/// <summary>
/// Helpers for natural-language cross-session @mention routing.
/// </summary>
public static class RoutingHelper
{
    private static readonly Regex AtMentionRegex = new(@"(?<!\S)@[\w.\-]+", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespace = new(@"\s{2,}", RegexOptions.Compiled);

    // Pattern A — verb + optional filler/noun + explicit "to" or ":" marker.
    // Requires "to" or ":" at end to avoid false positives (e.g. "forward slash commands").
    // Word boundaries on "a" and "an" prevent "a" from matching the first char of "about".
    // Examples: "please forward to :", "send a message to", "please pass this info to"
    private static readonly Regex RoutingToColonRegex = new(
        @"^(?:(?:please|can\s+you|could\s+you|would\s+you)\s+)?(?:tell|send|forward|pass(?:\s+(?:along|on))?|relay|share|ask|message|route|give)(?:\s+(?:this|the|a\b|an\b|some|any|them|him|her|along|on))*\s*(?:info(?:rmation)?|message|note|update|news|details|content|context|following)?\s*(?:to\s*|:\s*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern B — verb directly followed by explicit "that" or "saying" connector.
    // Examples: "please tell that X", "send saying X"
    private static readonly Regex RoutingThatSayingRegex = new(
        @"^(?:(?:please|can\s+you|could\s+you|would\s+you)\s+)?(?:tell|send|forward|pass|relay|share|ask|message|route|give)\s+(?:that|saying)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Strips residual connectors after "to" or ":" stripping: "that ...", "saying ...", ": ..."
    private static readonly Regex ConnectorRegex = new(
        @"^(?:that|saying|:)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Given a message that has already had @mention tokens stripped (strippedMessage),
    /// extracts the actual content the user wants forwarded by removing routing-instruction
    /// wrappers. Falls back to the full original prompt (minus @tokens) if stripping leaves
    /// nothing substantive, so the target session always receives useful context.
    ///
    /// Examples:
    ///   "please tell that the tests pass"      -> "the tests pass"
    ///   "can you forward to : hello world"     -> "hello world"
    ///   "please send a message to : hi"        -> "hi"
    ///   "please pass this info to"             -> "please pass this info to"  (fallback)
    ///   "forward slash commands are important" -> "forward slash commands are important"
    ///   "hello world"                          -> "hello world"  (unchanged)
    /// </summary>
    public static string ExtractForwardedContent(string strippedMessage, string originalPrompt)
    {
        var s = strippedMessage.Trim();

        // Try Pattern A: verb + ... + explicit "to" or ":"
        var matchA = RoutingToColonRegex.Match(s);
        if (matchA.Success && matchA.Length > 0)
        {
            s = s[matchA.Length..].Trim();
            // Strip any residual ": " or "that/saying " left after the "to" marker
            s = ConnectorRegex.Replace(s, "").Trim();
        }
        else
        {
            // Try Pattern B: verb + "that/saying"
            var matchB = RoutingThatSayingRegex.Match(s);
            if (matchB.Success && matchB.Length > 0)
                s = s[matchB.Length..].Trim();
        }

        // Collapse whitespace
        s = CollapseWhitespace.Replace(s, " ").Trim();

        // Fallback: if stripping left nothing substantive (< 3 chars), return the original
        // prompt with @tokens removed so the target still gets the full routing instruction
        // as context (e.g. "please pass this info to @Session" -> "please pass this info to")
        if (s.Length < 3)
        {
            var fallback = AtMentionRegex.Replace(originalPrompt, "").Trim();
            fallback = CollapseWhitespace.Replace(fallback, " ").Trim();
            return string.IsNullOrWhiteSpace(fallback) ? strippedMessage : fallback;
        }

        return s;
    }
}
