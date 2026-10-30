using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XboxToolkit.Internal.ContainerBuilder
{
    internal static class ContainerBuilderHelper
    {
        public static DirectoryEntry BuildDirectoryTree(string basePath, string relativePath, Dictionary<string, uint> directorySizes, uint sectorSize)
        {
            var rootEntry = new DirectoryEntry { Path = relativePath };
            var stack = new Stack<(DirectoryEntry entry, string basePath, string relativePath)>();
            stack.Push((rootEntry, basePath, relativePath));

            // Phase 1: Build the directory tree structure
            while (stack.Count > 0)
            {
                var (dirEntry, currentBasePath, currentRelativePath) = stack.Pop();
                var fullPath = string.IsNullOrEmpty(currentRelativePath) ? currentBasePath : Path.Combine(currentBasePath, currentRelativePath);

                // Get files in this directory
                var files = Directory.GetFiles(fullPath);
                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    var fileName = Path.GetFileName(file);
                    var fileRelativePath = string.IsNullOrEmpty(currentRelativePath) ? fileName : Path.Combine(currentRelativePath, fileName).Replace('\\', '/');
                    
                    dirEntry.Files.Add(new FileEntry
                    {
                        RelativePath = fileRelativePath,
                        FullPath = file,
                        Size = (uint)fileInfo.Length
                    });
                }

                // Get subdirectories and add them to stack
                var subdirs = Directory.GetDirectories(fullPath);
                foreach (var subdir in subdirs)
                {
                    var dirName = Path.GetFileName(subdir);
                    var subdirRelativePath = string.IsNullOrEmpty(currentRelativePath) ? dirName : Path.Combine(currentRelativePath, dirName).Replace('\\', '/');
                    var subdirEntry = new DirectoryEntry { Path = subdirRelativePath };
                    dirEntry.Subdirectories.Add(subdirEntry);
                    
                    // Push subdirectory onto stack for processing
                    stack.Push((subdirEntry, currentBasePath, subdirRelativePath));
                }
            }

            // Phase 2: Calculate directory sizes (post-order: children before parents)
            var directories = new List<DirectoryEntry>();
            var sizeStack = new Stack<DirectoryEntry>();
            sizeStack.Push(rootEntry);

            while (sizeStack.Count > 0)
            {
                var currentDir = sizeStack.Pop();
                directories.Add(currentDir);

                // Push subdirectories in reverse order so they're processed in correct order
                for (int i = currentDir.Subdirectories.Count - 1; i >= 0; i--)
                {
                    sizeStack.Push(currentDir.Subdirectories[i]);
                }
            }

            // Process directories in reverse order (post-order: children before parents)
            for (int i = directories.Count - 1; i >= 0; i--)
            {
                var dir = directories[i];
                uint totalSize = 0;

                // Calculate size needed for all entries in this directory
                // Only count entries with non-empty names (matching BuildDirectoryData logic)
                var entries = new List<(bool isDir, string name, uint size)>();
                
                foreach (var file in dir.Files)
                {
                    var fileName = Path.GetFileName(file.RelativePath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        entries.Add((false, fileName, file.Size));
                    }
                }

                foreach (var subdir in dir.Subdirectories)
                {
                    var dirName = Path.GetFileName(subdir.Path);
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        entries.Add((true, dirName, directorySizes[subdir.Path]));
                    }
                }

                // Each entry is: left(2) + right(2) + sector(4) + size(4) + attribute(1) + nameLength(1) + filename
                // Entries must be 4-byte aligned (padded to 4-byte boundary)
                // If entry would cross sector boundary, pad current size to sector boundary first (matches extract-xiso)
                foreach (var entry in entries)
                {
                    var nameBytes = Encoding.ASCII.GetBytes(entry.name);
                    var entryDataSize = 2 + 2 + 4 + 4 + 1 + 1 + (uint)nameBytes.Length;
                    var entrySize = (entryDataSize + 3) & ~3u; // Round up to 4-byte boundary
                    
                    // Check if adding this entry would cross a sector boundary
                    var sectorsBefore = totalSize / sectorSize;
                    var sectorsAfter = (totalSize + entrySize) / sectorSize;
                    if (sectorsAfter > sectorsBefore)
                    {
                        // Entry crosses sector boundary, pad current size to sector boundary first
                        totalSize = Helpers.RoundToMultiple(totalSize, sectorSize);
                    }
                    
                    totalSize += entrySize;
                }

                // Store raw directory size (not rounded to sector) - matches extract-xiso behavior
                // Padding to sector boundary will be added when writing directory entry
                directorySizes[dir.Path] = totalSize;
            }

            return rootEntry;
        }

        public static void CollectFileEntries(DirectoryEntry directory, List<FileEntry> fileEntries)
        {
            fileEntries.AddRange(directory.Files);
            foreach (var subdir in directory.Subdirectories)
            {
                CollectFileEntries(subdir, fileEntries);
            }
        }

        public static void AllocateDirectorySectors(DirectoryEntry directory, Dictionary<string, uint> directorySizes, SectorAllocator allocator, uint baseSector)
        {
            // Root directory is already allocated, skip it
            if (directory.Path != string.Empty)
            {
                var dirSize = directorySizes[directory.Path];
                var dirSectors = Helpers.RoundToMultiple(dirSize, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                directory.Sector = allocator.AllocateDirectorySectors(dirSectors) - baseSector;
            }

            foreach (var subdir in directory.Subdirectories)
            {
                AllocateDirectorySectors(subdir, directorySizes, allocator, baseSector);
            }
        }

        public static uint GetDirectorySector(string path, DirectoryEntry root, uint baseSector)
        {
            if (root.Path == path)
            {
                return root.Sector + baseSector;
            }

            foreach (var subdir in root.Subdirectories)
            {
                var result = GetDirectorySector(path, subdir, baseSector);
                if (result != 0)
                {
                    return result;
                }
            }

            return 0;
        }

        public static void BuildDirectoryData(DirectoryEntry directory, List<FileEntry> fileEntries, Dictionary<string, uint> directorySizes, Dictionary<string, byte[]> directoryData, uint baseSector)
        {
            var entries = new List<(bool isDir, string name, uint sector, uint size, string path)>();
            
            foreach (var file in directory.Files)
            {
                var fileName = Path.GetFileName(file.RelativePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    // Skip files with empty names
                    continue;
                }
                // Directory entries hold sectors relative to baseSector, as readers add it back
                entries.Add((false, fileName, file.Sector, file.Size, string.Empty));
            }

            foreach (var subdir in directory.Subdirectories)
            {
                // Recursively build subdirectory data first
                BuildDirectoryData(subdir, fileEntries, directorySizes, directoryData, baseSector);
                
                var dirName = Path.GetFileName(subdir.Path);
                if (string.IsNullOrEmpty(dirName))
                {
                    // Skip directories with empty names
                    continue;
                }
                
                // Add subdirectory to entries - even if it's empty, it should still be in the parent's entry list
                // For directories, file_size in entry = directory_size + padding to sector boundary (matches extract-xiso)
                var subdirSize = directorySizes[subdir.Path];
                var subdirSizeWithPadding = subdirSize + (Constants.XGD_SECTOR_SIZE - (subdirSize % Constants.XGD_SECTOR_SIZE)) % Constants.XGD_SECTOR_SIZE;
                entries.Add((true, dirName, subdir.Sector, subdirSizeWithPadding, subdir.Path));
            }

            // Sort entries by name for binary tree
            entries.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            // Build binary tree structure
            // Directory size is stored raw, but we need to round to sector boundary for the actual data buffer
            var dirSize = directorySizes[directory.Path];
            var dirSizeRounded = Helpers.RoundToMultiple(dirSize, Constants.XGD_SECTOR_SIZE);
            var dirData = new byte[dirSizeRounded];
            // Initialize to 0xFF padding (matches extract-xiso XISO_PAD_BYTE)
            for (int i = 0; i < dirData.Length; i++)
            {
                dirData[i] = Constants.XISO_PAD_BYTE;
            }
            var offset = 0u;

            // Build binary tree even if entries is empty (will result in all zeros, which is correct for empty directories)
            if (entries.Count > 0)
            {
                BuildBinaryTree(entries, dirData, ref offset, 0, entries.Count - 1, baseSector);
            }
            // If entries.Count == 0, dirData remains all zeros, which is correct for an empty directory

            directoryData[directory.Path] = dirData;
        }

        private static void BuildBinaryTree(List<(bool isDir, string name, uint sector, uint size, string path)> entries, byte[] dirData, ref uint offset, int start, int end, uint baseSector)
        {
            if (start > end)
            {
                return;
            }

            var currentOffset = offset;
            var mid = (start + end) / 2;
            var entry = entries[mid];

            // Calculate entry size (must be 4-byte aligned for Xbox format)
            var entryDataSize = (uint)(2 + 2 + 4 + 4 + 1 + 1 + entry.name.Length);
            var entrySize = (entryDataSize + 3) & ~3u; // Round up to 4-byte boundary
            
            // Ensure name is not empty
            if (string.IsNullOrEmpty(entry.name))
            {
                throw new InvalidOperationException($"Directory entry has empty name at offset {currentOffset}");
            }
            
            var nameBytes = Encoding.ASCII.GetBytes(entry.name);
            if (nameBytes.Length == 0 || nameBytes.Length > 255)
            {
                throw new InvalidOperationException($"Directory entry name has invalid length: {nameBytes.Length}");
            }
            
            // Advance offset past this entry (reserve space, already aligned)
            offset += entrySize;

            // Calculate where children will be written (offsets are in 4-byte units)
            // Use 0 for no children (matches extract-xiso.c line 1853-1854), not 0xFFFF (which is padding)
            ushort leftOffset = 0;
            ushort rightOffset = 0;

            if (start < mid)
            {
                // Left child will be written at current offset (already 4-byte aligned)
                leftOffset = (ushort)(offset / 4);
                BuildBinaryTree(entries, dirData, ref offset, start, mid - 1, baseSector);
            }

            if (mid < end)
            {
                // Right child will be written at current offset (after left subtree, already aligned)
                rightOffset = (ushort)(offset / 4);
                BuildBinaryTree(entries, dirData, ref offset, mid + 1, end, baseSector);
            }

            // Write entry at currentOffset (after children, so offsets are correct)
            using (var stream = new MemoryStream(dirData))
            using (var writer = new BinaryWriter(stream))
            {
                stream.Position = currentOffset;
                writer.Write(leftOffset);
                writer.Write(rightOffset);
                writer.Write(entry.sector);
                writer.Write(entry.size);
                writer.Write((byte)(entry.isDir ? 0x10 : 0x20)); // 0x10 for dir, 0x20 for file (XISO_ATTRIBUTE_ARC)
                writer.Write((byte)nameBytes.Length);
                writer.Write(nameBytes);
                
                // Pad to 4-byte boundary with 0xFF (matches extract-xiso)
                var padding = entrySize - entryDataSize;
                if (padding > 0)
                {
                    var paddingBytes = new byte[padding];
                    for (int i = 0; i < padding; i++)
                    {
                        paddingBytes[i] = Constants.XISO_PAD_BYTE;
                    }
                    writer.Write(paddingBytes);
                }
            }
        }

        public static void WriteXgdHeader(byte[] sector, uint rootDirSector, uint rootDirSize)
        {
            using (var stream = new MemoryStream(sector))
            using (var writer = new BinaryWriter(stream))
            {
                var magic = Encoding.UTF8.GetBytes(Constants.XGD_IMAGE_MAGIC);
                writer.Write(magic);
                if (magic.Length < 20)
                {
                    writer.Write(new byte[20 - magic.Length]);
                }

                writer.Write(rootDirSector);
                writer.Write(rootDirSize);
                writer.Write(DateTime.Now.ToFileTime());
                writer.Write(new byte[0x7c8]); // Padding
                
                writer.Write(magic); // MagicTail
                if (magic.Length < 20)
                {
                    writer.Write(new byte[20 - magic.Length]);
                }
            }
        }
    }
}

