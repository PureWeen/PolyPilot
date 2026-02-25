using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ResumeSessionInputTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ResumeSessionInputTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public void TryParseResumeSessionId_ValidGuidWithWhitespace_NormalizesAndReturnsTrue()
    {
        var svc = CreateService();
        var guid = Guid.NewGuid();
        var input = $"  {guid.ToString().ToUpperInvariant()}  ";

        var ok = svc.TryParseResumeSessionId(input, out var sessionId);

        Assert.True(ok);
        Assert.Equal(guid.ToString(), sessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("feature/fix-123")]
    public void TryParseResumeSessionId_InvalidInput_ReturnsFalse(string input)
    {
        var svc = CreateService();

        var ok = svc.TryParseResumeSessionId(input, out var sessionId);

        Assert.False(ok);
        Assert.Equal(string.Empty, sessionId);
    }

    [Fact]
    public void GetResumeDisplayName_UsesAlias_WhenAliasExists()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();
        svc.SetSessionAlias(sessionId, "My Existing Alias");

        var displayName = svc.GetResumeDisplayName(sessionId);

        Assert.Equal("My Existing Alias", displayName);
    }

    [Fact]
    public void GetResumeDisplayName_WithoutAlias_UsesShortSessionId()
    {
        var svc = CreateService();
        var sessionId = Guid.NewGuid().ToString();

        var displayName = svc.GetResumeDisplayName(sessionId);

        Assert.Equal($"resumed-{sessionId[..8]}", displayName);
    }
}
