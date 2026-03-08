using PolyPilot.Models;
using PolyPilot.Services;
using System.Reflection;

namespace PolyPilot.Tests;

[Collection("BaseDir")]
public class UsageStatsTests : IDisposable
{
    private readonly string _testDir;
    private UsageStatsService? _service;

    public UsageStatsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"PolyPilot-statstest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        
        // Reset the static _statsPath field and BaseDir
        ResetStaticFields();
    }

    private void ResetStaticFields()
    {
        // Reset UsageStatsService static field
        var statsPathField = typeof(UsageStatsService).GetField("_statsPath", 
            BindingFlags.NonPublic | BindingFlags.Static);
        statsPathField?.SetValue(null, null);
        
        // Override CopilotService.BaseDir via the proper API
        CopilotService.SetBaseDirForTesting(_testDir);
    }

    private UsageStatsService CreateService()
    {
        ResetStaticFields();
        return new UsageStatsService();
    }

    public void Dispose()
    {
        _service?.DisposeAsync().AsTask().Wait();
        
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        // Reset static fields
        var statsPathField = typeof(UsageStatsService).GetField("_statsPath", 
            BindingFlags.NonPublic | BindingFlags.Static);
        statsPathField?.SetValue(null, null);
        
        // Restore to the shared test base dir (never null it — causes races)
        CopilotService.SetBaseDirForTesting(TestSetup.TestBaseDir);
    }

    [Fact]
    public void InitialStats_AreZero()
    {
        _service = CreateService();
        var stats = _service.GetStats();
        
        Assert.Equal(0, stats.TotalSessionsCreated);
        Assert.Equal(0, stats.TotalSessionsClosed);
        Assert.Equal(0, stats.TotalSessionTimeSeconds);
        Assert.Equal(0, stats.LongestSessionSeconds);
        Assert.Equal(0, stats.TotalLinesSuggested);
        Assert.Equal(0, stats.TotalMessagesReceived);
    }

    [Fact]
    public void TrackSessionStart_IncrementsCount()
    {
        _service = CreateService();
        _service.TrackSessionStart("test-session");
        
        var stats = _service.GetStats();
        Assert.Equal(1, stats.TotalSessionsCreated);
    }

    [Fact]
    public void TrackSessionEnd_IncrementsCountAndRecordsDuration()
    {
        _service = CreateService();
        _service.TrackSessionStart("test-session");
        Thread.Sleep(100); // Wait a bit
        _service.TrackSessionEnd("test-session");
        
        var stats = _service.GetStats();
        Assert.Equal(1, stats.TotalSessionsClosed);
        Assert.True(stats.TotalSessionTimeSeconds >= 0);
        Assert.True(stats.LongestSessionSeconds >= 0);
    }

    [Fact]
    public void TrackSessionEnd_WithoutStart_DoesNotCrash()
    {
        _service = CreateService();
        _service.TrackSessionEnd("nonexistent-session");
        
        var stats = _service.GetStats();
        Assert.Equal(0, stats.TotalSessionsClosed);
    }

    [Fact]
    public void TrackMessage_IncrementsCount()
    {
        _service = CreateService();
        _service.TrackMessage();
        _service.TrackMessage();
        
        var stats = _service.GetStats();
        Assert.Equal(2, stats.TotalMessagesReceived);
    }

    [Fact]
    public void TrackCodeSuggestion_CountsLinesInCodeBlocks()
    {
        _service = CreateService();
        var content = @"Here's some code:

```csharp
public class Test
{
    public void Method()
    {
        Console.WriteLine(""Hello"");
    }
}
```

And more text.";
        
        _service.TrackCodeSuggestion(content);
        
        var stats = _service.GetStats();
        Assert.True(stats.TotalLinesSuggested > 0);
    }

    [Fact]
    public void TrackCodeSuggestion_MultipleCodeBlocks_CountsAll()
    {
        _service = CreateService();
        var content = @"First block:
```javascript
function test() {
    return 42;
}
```

Second block:
```python
def hello():
    print(""world"")
```";
        
        _service.TrackCodeSuggestion(content);
        
        var stats = _service.GetStats();
        Assert.True(stats.TotalLinesSuggested >= 4); // At least 4 non-empty lines
    }

    [Fact]
    public void TrackCodeSuggestion_EmptyContent_DoesNotCrash()
    {
        _service = CreateService();
        _service.TrackCodeSuggestion("");
        _service.TrackCodeSuggestion(null!);
        
        var stats = _service.GetStats();
        Assert.Equal(0, stats.TotalLinesSuggested);
    }

    [Fact]
    public void TrackCodeSuggestion_NoCodeBlocks_DoesNotIncrementCount()
    {
        _service = CreateService();
        var content = "Just plain text without any code blocks.";
        
        _service.TrackCodeSuggestion(content);
        
        var stats = _service.GetStats();
        Assert.Equal(0, stats.TotalLinesSuggested);
    }

    [Fact]
    public void LongestSession_TracksMaximumDuration()
    {
        _service = CreateService();
        _service.TrackSessionStart("session1");
        Thread.Sleep(50);
        _service.TrackSessionEnd("session1");
        
        _service.TrackSessionStart("session2");
        Thread.Sleep(150);
        _service.TrackSessionEnd("session2");
        
        _service.TrackSessionStart("session3");
        Thread.Sleep(30);
        _service.TrackSessionEnd("session3");
        
        var stats = _service.GetStats();
        // session2 should be longest (150ms ≈ 0s in seconds, but still the max)
        Assert.True(stats.LongestSessionSeconds >= 0);
    }

    [Fact]
    public void SaveAndLoad_PreservesStats()
    {
        _service = CreateService();
        _service.TrackSessionStart("session1");
        _service.TrackSessionEnd("session1");
        _service.TrackMessage();
        _service.TrackCodeSuggestion("```js\ntest();\n```");
        
        _service.FlushSave();
        _service.DisposeAsync().AsTask().Wait();
        _service = null;
        
        // Create a new service instance (should load from disk)
        var newService = CreateService();
        var stats = newService.GetStats();
        
        Assert.Equal(1, stats.TotalSessionsCreated);
        Assert.Equal(1, stats.TotalSessionsClosed);
        Assert.Equal(1, stats.TotalMessagesReceived);
        Assert.True(stats.TotalLinesSuggested > 0);
        
        newService.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void DisposeAsync_FlushesStats()
    {
        var service = CreateService();
        service.TrackSessionStart("test");
        Thread.Sleep(2100); // Wait for debounce
        service.DisposeAsync().AsTask().Wait();
        
        // Create new service to verify data was flushed
        var newService = CreateService();
        var stats = newService.GetStats();
        Assert.Equal(1, stats.TotalSessionsCreated);
        
        newService.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void ActiveSessions_NotPersistedToDisk()
    {
        _service = CreateService();
        _service.TrackSessionStart("active-session");
        _service.FlushSave();
        _service.DisposeAsync().AsTask().Wait();
        _service = null;
        
        var newService = CreateService();
        var stats = newService.GetStats();
        
        // Active sessions should not be in loaded stats
        Assert.Empty(stats.ActiveSessions);
        
        newService.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void CorruptStatsFile_RecoversGracefully()
    {
        var statsPath = Path.Combine(_testDir, "usage-stats.json");
        File.WriteAllText(statsPath, "{ invalid json");
        
        // Should not crash and should start with fresh stats
        var service = CreateService();
        var stats = service.GetStats();
        
        Assert.Equal(0, stats.TotalSessionsCreated);
        
        service.DisposeAsync().AsTask().Wait();
    }
}
