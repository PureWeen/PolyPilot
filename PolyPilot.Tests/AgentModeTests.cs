using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class AgentModeTests
{
    [Fact]
    public void SendMessagePayload_AgentMode_DefaultsToNull()
    {
        var payload = new SendMessagePayload { SessionName = "s1", Message = "hello" };
        Assert.Null(payload.AgentMode);
    }

    [Theory]
    [InlineData("autopilot")]
    [InlineData("plan")]
    [InlineData("interactive")]
    [InlineData("shell")]
    public void SendMessagePayload_AgentMode_RoundTrips(string mode)
    {
        var payload = new SendMessagePayload
        {
            SessionName = "test",
            Message = "do something",
            AgentMode = mode
        };

        var msg = BridgeMessage.Create(BridgeMessageTypes.SendMessage, payload);
        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<BridgeMessage>(json);

        Assert.NotNull(deserialized);
        var restored = deserialized!.GetPayload<SendMessagePayload>();
        Assert.NotNull(restored);
        Assert.Equal(mode, restored!.AgentMode);
        Assert.Equal("test", restored.SessionName);
        Assert.Equal("do something", restored.Message);
    }

    [Fact]
    public void SendMessagePayload_NullAgentMode_OmittedInJson()
    {
        var payload = new SendMessagePayload
        {
            SessionName = "s1",
            Message = "hello"
        };

        var json = JsonSerializer.Serialize(payload);
        // Null properties should still deserialize cleanly
        var restored = JsonSerializer.Deserialize<SendMessagePayload>(json);
        Assert.NotNull(restored);
        Assert.Null(restored!.AgentMode);
    }

    [Fact]
    public void SendMessagePayload_AgentMode_BackwardCompatible()
    {
        // Old clients send JSON without AgentMode field - should deserialize as null
        var json = """{"SessionName":"s1","Message":"hello"}""";
        var payload = JsonSerializer.Deserialize<SendMessagePayload>(json);
        Assert.NotNull(payload);
        Assert.Null(payload!.AgentMode);
        Assert.Equal("s1", payload.SessionName);
        Assert.Equal("hello", payload.Message);
    }
}
