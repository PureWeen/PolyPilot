using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class UsageStatsService : IAsyncDisposable
{
    private UsageStatistics _stats = new();
    private readonly object _statsLock = new();
    private readonly object _timerLock = new();
    private Timer? _saveDebounce;
    private volatile bool _disposed;
    
    private static string? _statsPath;
    private static string StatsPath => _statsPath ??= Path.Combine(CopilotService.BaseDir, "usage-stats.json");
    
    public UsageStatsService()
    {
        LoadStats();
    }
    
    public UsageStatistics GetStats()
    {
        lock (_statsLock)
        {
            return new UsageStatistics
            {
                TotalSessionsCreated = _stats.TotalSessionsCreated,
                TotalSessionsClosed = _stats.TotalSessionsClosed,
                TotalSessionTimeSeconds = _stats.TotalSessionTimeSeconds,
                LongestSessionSeconds = _stats.LongestSessionSeconds,
                TotalLinesSuggested = _stats.TotalLinesSuggested,
                TotalMessagesReceived = _stats.TotalMessagesReceived,
                FirstUsedAt = _stats.FirstUsedAt,
                LastUpdatedAt = _stats.LastUpdatedAt
            };
        }
    }
    
    public void TrackSessionResume(string sessionId)
    {
        // Record start time for duration tracking without incrementing TotalSessionsCreated
        // (resumed sessions were already counted in a prior run)
        lock (_statsLock)
        {
            _stats.ActiveSessions[sessionId] = DateTime.UtcNow;
        }
    }
    
    public void TrackSessionStart(string sessionId)
    {
        lock (_statsLock)
        {
            _stats.TotalSessionsCreated++;
            _stats.ActiveSessions[sessionId] = DateTime.UtcNow;
            _stats.LastUpdatedAt = DateTime.UtcNow;
        }
        DebounceSave();
    }
    
    public void TrackSessionEnd(string sessionId)
    {
        lock (_statsLock)
        {
            if (_stats.ActiveSessions.TryGetValue(sessionId, out var startTime))
            {
                var duration = (long)(DateTime.UtcNow - startTime).TotalSeconds;
                _stats.TotalSessionTimeSeconds += duration;
                _stats.TotalSessionsClosed++;
                
                if (duration > _stats.LongestSessionSeconds)
                {
                    _stats.LongestSessionSeconds = duration;
                }
                
                _stats.ActiveSessions.Remove(sessionId);
            }
            _stats.LastUpdatedAt = DateTime.UtcNow;
        }
        DebounceSave();
    }
    
    public void TrackMessage()
    {
        lock (_statsLock)
        {
            _stats.TotalMessagesReceived++;
            _stats.LastUpdatedAt = DateTime.UtcNow;
        }
        DebounceSave();
    }
    
    public void TrackCodeSuggestion(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;
            
        var lines = CountCodeLines(content);
        if (lines > 0)
        {
            lock (_statsLock)
            {
                _stats.TotalLinesSuggested += lines;
                _stats.LastUpdatedAt = DateTime.UtcNow;
            }
            DebounceSave();
        }
    }
    
    private static readonly Regex CodeBlockRegex = new(@"```[\w]*\r?\n(.*?)\r?\n```", RegexOptions.Compiled | RegexOptions.Singleline);

    private int CountCodeLines(string content)
    {
        var matches = CodeBlockRegex.Matches(content);
        
        int totalLines = 0;
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                var codeContent = match.Groups[1].Value;
                // Count non-empty lines
                var lines = codeContent.Split('\n')
                    .Select(l => l.Trim())
                    .Count(l => !string.IsNullOrWhiteSpace(l));
                totalLines += lines;
            }
        }
        
        return totalLines;
    }
    
    private void LoadStats()
    {
        try
        {
            if (File.Exists(StatsPath))
            {
                var json = File.ReadAllText(StatsPath);
                var loaded = JsonSerializer.Deserialize<UsageStatistics>(json);
                if (loaded != null)
                {
                    lock (_statsLock)
                    {
                        _stats = loaded;
                        // Don't restore ActiveSessions from disk
                        _stats.ActiveSessions = new();
                    }
                }
            }
        }
        catch
        {
            // Silently ignore errors - start fresh if corrupt
        }
    }
    
    private void DebounceSave()
    {
        lock (_timerLock)
        {
            _saveDebounce?.Dispose();
            _saveDebounce = new Timer(_ => SaveStats(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }
    
    private void SaveStats()
    {
        if (_disposed)
            return;
            
        try
        {
            lock (_statsLock)
            {
                var directory = Path.GetDirectoryName(StatsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                
                var json = JsonSerializer.Serialize(_stats, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StatsPath, json);
            }
        }
        catch
        {
            // Silently ignore save errors
        }
    }
    
    public void FlushSave()
    {
        lock (_timerLock)
        {
            _saveDebounce?.Dispose();
            _saveDebounce = null;
        }
        SaveStats();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        
        // Flush before marking disposed so SaveStats() doesn't early-return
        FlushSave();
        _disposed = true;
        await Task.CompletedTask;
    }
}
