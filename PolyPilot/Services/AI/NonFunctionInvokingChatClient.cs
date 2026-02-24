// Adapted from https://github.com/dotnet/maui/blob/main/src/AI/samples/Essentials.AI.Sample/AI/NonFunctionInvokingChatClient.cs
// Workaround for https://github.com/dotnet/extensions/issues/7204
// Agent Framework's ChatClientAgent double-invokes tool calls when FunctionInvokingChatClient is in the pipeline.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PolyPilot.Services.AI;

/// <summary>
/// A chat client wrapper that prevents Agent Framework from adding its own function invocation layer.
/// </summary>
/// <remarks>
/// <para>
/// Some chat clients handle tool invocation internally - when tools are registered, the underlying
/// service invokes them automatically and returns the results. However, Agent Framework's 
/// <c>ChatClientAgent</c> also tries to invoke tools when it sees <see cref="FunctionCallContent"/> 
/// in the response, causing double invocation.
/// </para>
/// <para>
/// This wrapper solves the problem by:
/// <list type="number">
/// <item>The inner handler converts <see cref="FunctionCallContent"/> and <see cref="FunctionResultContent"/>
/// to internal marker types that <see cref="FunctionInvokingChatClient"/> doesn't recognize</item>
/// <item>We wrap the handler with a real <see cref="FunctionInvokingChatClient"/>, satisfying 
/// Agent Framework's <c>GetService&lt;FunctionInvokingChatClient&gt;()</c> check so it won't create another</item>
/// <item>The outer layer unwraps the marker types back to the original content types for the caller</item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class NonFunctionInvokingChatClient : DelegatingChatClient
{
    private readonly ILogger _logger;

    public NonFunctionInvokingChatClient(
        IChatClient innerClient,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? serviceProvider = null)
        : base(CreateInnerClient(innerClient, loggerFactory, serviceProvider))
    {
        _logger = (ILogger?)loggerFactory?.CreateLogger<NonFunctionInvokingChatClient>() ?? NullLogger.Instance;
    }

    private static FunctionInvokingChatClient CreateInnerClient(
        IChatClient innerClient,
        ILoggerFactory? loggerFactory,
        IServiceProvider? serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        var handler = new ToolCallPassThroughHandler(innerClient);
        return new FunctionInvokingChatClient(handler, loggerFactory, serviceProvider);
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            Unwrap(message.Contents);
        }
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            Unwrap(update.Contents);
            yield return update;
        }
    }

    private void Unwrap(IList<AIContent> contents)
    {
        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is ServerFunctionCallContent serverFcc)
            {
                var fcc = serverFcc.FunctionCallContent;
                LogFunctionInvoking(fcc.Name, fcc.CallId, fcc.Arguments);
                contents[i] = fcc;
            }
            else if (contents[i] is ServerFunctionResultContent serverFrc)
            {
                var frc = serverFrc.FunctionResultContent;
                LogFunctionInvocationCompleted(frc.CallId, frc.Result);
                contents[i] = frc;
            }
        }
    }

    private void LogFunctionInvoking(string functionName, string callId, IDictionary<string, object?>? arguments)
    {
        if (_logger.IsEnabled(LogLevel.Trace) && arguments is not null)
        {
            var argsJson = JsonSerializer.Serialize(arguments, AIJsonUtilities.DefaultOptions);
            LogToolInvokedSensitive(functionName, callId, argsJson);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogToolInvoked(functionName, callId);
        }
    }

    private void LogFunctionInvocationCompleted(string callId, object? result)
    {
        if (_logger.IsEnabled(LogLevel.Trace) && result is not null)
        {
            var resultJson = result is string s ? s : JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
            LogToolInvocationCompletedSensitive(callId, resultJson);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogToolInvocationCompleted(callId);
        }
    }

    [LoggerMessage(LogLevel.Debug, "Received tool call: {ToolName} (ID: {ToolCallId})")]
    private partial void LogToolInvoked(string toolName, string toolCallId);

    [LoggerMessage(LogLevel.Trace, "Received tool call: {ToolName} (ID: {ToolCallId}) with arguments: {Arguments}")]
    private partial void LogToolInvokedSensitive(string toolName, string toolCallId, string arguments);

    [LoggerMessage(LogLevel.Debug, "Received tool result for call ID: {ToolCallId}")]
    private partial void LogToolInvocationCompleted(string toolCallId);

    [LoggerMessage(LogLevel.Trace, "Received tool result for call ID: {ToolCallId}: {Result}")]
    private partial void LogToolInvocationCompletedSensitive(string toolCallId, string result);

    /// <summary>
    /// Handler that wraps the inner client and converts tool call/result content to server-handled types.
    /// </summary>
    private sealed class ToolCallPassThroughHandler(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                Wrap(message.Contents);
            }
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                Wrap(update.Contents);
                yield return update;
            }
        }

        private static void Wrap(IList<AIContent> contents)
        {
            for (var i = 0; i < contents.Count; i++)
            {
                if (contents[i] is FunctionCallContent fcc)
                    contents[i] = new ServerFunctionCallContent(fcc);
                else if (contents[i] is FunctionResultContent frc)
                    contents[i] = new ServerFunctionResultContent(frc);
            }
        }
    }

    /// <summary>
    /// Marker type for function calls that were already handled by the inner client.
    /// </summary>
    private sealed class ServerFunctionCallContent(FunctionCallContent functionCallContent) : AIContent
    {
        public FunctionCallContent FunctionCallContent { get; } = functionCallContent;
    }

    /// <summary>
    /// Marker type for function results that were already produced by the inner client.
    /// </summary>
    private sealed class ServerFunctionResultContent(FunctionResultContent functionResultContent) : AIContent
    {
        public FunctionResultContent FunctionResultContent { get; } = functionResultContent;
    }
}
