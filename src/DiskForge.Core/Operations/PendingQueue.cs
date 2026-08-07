using System.Collections;

namespace DiskForge.Core.Operations;

/// <summary>
/// The staged batch of operations (§1.2, GParted/EaseUS model). Nothing here executes until the
/// user reviews the full list and explicitly Applies. Supports staging, reordering and removal.
/// </summary>
public sealed class PendingQueue : IReadOnlyCollection<IDiskOperation>
{
    private readonly List<IDiskOperation> _ops = new();

    public int Count => _ops.Count;
    public bool HasDestructive => _ops.Any(o => o.Describe().IsDestructive);
    public IReadOnlyList<IDiskOperation> Items => _ops;

    public event EventHandler? Changed;

    public void Enqueue(IDiskOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        _ops.Add(op);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(IDiskOperation op)
    {
        var removed = _ops.Remove(op);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _ops.Count) throw new ArgumentOutOfRangeException(nameof(oldIndex));
        newIndex = Math.Clamp(newIndex, 0, _ops.Count - 1);
        if (oldIndex == newIndex) return;
        var op = _ops[oldIndex];
        _ops.RemoveAt(oldIndex);
        _ops.Insert(newIndex, op);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_ops.Count == 0) return;
        _ops.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerator<IDiskOperation> GetEnumerator() => _ops.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
