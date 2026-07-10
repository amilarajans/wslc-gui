using System.Collections.ObjectModel;

namespace OrchardWin.Core.Services;

/// Diff-and-patch helpers for <see cref="ObservableCollection{T}"/> so list UIs can keep a
/// stable ItemsSource reference. Replacing the whole collection every poll is the #1 cause of
/// WinUI ListView flicker (tear-down + rebind + selection loss).
public static class ObservableCollectionSync
{
    /// Update <paramref name="target"/> to match <paramref name="desired"/>.
    /// Returns true if any item was added, removed, or replaced.
    public static bool Sync<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, T, bool> same)
    {
        if (target.Count == desired.Count)
        {
            var allSame = true;
            for (var i = 0; i < desired.Count; i++)
            {
                if (!same(target[i], desired[i]))
                {
                    allSame = false;
                    break;
                }
            }

            if (allSame) return false;

            for (var i = 0; i < desired.Count; i++)
            {
                if (!same(target[i], desired[i]))
                    target[i] = desired[i];
            }

            return true;
        }

        target.Clear();
        foreach (var item in desired)
            target.Add(item);
        return true;
    }

    /// Like <see cref="Sync{T}"/> but only compares by key for identity; always replaces
    /// the item at each index when keys match (for live stats payloads that change values).
    public static bool SyncByKey<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, TKey> key,
        IEqualityComparer<TKey>? keyComparer = null)
        where TKey : notnull
    {
        keyComparer ??= EqualityComparer<TKey>.Default;
        var nextKeys = desired.Select(key).ToHashSet(keyComparer);

        var changed = false;
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!nextKeys.Contains(key(target[i])))
            {
                target.RemoveAt(i);
                changed = true;
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var k = key(item);
            var existing = -1;
            for (var j = 0; j < target.Count; j++)
            {
                if (keyComparer.Equals(key(target[j]), k))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                if (i >= target.Count) target.Add(item);
                else target.Insert(i, item);
                changed = true;
            }
            else
            {
                if (!Equals(target[existing], item))
                {
                    target[existing] = item;
                    changed = true;
                }

                if (existing != i)
                {
                    target.Move(existing, i);
                    changed = true;
                }
            }
        }

        return changed;
    }
}
