using System.IO.Enumeration;

namespace TakosToolkit.Core.DiskScanning;

/// <summary>Live counters a UI can poll while a scan runs.</summary>
public sealed class ScanProgress
{
    private long _files;
    private long _folders;
    private long _bytes;
    private long _errors;

    public long Files => Interlocked.Read(ref _files);
    public long Folders => Interlocked.Read(ref _folders);
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Folders that could not be read (usually access denied).</summary>
    public long Errors => Interlocked.Read(ref _errors);

    public string CurrentPath { get; internal set; } = string.Empty;

    /// <summary>How the scan was (or is being) done.</summary>
    public ScanMethod Method { get; internal set; } = ScanMethod.FolderWalk;

    /// <summary>Why the fast MFT scan wasn't used, when it was attempted but failed.</summary>
    public string? MftFallbackReason { get; internal set; }

    internal void AddFolder(long files, long bytes)
    {
        Interlocked.Increment(ref _folders);
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void AddFiles(long files, long bytes)
    {
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void SetFolders(long folders) => Interlocked.Exchange(ref _folders, folders);

    internal void AddError() => Interlocked.Increment(ref _errors);

    internal void Reset()
    {
        Interlocked.Exchange(ref _files, 0);
        Interlocked.Exchange(ref _folders, 0);
        Interlocked.Exchange(ref _bytes, 0);
        Interlocked.Exchange(ref _errors, 0);
        CurrentPath = string.Empty;
    }
}

public enum ScanMethod
{
    /// <summary>Enumerating folders one by one. Works everywhere.</summary>
    FolderWalk,

    /// <summary>Reading the NTFS Master File Table directly. Much faster; needs admin.</summary>
    Mft,
}

/// <summary>
/// Builds a <see cref="FileNode"/> tree. Whole NTFS drives are read straight from the MFT when
/// running as admin; everything else (folders, shares, non-admin) is enumerated folder by folder.
/// </summary>
public static class DiskScanner
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: cloud-only placeholder (e.g. OneDrive "online-only").
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, DateTime LastWriteUtc, FileAttributes Attributes);

    /// <param name="allowMft">Try the fast MFT reader when <paramref name="rootPath"/> is the root of a local NTFS drive.</param>
    public static Task<FileNode> ScanAsync(string rootPath, ScanProgress progress, CancellationToken cancellationToken = default, bool allowMft = true)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Folder not found: {rootPath}");

        return Task.Run(() =>
        {
            if (allowMft && MftScanner.IsSupported(rootPath))
            {
                try
                {
                    progress.Method = ScanMethod.Mft;
                    return MftScanner.Scan(rootPath, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    progress.Reset();
                    progress.MftFallbackReason = ex.Message;
                }
            }

            progress.Method = ScanMethod.FolderWalk;
            var root = new FileNode(rootPath, null, isDirectory: true);
            ScanDirectory(root, rootPath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return root;
        }, cancellationToken);
    }

    private static void ScanDirectory(FileNode node, string path, ScanProgress progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return;

        var children = new List<FileNode>();
        var subfolders = new List<(FileNode Node, string Path)>();
        long size = 0;
        long fileCount = 0;

        try
        {
            var entries = new FileSystemEnumerable<Entry>(
                path,
                (ref FileSystemEntry e) => new Entry(
                    e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcDateTime, e.Attributes),
                Options);

            foreach (var e in entries)
            {
                if (e.IsDirectory)
                {
                    // Skip junctions and symlinks so nothing is counted twice or loops forever.
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    var child = new FileNode(e.Name, node, isDirectory: true) { LastWriteTimeUtc = e.LastWriteUtc };
                    children.Add(child);
                    subfolders.Add((child, Path.Join(path, e.Name)));
                }
                else
                {
                    // Cloud-only files take no local disk space.
                    long length = (e.Attributes & RecallOnDataAccess) != 0 ? 0 : e.Length;
                    children.Add(new FileNode(e.Name, node, isDirectory: false)
                    {
                        Size = length,
                        FileCount = 1,
                        LastWriteTimeUtc = e.LastWriteUtc,
                    });
                    size += length;
                    fileCount++;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            progress.AddError();
        }

        progress.AddFolder(fileCount, size);
        progress.CurrentPath = path;

        if (subfolders.Count > 1)
        {
            Parallel.ForEach(subfolders, sub => ScanDirectory(sub.Node, sub.Path, progress, ct));
        }
        else if (subfolders.Count == 1)
        {
            ScanDirectory(subfolders[0].Node, subfolders[0].Path, progress, ct);
        }

        foreach (var (sub, _) in subfolders)
        {
            size += sub.Size;
            fileCount += sub.FileCount;
        }

        children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        children.TrimExcess();
        node.SetChildren(children);
        node.Size = size;
        node.FileCount = fileCount;
    }
}
