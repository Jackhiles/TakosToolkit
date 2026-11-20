namespace TakosToolkit.Core.DiskScanning;

public sealed record ExtensionStat(string Extension, long Size, long Count);

public sealed record CategoryStat(FileCategory Category, long Size, long Count);

/// <summary>Summaries of a scanned folder: largest files and breakdowns by type.</summary>
public sealed class ScanStatistics
{
    public required IReadOnlyList<FileNode> LargestFiles { get; init; }
    public required IReadOnlyList<ExtensionStat> Extensions { get; init; }
    public required IReadOnlyList<CategoryStat> Categories { get; init; }

    public static ScanStatistics Compute(FileNode folder, int topFiles = 100, int topExtensions = 50)
    {
        var largest = new PriorityQueue<FileNode, long>(topFiles + 1);
        var extensions = new Dictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
        var categories = new Dictionary<FileCategory, (long Size, long Count)>();

        foreach (var file in folder.EnumerateFiles())
        {
            largest.Enqueue(file, file.Size);
            if (largest.Count > topFiles)
                largest.Dequeue();

            string ext = file.Extension.Length > 0 ? file.Extension : "(none)";
            extensions.TryGetValue(ext, out var e);
            extensions[ext] = (e.Size + file.Size, e.Count + 1);

            var category = FileCategorizer.Get(file);
            categories.TryGetValue(category, out var c);
            categories[category] = (c.Size + file.Size, c.Count + 1);
        }

        var largestList = new List<FileNode>(largest.Count);
        while (largest.Count > 0)
            largestList.Add(largest.Dequeue());
        largestList.Reverse();

        return new ScanStatistics
        {
            LargestFiles = largestList,
            Extensions = extensions
                .Select(kv => new ExtensionStat(kv.Key, kv.Value.Size, kv.Value.Count))
                .OrderByDescending(s => s.Size)
                .Take(topExtensions)
                .ToList(),
            Categories = categories
                .Select(kv => new CategoryStat(kv.Key, kv.Value.Size, kv.Value.Count))
                .OrderByDescending(s => s.Size)
                .ToList(),
        };
    }
}
