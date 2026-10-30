using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using XboxToolkit.Models;

namespace XboxToolkit
{
    // An STFS package (CON, LIVE or PIRS). Files can be read, and the package rebuilt with some files replaced, which
    // lays the blocks out again and regenerates the hash tables, top hash and header hash. The header signature is
    // kept as it is, so a rebuilt package only loads where signature checks are not enforced.
    public class StfsPackage
    {
        private const int BlockSize = 0x1000;
        private const int HashEntrySize = 0x18;
        private const int FileEntrySize = 0x40;
        private const int BlocksPerTable = 0xAA;
        private const int BlocksPerLevel1 = 0x70E4;

        private readonly byte[] mData;
        private readonly int mFirstBlockOffset;
        // 0: one hash table per level (read-only packages), 1: two tables per level, the current one picked by a status bit.
        private readonly int mTablesShift;
        private readonly int[] mBlockStep;
        private readonly int mTopLevel;
        private List<StfsFileEntry>? mEntries;

        public static bool IsStfs(byte[] data)
        {
            if (data.Length < BlockSize)
            {
                return false;
            }
            var magic = Encoding.ASCII.GetString(data, 0, 4);
            // The volume descriptor type is 0 for STFS and 1 for SVOD (GOD).
            return (magic == "CON " || magic == "LIVE" || magic == "PIRS") && BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0x3A9)) == 0;
        }

        public StfsPackage(byte[] data)
        {
            if (IsStfs(data) == false)
            {
                throw new ArgumentException("Not an STFS package.", nameof(data));
            }
            mData = data;
            var headerSize = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0x340));
            mFirstBlockOffset = (headerSize + BlockSize - 1) & ~(BlockSize - 1);
            mTablesShift = ~data[0x37B] & 1;
            mBlockStep = mTablesShift == 0 ? new[] { 0xAB, 0x718F } : new[] { 0xAC, 0x723A };
            var allocatedBlocks = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0x395));
            mTopLevel = allocatedBlocks <= BlocksPerTable ? 0 : allocatedBlocks <= BlocksPerLevel1 ? 1 : 2;
        }

        public byte[] Data => mData;

        public IReadOnlyList<StfsFileEntry> Entries => mEntries ??= ReadEntries();

        private int BlockOffset(int block)
        {
            var backing = (((block + BlocksPerTable) / BlocksPerTable) << mTablesShift) + block;
            if (block >= BlocksPerTable)
            {
                backing += ((block + BlocksPerLevel1) / BlocksPerLevel1) << mTablesShift;
                if (block >= BlocksPerLevel1)
                {
                    backing += 1 << mTablesShift;
                }
            }
            return mFirstBlockOffset + backing * BlockSize;
        }

        private int HashBlockNumber(int level, int block)
        {
            if (level == 0)
            {
                var number = (block / BlocksPerTable) * mBlockStep[0];
                if (block >= BlocksPerTable)
                {
                    number += ((block / BlocksPerLevel1) + 1) << mTablesShift;
                    if (block >= BlocksPerLevel1)
                    {
                        number += 1 << mTablesShift;
                    }
                }
                return number;
            }
            if (level == 1)
            {
                return block < BlocksPerLevel1 ? mBlockStep[0] : (1 << mTablesShift) + (block / BlocksPerLevel1) * mBlockStep[1];
            }
            return mBlockStep[1];
        }

        // Offset of the current hash table at a level that covers a data block.
        private int TableOffset(int level, int block)
        {
            var offset = mFirstBlockOffset + HashBlockNumber(level, block) * BlockSize;
            if (mTablesShift == 0)
            {
                return offset;
            }
            var second = level == mTopLevel ? (mData[0x37B] & 2) != 0 : (mData[EntryOffset(level + 1, block) + 0x14] & 0x40) != 0;
            return second ? offset + BlockSize : offset;
        }

        private int EntryOffset(int level, int block)
        {
            var index = level == 0 ? block % BlocksPerTable : level == 1 ? (block / BlocksPerTable) % BlocksPerTable : (block / BlocksPerLevel1) % BlocksPerTable;
            return TableOffset(level, block) + index * HashEntrySize;
        }

        private int NextBlock(int block)
        {
            return ReadInt24BE(mData, EntryOffset(0, block) + 0x15);
        }

        private static int ReadInt24BE(byte[] buffer, int offset)
        {
            return (buffer[offset] << 16) | (buffer[offset + 1] << 8) | buffer[offset + 2];
        }

        private static void WriteInt24BE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 16);
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)value;
        }

        private static int ReadInt24LE(byte[] buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        }

        private static void WriteInt24LE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
        }

        private IEnumerable<int> GetBlocks(int startBlock, int count, bool consecutive)
        {
            var block = startBlock;
            for (var i = 0; i < count; i++)
            {
                yield return block;
                block = consecutive ? block + 1 : NextBlock(block);
            }
        }

        private List<StfsFileEntry> ReadEntries()
        {
            var fileTableBlocks = BinaryPrimitives.ReadInt16LittleEndian(mData.AsSpan(0x37C));
            var fileTableBlock = ReadInt24LE(mData, 0x37E);

            var entries = new List<StfsFileEntry>();
            var names = new List<string>();
            var parents = new List<short>();
            foreach (var block in GetBlocks(fileTableBlock, fileTableBlocks, false))
            {
                var blockOffset = BlockOffset(block);
                for (var i = 0; i < BlockSize / FileEntrySize; i++)
                {
                    var offset = blockOffset + i * FileEntrySize;
                    var flags = mData[offset + 0x28];
                    var nameLength = flags & 0x3F;
                    if (nameLength == 0)
                    {
                        break;
                    }
                    var entry = new StfsFileEntry
                    {
                        IsDirectory = (flags & 0x80) != 0,
                        Size = BinaryPrimitives.ReadInt32BigEndian(mData.AsSpan(offset + 0x34)),
                        StartBlock = ReadInt24LE(mData, offset + 0x2F),
                        BlockCount = ReadInt24LE(mData, offset + 0x29),
                        Consecutive = (flags & 0x40) != 0,
                        RawEntry = mData.AsSpan(offset, FileEntrySize).ToArray()
                    };
                    entries.Add(entry);
                    names.Add(Encoding.ASCII.GetString(mData, offset, nameLength));
                    parents.Add(BinaryPrimitives.ReadInt16BigEndian(mData.AsSpan(offset + 0x32)));
                }
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var path = names[i];
                var depth = 0;
                for (int parent = parents[i]; parent >= 0 && parent < entries.Count && depth < entries.Count; parent = parents[parent], depth++)
                {
                    path = names[parent] + "\\" + path;
                }
                entries[i].Path = path;
            }
            return entries;
        }

        public byte[] ReadFile(StfsFileEntry entry)
        {
            var result = new byte[entry.IsDirectory ? 0 : entry.Size];
            var position = 0;
            foreach (var block in GetBlocks(entry.StartBlock, entry.BlockCount, entry.Consecutive))
            {
                var length = Math.Min(BlockSize, result.Length - position);
                if (length <= 0)
                {
                    break;
                }
                Array.Copy(mData, BlockOffset(block), result, position, length);
                position += length;
            }
            return result;
        }

        // Builds a new read-only package with the same header and files, taking a file's data from replacements where given.
        public byte[] Rebuild(IReadOnlyDictionary<StfsFileEntry, byte[]>? replacements = null)
        {
            var entries = Entries;
            var contents = new byte[entries.Count][];
            for (var i = 0; i < entries.Count; i++)
            {
                contents[i] = replacements != null && replacements.TryGetValue(entries[i], out var replacement) ? replacement : ReadFile(entries[i]);
            }

            // The file table comes first, then each file's blocks in file table order.
            var fileTableBlocks = Math.Max(1, (entries.Count * FileEntrySize + BlockSize - 1) / BlockSize);
            var startBlocks = new int[entries.Count];
            var blockCounts = new int[entries.Count];
            var allocatedBlocks = fileTableBlocks;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsDirectory || contents[i].Length == 0)
                {
                    continue;
                }
                startBlocks[i] = allocatedBlocks;
                blockCounts[i] = (contents[i].Length + BlockSize - 1) / BlockSize;
                allocatedBlocks += blockCounts[i];
            }

            var header = mData.AsSpan(0, mFirstBlockOffset).ToArray();
            header[0x37B] = (byte)((header[0x37B] & ~3) | 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(0x37C), (short)fileTableBlocks);
            WriteInt24LE(header, 0x37E, 0);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x395), allocatedBlocks);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x399), 0);

            var layout = new StfsPackage(header);
            var output = new byte[layout.BlockOffset(allocatedBlocks - 1) + BlockSize];
            Array.Copy(header, output, header.Length);
            var package = new StfsPackage(output);

            var fileTable = new byte[fileTableBlocks * BlockSize];
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i].RawEntry.AsSpan().ToArray();
                if (entries[i].IsDirectory == false)
                {
                    entry[0x28] = (byte)((entry[0x28] & 0xBF) | (blockCounts[i] > 0 ? 0x40 : 0));
                    WriteInt24LE(entry, 0x29, blockCounts[i]);
                    WriteInt24LE(entry, 0x2C, blockCounts[i]);
                    WriteInt24LE(entry, 0x2F, startBlocks[i]);
                    BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(0x34), contents[i].Length);
                }
                Array.Copy(entry, 0, fileTable, i * FileEntrySize, FileEntrySize);
            }
            package.WriteChain(0, fileTable, fileTableBlocks);
            for (var i = 0; i < entries.Count; i++)
            {
                if (blockCounts[i] > 0)
                {
                    package.WriteChain(startBlocks[i], contents[i], blockCounts[i]);
                }
            }
            package.HashTables(allocatedBlocks);
            return output;
        }

        // Writes data to consecutive blocks and fills in their level 0 hash entries.
        private void WriteChain(int startBlock, byte[] data, int blockCount)
        {
            for (var i = 0; i < blockCount; i++)
            {
                var block = startBlock + i;
                var blockOffset = BlockOffset(block);
                var position = i * BlockSize;
                Array.Copy(data, position, mData, blockOffset, Math.Min(BlockSize, data.Length - position));
                var entryOffset = EntryOffset(0, block);
                SHA1.HashData(mData.AsSpan(blockOffset, BlockSize), mData.AsSpan(entryOffset, 20));
                mData[entryOffset + 0x14] = 0x80;
                WriteInt24BE(mData, entryOffset + 0x15, i == blockCount - 1 ? 0xFFFFFF : block + 1);
            }
        }

        // Hashes each table into its parent, up to the top table, whose hash goes in the volume descriptor, then the header.
        private void HashTables(int allocatedBlocks)
        {
            for (var level = 1; level <= mTopLevel; level++)
            {
                // Level 1 and 2 tables end with the number of data blocks they cover.
                var coverage = level == 1 ? BlocksPerLevel1 : BlocksPerLevel1 * BlocksPerTable;
                for (var block = 0; block < allocatedBlocks; block += coverage)
                {
                    BinaryPrimitives.WriteInt32BigEndian(mData.AsSpan(TableOffset(level, block) + 0xFF0), Math.Min(coverage, allocatedBlocks - block));
                }

                var span = level == 1 ? BlocksPerTable : BlocksPerLevel1;
                for (var block = 0; block < allocatedBlocks; block += span)
                {
                    SHA1.HashData(mData.AsSpan(TableOffset(level - 1, block), BlockSize), mData.AsSpan(EntryOffset(level, block), 20));
                }
            }
            SHA1.HashData(mData.AsSpan(TableOffset(mTopLevel, 0), BlockSize), mData.AsSpan(0x381, 20));
            BinaryPrimitives.WriteInt64BigEndian(mData.AsSpan(0x34C), mData.Length - mFirstBlockOffset);
            SHA1.HashData(mData.AsSpan(0x344, mFirstBlockOffset - 0x344), mData.AsSpan(0x32C, 20));
        }
    }
}
