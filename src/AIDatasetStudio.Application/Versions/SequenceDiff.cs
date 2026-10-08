namespace AIDatasetStudio.Application.Versions;

public enum DiffKind
{
    Equal,
    Deleted,
    Inserted,
}

/// <summary>
/// One step of an edit script: an element kept (both indexes), removed from the old sequence
/// (<see cref="OldIndex"/>) or added from the new one (<see cref="NewIndex"/>).
/// </summary>
public readonly record struct DiffStep(DiffKind Kind, int OldIndex, int NewIndex);

/// <summary>
/// Shortest edit script between two sequences (Myers, "An O(ND) Difference Algorithm", 1986)
/// after trimming the common start and end. Past <paramref name="maxEdits"/> differences it
/// gives up on finding the shortest script and replaces the differing middle as a whole, so
/// unrelated long texts cost bounded time and memory.
/// </summary>
public static class SequenceDiff
{
    public const int DefaultMaxEdits = 1_000;

    public static IReadOnlyList<DiffStep> Compute<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        IEqualityComparer<T>? comparer = null,
        int maxEdits = DefaultMaxEdits)
    {
        ArgumentNullException.ThrowIfNull(oldItems);
        ArgumentNullException.ThrowIfNull(newItems);
        comparer ??= EqualityComparer<T>.Default;

        var prefix = 0;
        while (prefix < oldItems.Count && prefix < newItems.Count && comparer.Equals(oldItems[prefix], newItems[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldItems.Count - prefix && suffix < newItems.Count - prefix
            && comparer.Equals(oldItems[oldItems.Count - 1 - suffix], newItems[newItems.Count - 1 - suffix]))
        {
            suffix++;
        }

        var steps = new List<DiffStep>(Math.Max(oldItems.Count, newItems.Count));
        for (var i = 0; i < prefix; i++)
        {
            steps.Add(new DiffStep(DiffKind.Equal, i, i));
        }

        var oldCount = oldItems.Count - prefix - suffix;
        var newCount = newItems.Count - prefix - suffix;
        if (oldCount == 0 || newCount == 0 || !TryMiddle(oldItems, newItems, prefix, oldCount, newCount, comparer, maxEdits, steps))
        {
            Replace(steps, prefix, oldCount, newCount);
        }

        for (var i = 0; i < suffix; i++)
        {
            steps.Add(new DiffStep(DiffKind.Equal, oldItems.Count - suffix + i, newItems.Count - suffix + i));
        }

        return steps;
    }

    private static void Replace(List<DiffStep> steps, int start, int oldCount, int newCount)
    {
        for (var i = 0; i < oldCount; i++)
        {
            steps.Add(new DiffStep(DiffKind.Deleted, start + i, -1));
        }

        for (var i = 0; i < newCount; i++)
        {
            steps.Add(new DiffStep(DiffKind.Inserted, -1, start + i));
        }
    }

    // The greedy forward pass, keeping each round's furthest points (diagonals -d..d) to walk back.
    private static bool TryMiddle<T>(
        IReadOnlyList<T> oldItems,
        IReadOnlyList<T> newItems,
        int start,
        int n,
        int m,
        IEqualityComparer<T> comparer,
        int maxEdits,
        List<DiffStep> steps)
    {
        var max = n + m;
        var offset = max + 1;
        var v = new int[(2 * max) + 3];
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= Math.Min(max, maxEdits); d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]        // down: insert
                    : v[offset + k - 1] + 1;   // right: delete
                var y = x - k;
                while (x < n && y < m && comparer.Equals(oldItems[start + x], newItems[start + y]))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }

            var snapshot = new int[(2 * d) + 1];
            Array.Copy(v, offset - d, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);
            if (found >= 0)
            {
                break;
            }
        }

        if (found < 0)
        {
            return false;
        }

        var middle = new List<DiffStep>(n + m);
        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var previous = trace[d - 1];
            var k = cx - cy;
            var prevK = k == -d || (k != d && previous[k - 1 + (d - 1)] < previous[k + 1 + (d - 1)]) ? k + 1 : k - 1;
            var prevX = previous[prevK + (d - 1)];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
                middle.Add(new DiffStep(DiffKind.Equal, start + cx, start + cy));
            }

            if (prevK == k + 1)
            {
                cy--;
                middle.Add(new DiffStep(DiffKind.Inserted, -1, start + cy));
            }
            else
            {
                cx--;
                middle.Add(new DiffStep(DiffKind.Deleted, start + cx, -1));
            }
        }

        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            middle.Add(new DiffStep(DiffKind.Equal, start + cx, start + cy));
        }

        middle.Reverse();
        steps.AddRange(middle);
        return true;
    }
}
