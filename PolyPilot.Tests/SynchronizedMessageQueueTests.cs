using PolyPilot.Models;

namespace PolyPilot.Tests;

public class SynchronizedMessageQueueTests
{
    [Fact]
    public void Add_And_Count_AreConsistent()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("a");
        queue.Add("b");
        Assert.Equal(2, queue.Count);
        Assert.Equal("a", queue[0]);
        Assert.Equal("b", queue[1]);
    }

    [Fact]
    public void TryDequeue_ReturnsFirstItem()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("first");
        queue.Add("second");

        var item = queue.TryDequeue();
        Assert.Equal("first", item);
        Assert.Single(queue);
        Assert.Equal("second", queue[0]);
    }

    [Fact]
    public void TryDequeue_ReturnsNull_WhenEmpty()
    {
        var queue = new SynchronizedMessageQueue();
        Assert.Null(queue.TryDequeue());
    }

    [Fact]
    public void TryRemoveAt_ReturnsFalse_ForInvalidIndex()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("only");
        Assert.False(queue.TryRemoveAt(-1));
        Assert.False(queue.TryRemoveAt(1));
        Assert.True(queue.TryRemoveAt(0));
        Assert.Empty(queue);
    }

    [Fact]
    public void AddAndGetCount_ReturnsNewCount()
    {
        var queue = new SynchronizedMessageQueue();
        Assert.Equal(1, queue.AddAndGetCount("a"));
        Assert.Equal(2, queue.AddAndGetCount("b"));
        Assert.Equal(3, queue.AddAndGetCount("c"));
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("a");
        queue.Add("b");
        queue.Clear();
        Assert.Empty(queue);
    }

    [Fact]
    public void Insert_PlacesAtCorrectIndex()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("a");
        queue.Add("c");
        queue.Insert(1, "b");
        Assert.Equal(3, queue.Count);
        Assert.Equal("b", queue[1]);
    }

    [Fact]
    public void RemoveAll_FiltersMatchingItems()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("keep");
        queue.Add("remove-1");
        queue.Add("keep-2");
        queue.Add("remove-2");
        queue.RemoveAll(s => s.StartsWith("remove"));
        Assert.Equal(2, queue.Count);
        Assert.Equal("keep", queue[0]);
        Assert.Equal("keep-2", queue[1]);
    }

    [Fact]
    public void Snapshot_ReturnsIndependentCopy()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("a");
        queue.Add("b");
        var snap = queue.Snapshot();
        queue.Clear();
        Assert.Equal(2, snap.Count);
        Assert.Empty(queue);
    }

    [Fact]
    public void Enumeration_IteratesSnapshot()
    {
        var queue = new SynchronizedMessageQueue();
        queue.Add("x");
        queue.Add("y");
        var items = queue.ToList();
        Assert.Equal(new[] { "x", "y" }, items);
    }

    [Fact]
    public void Any_ReflectsState()
    {
        var queue = new SynchronizedMessageQueue();
        Assert.False(queue.Any());
        queue.Add("item");
        Assert.True(queue.Any());
    }

    [Fact]
    public async Task ConcurrentAddAndDequeue_DoesNotCorruptList()
    {
        var queue = new SynchronizedMessageQueue();
        const int iterations = 1000;
        var dequeued = new List<string>();
        var dequeueLock = new object();

        var addTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
                queue.Add($"msg-{i}");
        });

        var dequeueTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var item = queue.TryDequeue();
                if (item != null)
                {
                    lock (dequeueLock)
                        dequeued.Add(item);
                }
                else
                {
                    // Small delay to let producer add items
                    Thread.SpinWait(10);
                    i--; // retry
                }
            }
        });

        await Task.WhenAll(addTask, dequeueTask);

        // All items produced should have been consumed
        Assert.Empty(queue);
        Assert.Equal(iterations, dequeued.Count);
    }

    [Fact]
    public async Task ConcurrentAddClearSnapshot_NoExceptions()
    {
        var queue = new SynchronizedMessageQueue();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new List<Exception>();

        var tasks = new[]
        {
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                    queue.Add("item");
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                    queue.Clear();
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { _ = queue.Snapshot(); }
                    catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
                }
            }),
            Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try { _ = queue.Count; _ = queue.Any(); }
                    catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
                }
            }),
        };

        await Task.WhenAll(tasks);
        Assert.Empty(exceptions);
    }
}
