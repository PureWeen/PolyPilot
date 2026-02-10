using System.Text.Json;

namespace AutoPilot.App.Tests;

/// <summary>
/// Tests for the FormatToolResult and ExtractToolInput logic patterns
/// used by CopilotService.HandleSessionEvent. These are static helper methods
/// that use reflection to extract properties from SDK event data objects.
/// Since the SDK types aren't directly available, we test with anonymous
/// objects that match the expected property patterns.
/// </summary>
public class ToolResultFormattingTests
{
    // Mirrors CopilotService.FormatToolResult logic
    private static string FormatToolResult(object? result)
    {
        if (result == null) return "";
        if (result is string str) return str;
        try
        {
            var resultType = result.GetType();
            foreach (var propName in new[] { "DetailedContent", "detailedContent", "Content", "content", "Message", "message", "Text", "text", "Value", "value" })
            {
                var prop = resultType.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(result)?.ToString();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (json != "{}" && json != "null") return json;
        }
        catch { }
        return result.ToString() ?? "";
    }

    // Mirrors CopilotService.ExtractToolInput logic
    private static string? ExtractToolInput(object? data)
    {
        if (data == null) return null;
        try
        {
            var type = data.GetType();
            foreach (var propName in new[] { "Input", "Arguments", "Args", "Parameters", "input", "arguments" })
            {
                var prop = type.GetProperty(propName);
                if (prop == null) continue;
                var val = prop.GetValue(data);
                if (val == null) continue;
                if (val is string s && !string.IsNullOrEmpty(s)) return s;
                try
                {
                    var json = JsonSerializer.Serialize(val, new JsonSerializerOptions { WriteIndented = false });
                    if (json != "{}" && json != "null" && json != "\"\"") return json;
                }
                catch { return val.ToString(); }
            }
        }
        catch { }
        return null;
    }

    [Fact]
    public void FormatToolResult_Null_ReturnsEmpty()
    {
        Assert.Equal("", FormatToolResult(null));
    }

    [Fact]
    public void FormatToolResult_String_ReturnsDirectly()
    {
        Assert.Equal("hello world", FormatToolResult("hello world"));
    }

    [Fact]
    public void FormatToolResult_ObjectWithContent_ReturnsContent()
    {
        var obj = new { Content = "file created successfully" };
        Assert.Equal("file created successfully", FormatToolResult(obj));
    }

    [Fact]
    public void FormatToolResult_ObjectWithDetailedContent_PrefersDetailedContent()
    {
        var obj = new { DetailedContent = "detailed info", Content = "basic info" };
        Assert.Equal("detailed info", FormatToolResult(obj));
    }

    [Fact]
    public void FormatToolResult_ObjectWithMessage_ReturnsMessage()
    {
        var obj = new { Message = "operation completed" };
        Assert.Equal("operation completed", FormatToolResult(obj));
    }

    [Fact]
    public void FormatToolResult_ObjectWithText_ReturnsText()
    {
        var obj = new { Text = "some text output" };
        Assert.Equal("some text output", FormatToolResult(obj));
    }

    [Fact]
    public void FormatToolResult_ObjectWithValue_ReturnsValue()
    {
        var obj = new { Value = "42" };
        Assert.Equal("42", FormatToolResult(obj));
    }

    [Fact]
    public void FormatToolResult_ObjectWithEmptyContent_SkipsToNext()
    {
        var obj = new { Content = "", Message = "fallback message" };
        Assert.Equal("fallback message", FormatToolResult(obj));
    }

    [Fact]
    public void FormatToolResult_ObjectWithNoKnownProps_SerializesToJson()
    {
        var obj = new { UnknownProp = "data", Count = 5 };
        var result = FormatToolResult(obj);
        Assert.Contains("UnknownProp", result);
        Assert.Contains("data", result);
    }

    [Fact]
    public void ExtractToolInput_Null_ReturnsNull()
    {
        Assert.Null(ExtractToolInput(null));
    }

    [Fact]
    public void ExtractToolInput_ObjectWithInput_ReturnsInput()
    {
        var obj = new { Input = "ls -la /tmp" };
        Assert.Equal("ls -la /tmp", ExtractToolInput(obj));
    }

    [Fact]
    public void ExtractToolInput_ObjectWithArguments_ReturnsArguments()
    {
        var obj = new { Arguments = "{\"path\": \"/tmp\"}" };
        Assert.Equal("{\"path\": \"/tmp\"}", ExtractToolInput(obj));
    }

    [Fact]
    public void ExtractToolInput_ObjectWithComplexInput_SerializesToJson()
    {
        var obj = new { Input = new { Command = "bash", Args = "-c ls" } };
        var result = ExtractToolInput(obj);
        Assert.NotNull(result);
        Assert.Contains("Command", result!);
        Assert.Contains("bash", result);
    }

    [Fact]
    public void ExtractToolInput_ObjectWithNoKnownProps_ReturnsNull()
    {
        var obj = new { SomethingElse = "data" };
        Assert.Null(ExtractToolInput(obj));
    }

    [Fact]
    public void ExtractToolInput_ObjectWithEmptyInput_ReturnsNull()
    {
        var obj = new { Input = "" };
        Assert.Null(ExtractToolInput(obj));
    }

    [Fact]
    public void ExtractToolInput_ObjectWithNullInput_SkipsToNext()
    {
        var obj = new { Input = (string?)null, Arguments = "fallback" };
        Assert.Equal("fallback", ExtractToolInput(obj));
    }
}
