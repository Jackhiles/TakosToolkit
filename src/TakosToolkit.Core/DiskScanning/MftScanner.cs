using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace TakosToolkit.Core.DiskScanning;

/// <summary>
/// Scans a whole NTFS volume by reading its Master File Table (MFT) directly, the way WizTree does.
/// Every file and folder has a fixed-size record in the MFT, so one big sequential read replaces
/// hundreds of thousands of folder listings. Needs admin rights to open the raw volume.
/// </summary>
/// <remarks>
/// Each file is counted once: hard links (common in C:\Windows\WinSxS) count under their first
/// name only, and alternate data streams are not counted.
/// </remarks>
internal static class MftScanner
{
    private const int RootRecord = 5;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const ulong ReferenceMask = 0x0000_FFFF_FFFF_FFFF;
    private const uint FileSignature = 0x454C4946; // "FILE"
    private const uint RecallOnDataAccess = 0x00400000;

    private const uint AttrStandardInformation = 0x10;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    internal const byte FlagInUse = 1;
    internal const byte FlagDirectory = 2;

    public static bool IsSupported(string rootPath)
    {
        try
        {
            string full = Path.GetFullPath(rootPath);
            if (full.Length != 3 || full[1] != ':' || full[2] != '\\')
                return false;
            var drive = new DriveInfo(full);
            return drive.IsReady
                && drive.DriveType is DriveType.Fixed or DriveType.Removable
                && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static FileNode Scan(string rootPath, ScanProgress progress, CancellationToken ct)
    {
        string root = Path.GetFullPath(rootPath);
        var table = ReadTable(root, progress, ct);

        progress.CurrentPath = "Building folder tree…";
        var tree = BuildTree(root, table, out long folders, ct);

        progress.Reset();
        progress.AddFiles(tree.FileCount, tree.Size);
        progress.SetFolders(folders);
        return tree;
    }

    internal static RecordTable ReadTable(string root, ScanProgress progress, CancellationToken ct)
    {
        progress.CurrentPath = "Opening volume…";

        using var volume = OpenVolume(root[0]);
        var vd = GetVolumeData(volume);

        int recordSize = (int)vd.BytesPerFileRecordSegment;
        long clusterSize = vd.BytesPerCluster;
        long ioUnit = Math.Max(clusterSize, vd.BytesPerSector);
        if (recordSize < 512 || recordSize > 65536 || ChunkSize % recordSize != 0 || ChunkSize % ioUnit != 0)
            throw new NotSupportedException($"Unusual NTFS layout (record {recordSize} B, cluster {clusterSize} B).");

        // Record 0 is the MFT's own record; its $DATA attribute says where the rest of the MFT lives.
        var first = new byte[RoundUp(recordSize, ioUnit)];
        ReadAt(volume, vd.MftStartLcn * clusterSize, first, 0, first.Length);
        var record0 = first.AsSpan(0, recordSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record0) != FileSignature || !ApplyFixups(record0))
            throw new InvalidDataException("The MFT's first record is damaged.");

        var runs = FindMftRuns(record0) ?? throw new InvalidDataException("Couldn't locate the MFT's data runs.");
        long mftBytes = vd.MftValidDataLength;
        if (runs.Sum(r => r.Clusters) * clusterSize < mftBytes)
            throw new NotSupportedException("The MFT is too fragmented to read directly.");

        var table = new RecordTable(checked((int)(mftBytes / recordSize)));
        ReadAndParse(volume, runs, clusterSize, ioUnit, mftBytes, recordSize, table, progress, ct);
        return table;
    }

    // ---- Reading -----------------------------------------------------------------------------

    private readonly record struct Chunk(int FirstRecord, byte[] Buffer, int RecordCount);

    private static void ReadAndParse(
        SafeFileHandle volume, List<(long Lcn, long Clusters)> runs, long clusterSize, long ioUnit,
        long mftBytes, int recordSize, RecordTable table, ScanProgress progress, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(4) { SingleWriter = true });
        var buffers = new ConcurrentBag<byte[]>();
        var extensions = new ConcurrentQueue<(int BaseRecord, byte[] Record)>();

        // Reading is sequential; parsing is spread across cores while the next chunk is read.
        int workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var consumers = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
            {
                long files = 0, bytes = 0;
                for (int i = 0; i < chunk.RecordCount; i++)
                {
                    int index = chunk.FirstRecord + i;
                    ParseRecord(chunk.Buffer.AsSpan(i * recordSize, recordSize), index, table, extensions);
                    if ((table.Flags[index] & (FlagInUse | FlagDirectory)) == FlagInUse)
                    {
                        files++;
                        bytes += table.Sizes[index];
                    }
                }
                progress.AddFiles(files, bytes);
                buffers.Add(chunk.Buffer);
            }
        }, ct)).ToArray();

        try
        {
            for (long offset = 0; offset < mftBytes; offset += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int length = (int)Math.Min(ChunkSize, RoundUp(mftBytes - offset, ioUnit));
                var buffer = buffers.TryTake(out var reused) ? reused : new byte[ChunkSize];
                ReadMft(volume, runs, clusterSize, offset, buffer, length);

                int records = (int)(Math.Min(length, mftBytes - offset) / recordSize);
                channel.Writer.WriteAsync(new Chunk((int)(offset / recordSize), buffer, records), ct).AsTask().GetAwaiter().GetResult();
                progress.CurrentPath = $"Reading MFT… {offset * 100 / mftBytes}%";
            }
            channel.Writer.Complete();
            Task.WaitAll(consumers, ct);
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            try { Task.WaitAll(consumers); } catch (AggregateException) { }
            if (ex is AggregateException { InnerException: { } inner })
                throw inner;
            throw;
        }

        // Records that hold overflow attributes for another record ("extension records") are
        // applied afterwards, on one thread, so they can't race with their base record.
        foreach (var (baseRecord, record) in extensions)
        {
            if (baseRecord < table.Count)
                ParseAttributes(record, baseRecord, table, isBaseRecord: false);
        }
    }

    /// <summary>Reads <paramref name="length"/> bytes of the MFT starting at MFT offset <paramref name="mftOffset"/>.</summary>
    private static void ReadMft(SafeFileHandle volume, List<(long Lcn, long Clusters)> runs, long clusterSize, long mftOffset, byte[] buffer, int length)
    {
        int done = 0;
        long runStart = 0;
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * clusterSize;
            long runEnd = runStart + runBytes;
            long position = mftOffset + done;
            if (position < runEnd && done < length)
            {
                int count = (int)Math.Min(length - done, runEnd - position);
                if (lcn < 0)
                    Array.Clear(buffer, done, count); // sparse run
                else
                    ReadAt(volume, lcn * clusterSize + (position - runStart), buffer, done, count);
                done += count;
            }
            if (done >= length)
                return;
            runStart = runEnd;
        }
        throw new InvalidDataException("Reached the end of the MFT's data runs unexpectedly.");
    }

    // ---- Parsing (internal for tests) --------------------------------------------------------

    /// <summary>Per-record results, indexed by MFT record number.</summary>
    internal sealed class RecordTable(int count)
    {
        public int Count { get; } = count;
        public readonly byte[] Flags = new byte[count];
        public readonly int[] Parents = CreateParents(count);
        public readonly string?[] Names = new string?[count];
        public readonly byte[] NameRanks = new byte[count];
        public readonly long[] Sizes = new long[count];
        public readonly long[] Modified = new long[count];
        public readonly uint[] Attributes = new uint[count];

        private static int[] CreateParents(int count)
        {
            var parents = new int[count];
            Array.Fill(parents, -1);
            return parents;
        }
    }

    internal static void ParseRecord(Span<byte> record, int index, RecordTable table, ConcurrentQueue<(int, byte[])>? extensions)
    {
        if (record.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(record) != FileSignature || !ApplyFixups(record))
            return;

        ushort flags = U16(record, 0x16);
        if ((flags & 1) == 0)
            return; // deleted / unused record

        ulong baseReference = U64(record, 0x20) & ReferenceMask;
        if (baseReference != 0)
        {
            if (baseReference <= int.MaxValue)
                extensions?.Enqueue(((int)baseReference, record.ToArray()));
            return;
        }

        table.Flags[index] = (byte)(FlagInUse | ((flags & 2) != 0 ? FlagDirectory : 0));
        ParseAttributes(record, index, table, isBaseRecord: true);
    }

    internal static void ParseAttributes(ReadOnlySpan<byte> record, int target, RecordTable table, bool isBaseRecord)
    {
        int used = (int)Math.Min(U32(record, 0x18), (uint)record.Length);
        int offset = U16(record, 0x14);

        while (offset + 16 <= used)
        {
            uint type = U32(record, offset);
            if (type == AttrEnd)
                break;
            int length = (int)U32(record, offset + 4);
            if (length < 16 || offset + length > used)
                break;

            var attr = record.Slice(offset, length);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            switch (type)
            {
                case AttrStandardInformation when !nonResident && isBaseRecord:
                {
                    var value = ResidentValue(attr);
                    if (value.Length >= 0x24)
                    {
                        table.Modified[target] = I64(value, 0x08);
                        table.Attributes[target] = U32(value, 0x20);
                    }
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    var value = ResidentValue(attr);
                    if (value.Length < 0x42)
                        break;
                    int chars = value[0x40];
                    if (0x42 + chars * 2 > value.Length)
                        break;

                    // Prefer the long Win32 name over the 8.3 DOS alias. With hard links, keep the first name.
                    byte rank = value[0x41] switch { 1 or 3 => 3, 0 => 2, _ => 1 };
                    if (rank > table.NameRanks[target])
                    {
                        ulong parent = U64(value, 0) & ReferenceMask;
                        table.NameRanks[target] = rank;
                        table.Names[target] = Encoding.Unicode.GetString(value.Slice(0x42, chars * 2));
                        table.Parents[target] = parent <= int.MaxValue ? (int)parent : -1;
                    }
                    break;
                }
                case AttrData when nameLength == 0: // unnamed stream = the file's contents
                {
                    if (!nonResident)
                        table.Sizes[target] = U32(attr, 0x10);
                    else if (length >= 0x40 && I64(attr, 0x10) == 0) // first extent carries the real size
                        table.Sizes[target] = I64(attr, 0x30);
                    break;
                }
            }

            offset += length;
        }
    }

    /// <summary>
    /// Undoes NTFS "update sequence" protection: the last two bytes of every 512-byte stride are
    /// swapped out on disk and must be restored. Returns false if the record is torn or corrupt.
    /// </summary>
    internal static bool ApplyFixups(Span<byte> record)
    {
        int usaOffset = U16(record, 0x04);
        int usaCount = U16(record, 0x06);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * 512 > record.Length)
            return false;

        ushort sequence = U16(record, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int position = i * 512 - 2;
            if (U16(record, position) != sequence)
                return false;
            record[position] = record[usaOffset + i * 2];
            record[position + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Decodes a non-resident attribute's run list into (starting cluster, cluster count) pairs. Sparse runs get LCN -1.</summary>
    internal static List<(long Lcn, long Clusters)> DecodeRunList(ReadOnlySpan<byte> runList)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        int p = 0;
        while (p < runList.Length && runList[p] != 0)
        {
            int lengthSize = runList[p] & 0x0F;
            int offsetSize = runList[p] >> 4;
            p++;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || p + lengthSize + offsetSize > runList.Length)
                break;

            long clusters = ReadVarInt(runList.Slice(p, lengthSize), signed: false);
            p += lengthSize;

            if (offsetSize == 0)
            {
                runs.Add((-1, clusters));
            }
            else
            {
                lcn += ReadVarInt(runList.Slice(p, offsetSize), signed: true);
                p += offsetSize;
                runs.Add((lcn, clusters));
            }
        }
        return runs;
    }

    private static List<(long Lcn, long Clusters)>? FindMftRuns(ReadOnlySpan<byte> record0)
    {
        int used = (int)Math.Min(U32(record0, 0x18), (uint)record0.Length);
        int offset = U16(record0, 0x14);
        while (offset + 16 <= used)
        {
            uint type = U32(record0, offset);
            int length = (int)U32(record0, offset + 4);
            if (type == AttrEnd || length < 16 || offset + length > used)
                break;

            var attr = record0.Slice(offset, length);
            if (type == AttrData && attr[8] != 0 && attr[9] == 0)
            {
                int runOffset = U16(attr, 0x20);
                return runOffset < length ? DecodeRunList(attr[runOffset..]) : null;
            }
            offset += length;
        }
        return null;
    }

    // ---- Tree building -----------------------------------------------------------------------

    internal static FileNode BuildTree(string rootPath, RecordTable table, out long folders, CancellationToken ct)
    {
        if (table.Count <= RootRecord)
            throw new InvalidDataException("The MFT has no root folder record.");

        var root = new FileNode(rootPath, null, isDirectory: true);
        var nodes = new FileNode?[table.Count];
        var dead = new bool[table.Count];
        var visiting = new bool[table.Count];
        var chain = new List<int>();
        nodes[RootRecord] = root;
        folders = 1;

        for (int i = 0; i < table.Count; i++)
        {
            if ((i & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            if (nodes[i] is not null || dead[i] || !IsUsable(table, i))
                continue;

            // Walk up until we reach a folder that's already in the tree.
            chain.Clear();
            FileNode? anchor = null;
            for (int cur = i; ;)
            {
                if (nodes[cur] is { } existing)
                {
                    anchor = existing;
                    break;
                }
                if (dead[cur] || visiting[cur] || !IsUsable(table, cur))
                    break;
                int parent = table.Parents[cur];
                if (parent < 0 || parent >= table.Count || parent == cur || (table.Flags[parent] & FlagDirectory) == 0)
                    break;
                visiting[cur] = true;
                chain.Add(cur);
                cur = parent;
            }

            foreach (int c in chain)
                visiting[c] = false;

            if (anchor is null)
            {
                // Orphaned (e.g. parent deleted mid-scan) or a reference loop: leave it out.
                foreach (int c in chain)
                    dead[c] = true;
                continue;
            }

            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int c = chain[k];
                bool isDirectory = (table.Flags[c] & FlagDirectory) != 0;
                long size = isDirectory || (table.Attributes[c] & RecallOnDataAccess) != 0 ? 0 : table.Sizes[c];
                var node = new FileNode(table.Names[c]!, anchor, isDirectory)
                {
                    Size = size,
                    FileCount = isDirectory ? 0 : 1,
                    LastWriteTimeUtc = FromFileTime(table.Modified[c]),
                };
                anchor.AddChild(node);
                nodes[c] = node;
                if (isDirectory)
                    folders++;
                anchor = node;
            }
        }

        Summarize(root);
        return root;
    }

    private static bool IsUsable(RecordTable table, int index) =>
        (table.Flags[index] & FlagInUse) != 0 && table.Names[index] is not null;

    /// <summary>Totals folder sizes bottom-up and sorts children largest first.</summary>
    private static void Summarize(FileNode folder)
    {
        var children = folder.MutableChildren;
        if (children is null)
            return;

        long size = 0, files = 0;
        foreach (var child in children)
        {
            if (child.IsDirectory)
                Summarize(child);
            size += child.Size;
            files += child.FileCount;
        }
        children.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        children.TrimExcess();
        folder.Size = size;
        folder.FileCount = files;
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < 0x18)
            return default;
        uint length = U32(attr, 0x10);
        int offset = U16(attr, 0x14);
        return offset + length <= attr.Length ? attr.Slice(offset, (int)length) : default;
    }

    private static long ReadVarInt(ReadOnlySpan<byte> bytes, bool signed)
    {
        long value = 0;
        for (int i = bytes.Length - 1; i >= 0; i--)
            value = (value << 8) | bytes[i];
        if (signed && bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
            value -= 1L << (bytes.Length * 8); // sign-extend
        return value;
    }

    private static DateTime FromFileTime(long fileTime) =>
        fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;

    private static long RoundUp(long value, long multiple) => (value + multiple - 1) / multiple * multiple;

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);
    private static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s[o..]);

    // ---- Win32 -------------------------------------------------------------------------------

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    private struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    private static SafeFileHandle OpenVolume(char driveLetter)
    {
        var handle = CreateFileW($@"\\.\{driveLetter}:", GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    private static NtfsVolumeData GetVolumeData(SafeFileHandle volume)
    {
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, out var data, Marshal.SizeOf<NtfsVolumeData>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return data;
    }

    private static unsafe void ReadAt(SafeFileHandle handle, long position, byte[] buffer, int bufferOffset, int count)
    {
        fixed (byte* p = buffer)
        {
            int done = 0;
            while (done < count)
            {
                long at = position + done;
                var overlapped = new NativeOverlapped
                {
                    OffsetLow = unchecked((int)(at & 0xFFFFFFFF)),
                    OffsetHigh = unchecked((int)(at >> 32)),
                };
                if (!ReadFile(handle, p + bufferOffset + done, count - done, out int read, &overlapped))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (read == 0)
                    throw new EndOfStreamException("Unexpected end of volume while reading the MFT.");
                done += read;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, out NtfsVolumeData outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle file, byte* buffer, int toRead, out int read, NativeOverlapped* overlapped);
}
