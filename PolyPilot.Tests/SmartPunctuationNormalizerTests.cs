using PolyPilot.Models;

namespace PolyPilot.Tests;

public class SmartPunctuationNormalizerTests
{
    [Fact]
    public void EmDash_ConvertedToDoubleDash()
    {
        var input = "git log \u2014oneline";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("git log --oneline", result);
    }

    [Fact]
    public void EnDash_ConvertedToSingleDash()
    {
        var input = "pages 1\u20135";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("pages 1-5", result);
    }

    [Fact]
    public void SmartSingleQuotes_ConvertedToAscii()
    {
        var input = "\u2018hello\u2019";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("'hello'", result);
    }

    [Fact]
    public void SmartDoubleQuotes_ConvertedToAscii()
    {
        var input = "\u201Chello world\u201D";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("\"hello world\"", result);
    }

    [Fact]
    public void CliCommandWithFlags_PreservesDoubleDash()
    {
        // This is the exact bug scenario: user types "git log --oneline"
        // but WebKit converts it to "git log —oneline"
        var input = "git log \u2014oneline \u2014graph";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("git log --oneline --graph", result);
    }

    [Fact]
    public void MixedSmartPunctuation_AllNormalized()
    {
        var input = "echo \u201CHello\u201D \u2014 it\u2019s a \u2018test\u2019 \u2013 done";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("echo \"Hello\" -- it's a 'test' - done", result);
    }

    [Fact]
    public void NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SmartPunctuationNormalizer.Normalize(null!));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", SmartPunctuationNormalizer.Normalize(""));
    }

    [Fact]
    public void PlainAscii_UnchangedPassthrough()
    {
        var input = "git log --oneline --graph";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void BangMode_CommandWithFlags()
    {
        // "! mode" sends shell commands — em dash breaks flag syntax
        var input = "! ls \u2014la /tmp";
        var result = SmartPunctuationNormalizer.Normalize(input);
        Assert.Equal("! ls --la /tmp", result);
    }
}
