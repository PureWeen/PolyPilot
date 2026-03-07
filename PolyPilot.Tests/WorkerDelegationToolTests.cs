using Microsoft.Extensions.AI;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for WorkerDelegationContext and WorkerDelegationTool.
/// </summary>
public class WorkerDelegationToolTests
{
    // --- WorkerDelegationContext ---

    [Fact]
    public void Context_InitialState_IsEmpty()
    {
        var ctx = new WorkerDelegationContext();
        Assert.Empty(ctx.WorkerNames);
        Assert.Empty(ctx.OriginalPrompt);
        Assert.Empty(ctx.DispatchedResults);
    }

    [Fact]
    public void Context_Reset_SetsProperties()
    {
        var ctx = new WorkerDelegationContext();
        var workers = new List<string> { "alice", "bob" };
        using var cts = new CancellationTokenSource();

        ctx.Reset("do the work", workers, cts.Token);

        Assert.Equal("do the work", ctx.OriginalPrompt);
        Assert.Equal(2, ctx.WorkerNames.Count);
        Assert.Empty(ctx.DispatchedResults);
    }

    [Fact]
    public void Context_Reset_ClearsOldResults()
    {
        var ctx = new WorkerDelegationContext();
        ctx.Reset("prompt1", new List<string> { "w1" }, CancellationToken.None);
        ctx.AddResult(new ToolDispatchedResult("w1", "resp", true, null, TimeSpan.Zero));
        Assert.Single(ctx.DispatchedResults);

        ctx.Reset("prompt2", new List<string> { "w2" }, CancellationToken.None);
        Assert.Empty(ctx.DispatchedResults);
    }

    [Fact]
    public void Context_GetNextWorker_RoundRobins()
    {
        var ctx = new WorkerDelegationContext();
        ctx.Reset("p", new List<string> { "a", "b", "c" }, CancellationToken.None);

        var first = ctx.GetNextWorker();
        var second = ctx.GetNextWorker();
        var third = ctx.GetNextWorker();
        var fourth = ctx.GetNextWorker(); // wraps back to a

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal(first, fourth); // round-robin wraps
    }

    [Fact]
    public void Context_GetNextWorker_NoWorkers_ReturnsNull()
    {
        var ctx = new WorkerDelegationContext();
        Assert.Null(ctx.GetNextWorker());
    }

    [Fact]
    public void Context_AddResult_AccumulatesResults()
    {
        var ctx = new WorkerDelegationContext();
        ctx.AddResult(new ToolDispatchedResult("w1", "r1", true, null, TimeSpan.Zero));
        ctx.AddResult(new ToolDispatchedResult("w2", null, false, "err", TimeSpan.Zero));

        Assert.Equal(2, ctx.DispatchedResults.Count);
        Assert.True(ctx.DispatchedResults[0].Success);
        Assert.False(ctx.DispatchedResults[1].Success);
        Assert.Equal("err", ctx.DispatchedResults[1].Error);
    }

    // --- WorkerDelegationTool ---

    [Fact]
    public void Tool_ToolName_IsTask()
    {
        Assert.Equal("task", WorkerDelegationTool.ToolName);
    }

    [Fact]
    public async Task Tool_Invoke_DispatchesToWorkerAndRecordsResult()
    {
        var ctx = new WorkerDelegationContext();
        ctx.Reset("original", new List<string> { "worker1" }, CancellationToken.None);

        var called = false;
        Task<(bool, string?, string?, TimeSpan)> Execute(string w, string task, string orig, CancellationToken ct)
        {
            called = true;
            Assert.Equal("worker1", w);
            Assert.Equal("original", orig);
            return Task.FromResult<(bool, string?, string?, TimeSpan)>((true, "done", null, TimeSpan.FromSeconds(1)));
        }

        var fn = WorkerDelegationTool.CreateFunction(ctx, Execute);
        Assert.Equal("task", fn.Name);

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["prompt"] = "do something" });
        var result = await fn.InvokeAsync(args);
        var resultStr = result?.ToString() ?? "";

        Assert.True(called);
        Assert.Contains("worker1", resultStr);
        Assert.Single(ctx.DispatchedResults);
        Assert.True(ctx.DispatchedResults[0].Success);
    }

    [Fact]
    public async Task Tool_Invoke_NoWorkers_ReturnsErrorJson()
    {
        var ctx = new WorkerDelegationContext();
        // No Reset — WorkerNames is empty

        Task<(bool, string?, string?, TimeSpan)> Execute(string w, string task, string orig, CancellationToken ct)
            => Task.FromResult<(bool, string?, string?, TimeSpan)>((true, "done", null, TimeSpan.Zero));

        var fn = WorkerDelegationTool.CreateFunction(ctx, Execute);

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["prompt"] = "something" });
        var result = await fn.InvokeAsync(args);
        var resultStr = result?.ToString() ?? "";

        Assert.Contains("error", resultStr);
        Assert.Contains("No workers", resultStr);
    }

    [Fact]
    public async Task Tool_Invoke_WorkerFailure_RecordsFailure()
    {
        var ctx = new WorkerDelegationContext();
        ctx.Reset("orig", new List<string> { "w1" }, CancellationToken.None);

        Task<(bool, string?, string?, TimeSpan)> Execute(string w, string task, string orig, CancellationToken ct)
            => Task.FromResult<(bool, string?, string?, TimeSpan)>((false, null, "worker crashed", TimeSpan.Zero));

        var fn = WorkerDelegationTool.CreateFunction(ctx, Execute);
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["prompt"] = "task" });
        var result = await fn.InvokeAsync(args);
        var resultStr = result?.ToString() ?? "";

        Assert.Contains("error", resultStr);
        Assert.Single(ctx.DispatchedResults);
        Assert.False(ctx.DispatchedResults[0].Success);
        Assert.Equal("worker crashed", ctx.DispatchedResults[0].Error);
    }

    [Fact]
    public void ToolDispatchedResult_Record_Properties()
    {
        var dur = TimeSpan.FromSeconds(2);
        var r = new ToolDispatchedResult("alice", "hello", true, null, dur);
        Assert.Equal("alice", r.WorkerName);
        Assert.Equal("hello", r.Response);
        Assert.True(r.Success);
        Assert.Null(r.Error);
        Assert.Equal(dur, r.Duration);
    }
}
