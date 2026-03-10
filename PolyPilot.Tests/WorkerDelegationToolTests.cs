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
        Assert.True(ctx.DispatchedResults[0].Dispatched); // Marked as dispatched, not completed

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
        Assert.Single(ctx.DispatchedResults); // Placeholder (dispatched=true, worker was dispatched)
        Assert.True(ctx.DispatchedResults[0].Dispatched);
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

    [Fact]
    public async Task Context_AwaitAllPendingAsync_HandlesThrowingTask()
    {
        // A worker task that throws (not returns failure) must be caught by AwaitAllPendingAsync.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "good", "bad" }, CancellationToken.None);

        ctx.AddPendingTask("good", Task.FromResult(
            new ToolDispatchedResult("good", "done", true, null, TimeSpan.FromSeconds(1))));
        ctx.AddPendingTask("bad", Task.FromException<ToolDispatchedResult>(
            new InvalidOperationException("session exploded")));

        var results = await ctx.AwaitAllPendingAsync();

        Assert.Equal(2, results.Count);
        var good = results.First(r => r.WorkerName == "good");
        var bad = results.First(r => r.WorkerName == "bad");
        Assert.True(good.Success);
        Assert.False(bad.Success);
        Assert.Contains("session exploded", bad.Error);
    }

    [Fact]
    public void Context_ResetResults_PreservesRoundRobin()
    {
        // ResetResults should clear results but keep the round-robin index advancing.
        var ctx = new WorkerDelegationContext();
        ctx.Reset("p", new List<string> { "a", "b", "c" }, CancellationToken.None);

        var first = ctx.GetNextWorker();  // index 0 → "a"
        var second = ctx.GetNextWorker(); // index 1 → "b"

        // ResetResults preserves round-robin (unlike Reset which resets to -1)
        ctx.ResetResults("p2", new List<string> { "a", "b", "c" }, CancellationToken.None);

        var third = ctx.GetNextWorker(); // index 2 → "c" (continues from where it left off)
        Assert.Equal("a", first);
        Assert.Equal("b", second);
        Assert.Equal("c", third);
    }

    [Fact]
    public void Context_ObserveAllPending_SuppressesExceptions()
    {
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "w1" }, CancellationToken.None);

        ctx.AddPendingTask("w1", Task.FromException<ToolDispatchedResult>(
            new InvalidOperationException("boom")));

        // Should not throw — just observes the exception and clears pending tasks
        ctx.ObserveAllPending();

        Assert.Empty(ctx.PendingTasks);
    }

    // --- Parallel dispatch and nudge scenario tests ---

    [Fact]
    public void Context_ResetResults_PreservesPendingTasks()
    {
        // ResetResults must not clear pending tasks — workers are still running
        // from previous dispatch iterations while the loop continues.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "w1", "w2" }, CancellationToken.None);

        ctx.AddPendingTask("w1", Task.FromResult(
            new ToolDispatchedResult("w1", "done", true, null, TimeSpan.FromSeconds(1))));

        ctx.ResetResults("p2", new List<string> { "w2" }, CancellationToken.None);

        // Results are cleared but pending tasks survive
        Assert.Empty(ctx.DispatchedResults);
        Assert.Single(ctx.PendingTasks);
        Assert.True(ctx.PendingTasks.ContainsKey("w1"));
    }

    [Fact]
    public void Context_Reset_PreservesPendingTasks()
    {
        // Reset (non-Full) also preserves pending tasks — only FullReset clears them.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "w1", "w2" }, CancellationToken.None);

        ctx.AddPendingTask("w1", Task.FromResult(
            new ToolDispatchedResult("w1", "done", true, null, TimeSpan.FromSeconds(1))));

        ctx.Reset("p2", new List<string> { "w2" }, CancellationToken.None);

        Assert.Empty(ctx.DispatchedResults);
        Assert.Single(ctx.PendingTasks); // pending tasks survive Reset
    }

    [Fact]
    public void Context_FullReset_ClearsPendingTasks()
    {
        // FullReset clears everything — pending tasks, results, round-robin.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "w1" }, CancellationToken.None);

        ctx.AddPendingTask("w1", Task.FromResult(
            new ToolDispatchedResult("w1", "done", true, null, TimeSpan.FromSeconds(1))));
        ctx.AddResult(new ToolDispatchedResult("w1", "done", true, null, TimeSpan.Zero));

        ctx.FullReset("p2", new List<string> { "w1" }, CancellationToken.None);

        Assert.Empty(ctx.DispatchedResults);
        Assert.Empty(ctx.PendingTasks); // FullReset clears pending
    }

    [Fact]
    public async Task Context_PendingTasksAccumulateAcrossResets()
    {
        // Simulates the parallel dispatch loop: each iteration dispatches one worker,
        // resets results (not pending), and the next iteration dispatches another.
        // All pending tasks must accumulate and be awaitable at the end.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "w1", "w2", "w3" }, CancellationToken.None);

        // Iteration 1: dispatch w1
        ctx.AddPendingTask("w1", Task.FromResult(
            new ToolDispatchedResult("w1", "result1", true, null, TimeSpan.FromSeconds(5))));
        ctx.AddResult(new ToolDispatchedResult("w1", null, false, null, TimeSpan.Zero, Dispatched: true));

        // Iteration 2: reset results (not pending) and dispatch w2
        ctx.ResetResults("p", new List<string> { "w2", "w3" }, CancellationToken.None);
        ctx.AddPendingTask("w2", Task.FromResult(
            new ToolDispatchedResult("w2", "result2", true, null, TimeSpan.FromSeconds(3))));
        ctx.AddResult(new ToolDispatchedResult("w2", null, false, null, TimeSpan.Zero, Dispatched: true));

        // Iteration 3: reset results and dispatch w3
        ctx.ResetResults("p", new List<string> { "w3" }, CancellationToken.None);
        ctx.AddPendingTask("w3", Task.FromResult(
            new ToolDispatchedResult("w3", "result3", true, null, TimeSpan.FromSeconds(1))));

        // All 3 pending tasks should be awaitable
        var results = await ctx.AwaitAllPendingAsync();
        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.WorkerName == "w1" && r.Response == "result1");
        Assert.Contains(results, r => r.WorkerName == "w2" && r.Response == "result2");
        Assert.Contains(results, r => r.WorkerName == "w3" && r.Response == "result3");
    }

    [Fact]
    public async Task Tool_MultipleDispatches_RoundRobinsWorkers()
    {
        // Simulates the model calling `task` tool 3 times — each call should get a different worker.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("orig", new List<string> { "alpha", "beta", "gamma" }, CancellationToken.None);

        var dispatched = new List<string>();
        Task<(bool, string?, string?, TimeSpan)> Execute(string w, string task, string orig, CancellationToken ct)
        {
            dispatched.Add(w);
            return Task.FromResult<(bool, string?, string?, TimeSpan)>((true, $"done by {w}", null, TimeSpan.FromSeconds(1)));
        }

        var fn = WorkerDelegationTool.CreateFunction(ctx, Execute);
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["prompt"] = "work" });

        // Invoke 3 times (simulating the model calling tool 3 times across turns)
        await fn.InvokeAsync(args);
        await fn.InvokeAsync(args);
        await fn.InvokeAsync(args);

        // 3 placeholder results should be recorded
        Assert.Equal(3, ctx.DispatchedResults.Count);

        // Await all pending — each dispatch is fire-and-forget
        var results = await ctx.AwaitAllPendingAsync();
        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.WorkerName == "alpha");
        Assert.Contains(results, r => r.WorkerName == "beta");
        Assert.Contains(results, r => r.WorkerName == "gamma");
    }

    [Fact]
    public void Context_NudgeScenario_EmptyResultsThenDispatch()
    {
        // Simulates the nudge scenario: first turn produces no tool calls,
        // then after ResetResults + nudge, the model dispatches a worker.
        var ctx = new WorkerDelegationContext();
        ctx.FullReset("p", new List<string> { "w1", "w2" }, CancellationToken.None);

        // First turn: no tool calls
        Assert.Empty(ctx.DispatchedResults);

        // Nudge: ResetResults (preserves round-robin), then model dispatches
        ctx.ResetResults("p", new List<string> { "w1", "w2" }, CancellationToken.None);

        // Model calls tool — dispatches w1 (round-robin starts at 0 since FullReset was used initially)
        ctx.AddResult(new ToolDispatchedResult("w1", null, false, null, TimeSpan.Zero, Dispatched: true));
        ctx.AddPendingTask("w1", Task.FromResult(
            new ToolDispatchedResult("w1", "done", true, null, TimeSpan.FromSeconds(1))));

        Assert.Single(ctx.DispatchedResults);
        Assert.True(ctx.DispatchedResults[0].Dispatched);
        Assert.Single(ctx.PendingTasks);
    }

    [Fact]
    public void ToolDispatchedResult_DispatchedFlag_DefaultsFalse()
    {
        // The Dispatched flag distinguishes placeholders (dispatched=true) from completed results.
        var placeholder = new ToolDispatchedResult("w1", null, false, null, TimeSpan.Zero, Dispatched: true);
        var completed = new ToolDispatchedResult("w1", "done", true, null, TimeSpan.FromSeconds(2));

        Assert.True(placeholder.Dispatched);
        Assert.False(completed.Dispatched); // default
    }
}
