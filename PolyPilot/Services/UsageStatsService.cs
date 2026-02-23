using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class UsageStatsService : IAsyncDisposable
{
    private UsageStatistics _stats = new();
    private readonly object _statsLock = new();
    private Timer? _saveDebounce;
    private bool _disposed;
    
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
    
    public void TrackSessionStart(string sessionName)
    {
        lock (_statsLock)
        {
            _stats.TotalSessionsCreated++;
            _stats.ActiveSessions[sessionName] = DateTime.UtcNow;
            _stats.LastUpdatedAt = DateTime.UtcNow;
        }
        DebounceSave();
    }
    
    public void TrackSessionEnd(string sessionName)
    {
        lock (_statsLock)
        {
            if (_stats.ActiveSessions.TryGetValue(sessionName, out var startTime))
            {
                var duration = (long)(DateTime.UtcNow - startTime).TotalSeconds;
                _stats.TotalSessionTimeSeconds += duration;
                _stats.TotalSessionsClosed++;
                
                if (duration > _stats.LongestSessionSeconds)
                {
                    _stats.LongestSessionSeconds = duration;
                }
                
                _stats.ActiveSessions.Remove(sessionName);
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
    
    private int CountCodeLines(string content)
    {
        // Match code blocks: ```language\n...\n``` or ```\n...\n```
        var codeBlockPattern = @"```[\w]*\r?\n(.*?)\r?\n```";
        var matches = Regex.Matches(content, codeBlockPattern, RegexOptions.Singleline);
        
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
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ => SaveStats(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
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
                {
                    Directory.CreateDirectory(directory);
                }
                
                // Create copy without ActiveSessions
                var toSave = new UsageStatistics
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
                
                var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
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
        _saveDebounce?.Dispose();
        _saveDebounce = null;
        SaveStats();
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
            
        _disposed = true;
        FlushSave();
        _saveDebounce?.Dispose();
        await Task.CompletedTask;
    }
}
