using TakosToolkit.Core;
using TakosToolkit.Core.DiskScanning;

namespace TakosToolkit.Tests;

public sealed class DiskScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TakosToolkitTests_" + Guid.NewGuid().ToString("N"));

    public DiskScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        File.WriteAllBytes(Path.Combine(_root, "top.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_root, "a", "one.mp4"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "a", "deep", "two.zip"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "b", "three.txt"), new byte[500]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Totals_and_structure_are_correct()
    {
        var progress = new ScanProgress();
        var root = await DiskScanner.ScanAsync(_root, progress);

        Assert.Equal(4600, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(4, progress.Files);
        Assert.Equal(5, progress.Folders); // root, a, a\deep, b, empty

        // Children are sorted largest first.
        Assert.Equal(["a", "b", "top.bin", "empty"], root.Children.Select(c => c.Name));
        var a = root.Children[0];
        Assert.Equal(4000, a.Size);
        Assert.Equal(Path.Combine(_root, "a", "deep", "two.zip"), a.Children[0].Children[0].FullPath);
    }

    [Fact]
    public async Task Statistics_rank_largest_files_and_types()
    {
        var root = await DiskScanner.ScanAsync(_root, new ScanProgress());
        var stats = ScanStatistics.Compute(root, topFiles: 2);

        Assert.Equal(["two.zip", "one.mp4"], stats.LargestFiles.Select(f => f.Name));
        Assert.Equal(".zip", stats.Extensions[0].Extension);
        Assert.Equal(FileCategory.Archive, stats.Categories[0].Category);
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskScanner.ScanAsync(_root, new ScanProgress(), cts.Token));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10L * 1024 * 1024 * 1024, "10.0 GB")]
    public void ByteSize_formats(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes).Replace(',', '.'));

    [Theory]
    [InlineData("movie.MKV", FileCategory.Video)]
    [InlineData("pagefile.sys", FileCategory.System)]
    [InlineData("outlook.ost", FileCategory.MailAndData)]
    [InlineData("noextension", FileCategory.Other)]
    public void Categorizer_classifies(string name, FileCategory expected) =>
        Assert.Equal(expected, FileCategorizer.Get(name));
}
