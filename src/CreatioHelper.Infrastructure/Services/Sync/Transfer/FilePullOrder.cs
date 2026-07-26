using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Applies the folder's configured pull order to the set of files a puller is about
/// to download. Mirrors Syncthing's folder &lt;order&gt; setting.
/// </summary>
public static class FilePullOrder
{
    public static IReadOnlyList<FileMetadata> Apply(IEnumerable<FileMetadata> files, SyncPullOrder order)
        => Apply(files, order, f => f.FileName, f => f.Size, f => f.ModifiedTime);

    public static IReadOnlyList<FileAction> Apply(IEnumerable<FileAction> actions, SyncPullOrder order)
        => Apply(actions, order, a => a.FileName, a => a.FileSize, a => a.FileInfo?.ModifiedTime ?? a.CreatedAt);

    public static IReadOnlyList<T> Apply<T>(
        IEnumerable<T> items,
        SyncPullOrder order,
        Func<T, string> name,
        Func<T, long> size,
        Func<T, DateTime> modified)
    {
        ArgumentNullException.ThrowIfNull(items);

        return order switch
        {
            SyncPullOrder.Alphabetic => items.OrderBy(name, StringComparer.OrdinalIgnoreCase).ToList(),
            SyncPullOrder.SmallestFirst => items.OrderBy(size).ToList(),
            SyncPullOrder.LargestFirst => items.OrderByDescending(size).ToList(),
            SyncPullOrder.OldestFirst => items.OrderBy(modified).ToList(),
            SyncPullOrder.NewestFirst => items.OrderByDescending(modified).ToList(),
            _ => Shuffle(items)
        };
    }

    private static IReadOnlyList<T> Shuffle<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
