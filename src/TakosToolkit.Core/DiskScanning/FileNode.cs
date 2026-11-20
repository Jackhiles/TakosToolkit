namespace TakosToolkit.Core.DiskScanning;

/// <summary>A file or folder in a scanned tree. Folder sizes include everything beneath them.</summary>
public sealed class FileNode
{
    private List<FileNode>? _children;

    public FileNode(string name, FileNode? parent, bool isDirectory)
    {
        Name = name;
        Parent = parent;
        IsDirectory = isDirectory;
    }

    /// <summary>The entry name. For the scan root this is the full root path (e.g. "C:\").</summary>
    public string Name { get; }

    public FileNode? Parent { get; }

    public bool IsDirectory { get; }

    /// <summary>Size in bytes. For folders, the total of all descendants.</summary>
    public long Size { get; internal set; }

    /// <summary>Number of files. 1 for a file; the total of all descendant files for a folder.</summary>
    public long FileCount { get; internal set; }

    public DateTime LastWriteTimeUtc { get; internal set; }

    /// <summary>Children sorted largest first.</summary>
    public IReadOnlyList<FileNode> Children => (IReadOnlyList<FileNode>?)_children ?? [];

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name).ToLowerInvariant();

    public string FullPath
    {
        get
        {
            if (Parent is null)
                return Name;

            var parts = new Stack<string>();
            for (FileNode? n = this; n is not null; n = n.Parent)
                parts.Push(n.Name);
            return Path.Join([.. parts]);
        }
    }

    public FileNode Root
    {
        get
        {
            FileNode n = this;
            while (n.Parent is not null)
                n = n.Parent;
            return n;
        }
    }

    internal void SetChildren(List<FileNode> children) => _children = children;

    internal void AddChild(FileNode child) => (_children ??= []).Add(child);

    internal List<FileNode>? MutableChildren => _children;

    /// <summary>Enumerates every file (not folder) beneath this node.</summary>
    public IEnumerable<FileNode> EnumerateFiles()
    {
        if (!IsDirectory)
        {
            yield return this;
            yield break;
        }

        var stack = new Stack<FileNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            foreach (var child in stack.Pop().Children)
            {
                if (child.IsDirectory)
                    stack.Push(child);
                else
                    yield return child;
            }
        }
    }

    public override string ToString() => $"{Name} ({ByteSize.Format(Size)})";
}
