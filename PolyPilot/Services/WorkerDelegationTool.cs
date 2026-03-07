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

    public IReadOnlyList<string> WorkerNames { get; private set; } = Array.Empty<string>();
    public string OriginalPrompt { get; private set; } = "";
    public CancellationToken CancellationToken { get; private set; }

    private readonly List<ToolDispatchedResult> _results = new();
    public IReadOnlyList<ToolDispatchedResult> DispatchedResults => _results;

    /// <summary>Resets state for a new planning-prompt round.</summary>
    public void Reset(string originalPrompt, IReadOnlyList<string> workerNames, CancellationToken ct)
    {
        OriginalPrompt = originalPrompt;
        WorkerNames = workerNames;
        CancellationToken = ct;
        _results.Clear();
        _roundRobinIndex = -1;
    }

    public string? GetNextWorker()
    {
        if (WorkerNames.Count == 0) return null;
        var idx = (int)((uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)WorkerNames.Count);
        return WorkerNames[idx];
    }

    internal void AddResult(ToolDispatchedResult result) => _results.Add(result);
}

/// <summary>One worker result produced via the task-tool interception path.</summary>
internal sealed record ToolDispatchedResult(
    string WorkerName,
    string? Response,
    bool Success,
    string? Error,
    TimeSpan Duration);

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
            holder.InvokeAsync,
            name: ToolName,
            description: "Launch a specialized agent worker to complete a task within the multi-agent group. "
                       + "Call this once per sub-task you want to delegate. "
                       + "Each invocation dispatches one worker session and returns its response.");
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
        internal async Task<string> InvokeAsync(
            [Description("The detailed task prompt for the worker agent")] string prompt,
            [Description("Agent type hint: explore, task, general-purpose, or code-review")] string agent_type = "general-purpose",
            [Description("A short human-readable description of what this agent call accomplishes")] string description = "")
        {
            var worker = _context.GetNextWorker();
            if (worker == null)
                return JsonSerializer.Serialize(new { error = "No workers are available in this multi-agent group" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var (success, response, error, duration) = await _executeWorker(
                    worker, prompt, _context.OriginalPrompt, _context.CancellationToken);
                sw.Stop();

                _context.AddResult(new ToolDispatchedResult(worker, response, success, error, duration));

                if (!success)
                    return JsonSerializer.Serialize(new { worker, error });

                return JsonSerializer.Serialize(new { worker, response });
            }
            catch (Exception ex)
            {
                sw.Stop();
                _context.AddResult(new ToolDispatchedResult(worker, null, false, ex.Message, sw.Elapsed));
                return JsonSerializer.Serialize(new { worker, error = ex.Message });
            }
        }
    }
}
