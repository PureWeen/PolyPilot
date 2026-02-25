using PolyPilot.Services;
using Xunit;

namespace PolyPilot.Tests;

public class CommandParserTests
{
    [Fact]
    public void Model_Command_Returns_ModelType()
    {
        var result = CommandParser.Parse("/model");
        Assert.Equal(CommandType.Model, result.Type);
    }

    [Fact]
    public void Model_Command_CaseInsensitive()
    {
        var result = CommandParser.Parse("/MODEL");
        Assert.Equal(CommandType.Model, result.Type);
    }

    [Fact]
    public void Model_Command_WithWhitespace()
    {
        var result = CommandParser.Parse("  /model  ");
        Assert.Equal(CommandType.Model, result.Type);
    }

    [Fact]
    public void Status_Command_Still_Works()
    {
        var result = CommandParser.Parse("/status");
        Assert.Equal(CommandType.Status, result.Type);
    }

    [Fact]
    public void Help_Command_Still_Works()
    {
        var result = CommandParser.Parse("/help");
        Assert.Equal(CommandType.Help, result.Type);
    }

    [Fact]
    public void Plain_Text_Returns_Prompt()
    {
        var result = CommandParser.Parse("hello world");
        Assert.Equal(CommandType.Prompt, result.Type);
        Assert.Equal("hello world", result.Argument);
    }

    [Fact]
    public void Unknown_SlashCommand_Returns_Prompt()
    {
        var result = CommandParser.Parse("/unknown");
        Assert.Equal(CommandType.Prompt, result.Type);
    }

    [Fact]
    public void Empty_Input_Returns_Prompt()
    {
        var result = CommandParser.Parse("");
        Assert.Equal(CommandType.Prompt, result.Type);
    }
}
