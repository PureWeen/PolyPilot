using System.Collections;

namespace PolyPilot.Models;

/// <summary>
/// Thread-safe message queue for session prompts. Wraps a <see cref="List{T}"/>
/// with internal locking so that concurrent reads/writes from UI, SDK event,
/// and WebSocket threads do not corrupt the backing list.
/// Implements <see cref="IReadOnlyList{T}"/> for backward-compatible enumeration
/// (enumerator operates on a snapshot).
/// </summary>
public class SynchronizedMessageQueue : IReadOnlyList<string>
{
    private readonly List<string> _items = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public string this[int index]
    {
        get { lock (_lock) return _items[index]; }
    }

    public void Add(string item)
    {
        lock (_lock) _items.Add(item);
    }

    /// <summary>
    /// Adds an item and returns the new count atomically.
    /// Use when the count is needed immediately after adding (e.g., to align parallel queues).
    /// </summary>
    public int AddAndGetCount(string item)
    {
        lock (_lock)
        {
            _items.Add(item);
            return _items.Count;
        }
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }

    public void Insert(int index, string item)
    {
        lock (_lock) _items.Insert(index, item);
    }

    public void RemoveAt(int index)
    {
        lock (_lock) _items.RemoveAt(index);
    }

    public void RemoveAll(Predicate<string> match)
    {
        lock (_lock) _items.RemoveAll(match);
    }

    /// <summary>
    /// Atomically removes and returns the first item, or null if the queue is empty.
    /// Replaces the non-atomic pattern: if (Count > 0) { var x = [0]; RemoveAt(0); }
    /// </summary>
    public string? TryDequeue()
    {
        lock (_lock)
        {
            if (_items.Count == 0) return null;
            var item = _items[0];
            _items.RemoveAt(0);
            return item;
        }
    }

    /// <summary>
    /// Atomically removes the item at the given index if valid. Returns true if removed.
    /// </summary>
    public bool TryRemoveAt(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _items.Count) return false;
            _items.RemoveAt(index);
            return true;
        }
    }

    /// <summary>
    /// Returns a point-in-time copy safe for iteration outside any lock.
    /// </summary>
    public List<string> Snapshot()
    {
        lock (_lock) return new List<string>(_items);
    }

    public bool Any()
    {
        lock (_lock) return _items.Count > 0;
    }

    /// <summary>Enumerates a snapshot — safe for concurrent use.</summary>
    public IEnumerator<string> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
