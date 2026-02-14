using PolyPilot.Models;

namespace PolyPilot.Tests;

public class FiestaModelTests
{
    [Fact]
    public void LinkedWorker_DefaultId_IsGenerated()
    {
        var worker = new FiestaLinkedWorker();
        Assert.False(string.IsNullOrWhiteSpace(worker.Id));
    }

    [Fact]
    public void FiestaState_RoundTrips_LinkedWorkers()
    {
        var state = new FiestaState
        {
            LinkedWorkers = new()
            {
                new FiestaLinkedWorker
                {
                    Id = "worker-1",
                    Name = "mac-mini",
                    Hostname = "mac-mini.local",
                    BridgeUrl = "http://192.168.1.20:4322",
                    Token = "secret-token"
                }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<FiestaState>(json);

        Assert.NotNull(restored);
        Assert.Single(restored!.LinkedWorkers);
        Assert.Equal("mac-mini", restored.LinkedWorkers[0].Name);
        Assert.Equal("http://192.168.1.20:4322", restored.LinkedWorkers[0].BridgeUrl);
    }
}

public class FiestaBridgePayloadTests
{
    [Fact]
    public void FiestaAssignPayload_RoundTrip()
    {
        var payload = new FiestaAssignPayload
        {
            TaskId = "task-1",
            HostSessionName = "HostSession",
            FiestaName = "Sprint-Fiesta",
            Prompt = "@mac-mini run tests"
        };

        var msg = BridgeMessage.Create(BridgeMessageTypes.FiestaAssign, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<FiestaAssignPayload>();

        Assert.Equal("task-1", restored!.TaskId);
        Assert.Equal("HostSession", restored.HostSessionName);
        Assert.Equal("Sprint-Fiesta", restored.FiestaName);
    }

    [Fact]
    public void FiestaTaskCompletePayload_RoundTrip()
    {
        var payload = new FiestaTaskCompletePayload
        {
            TaskId = "task-2",
            WorkerName = "mac-mini",
            Success = true,
            Summary = "Completed successfully"
        };

        var msg = BridgeMessage.Create(BridgeMessageTypes.FiestaTaskComplete, payload);
        var restored = BridgeMessage.Deserialize(msg.Serialize())!.GetPayload<FiestaTaskCompletePayload>();

        Assert.NotNull(restored);
        Assert.True(restored!.Success);
        Assert.Equal("Completed successfully", restored.Summary);
    }

    [Fact]
    public void FiestaMessageTypes_AreStable()
    {
        Assert.Equal("fiesta_assign", BridgeMessageTypes.FiestaAssign);
        Assert.Equal("fiesta_task_started", BridgeMessageTypes.FiestaTaskStarted);
        Assert.Equal("fiesta_task_delta", BridgeMessageTypes.FiestaTaskDelta);
        Assert.Equal("fiesta_task_complete", BridgeMessageTypes.FiestaTaskComplete);
        Assert.Equal("fiesta_task_error", BridgeMessageTypes.FiestaTaskError);
    }
}
