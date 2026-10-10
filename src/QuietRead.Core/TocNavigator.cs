namespace QuietRead.Core;

public sealed class TocNavigator
{
    public IReadOnlyList<TocItem> Items { get; }
    private readonly int[] _parents;
    public TocNavigator(IReadOnlyList<TocItem> items)
    {
        if (items.Count > ReaderLimits.SpineItems) throw new ArgumentException("目录超限。", nameof(items));
        Items = items; _parents = new int[items.Count]; var stack = new Stack<int>();
        for (int i = 0; i < items.Count; i++)
        {
            while (stack.Count > 0 && items[stack.Peek()].Depth >= items[i].Depth) stack.Pop();
            _parents[i] = stack.Count == 0 ? -1 : stack.Peek(); stack.Push(i);
        }
    }
    public bool HasChildren(int index) => index + 1 < Items.Count && _parents[index + 1] == index;
    public IReadOnlyList<int> Visible(string filter, IReadOnlySet<int> collapsed)
    {
        var show = new bool[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            if (filter.Length > 0)
            {
                if (!Items[i].Title.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                for (int p = i; p >= 0; p = _parents[p]) show[p] = true;
            }
            else
            {
                bool hidden = false;
                for (int p = _parents[i]; p >= 0; p = _parents[p]) if (collapsed.Contains(p)) { hidden = true; break; }
                show[i] = !hidden;
            }
        }
        return Enumerable.Range(0, Items.Count).Where(i => show[i]).ToArray();
    }
    public int Current(int chapter, int block, IReadOnlyDictionary<string, int> anchors)
    {
        int selected = -1, maximum = -1;
        for (int i = 0; i < Items.Count; i++)
        {
            TocItem item = Items[i];
            if (item.ChapterIndex != chapter) continue;
            int at = item.Fragment.Length == 0 ? 0 : anchors.GetValueOrDefault(item.Fragment, int.MaxValue);
            if (at <= block && at >= maximum) { selected = i; maximum = at; }
        }
        return selected;
    }
}
