using System.Text;
using GitHub.Copilot.SDK;

namespace PolyPilot.Models;

public record ChatMessage(string Role, string Content, DateTime Timestamp);

public class AgentSession : IAsyncDisposable
{
    public string Name { get; }
    public string Model { get; }
    public DateTime CreatedAt { get; }
    public List<ChatMessage> History { get; } = new();
    public bool IsProcessing { get; private set; }
    public string? SessionId { get; }
    public bool IsResumed { get; }
    
    private readonly CopilotSession _session;
    private TaskCompletionSource<string>? _responseCompletion;
    private readonly StringBuilder _currentResponse = new();
    private bool _hasReceivedDeltasThisTurn;

    public int MessageCount => History.Count;

    public AgentSession(string name, string model, CopilotSession session, string? sessionId = null, bool isResumed = false)
    {
        Name = name;
        Model = model;
        CreatedAt = DateTime.Now;
        SessionId = sessionId;
        IsResumed = isResumed;
        _session = session;
        
        _session.On(HandleSessionEvent);
    }

    private void HandleSessionEvent(SessionEvent evt)
    {
        switch (evt)
        {
            case AssistantTurnStartEvent:
                // Reset delta tracking at start of each turn
                _hasReceivedDeltasThisTurn = false;
                break;
            case AssistantMessageDeltaEvent delta:
                // Incremental content streaming
                var deltaContent = delta.Data.DeltaContent;
                if (!string.IsNullOrEmpty(deltaContent))
                {
                    _currentResponse.Append(deltaContent);
                    OnContentReceived?.Invoke(deltaContent);
                    _hasReceivedDeltasThisTurn = true;
                }
                break;
            case AssistantMessageEvent msg:
                // Full message event - only append if we haven't received deltas
                // (prevents duplication if SDK sends both delta + full message)
                var content = msg.Data.Content;
                if (!string.IsNullOrEmpty(content) && !_hasReceivedDeltasThisTurn)
                {
                    _currentResponse.Append(content);
                    OnContentReceived?.Invoke(content);
                }
                break;
            case SessionIdleEvent:
                CompleteResponse();
                break;
            case SessionErrorEvent err:
                OnError?.Invoke(err.Data.Message);
                _responseCompletion?.TrySetException(new Exception(err.Data.Message));
                IsProcessing = false;
                break;
        }
    }

    private void CompleteResponse()
    {
        var response = _currentResponse.ToString();
        if (!string.IsNullOrEmpty(response))
        {
            History.Add(new ChatMessage("assistant", response, DateTime.Now));
        }
        _responseCompletion?.TrySetResult(response);
        _currentResponse.Clear();
        IsProcessing = false;
    }

    public event Action<string>? OnContentReceived;
    public event Action<string>? OnError;

    public async Task<string> SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (IsProcessing)
            throw new InvalidOperationException("Session is already processing a request.");

        IsProcessing = true;
        _responseCompletion = new TaskCompletionSource<string>();
        _currentResponse.Clear();
        _hasReceivedDeltasThisTurn = false;

        History.Add(new ChatMessage("user", prompt, DateTime.Now));

        await _session.SendAsync(new MessageOptions
        {
            Prompt = prompt
        }, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.Token.Register(() => _responseCompletion.TrySetCanceled());

        return await _responseCompletion.Task;
    }

    public void ClearHistory()
    {
        History.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
    }
}
