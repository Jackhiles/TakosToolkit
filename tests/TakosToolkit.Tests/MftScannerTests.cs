using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using TakosToolkit.Core.DiskScanning;

namespace TakosToolkit.Tests;

public class MftScannerTests
{
    private const int RecordSize = 1024;

    /// <summary>Builds an on-disk style FILE record (with update-sequence fixups applied, as NTFS writes them).</summary>
    private sealed class RecordBuilder
    {
        private readonly byte[] _rec = new byte[RecordSize];
        private int _offset = 0x38;

        public RecordBuilder(bool isDirectory = false, ulong baseRecord = 0, bool inUse = true)
        {
            Encoding.ASCII.GetBytes("FILE").CopyTo(_rec, 0);
            W16(0x04, 0x30);                        // update sequence array offset
            W16(0x06, 3);                           // 1 sequence number + 2 strides
            W16(0x14, 0x38);                        // first attribute
            W16(0x16, (ushort)((inUse ? 1 : 0) | (isDirectory ? 2 : 0)));
            W64(0x20, baseRecord);
        }

        public RecordBuilder StandardInfo(DateTime modifiedUtc, uint attributes = 0)
        {
            var value = new byte[0x48];
            BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x08), modifiedUtc.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x20), attributes);
            return Resident(0x10, value);
        }

        public RecordBuilder FileName(string name, ulong parent, byte nameSpace = 1)
        {
            var value = new byte[0x42 + name.Length * 2];
            BinaryPrimitives.WriteUInt64LittleEndian(value, parent | (1UL << 48)); // high bits = sequence number
            value[0x40] = (byte)name.Length;
            value[0x41] = nameSpace;
            Encoding.Unicode.GetBytes(name).CopyTo(value, 0x42);
            return Resident(0x30, value);
        }

        public RecordBuilder ResidentData(int length) => Resident(0x80, new byte[length]);

        public RecordBuilder NonResidentData(long size, long lowestVcn = 0)
        {
            int len = 0x48;
            W32(_offset, 0x80);
            W32(_offset + 4, (uint)len);
            _rec[_offset + 8] = 1; // non-resident
            W64(_offset + 0x10, (ulong)lowestVcn);
            W16(_offset + 0x20, 0x40);
            W64(_offset + 0x28, (ulong)size);
            W64(_offset + 0x30, (ulong)size);
            _offset += len;
            return this;
        }

        public byte[] Build()
        {
            W32(_offset, 0xFFFFFFFF);
            W32(0x18, (uint)(_offset + 8));         // bytes in use

            // Protect each 512-byte stride: save its last 2 bytes in the USA and stamp the sequence number.
            const ushort sequence = 0x0042;
            W16(0x30, sequence);
            for (int i = 1; i <= 2; i++)
            {
                int pos = i * 512 - 2;
                _rec[0x30 + i * 2] = _rec[pos];
                _rec[0x30 + i * 2 + 1] = _rec[pos + 1];
                W16(pos, sequence);
            }
            return _rec;
        }

        private RecordBuilder Resident(uint type, byte[] value)
        {
            int len = (0x18 + value.Length + 7) & ~7;
            W32(_offset, type);
            W32(_offset + 4, (uint)len);
            W32(_offset + 0x10, (uint)value.Length);
            W16(_offset + 0x14, 0x18);
            value.CopyTo(_rec, _offset + 0x18);
            _offset += len;
            return this;
        }

        private void W16(int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(o), v);
        private void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_rec.AsSpan(o), v);
        private void W64(int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(_rec.AsSpan(o), v);
    }

    [Fact]
    public void Parses_name_parent_size_and_time()
    {
        var modified = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var record = new RecordBuilder()
            .StandardInfo(modified)
            .FileName("REPORT~1.PDF", parent: 40, nameSpace: 2)   // DOS alias
            .FileName("Quarterly Report.pdf", parent: 40)         // Win32 name wins
            .NonResidentData(123_456_789)
            .Build();

        var table = new MftScanner.RecordTable(64);
        MftScanner.ParseRecord(record, 50, table, null);

        Assert.Equal(MftScanner.FlagInUse, table.Flags[50]);
        Assert.Equal("Quarterly Report.pdf", table.Names[50]);
        Assert.Equal(40, table.Parents[50]);
        Assert.Equal(123_456_789, table.Sizes[50]);
        Assert.Equal(modified.ToFileTimeUtc(), table.Modified[50]);
    }

    [Fact]
    public void Torn_record_is_rejected()
    {
        var record = new RecordBuilder().FileName("a.txt", 5).ResidentData(10).Build();
        record[1022] ^= 0xFF; // sector end no longer matches the sequence number

        var table = new MftScanner.RecordTable(16);
        MftScanner.ParseRecord(record, 10, table, null);
        Assert.Equal(0, table.Flags[10]);
        Assert.Null(table.Names[10]);
    }

    [Fact]
    public void Unused_records_are_skipped_and_extension_records_are_queued()
    {
        var table = new MftScanner.RecordTable(16);
        MftScanner.ParseRecord(new RecordBuilder(inUse: false).FileName("gone.txt", 5).Build(), 10, table, null);
        Assert.Null(table.Names[10]);

        var queue = new ConcurrentQueue<(int, byte[])>();
        MftScanner.ParseRecord(new RecordBuilder(baseRecord: 12).NonResidentData(5000).Build(), 11, table, queue);
        Assert.Equal(0, table.Flags[11]);
        Assert.True(queue.TryDequeue(out var ext));
        Assert.Equal(12, ext.Item1);

        // Applying the extension fills in the base record's size.
        MftScanner.ParseAttributes(ext.Item2, 12, table, isBaseRecord: false);
        Assert.Equal(5000, table.Sizes[12]);
    }

    [Fact]
    public void Decodes_run_lists_with_relative_and_negative_offsets()
    {
        byte[] runList =
        [
            0x31, 0x10, 0x00, 0x00, 0x01,   // 16 clusters at LCN 0x10000
            0x21, 0x08, 0x00, 0x01,         // 8 clusters at +0x100
            0x11, 0x04, 0xFF,               // 4 clusters at -1
            0x01, 0x02,                     // 2 sparse clusters
            0x00,
        ];

        var runs = MftScanner.DecodeRunList(runList);
        Assert.Equal([(0x10000L, 16L), (0x10100L, 8L), (0x100FFL, 4L), (-1L, 2L)], runs);
    }

    [Fact]
    public void Builds_tree_and_drops_orphans()
    {
        var t = new MftScanner.RecordTable(40);
        void Add(int i, string name, int parent, bool dir, long size = 0)
        {
            t.Flags[i] = (byte)(MftScanner.FlagInUse | (dir ? MftScanner.FlagDirectory : 0));
            t.Names[i] = name;
            t.Parents[i] = parent;
            t.Sizes[i] = size;
        }

        Add(5, ".", 5, dir: true);
        Add(30, "file-before-parent.bin", 35, dir: false, size: 700); // parent appears later in the MFT
        Add(31, "Users", 5, dir: true);
        Add(32, "big.iso", 31, dir: false, size: 5000);
        Add(33, "orphan.txt", 39, dir: false, size: 99);               // parent 39 not in use
        Add(34, "loop-a", 36, dir: true);                             // 34 <-> 36 reference loop
        Add(35, "Data", 31, dir: true);
        Add(36, "loop-b", 34, dir: true);
        Add(37, "top.txt", 5, dir: false, size: 10);

        var root = MftScanner.BuildTree(@"X:\", t, out long folders, CancellationToken.None);

        Assert.Equal(5710, root.Size);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(3, folders); // root, Users, Data
        Assert.Equal(["Users", "top.txt"], root.Children.Select(c => c.Name));
        var users = root.Children[0];
        Assert.Equal(["big.iso", "Data"], users.Children.Select(c => c.Name));
        Assert.Equal(@"X:\Users\Data\file-before-parent.bin", users.Children[1].Children[0].FullPath);
    }
}
