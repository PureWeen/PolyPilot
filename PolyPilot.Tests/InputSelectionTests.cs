using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Regression tests for the input selection bug: @onkeydown on value-bound inputs
/// causes Blazor to re-render before the browser processes the keystroke, which
/// clears text selection and breaks multi-character delete (e.g., double-click + backspace).
/// Value-bound inputs must use @onkeyup instead.
/// </summary>
public class InputSelectionTests
{
    private static readonly string ComponentsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PolyPilot", "Components"));

    /// <summary>
    /// Scans all .razor files for inputs that have a value binding (value="@..." or @bind)
    /// combined with @onkeydown. This combination causes text selection to be cleared
    /// during re-render, breaking double-click + backspace. These inputs must use @onkeyup.
    /// </summary>
    [Fact]
    public void ValueBoundInputs_MustNotUse_OnKeyDown()
    {
        if (!Directory.Exists(ComponentsDir))
        {
            // Skip gracefully in CI or environments where source isn't available
            return;
        }

        var razorFiles = Directory.GetFiles(ComponentsDir, "*.razor", SearchOption.AllDirectories);
        Assert.NotEmpty(razorFiles);

        var violations = new List<string>();

        // Regex to match <input ...> or <textarea ...> tags (possibly multi-line).
        // Uses alternation to handle > inside quoted attributes (e.g., "() => Foo()").
        var tagPattern = new Regex(@"<(input|textarea)\b(?:[^>""']|""[^""]*""|'[^']*')*(?:/>|>)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var hasValueBinding = new Regex(@"(value=""@|@bind="")", RegexOptions.IgnoreCase);
        var hasOnKeyDown = new Regex(@"@onkeydown\b", RegexOptions.IgnoreCase);

        foreach (var file in razorFiles)
        {
            var content = File.ReadAllText(file);
            var matches = tagPattern.Matches(content);

            foreach (Match match in matches)
            {
                var tag = match.Value;
                if (hasValueBinding.IsMatch(tag) && hasOnKeyDown.IsMatch(tag))
                {
                    var lineNumber = content[..match.Index].Count(c => c == '\n') + 1;
                    var relativePath = Path.GetRelativePath(ComponentsDir, file);
                    violations.Add($"{relativePath}:{lineNumber} - input has value binding + @onkeydown (should use @onkeyup)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Found value-bound inputs using @onkeydown instead of @onkeyup. " +
            "This causes text selection to be cleared during Blazor re-render, " +
            "breaking double-click + backspace.\n\nViolations:\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Verifies the specific inputs that were fixed use @onkeyup.
    /// </summary>
    [Theory]
    [InlineData("Layout/CreateSessionForm.razor", "ns-name", "@onkeyup")]
    [InlineData("Layout/CreateSessionForm.razor", "wt-branch-input", "@onkeyup")]
    [InlineData("Layout/SessionListItem.razor", "rename-input", "@onkeyup")]
    [InlineData("SessionCard.razor", "card-rename-input", "@onkeyup")]
    public void SpecificInputs_UseOnKeyUp(string relativePath, string cssClass, string expectedEvent)
    {
        var filePath = Path.Combine(ComponentsDir, relativePath);
        if (!File.Exists(filePath))
        {
            return; // Skip gracefully if source not available
        }

        var content = File.ReadAllText(filePath);

        // Find input tags with the specified CSS class (handles > inside quoted attrs)
        var tagPattern = new Regex($@"<input\b(?:[^>""']|""[^""]*""|'[^']*')*class=""[^""]*{Regex.Escape(cssClass)}[^""]*""(?:[^>""']|""[^""]*""|'[^']*')*(?:/>|>)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var matches = tagPattern.Matches(content);
        Assert.True(matches.Count > 0, $"No input with class '{cssClass}' found in {relativePath}");

        foreach (Match match in matches)
        {
            var tag = match.Value;
            Assert.Contains(expectedEvent, tag);
            Assert.DoesNotContain("@onkeydown", tag);
        }
    }
}
