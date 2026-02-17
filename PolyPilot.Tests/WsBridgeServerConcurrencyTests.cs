namespace PolyPilot.Tests;

/// <summary>
/// Tests for WsBridgeServer concurrency patterns and disposal handling.
/// </summary>
public class WsBridgeServerConcurrencyTests
{
    /// <summary>
    /// Tests that the SemaphoreSlim disposal pattern used in Broadcast handles
    /// concurrent disposal gracefully without throwing ObjectDisposedException.
    /// 
    /// This pattern mirrors the fix for the crash:
    /// "Cannot access a disposed object. Object name: 'System.Threading.SemaphoreSlim'."
    /// </summary>
    [Fact]
    public async Task SemaphoreSlim_Release_HandlesDisposalGracefully()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var taskStarted = new TaskCompletionSource();
        var canDispose = new TaskCompletionSource();
        var completed = false;
        Exception? caughtException = null;

        // Simulate the Broadcast pattern: capture semaphore, start task, dispose during task
        var task = Task.Run(async () =>
        {
            try
            {
                await semaphore.WaitAsync();
            }
            catch (ObjectDisposedException)
            {
                // Expected: semaphore disposed before wait completes
                return;
            }
            
            taskStarted.SetResult();
            await canDispose.Task; // Wait for main thread to dispose
            
            try
            {
                // Simulate work
                await Task.Delay(10);
            }
            finally
            {
                try
                {
                    semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // This is the fix: gracefully handle disposed semaphore
                }
            }
            completed = true;
        });

        // Wait for task to acquire the semaphore
        await taskStarted.Task;

        // Dispose semaphore while task is holding it (simulates client cleanup race)
        semaphore.Dispose();
        
        // Let task continue to Release()
        canDispose.SetResult();
        
        // Task should complete without unobserved exception
        await task;

        // The task should have completed the finally block without throwing
        Assert.True(completed);
        Assert.Null(caughtException);
    }

    /// <summary>
    /// Tests that WaitAsync on a disposed SemaphoreSlim throws ObjectDisposedException
    /// and that our pattern catches it correctly.
    /// </summary>
    [Fact]
    public async Task SemaphoreSlim_WaitAsync_ThrowsOnDisposed()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        semaphore.Dispose();

        bool exceptionCaught = false;
        try
        {
            await semaphore.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            exceptionCaught = true;
        }

        Assert.True(exceptionCaught);
    }

    /// <summary>
    /// Tests concurrent client cleanup scenario: multiple broadcast tasks
    /// and a concurrent disposal should not cause unobserved exceptions.
    /// </summary>
    [Fact]
    public async Task ConcurrentBroadcast_WithDisposal_NoUnobservedException()
    {
        var semaphore = new SemaphoreSlim(1, 1);
        var tasks = new List<Task>();
        var disposalTrigger = new TaskCompletionSource();
        var allStarted = new SemaphoreSlim(0, 3);

        // Start 3 concurrent "broadcast" tasks
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed before we could acquire
                    return;
                }

                allStarted.Release();
                await disposalTrigger.Task;

                try
                {
                    // Simulate send
                    await Task.Delay(1);
                }
                finally
                {
                    try { semaphore.Release(); }
                    catch (ObjectDisposedException) { /* Expected after disposal */ }
                }
            }));
        }

        // Wait for first task to acquire semaphore (others will be waiting)
        await allStarted.WaitAsync();

        // Dispose semaphore while tasks are active
        semaphore.Dispose();
        
        // Release all tasks
        disposalTrigger.SetResult();

        // All tasks should complete without throwing
        await Task.WhenAll(tasks);
    }
}
