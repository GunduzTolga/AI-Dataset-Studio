namespace AIDatasetStudio.Application.Export;

/// <summary>
/// Joins the split groups of a family: a record and the variations written from it (SDD §17.6)
/// ask for the same thing in other words, so they go to the same part, together with every record
/// that shares a group with one of them (<see cref="ExportSplit.GroupKey"/>). Records are added in
/// export order; a joined group takes the key of its first record, so a split without
/// variations comes out exactly as before.
/// </summary>
public sealed class SplitFamilies
{
    private const string FamilyPrefix = "f:";

    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _keys = [];
    private readonly List<int> _parent = [];

    // Per set: the earliest group key in it (its position in _keys); family keys never lead.
    private readonly List<int> _lead = [];

    /// <param name="groupKey">The record's own group key.</param>
    /// <param name="family">The family's first record: the record a variation was written from, otherwise the record itself.</param>
    public void Add(string groupKey, Guid family)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        var group = Index(groupKey, leads: true);
        var parent = Index(FamilyPrefix + family.ToString("D"), leads: false);
        Union(group, parent);
    }

    /// <summary>
    /// The joined key of each group key that changed; group keys not in it stay as they are.
    /// </summary>
    public IReadOnlyDictionary<string, string> Build()
    {
        var joined = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _keys.Count; i++)
        {
            if (_keys[i].StartsWith(FamilyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var lead = _lead[Find(i)];
            if (lead != i)
            {
                joined[_keys[i]] = _keys[lead];
            }
        }

        return joined;
    }

    private int Index(string key, bool leads)
    {
        if (_index.TryGetValue(key, out var index))
        {
            return index;
        }

        index = _keys.Count;
        _index[key] = index;
        _keys.Add(key);
        _parent.Add(index);
        _lead.Add(leads ? index : int.MaxValue);
        return index;
    }

    private int Find(int i)
    {
        while (_parent[i] != i)
        {
            _parent[i] = _parent[_parent[i]];
            i = _parent[i];
        }

        return i;
    }

    private void Union(int a, int b)
    {
        var (x, y) = (Find(a), Find(b));
        if (x == y)
        {
            return;
        }

        // The set with the earlier key keeps its root; a lone family key has none and joins.
        if (_lead[y] < _lead[x])
        {
            (x, y) = (y, x);
        }

        _parent[y] = x;
    }
}
