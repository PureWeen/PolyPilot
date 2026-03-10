using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PolyPilot.Services;

/// <summary>
/// Mutable context shared between the orchestrator's reflect loop and the custom
/// "task" AIFunction that intercepts the model's delegation calls.
/// Updated by the reflect loop before each planning prompt; written by the
/// tool callback as workers complete.
/// </summary>
internal sealed class WorkerDelegationContext
{
    private int _roundRobinIndex = -1;
    private readonly object _resultsLock = new();

    public IReadOnlyList<string> WorkerNames { get; private set; } = Array.Empty<string>();
    public string OriginalPrompt { get; private set; } = "";
    public CancellationToken CancellationToken { get; private set; }

    /// <summary>
    /// Called when a tool dispatch starts to keep the orchestrator session's
    /// ActiveToolCallCount > 0, preventing premature SessionIdleEvent completion.
    /// </summary>
    public Action? OnToolDispatchStart { get; set; }

    /// <summary>Called when a tool dispatch completes to decrement ActiveToolCallCount.</summary>
    public Action? OnToolDispatchEnd { get; set; }

    private readonly List<ToolDispatchedResult> _results = new();

    /// <summary>
    /// Pending worker tasks that were dispatched but not yet completed.
    /// Used for parallel dispatch: callbacks fire workers and return immediately,
    /// then the dispatch loop awaits all pending tasks after dispatching is done.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<ToolDispatchedResult>> _pendingTasks = new();

    /// <summary>
    /// Returns a thread-safe snapshot of results recorded so far.
    /// The SDK may invoke the task tool multiple times concurrently (parallel tool calls),
    /// so all access to _results is synchronized via _resultsLock.
    /// </summary>
    public IReadOnlyList<ToolDispatchedResult> DispatchedResults
    {
        get { lock (_resultsLock) { return _results.ToList(); } }
    }

    /// <summary>Returns a snapshot of pending worker tasks (dispatched but not yet completed).</summary>
    public IReadOnlyDictionary<string, Task<ToolDispatchedResult>> PendingTasks =>
        new Dictionary<string, Task<ToolDispatchedResult>>(_pendingTasks);

    /// <summary>Resets state for a new planning-prompt round. Does NOT clear pending tasks.
    /// Resets the round-robin index — use <see cref="ResetResults"/> to preserve round-robin state.</summary>
    public void Reset(string originalPrompt, IReadOnlyList<string> workerNames, CancellationToken ct)
    {
        OriginalPrompt = originalPrompt;
        WorkerNames = workerNames;
        CancellationToken = ct;
        lock (_resultsLock) { _results.Clear(); }
        _roundRobinIndex = -1;
    }

    /// <summary>Clears dispatched results for a new iteration but preserves the round-robin index.
    /// Use in OrchestratorReflect mode where each iteration dispatches one worker and the
    /// round-robin must advance across iterations (worker-1 → worker-2 → worker-1).</summary>
    public void ResetResults(string originalPrompt, IReadOnlyList<string> workerNames, CancellationToken ct)
    {
        OriginalPrompt = originalPrompt;
        WorkerNames = workerNames;
        CancellationToken = ct;
        lock (_resultsLock) { _results.Clear(); }
        // Deliberately does NOT reset _roundRobinIndex
    }

    /// <summary>Full reset including pending tasks (used when starting a completely new dispatch cycle).</summary>
    public void FullReset(string originalPrompt, IReadOnlyList<string> workerNames, CancellationToken ct)
    {
        Reset(originalPrompt, workerNames, ct);
        _pendingTasks.Clear();
    }

    public string? GetNextWorker()
    {
        if (WorkerNames.Count == 0) return null;
        var idx = (int)((uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)WorkerNames.Count);
        return WorkerNames[idx];
    }

    internal void AddResult(ToolDispatchedResult result)
    {
        lock (_resultsLock) { _results.Add(result); }
    }

    internal void AddPendingTask(string workerName, Task<ToolDispatchedResult> task)
    {
        _pendingTasks[workerName] = task;
    }

    /// <summary>
    /// Awaits all pending worker tasks and returns their results.
    /// Call this after the dispatch loop completes to collect parallel results.
    /// </summary>
    internal async Task<List<ToolDispatchedResult>> AwaitAllPendingAsync()
    {
        var results = new List<ToolDispatchedResult>();
        foreach (var (name, task) in _pendingTasks)
        {
            try
            {
                results.Add(await task);
            }
            catch (Exception ex)
            {
                results.Add(new ToolDispatchedResult(name, null, false, ex.Message, TimeSpan.Zero));
            }
        }
        _pendingTasks.Clear();
        return results;
    }

