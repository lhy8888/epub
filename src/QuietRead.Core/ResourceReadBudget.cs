namespace QuietRead.Core;

/// <summary>Charges attempted resource allocations, including reads that fail validation.</summary>
public sealed class ResourceReadBudget
{
    private readonly object _gate = new();
    private int _remaining;

    public ResourceReadBudget(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _remaining = maximumBytes;
    }

    public int RemainingBytes { get { lock (_gate) return _remaining; } }

    internal void Reserve(int bytes)
    {
        lock (_gate)
        {
            if (bytes > _remaining) throw new EpubException("本段图片累计输入超过允许的大小限制。");
            _remaining -= bytes;
        }
    }
}
