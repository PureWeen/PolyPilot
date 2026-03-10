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
        ctx.FullReset("original", new List<string> { "worker1" }, CancellationToken.None);

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

        // Callback returns immediately with "dispatched" status (parallel dispatch)
        Assert.Contains("dispatched", resultStr);
        Assert.Contains("worker1", resultStr);
        Assert.Single(ctx.DispatchedResults); // Placeholder result recorded
        Assert.True(ctx.DispatchedResults[0].Success);

        // Actual result is in PendingTasks — await it
        var pendingResults = await ctx.AwaitAllPendingAsync();
        Assert.True(called);
        Assert.Single(pendingResults);
        Assert.True(pendingResults[0].Success);
        Assert.Equal("done", pendingResults[0].Response);
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
        ctx.FullReset("orig", new List<string> { "w1" }, CancellationToken.None);

        Task<(bool, string?, string?, TimeSpan)> Execute(string w, string task, string orig, CancellationToken ct)
            => Task.FromResult<(bool, string?, string?, TimeSpan)>((false, null, "worker crashed", TimeSpan.Zero));

        var fn = WorkerDelegationTool.CreateFunction(ctx, Execute);
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["prompt"] = "task" });
        var result = await fn.InvokeAsync(args);
        var resultStr = result?.ToString() ?? "";

        // Callback returns "dispatched" immediately (parallel dispatch)
        Assert.Contains("dispatched", resultStr);
        Assert.Single(ctx.DispatchedResults); // Placeholder (success=true, worker was dispatched)

        // Actual failure is in PendingTasks
        var pendingResults = await ctx.AwaitAllPendingAsync();
        Assert.Single(pendingResults);
        Assert.False(pendingResults[0].Success);
        Assert.Equal("worker crashed", pendingResults[0].Error);
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

    [Fact]
    public void Context_DispatchedResults_ReturnsConcurrentSnapshot()
    {
        // DispatchedResults must return a snapshot (not the live list) so concurrent callers
        // can't observe a partially-mutated list.
        var ctx = new WorkerDelegationContext();
        ctx.Reset("p", new List<string> { "w1", "w2" }, CancellationToken.None);

        ctx.AddResult(new ToolDispatchedResult("w1", "r1", true, null, TimeSpan.Zero));
        var snapshot = ctx.DispatchedResults; // capture snapshot

        // Adding another result after the snapshot was taken must not change the snapshot
        ctx.AddResult(new ToolDispatchedResult("w2", "r2", true, null, TimeSpan.Zero));

        Assert.Single(snapshot);           // snapshot is frozen at 1
        Assert.Equal(2, ctx.DispatchedResults.Count); // current state has 2
    }

    [Fact]
    public async Task Context_ConcurrentAddResult_DoesNotCorrupt()
    {
        // Parallel tool calls must not race on _results. Run 100 concurrent AddResult
        // calls and verify all are recorded without corruption.
        var ctx = new WorkerDelegationContext();
        var workers = Enumerable.Range(0, 100).Select(i => $"w{i}").ToList();
        ctx.Reset("p", workers, CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            ctx.AddResult(new ToolDispatchedResult($"w{i}", "ok", true, null, TimeSpan.Zero)))));

        Assert.Equal(100, ctx.DispatchedResults.Count);
    }
}
