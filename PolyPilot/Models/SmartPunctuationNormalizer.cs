namespace PolyPilot.Models;

/// <summary>
/// Normalizes smart punctuation characters (em dash, en dash, smart quotes)
/// back to their ASCII equivalents. macOS/WebKit text substitution can convert
/// "--" to "—" in textareas, breaking CLI commands that use flag syntax.
/// </summary>
public static class SmartPunctuationNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text
            .Replace("\u2014", "--")  // em dash → --
            .Replace("\u2013", "-")   // en dash → -
            .Replace("\u2018", "'")   // left single quote → '
            .Replace("\u2019", "'")   // right single quote → '
            .Replace("\u201C", "\"")  // left double quote → "
            .Replace("\u201D", "\""); // right double quote → "
    }
}