    /// <summary>
    /// Observes all pending tasks to prevent unobserved task exceptions.
    /// Call this on cleanup/error paths where the dispatch loop exited before AwaitAllPendingAsync.
    /// </summary>
    internal void ObserveAllPending()
    {
        foreach (var (_, task) in _pendingTasks)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception; // observe to suppress UnobservedTaskException
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        _pendingTasks.Clear();
    }
}

/// <summary>One worker result produced via the task-tool interception path.</summary>
/// <param name="Dispatched">True if this is a placeholder indicating the worker was dispatched (not yet completed).</param>
internal sealed record ToolDispatchedResult(
    string WorkerName,
    string? Response,
    bool Success,
    string? Error,
    TimeSpan Duration,
    bool Dispatched = false);

/// <summary>
/// Provides a custom "task" AIFunction for orchestrator sessions running in OrchestratorReflect mode.
/// Intercepts the model's built-in task sub-agent calls and routes them to PolyPilot worker sessions.
/// </summary>
internal static class WorkerDelegationTool
{
    public const string ToolName = "task";

    public static AIFunction CreateFunction(
        WorkerDelegationContext context,
        Func<string, string, string, CancellationToken, Task<(bool success, string? response, string? error, TimeSpan duration)>> executeWorker)
    {
        var holder = new FunctionHolder(context, executeWorker);
        return AIFunctionFactory.Create(
            (Delegate)holder.InvokeAsync,
            new AIFunctionFactoryOptions
            {
                Name = ToolName,
                Description = "Launch a specialized agent worker to complete a task within the multi-agent group. "
                            + "Call this once per sub-task you want to delegate. "
                            + "Workers run in parallel — each call dispatches immediately and the system collects results later.",
                AdditionalProperties = new ReadOnlyDictionary<string, object?>(
                    new Dictionary<string, object?> { ["is_override"] = true })
            });
    }

    private sealed class FunctionHolder
    {
        private readonly WorkerDelegationContext _context;
        private readonly Func<string, string, string, CancellationToken, Task<(bool, string?, string?, TimeSpan)>> _executeWorker;

        internal FunctionHolder(
            WorkerDelegationContext context,
            Func<string, string, string, CancellationToken, Task<(bool, string?, string?, TimeSpan)>> executeWorker)
        {
            _context = context;
            _executeWorker = executeWorker;
        }

        [Description("Dispatch a task to a worker agent in the multi-agent group")]
        internal Task<string> InvokeAsync(
            [Description("The detailed task prompt for the worker agent")] string prompt,
            [Description("Agent type hint: explore, task, general-purpose, or code-review")] string agent_type = "general-purpose",
            [Description("A short human-readable description of what this agent call accomplishes")] string description = "")
        {
            var worker = _context.GetNextWorker();
            if (worker == null)
                return Task.FromResult(JsonSerializer.Serialize(new { error = "No workers are available in this multi-agent group" }));

            // Keep the orchestrator's ActiveToolCallCount elevated briefly so the SDK
            // doesn't fire SessionIdleEvent before we return.
            _context.OnToolDispatchStart?.Invoke();

            // Fire-and-forget: start worker dispatch in background, return immediately.
            // Workers run in parallel while the dispatch loop sends continuation prompts.
            var dispatchTask = Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var (success, response, error, duration) = await _executeWorker(
                        worker, prompt, _context.OriginalPrompt, _context.CancellationToken);
                    sw.Stop();
                    return new ToolDispatchedResult(worker, response, success, error, duration);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new ToolDispatchedResult(worker, null, false, ex.Message, sw.Elapsed);
                }
            });

            _context.AddPendingTask(worker, dispatchTask);
            // Add a placeholder result so the dispatch loop knows this worker was dispatched (not yet completed)
            _context.AddResult(new ToolDispatchedResult(worker, null, false, null, TimeSpan.Zero, Dispatched: true));

            // Release the ActiveToolCallCount so CompleteResponse can fire quickly,
            // allowing the dispatch loop to continue to the next worker.
            _context.OnToolDispatchEnd?.Invoke();

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                worker,
                status = "dispatched",
                message = $"Worker '{worker}' is now running in parallel. Call `task` again to dispatch the next worker, or stop if all work is assigned."
            }));
        }
    }
}
