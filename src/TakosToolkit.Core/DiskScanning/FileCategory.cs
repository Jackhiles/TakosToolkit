namespace TakosToolkit.Core.DiskScanning;

public enum FileCategory
{
    Other,
    Video,
    Audio,
    Image,
    Archive,
    DiskImage,
    Program,
    Document,
    Code,
    MailAndData,
    GameData,
    System,
}

public static class FileCategorizer
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "memory.dmp",
    };

    public static FileCategory Get(FileNode node) => Get(node.Name);

    public static FileCategory Get(string fileName)
    {
        if (SystemFileNames.Contains(fileName))
            return FileCategory.System;

        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var category) ? category : FileCategory.Other;
    }

    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.DiskImage => "Disk images / VMs",
        FileCategory.MailAndData => "Mail & databases",
        FileCategory.GameData => "Game data",
        FileCategory.Program => "Programs & libraries",
        FileCategory.System => "System & logs",
        _ => category.ToString(),
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts)
        {
            foreach (var e in exts)
                map["." + e] = c;
        }

        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "m4v", "webm", "flv", "mpg", "mpeg", "ts");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "m4a", "aac", "ogg", "wma", "opus");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "heic", "webp", "raw", "cr2", "nef", "psd", "svg", "ico");
        Add(FileCategory.Archive, "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "cab", "zst");
        Add(FileCategory.DiskImage, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "wim", "esd", "ova", "avhdx", "qcow2");
        Add(FileCategory.Program, "exe", "dll", "sys", "msi", "msp", "msix", "msixbundle", "appx", "appxbundle", "ocx", "drv", "mui", "pdb", "node");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "xlsm", "ppt", "pptx", "txt", "rtf", "csv", "md", "one", "odt", "ods", "vsdx");
        Add(FileCategory.Code, "cs", "js", "ts", "py", "ps1", "psm1", "json", "xml", "html", "css", "cpp", "c", "h", "java", "go", "rs", "sql", "yml", "yaml");
        Add(FileCategory.MailAndData, "pst", "ost", "edb", "mdb", "accdb", "db", "sqlite", "mdf", "ldf", "ndf", "bak", "nsf");
        Add(FileCategory.GameData, "pak", "ucas", "utoc", "cpk", "vpk", "bsa", "ba2", "rpf", "forge", "assets", "resource", "bundle", "wad", "gcf");
        Add(FileCategory.System,"log", "etl", "evtx", "dmp", "tmp", "temp", "cache", "old", "chk");
        return map;
    }
}
