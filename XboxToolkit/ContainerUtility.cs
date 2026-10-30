using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using XboxToolkit.Interface;
using XboxToolkit.Internal;
using XboxToolkit.Internal.Models;
using XboxToolkit.Internal.ContainerBuilder;

namespace XboxToolkit
{
    public static class ContainerUtility
    {
        public static string[] GetSlicesFromFile(string filename)
        {
            var slices = new List<string>();
            var extension = Path.GetExtension(filename);
            var fileWithoutExtension = Path.GetFileNameWithoutExtension(filename);
            var subExtension = Path.GetExtension(fileWithoutExtension);
            if (subExtension?.Length == 2 && char.IsNumber(subExtension[1]))
            {
                var fileWithoutSubExtension = Path.GetFileNameWithoutExtension(fileWithoutExtension);
                return Directory.GetFiles(Path.GetDirectoryName(filename) ?? "", $"{fileWithoutSubExtension}.?{extension}").OrderBy(s => s).ToArray();
            }
            return new string[] { filename };
        }

        public static bool TryAutoDetectContainerType(string filePath, out ContainerReader? containerReader)
        {
            if (ISOContainerReader.IsISO(filePath))
            {
                containerReader = new ISOContainerReader(filePath);
                return true;
            }
            else if (CCIContainerReader.IsCCI(filePath))
            {
                containerReader = new CCIContainerReader(filePath);
                return true;
            }
            else if (GODContainerReader.IsGOD(filePath))
            {
                containerReader = new GODContainerReader(filePath);
                return true;
            }
 
            containerReader = null;
            return false;
        }

        public static bool ExtractFilesFromContainer(ContainerReader containerReader, string destFilePath, Action<string>? progress = null)
        {
            if (containerReader.GetMountCount() == 0)
            {
                return false;
            }

            if (containerReader.TryExtractFiles(destFilePath, progress) == false)
            {
                return false;
            }

            return true;
        }

        public static bool ConvertFolderToISO(string inputFolder, ISOFormat format, string outputFile, long splitPoint, Action<float>? progress)
        {
            try
            {
                var isoFormat = format;

                if (Directory.Exists(inputFolder) == false)
                {
                    return false;
                }

                progress?.Invoke(0.0f);

                // Determine magic sector based on format
                uint magicSector;
                uint baseSector;
                uint rootDirSector;
                if (isoFormat == ISOFormat.XboxOriginal)
                {
                    magicSector = Constants.XGD_ISO_BASE_SECTOR;
                    baseSector = 0;
                    // For Xbox Original, root directory is always at sector 0x108 (matches extract-xiso)
                    rootDirSector = Constants.XISO_ROOT_DIRECTORY_SECTOR;
                }
                else // Xbox360
                {
                    // Use XGD3 as default (most common)
                    magicSector = Constants.XGD_MAGIC_SECTOR_XGD3;
                    baseSector = magicSector - Constants.XGD_ISO_BASE_SECTOR;
                    rootDirSector = 0; // Will be allocated
                }

                // Build directory tree structure and calculate sizes in one pass
                var directorySizes = new Dictionary<string, uint>();
                var rootDirectory = ContainerBuilderHelper.BuildDirectoryTree(inputFolder, string.Empty, directorySizes, Constants.XGD_SECTOR_SIZE);

                progress?.Invoke(0.1f);

                // Collect all file entries from the tree for sector allocation
                var fileEntries = new List<FileEntry>();
                ContainerBuilderHelper.CollectFileEntries(rootDirectory, fileEntries);

                progress?.Invoke(0.2f);

                // Allocate sectors efficiently - maximize usage between magic sector and directory tables
                var sectorAllocator = new SectorAllocator(magicSector, baseSector);
                
                // Allocate root directory
                // Directory size is stored raw (matches extract-xiso), round to sector boundary for allocation
                var rootDirSize = directorySizes[string.Empty];
                var rootDirSizeRounded = Helpers.RoundToMultiple(rootDirSize, Constants.XGD_SECTOR_SIZE);
                var rootDirSectors = rootDirSizeRounded / Constants.XGD_SECTOR_SIZE;
                
                if (isoFormat == ISOFormat.XboxOriginal)
                {
                    // Root directory is fixed at 0x108 for Xbox Original (matches extract-xiso)
                    rootDirectory.Sector = rootDirSector - baseSector;
                    // Mark root directory as fixed so allocator skips it
                    sectorAllocator.SetFixedRootDirectory(rootDirSector, rootDirSectors);
                }
                else
                {
                    // For Xbox360, allocate root directory after magic sector
                    rootDirSector = sectorAllocator.AllocateDirectorySectors(rootDirSectors);
                    rootDirectory.Sector = rootDirSector - baseSector;
                }

                // Allocate sectors for all subdirectories
                ContainerBuilderHelper.AllocateDirectorySectors(rootDirectory, directorySizes, sectorAllocator, baseSector);
                
                // Allocate sectors for files - try to fill space between base and magic sector first, then after directories
                // Process files in directory tree order (matching xdvdfs behavior)
                uint totalFileSectorsAllocated = 0;
                foreach (var fileEntry in fileEntries)
                {
                    var fileSectors = Helpers.RoundToMultiple(fileEntry.Size, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                    var allocatedSector = sectorAllocator.AllocateFileSectors(fileSectors);
                    fileEntry.Sector = allocatedSector - baseSector;
                    totalFileSectorsAllocated += fileSectors;
                }

                progress?.Invoke(0.4f);

                // Build directory data
                var directoryData = new Dictionary<string, byte[]>();
                ContainerBuilderHelper.BuildDirectoryData(rootDirectory, fileEntries, directorySizes, directoryData, baseSector);

                progress?.Invoke(0.6f);

                // Write ISO
                // Note: extract-xiso doesn't pre-calculate total sectors - it writes everything
                // then calculates from final file position. We'll do the same: write all allocated
                // sectors, then pad, then calculate total sectors from final position.
                
                // Get allocated sectors - this is the next available sector after all allocations
                var allocatedSectors = sectorAllocator.GetTotalSectors();
                
                var iteration = 0;
                var currentSector = 0u;
                
                // We'll determine the actual max sector after building the sector map
                // Start with a reasonable estimate
                uint maxSectorToWrite = Math.Max(allocatedSectors, magicSector);
                
                // Track if we've written all sectors
                bool allSectorsWritten = false;

                while (!allSectorsWritten)
                {
                    var splitting = false;
                    using (var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    using (var outputWriter = new BinaryWriter(outputStream))
                    {
                        // Build sector map for directory tables and files
                        var sectorMap = new Dictionary<uint, byte[]>();
                        
                        // Verify root directory is in directoryData
                        if (!directoryData.ContainsKey(string.Empty))
                        {
                            throw new InvalidOperationException("Root directory data is missing from directoryData! Cannot build ISO.");
                        }
                        
                        var rootDirData = directoryData[string.Empty];
                        if (rootDirData == null || rootDirData.Length == 0)
                        {
                            throw new InvalidOperationException("Root directory data is null or empty! Cannot build ISO.");
                        }
                        
                        // Add directory sectors to map - process root directory first to ensure it's not overwritten
                        // Sort to process root directory (string.Empty) first
                        var sortedDirectories = directoryData.OrderBy(d => d.Key == string.Empty ? 0 : 1).ToList();
                        
                        foreach (var dir in sortedDirectories)
                        {
                            var dirSectors = Helpers.RoundToMultiple((uint)dir.Value.Length, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                            var dirSector = dir.Key == string.Empty ? rootDirSector : ContainerBuilderHelper.GetDirectorySector(dir.Key, rootDirectory, baseSector);
                            
                            for (var i = 0u; i < dirSectors; i++)
                            {
                                var sectorIndex = dirSector + i;
                                var offset = (int)(i * Constants.XGD_SECTOR_SIZE);
                                var length = (int)Math.Min(Constants.XGD_SECTOR_SIZE, (uint)(dir.Value.Length - offset));
                                var sectorData = new byte[Constants.XGD_SECTOR_SIZE];
                                if (length > 0)
                                {
                                    Array.Copy(dir.Value, offset, sectorData, 0, length);
                                    // Fill remaining with 0xFF padding
                                    var sectorSizeInt = (int)Constants.XGD_SECTOR_SIZE;
                                    if (length < sectorSizeInt)
                                    {
                                        var paddingNeeded = sectorSizeInt - length;
                                        Helpers.FillArray(sectorData, Constants.XISO_PAD_BYTE, length, paddingNeeded);
                                    }
                                }
                                else
                                {
                                    // If no data, fill with 0xFF padding
                                    Helpers.FillArray(sectorData, Constants.XISO_PAD_BYTE);
                                }
                                
                                // Ensure we don't overwrite existing directory data
                                if (sectorMap.ContainsKey(sectorIndex))
                                {
                                    throw new InvalidOperationException($"Directory sector {sectorIndex} (path: '{dir.Key}') is already in sector map! This indicates duplicate directory entries.");
                                }
                                
                                sectorMap[sectorIndex] = sectorData;
                            }
                        }

                        // Add file sectors to map
                        foreach (var fileEntry in fileEntries)
                        {
                            var fileSector = fileEntry.Sector + baseSector;
                            var fileSectors = Helpers.RoundToMultiple(fileEntry.Size, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                            
                            // Check if file overlaps with root directory (shouldn't happen, but be safe)
                            if (isoFormat == ISOFormat.XboxOriginal && fileSector >= rootDirSector && fileSector < rootDirSector + rootDirSectors)
                            {
                                throw new InvalidOperationException($"File {fileEntry.FullPath} is allocated at sector {fileSector}, which overlaps with root directory at sector {rootDirSector}!");
                            }
                            
                            using (var fileStream = File.OpenRead(fileEntry.FullPath))
                            {
                                for (var i = 0u; i < fileSectors; i++)
                                {
                                    var sectorIndex = fileSector + i;
                                    
                                    // Don't overwrite directory sectors - this should never happen if allocation is correct
                                    if (sectorMap.ContainsKey(sectorIndex))
                                    {
                                        throw new InvalidOperationException($"File sector {sectorIndex} (from {fileEntry.FullPath}) conflicts with directory sector! This indicates a bug in sector allocation.");
                                    }
                                    
                                    var sectorData = new byte[Constants.XGD_SECTOR_SIZE];
                                    var bytesRead = fileStream.Read(sectorData, 0, (int)Constants.XGD_SECTOR_SIZE);
                                    if (bytesRead < Constants.XGD_SECTOR_SIZE)
                                    {
                                        // Pad file to sector boundary with 0xFF (matches extract-xiso)
                                        Helpers.FillArray(sectorData, Constants.XISO_PAD_BYTE, bytesRead, (int)(Constants.XGD_SECTOR_SIZE - bytesRead));
                                    }
                                    sectorMap[sectorIndex] = sectorData;
                                }
                            }
                        }

                        // Find the actual maximum sector that has data (from sector map, magic sector, or allocated sectors)
                        // GetTotalSectors() returns the next available sector, so last sector with data is GetTotalSectors() - 1
                        // But we need to include all sectors up to and including the last sector with data
                        uint actualMaxSector = 0;
                        if (sectorMap.Count > 0)
                        {
                            actualMaxSector = sectorMap.Keys.Max();
                        }
                        actualMaxSector = Math.Max(actualMaxSector, magicSector);
                        // GetTotalSectors() returns the next available sector after all allocations
                        // This means if it returns N, sectors 0 to N-1 have been allocated
                        // We need to write all allocated sectors, so we should write up to N-1 (inclusive)
                        // But we also need to account for the magic sector which might be higher
                        // extract-xiso writes everything and then calculates from final position
                        // So we should write all sectors up to the maximum, ensuring we don't miss any
                        if (allocatedSectors > 0)
                        {
                            // Write up to allocatedSectors to ensure we write all allocated sectors
                            // allocatedSectors is the next available, so we write 0 to allocatedSectors (inclusive)
                            // This ensures we write all allocated sectors plus one scrubbed sector if needed
                            actualMaxSector = Math.Max(actualMaxSector, allocatedSectors);
                        }
                        
                        // Don't add extra buffer - write exactly what's needed
                        // The actualMaxSector should be the maximum of:
                        // - The maximum sector in the sector map (files and directories)
                        // - The magic sector
                        // - The allocated sectors (next available sector - 1 for last sector with data)

                        // Write all sectors in order from 0 to actualMaxSector (inclusive)
                        // This ensures we write all sectors with data plus any needed scrubbed sectors
                        uint sectorsWritten = 0;
                        uint sectorsWithData = 0;
                        while (currentSector <= actualMaxSector)
                        {
                            var estimatedSize = outputStream.Position + Constants.XGD_SECTOR_SIZE;
                            if (splitPoint > 0 && estimatedSize > splitPoint)
                            {
                                splitting = true;
                                break;
                            }

                            byte[] sectorToWrite;
                            if (currentSector == magicSector)
                            {
                                // Write magic sector with XGD header
                                // IMPORTANT: Do NOT write from sectorMap here - the header must not be overwritten
                                if (sectorMap.ContainsKey(currentSector))
                                {
                                    throw new InvalidOperationException($"Sector {currentSector} (magic sector) is in sectorMap! This would overwrite the XGD header. This indicates a bug in sector allocation.");
                                }
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                ContainerBuilderHelper.WriteXgdHeader(sectorToWrite, rootDirSector - baseSector, rootDirSize);
                                sectorsWithData++;
                            }
                            else if (currentSector < baseSector)
                            {
                                // Sectors before base - extract-xiso writes zeros (0x00) for XISO_HEADER_OFFSET (0x10000 bytes = 32 sectors)
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                var headerOffsetSectors = Constants.XISO_FILE_MODULUS / Constants.XGD_SECTOR_SIZE; // 32 sectors
                                if (currentSector < headerOffsetSectors)
                                {
                                    // First 32 sectors (0x10000 bytes) are zeros (matches extract-xiso line 1017-1018)
                                    Helpers.FillArray(sectorToWrite, (byte)0x00);
                                }
                                else
                                {
                                    // Rest before base are 0xFF
                                    Helpers.FillArray(sectorToWrite, Constants.XISO_PAD_BYTE);
                                }
                            }
                            else if (isoFormat == ISOFormat.XboxOriginal && currentSector < Constants.XISO_ROOT_DIRECTORY_SECTOR)
                            {
                                // For Xbox Original, baseSector is 0, so we need to handle sectors before root directory separately
                                // extract-xiso writes zeros (0x00) for sectors before the root directory (sector 0x108)
                                // The first 32 sectors (0x10000 bytes) are zeros, and sectors 0x21-0x107 are also zeros
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                Helpers.FillArray(sectorToWrite, (byte)0x00);
                            }
                            else if (sectorMap.ContainsKey(currentSector))
                            {
                                // Sector with file or directory data
                                sectorToWrite = sectorMap[currentSector];
                                sectorsWithData++;
                            }
                            else
                            {
                                // Scrubbed sector (0xFF filled)
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                Helpers.FillArray(sectorToWrite, Constants.XISO_PAD_BYTE);
                            }

                            outputWriter.Write(sectorToWrite);
                            currentSector++;
                            sectorsWritten++;

                            var currentProgress = 0.6f + 0.4f * (currentSector / (float)(actualMaxSector + 1));
                            progress?.Invoke(currentProgress);
                        }
                        
                        // Mark that we've written all sectors
                        if (currentSector > actualMaxSector)
                        {
                            allSectorsWritten = true;
                        }

                        // After writing all sectors, for Xbox Original format (matches extract-xiso):
                        // 1. Pad to 0x10000 boundary
                        // 2. Calculate total sectors from final file size
                        // 3. Write volume descriptors at sector 0x10 (offset 0x8000)
                        if (isoFormat == ISOFormat.XboxOriginal && !splitting)
                        {
                            // First, pad to 0x10000 boundary (matches extract-xiso line 1062)
                            var currentPos = outputStream.Position;
                            var paddingNeeded = (int)((Constants.XISO_FILE_MODULUS - currentPos % Constants.XISO_FILE_MODULUS) % Constants.XISO_FILE_MODULUS);
                            if (paddingNeeded > 0)
                            {
                                var padding = new byte[paddingNeeded];
                                Helpers.FillArray(padding, Constants.XISO_PAD_BYTE);
                                outputWriter.Write(padding);
                            }
                        }

                    }

                    if (splitting || iteration > 0)
                    {
                        var destFile = Path.Combine(Path.GetDirectoryName(outputFile) ?? "", Path.GetFileNameWithoutExtension(outputFile) + $".{iteration + 1}{Path.GetExtension(outputFile)}");
                        if (File.Exists(destFile))
                        {
                            File.Delete(destFile);
                        }
                        File.Move(outputFile, destFile);
                    }

                    iteration++;
                }

                progress?.Invoke(1.0f);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Print(ex.ToString());
                return false;
            }
        }

        public static bool ConvertFolderToCCI(string inputFolder, ISOFormat format, string outputFile, long splitPoint, Action<float>? progress)
        {
            try
            {
                var isoFormat = format;

                if (Directory.Exists(inputFolder) == false)
                {
                    return false;
                }

                progress?.Invoke(0.0f);

                // Determine magic sector based on format
                uint magicSector;
                uint baseSector;
                uint rootDirSector;
                if (isoFormat == ISOFormat.XboxOriginal)
                {
                    magicSector = Constants.XGD_ISO_BASE_SECTOR;
                    baseSector = 0;
                    // For Xbox Original, root directory is always at sector 0x108 (matches extract-xiso)
                    rootDirSector = Constants.XISO_ROOT_DIRECTORY_SECTOR;
                }
                else // Xbox360
                {
                    // Use XGD3 as default (most common)
                    magicSector = Constants.XGD_MAGIC_SECTOR_XGD3;
                    baseSector = magicSector - Constants.XGD_ISO_BASE_SECTOR;
                    rootDirSector = 0; // Will be allocated
                }

                // Build directory tree structure and calculate sizes in one pass
                var directorySizes = new Dictionary<string, uint>();
                var rootDirectory = ContainerBuilderHelper.BuildDirectoryTree(inputFolder, string.Empty, directorySizes, Constants.XGD_SECTOR_SIZE);

                progress?.Invoke(0.1f);

                // Collect all file entries from the tree for sector allocation
                var fileEntries = new List<FileEntry>();
                ContainerBuilderHelper.CollectFileEntries(rootDirectory, fileEntries);
                
                // Sort files by directory path, then filename
                fileEntries.Sort((a, b) =>
                {
                    var dirA = Path.GetDirectoryName(a.RelativePath)?.Replace('\\', '/') ?? string.Empty;
                    var dirB = Path.GetDirectoryName(b.RelativePath)?.Replace('\\', '/') ?? string.Empty;
                    var dirCompare = string.Compare(dirA, dirB, StringComparison.OrdinalIgnoreCase);
                    if (dirCompare != 0) return dirCompare;
                    var fileA = Path.GetFileName(a.RelativePath);
                    var fileB = Path.GetFileName(b.RelativePath);
                    return string.Compare(fileA, fileB, StringComparison.OrdinalIgnoreCase);
                });

                progress?.Invoke(0.2f);
                
                // Allocate sectors efficiently - maximize usage between magic sector and directory tables
                var sectorAllocator = new SectorAllocator(magicSector, baseSector);
                
                // Allocate root directory
                // Directory size is stored raw (matches extract-xiso), round to sector boundary for allocation
                var rootDirSize = directorySizes[string.Empty];
                var rootDirSizeRounded = Helpers.RoundToMultiple(rootDirSize, Constants.XGD_SECTOR_SIZE);
                var rootDirSectors = rootDirSizeRounded / Constants.XGD_SECTOR_SIZE;
                
                if (isoFormat == ISOFormat.XboxOriginal)
                {
                    // Root directory is fixed at 0x108 for Xbox Original (matches extract-xiso)
                    rootDirectory.Sector = rootDirSector - baseSector;
                    // Mark root directory as fixed so allocator skips it
                    sectorAllocator.SetFixedRootDirectory(rootDirSector, rootDirSectors);
                }
                else
                {
                    // For Xbox360, allocate root directory after magic sector
                    rootDirSector = sectorAllocator.AllocateDirectorySectors(rootDirSectors);
                    rootDirectory.Sector = rootDirSector - baseSector;
                }

                // Allocate sectors for all subdirectories
                ContainerBuilderHelper.AllocateDirectorySectors(rootDirectory, directorySizes, sectorAllocator, baseSector);
                
                // Allocate sectors for files - try to fill space between base and magic sector first, then after directories
                uint totalFileSectorsAllocated = 0;
                foreach (var fileEntry in fileEntries)
                {
                    var fileSectors = Helpers.RoundToMultiple(fileEntry.Size, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                    var allocatedSector = sectorAllocator.AllocateFileSectors(fileSectors);
                    fileEntry.Sector = allocatedSector - baseSector;
                    totalFileSectorsAllocated += fileSectors;
                }

                progress?.Invoke(0.4f);

                // Build directory data
                var directoryData = new Dictionary<string, byte[]>();
                ContainerBuilderHelper.BuildDirectoryData(rootDirectory, fileEntries, directorySizes, directoryData, baseSector);

                progress?.Invoke(0.6f);

                // Write CCI
                var totalSectors = sectorAllocator.GetTotalSectors();
                var iteration = 0;
                var currentSector = 0u;

                while (currentSector < totalSectors)
                {
                    var cciIndices = new List<CCIIndex>();
                    var splitting = false;
                    var compressedData = new byte[Constants.XGD_SECTOR_SIZE];
                    var indexAlignment = 2;
                    var multiple = (1 << indexAlignment);

                    using (var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    using (var outputWriter = new BinaryWriter(outputStream))
                    {
                        // Build sector map for directory tables and files
                        var sectorMap = new Dictionary<uint, byte[]>();
                        
                        // Verify root directory is in directoryData
                        if (!directoryData.ContainsKey(string.Empty))
                        {
                            throw new InvalidOperationException("Root directory data is missing from directoryData! Cannot build ISO.");
                        }
                        
                        var rootDirData = directoryData[string.Empty];
                        if (rootDirData == null || rootDirData.Length == 0)
                        {
                            throw new InvalidOperationException("Root directory data is null or empty! Cannot build ISO.");
                        }
                        
                        // Add directory sectors to map - process root directory first to ensure it's not overwritten
                        // Sort to process root directory (string.Empty) first
                        var sortedDirectories = directoryData.OrderBy(d => d.Key == string.Empty ? 0 : 1).ToList();
                        
                        foreach (var dir in sortedDirectories)
                        {
                            var dirSectors = Helpers.RoundToMultiple((uint)dir.Value.Length, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                            var dirSector = dir.Key == string.Empty ? rootDirSector : ContainerBuilderHelper.GetDirectorySector(dir.Key, rootDirectory, baseSector);
                            
                            for (var i = 0u; i < dirSectors; i++)
                            {
                                var sectorIndex = dirSector + i;
                                var offset = (int)(i * Constants.XGD_SECTOR_SIZE);
                                var length = (int)Math.Min(Constants.XGD_SECTOR_SIZE, (uint)(dir.Value.Length - offset));
                                var sectorData = new byte[Constants.XGD_SECTOR_SIZE];
                                if (length > 0)
                                {
                                    Array.Copy(dir.Value, offset, sectorData, 0, length);
                                    // Fill remaining with 0xFF padding
                                    var sectorSizeInt = (int)Constants.XGD_SECTOR_SIZE;
                                    if (length < sectorSizeInt)
                                    {
                                        var paddingNeeded = sectorSizeInt - length;
                                        Helpers.FillArray(sectorData, Constants.XISO_PAD_BYTE, length, paddingNeeded);
                                    }
                                }
                                else
                                {
                                    // If no data, fill with 0xFF padding
                                    Helpers.FillArray(sectorData, Constants.XISO_PAD_BYTE);
                                }
                                
                                // Ensure we don't overwrite existing directory data
                                if (sectorMap.ContainsKey(sectorIndex))
                                {
                                    throw new InvalidOperationException($"Directory sector {sectorIndex} (path: '{dir.Key}') is already in sector map! This indicates duplicate directory entries.");
                                }
                                
                                sectorMap[sectorIndex] = sectorData;
                            }
                        }

                        // Add file sectors to map
                        foreach (var fileEntry in fileEntries)
                        {
                            var fileSector = fileEntry.Sector + baseSector;
                            var fileSectors = Helpers.RoundToMultiple(fileEntry.Size, Constants.XGD_SECTOR_SIZE) / Constants.XGD_SECTOR_SIZE;
                            
                            // Check if file overlaps with root directory (shouldn't happen, but be safe)
                            if (isoFormat == ISOFormat.XboxOriginal && fileSector >= rootDirSector && fileSector < rootDirSector + rootDirSectors)
                            {
                                throw new InvalidOperationException($"File {fileEntry.FullPath} is allocated at sector {fileSector}, which overlaps with root directory at sector {rootDirSector}!");
                            }
                            
                            using (var fileStream = File.OpenRead(fileEntry.FullPath))
                            {
                                for (var i = 0u; i < fileSectors; i++)
                                {
                                    var sectorIndex = fileSector + i;
                                    
                                    // Don't overwrite directory sectors - this should never happen if allocation is correct
                                    if (sectorMap.ContainsKey(sectorIndex))
                                    {
                                        throw new InvalidOperationException($"File sector {sectorIndex} (from {fileEntry.FullPath}) conflicts with directory sector! This indicates a bug in sector allocation.");
                                    }
                                    
                                    var sectorData = new byte[Constants.XGD_SECTOR_SIZE];
                                    var bytesRead = fileStream.Read(sectorData, 0, (int)Constants.XGD_SECTOR_SIZE);
                                    if (bytesRead < Constants.XGD_SECTOR_SIZE)
                                    {
                                        // Pad file to sector boundary with 0xFF (matches extract-xiso)
                                        Helpers.FillArray(sectorData, Constants.XISO_PAD_BYTE, bytesRead, (int)(Constants.XGD_SECTOR_SIZE - bytesRead));
                                    }
                                    sectorMap[sectorIndex] = sectorData;
                                }
                            }
                        }

                        // Write CCI header (placeholder values, will update later)
                        uint header = 0x4D494343U;
                        outputWriter.Write(header);

                        uint headerSize = 32;
                        outputWriter.Write(headerSize);

                        ulong uncompressedSize = 0UL;
                        outputWriter.Write(uncompressedSize);

                        ulong indexOffset = 0UL;
                        outputWriter.Write(indexOffset);

                        uint blockSize = 2048;
                        outputWriter.Write(blockSize);

                        byte version = 1;
                        outputWriter.Write(version);

                        outputWriter.Write((byte)indexAlignment);

                        ushort unused = 0;
                        outputWriter.Write(unused);

                        // Write all sectors in order with compression
                        while (currentSector < totalSectors)
                        {
                            var currentIndexSize = cciIndices.Count * 4;
                            var estimatedSize = outputStream.Position + currentIndexSize + Constants.XGD_SECTOR_SIZE;
                            if (splitPoint > 0 && estimatedSize > splitPoint)
                            {
                                splitting = true;
                                break;
                            }

                            byte[] sectorToWrite;
                            if (currentSector == magicSector)
                            {
                                // Write magic sector with XGD header
                                // IMPORTANT: Do NOT write from sectorMap here - the header must not be overwritten
                                if (sectorMap.ContainsKey(currentSector))
                                {
                                    throw new InvalidOperationException($"Sector {currentSector} (magic sector) is in sectorMap! This would overwrite the XGD header. This indicates a bug in sector allocation.");
                                }
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                ContainerBuilderHelper.WriteXgdHeader(sectorToWrite, rootDirSector - baseSector, rootDirSize);
                            }
                            else if (currentSector < baseSector)
                            {
                                // Sectors before base - extract-xiso writes zeros (0x00) for XISO_HEADER_OFFSET (0x10000 bytes = 32 sectors)
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                var headerOffsetSectors = Constants.XISO_FILE_MODULUS / Constants.XGD_SECTOR_SIZE; // 32 sectors
                                if (currentSector < headerOffsetSectors)
                                {
                                    // First 32 sectors (0x10000 bytes) are zeros (matches extract-xiso line 1017-1018)
                                    Helpers.FillArray(sectorToWrite, (byte)0x00);
                                }
                                else
                                {
                                    // Rest before base are 0xFF
                                    Helpers.FillArray(sectorToWrite, Constants.XISO_PAD_BYTE);
                                }
                            }
                            else if (isoFormat == ISOFormat.XboxOriginal && currentSector < Constants.XISO_ROOT_DIRECTORY_SECTOR)
                            {
                                // For Xbox Original, baseSector is 0, so we need to handle sectors before root directory separately
                                // extract-xiso writes zeros (0x00) for sectors before the root directory (sector 0x108)
                                // The first 32 sectors (0x10000 bytes) are zeros, and sectors 0x21-0x107 are also zeros
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                Helpers.FillArray(sectorToWrite, (byte)0x00);
                            }
                            else if (sectorMap.ContainsKey(currentSector))
                            {
                                // Sector with file or directory data
                                sectorToWrite = sectorMap[currentSector];
                            }
                            else
                            {
                                // Scrubbed sector (0xFF filled)
                                sectorToWrite = new byte[Constants.XGD_SECTOR_SIZE];
                                Helpers.FillArray(sectorToWrite, Constants.XISO_PAD_BYTE);
                            }

                            // Try to compress the sector
                            using (var memoryStream = new MemoryStream())
                            {
                                var compressed = false;
                                var compressedSize = K4os.Compression.LZ4.LZ4Codec.Encode(sectorToWrite, compressedData, K4os.Compression.LZ4.LZ4Level.L12_MAX);
                                if (compressedSize > 0 && compressedSize < (Constants.XGD_SECTOR_SIZE - (4 + multiple)))
                                {
                                    var padding = ((compressedSize + 1 + multiple - 1) / multiple * multiple) - (compressedSize + 1);

                                    memoryStream.WriteByte((byte)padding);
                                    memoryStream.Write(compressedData, 0, compressedSize);
                                    if (padding != 0)
                                    {
                                        memoryStream.Write(new byte[padding], 0, padding);
                                    }
                                    compressed = true;
                                }
                                else
                                {
                                    memoryStream.Write(sectorToWrite, 0, sectorToWrite.Length);
                                }

                                var blockData = memoryStream.ToArray();
                                outputWriter.Write(blockData, 0, blockData.Length);
                                cciIndices.Add(new CCIIndex { Value = (ulong)blockData.Length, LZ4Compressed = compressed });
                            }

                            uncompressedSize += Constants.XGD_SECTOR_SIZE;
                            currentSector++;

                            var currentProgress = 0.6f + 0.4f * (currentSector / (float)totalSectors);
                            progress?.Invoke(currentProgress);
                        }

                        // Write index table
                        indexOffset = (ulong)outputStream.Position;

                        var position = (ulong)headerSize;
                        for (var i = 0; i < cciIndices.Count; i++)
                        {
                            var index = (uint)(position >> indexAlignment) | (cciIndices[i].LZ4Compressed ? 0x80000000U : 0U);
                            outputWriter.Write(index);
                            position += cciIndices[i].Value;
                        }
                        var indexEnd = (uint)(position >> indexAlignment);
                        outputWriter.Write(indexEnd);

                        // Update header with actual values
                        outputStream.Position = 8;
                        outputWriter.Write(uncompressedSize);
                        outputWriter.Write(indexOffset);
                    }

                    if (splitting || iteration > 0)
                    {
                        var destFile = Path.Combine(Path.GetDirectoryName(outputFile) ?? "", Path.GetFileNameWithoutExtension(outputFile) + $".{iteration + 1}{Path.GetExtension(outputFile)}");
                        if (File.Exists(destFile))
                        {
                            File.Delete(destFile);
                        }
                        File.Move(outputFile, destFile);
                    }

                    iteration++;
                }

                progress?.Invoke(1.0f);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Print(ex.ToString());
                throw new InvalidOperationException($"CCI generation failed: {ex.Message}", ex);
            }
        }

        public static bool ConvertContainerToISO(ContainerReader containerReader, ProcessingOptions processingOptions, string outputFile, long splitPoint, Action<float>? progress)
        {
            var progressPercent = 0.0f;
            progress?.Invoke(progressPercent);

            if (containerReader.GetMountCount() == 0)
            {
                return false;
            }

            if (containerReader.TryGetDataSectors(out var dataSectors) == false)
            {
                return false;
            }

            var scrubbedSector = new byte[2048];
            for (var i = 0; i < scrubbedSector.Length; i++)
            {
                scrubbedSector[i] = 0x00;
            }

            var decoder = containerReader.GetDecoder();
            var xgdInfo = decoder.GetXgdInfo();

            var startSector = 0u;
            if (processingOptions.HasFlag(ProcessingOptions.RemoveVideoPartition) == false)
            {
                for (var i = 0u; i < xgdInfo.BaseSector; i++)
                {
                    dataSectors.Add(i);
                }
            }
            else
            {
                startSector = xgdInfo.BaseSector;
            }

            var totalSectors = processingOptions.HasFlag(ProcessingOptions.TrimSectors) == true ? Math.Min(Helpers.RoundToMultiple(dataSectors.Max() + 1, 2), decoder.TotalSectors()) : decoder.TotalSectors();

            var currentSector = startSector;
            var iteration = 0;

            while (currentSector < totalSectors)
            {
                var splitting = false;
                using (var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                using (var outputWriter = new BinaryWriter(outputStream))
                {
                    while (currentSector < totalSectors)
                    {
                        var estimatedSize = outputStream.Position + 2048;
                        if (splitPoint > 0 && estimatedSize > splitPoint)
                        {
                            splitting = true;
                            break;
                        }

                        var sectorToWrite = scrubbedSector;
                        var writeSector = (processingOptions.HasFlag(ProcessingOptions.ScrubSectors) == false) || dataSectors.Contains(currentSector);
                        if (writeSector == true)
                        {
                            if (decoder.TryReadSector(currentSector, out sectorToWrite) == false)
                            {
                                return false;
                            }
                        }
                        outputStream.Write(sectorToWrite, 0, sectorToWrite.Length);

                        var currentProgressPercent = (float)Math.Round((currentSector - startSector) / (float)totalSectors, 4);
                        if (Helpers.IsEqualTo(currentProgressPercent, progressPercent) == false)
                        {
                            progress?.Invoke(currentProgressPercent);
                            Interlocked.Exchange(ref progressPercent, currentProgressPercent);
                        }

                        currentSector++;
                    }
                }

                if (splitting || iteration > 0)
                {
                    var destFile = Path.Combine(Path.GetDirectoryName(outputFile) ?? "", Path.GetFileNameWithoutExtension(outputFile) + $".{iteration + 1}{Path.GetExtension(outputFile)}");
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }
                    File.Move(outputFile, destFile);
                }

                iteration++;
            }

            progress?.Invoke(1);
            return true;
        }

        public static bool ConvertContainerToCCI(ContainerReader containerReader, ProcessingOptions processingOptions, string outputFile, long splitPoint, Action<float>? progress)
        {
            var progressPercent = 0.0f;
            progress?.Invoke(progressPercent);

            if (containerReader.GetMountCount() == 0)
            {
                return false;
            }

            if (containerReader.TryGetDataSectors(out var dataSectors) == false)
            {
                return false;
            }

            var scrubbedSector = new byte[Constants.XGD_SECTOR_SIZE];
            for (var i = 0; i < scrubbedSector.Length; i++)
            {
                scrubbedSector[i] = 0x00;
            }

            var decoder = containerReader.GetDecoder();
            var xgdInfo = decoder.GetXgdInfo();

            var startSector = 0u;
            if (processingOptions.HasFlag(ProcessingOptions.RemoveVideoPartition) == false)
            {
                for (var i = 0u; i < xgdInfo.BaseSector; i++)
                {
                    dataSectors.Add(i);
                }
            }
            else
            {
                startSector = xgdInfo.BaseSector;
            }

            var totalSectors = processingOptions.HasFlag(ProcessingOptions.TrimSectors) == true ? Math.Min(Helpers.RoundToMultiple(dataSectors.Max() + 1, 2), decoder.TotalSectors()) : decoder.TotalSectors();

            var compressedData = new byte[2048];
            var currentSector = startSector;
            var iteration = 0;

            while (currentSector < totalSectors)
            {
                var cciIndices = new List<CCIIndex>();

                var splitting = false;
                using (var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                using (var outputWriter = new BinaryWriter(outputStream))
                {
                    uint header = 0x4D494343U;
                    outputWriter.Write(header);

                    uint headerSize = 32;
                    outputWriter.Write(headerSize);

                    ulong uncompressedSize = 0UL;
                    outputWriter.Write(uncompressedSize);

                    ulong indexOffset = 0UL;
                    outputWriter.Write(indexOffset);

                    uint blockSize = 2048;
                    outputWriter.Write(blockSize);

                    byte version = 1;
                    outputWriter.Write(version);

                    byte indexAlignment = 2;
                    outputWriter.Write(indexAlignment);

                    ushort unused = 0;
                    outputWriter.Write(unused);

                    while (currentSector < totalSectors)
                    {
                        var sectorToWrite = scrubbedSector;
                        var writeSector = (processingOptions.HasFlag(ProcessingOptions.ScrubSectors) == false) || dataSectors.Contains(currentSector);
                        if (writeSector == true)
                        {
                            if (decoder.TryReadSector(currentSector, out sectorToWrite) == false)
                            {
                                return false;
                            }
                        }

                        using (var memoryStream = new MemoryStream())
                        {
                            var compressed = false;
                            var compressedSize = K4os.Compression.LZ4.LZ4Codec.Encode(sectorToWrite, compressedData, K4os.Compression.LZ4.LZ4Level.L12_MAX);
                            if (compressedSize > 0 && compressedSize < (2048 - (4 + (1 << indexAlignment))))
                            {
                                var multiple = (1 << indexAlignment);
                                var padding = ((compressedSize + 1 + multiple - 1) / multiple * multiple) - (compressedSize + 1);

                                memoryStream.WriteByte((byte)padding);
                                memoryStream.Write(compressedData, 0, compressedSize);
                                if (padding != 0)
                                {
                                    memoryStream.Write(new byte[padding], 0, padding);
                                }
                                compressed = true;
                            }
                            else
                            {
                                memoryStream.Write(sectorToWrite, 0, sectorToWrite.Length);
                            }

                            var currentIndexSize = cciIndices.Count * 8;
                            var estimatedSize = outputStream.Position + currentIndexSize + memoryStream.Length;
                            if (splitPoint > 0 && estimatedSize > splitPoint)
                            {
                                splitting = true;
                                break;
                            }

                            outputWriter.Write(memoryStream.ToArray(), 0, (int)memoryStream.Length);
                            cciIndices.Add(new CCIIndex { Value = (ulong)memoryStream.Length, LZ4Compressed = compressed });
                        }

                        uncompressedSize += 2048;
                        currentSector++;
                       
                        if (progress != null)
                        {
                            progress((currentSector - startSector) / (float)(totalSectors - startSector));
                        }
                    }

                    indexOffset = (ulong)outputStream.Position;

                    var position = (ulong)headerSize;
                    for (var i = 0; i < cciIndices.Count; i++)
                    {
                        var index = (uint)(position >> indexAlignment) | (cciIndices[i].LZ4Compressed ? 0x80000000U : 0U);
                        outputWriter.Write(index); 
                        position += cciIndices[i].Value;
                    }
                    var indexEnd = (uint)(position >> indexAlignment);
                    outputWriter.Write(indexEnd);

                    outputStream.Position = 8;
                    outputWriter.Write(uncompressedSize);
                    outputWriter.Write(indexOffset);
                }

                if (splitting || iteration > 0)
                {
                    var destFile = Path.Combine(Path.GetDirectoryName(outputFile) ?? "", Path.GetFileNameWithoutExtension(outputFile) + $".{iteration + 1}{Path.GetExtension(outputFile)}");
                    if (File.Exists(destFile))
                    {
                        File.Delete(destFile);
                    }
                    File.Move(outputFile, destFile);
                }

                iteration++;
            }

            progress?.Invoke(1);
            return true;
        }

        public struct FileInfo
        {
            public bool IsFile { get; set; }
            public string Path { get; set; }
            public string Filename { get; set; }
            public long Size { get; set; }
            public int StartSector { get; set; }
            public int EndSector { get; set; }
            public string InSlices { get; set; }
        }

        public static void GetFileInfoFromContainer(ContainerReader containerReader, Action<FileInfo> info, Action<float>? progress, CancellationToken cancellationToken)
        {
            if (progress != null)
            {
                progress(0);
            }

            if (containerReader.GetMountCount() == 0)
            {
                throw new Exception("Container not mounted.");
            }

            try
            {
                var decoder = containerReader.GetDecoder();
                var xgdInfo = decoder.GetXgdInfo();

                var rootSectors = (xgdInfo.RootDirSize + Constants.XGD_SECTOR_SIZE - 1) / Constants.XGD_SECTOR_SIZE;
                var rootData = new byte[xgdInfo.RootDirSize];
                for (var i = 0; i < rootSectors; i++)
                {
                    var currentRootSector = xgdInfo.BaseSector + xgdInfo.RootDirSector + (uint)i;
                    if (decoder.TryReadSector(currentRootSector, out var sectorData) == false)
                    {
                        return;
                    }
                    var offset = i * Constants.XGD_SECTOR_SIZE;
                    var length = Math.Min(Constants.XGD_SECTOR_SIZE, xgdInfo.RootDirSize - offset);
                    Array.Copy(sectorData, 0, rootData, offset, length);
                }

                var treeNodes = new List<TreeNodeInfo>
                {
                    new TreeNodeInfo
                    {
                        DirectoryData = rootData,
                        Offset = 0,
                        Path = string.Empty
                    }
                };

                var totalNodes = 1;
                var processedNodes = 0;

                while (treeNodes.Count > 0)
                {
                    // Use stack (LIFO) for preorder traversal - pop from end
                    var currentTreeNode = treeNodes[treeNodes.Count - 1];
                    treeNodes.RemoveAt(treeNodes.Count - 1);
                    processedNodes++;

                    using (var directoryDataStream = new MemoryStream(currentTreeNode.DirectoryData))
                    using (var directoryDataDataReader = new BinaryReader(directoryDataStream))
                    {
                        if (currentTreeNode.Offset * 4 >= directoryDataStream.Length)
                        {
                            continue;
                        }

                        var entryOffset = currentTreeNode.Offset * 4;
                        directoryDataStream.Position = entryOffset;

                        // Read first 14 bytes (DirectoryEntryDiskNode structure)
                        var headerBuffer = new byte[14];
                        if (directoryDataStream.Read(headerBuffer, 0, 14) != 14)
                        {
                            continue;
                        }

                        // Check if entry is empty (all 0xFF or all 0x00) - matches xdvdfs deserialize check
                        bool allFF = true;
                        bool allZero = true;
                        for (int i = 0; i < 14; i++)
                        {
                            if (headerBuffer[i] != 0xFF) allFF = false;
                            if (headerBuffer[i] != 0x00) allZero = false;
                        }
                        if (allFF || allZero)
                        {
                            continue;
                        }

                        // Parse the header from buffer (matches xdvdfs deserialize)
                        // xdvdfs uses little-endian deserialization
                        var left = (ushort)(headerBuffer[0] | (headerBuffer[1] << 8));
                        var right = (ushort)(headerBuffer[2] | (headerBuffer[3] << 8));
                        var sector = (uint)(headerBuffer[4] | (headerBuffer[5] << 8) | (headerBuffer[6] << 16) | (headerBuffer[7] << 24));
                        var size = (uint)(headerBuffer[8] | (headerBuffer[9] << 8) | (headerBuffer[10] << 16) | (headerBuffer[11] << 24));
                        var attribute = headerBuffer[12];
                        var nameLength = headerBuffer[13];

                        // Validate nameLength is reasonable
                        if (nameLength == 0 || nameLength > 255)
                        {
                            continue;
                        }

                        // Validate we have enough bytes to read the filename
                        var filenameOffset = entryOffset + 14; // 0xe in xdvdfs
                        if (filenameOffset + nameLength > directoryDataStream.Length)
                        {
                            continue;
                        }

                        // Read filename at offset + 0xe (matches xdvdfs read_from_disk)
                        directoryDataStream.Position = filenameOffset;
                        var filenameBytes = directoryDataDataReader.ReadBytes(nameLength);
                        var filename = Encoding.ASCII.GetString(filenameBytes);

                        // Push right first, then left (so left is processed first when popping)
                        // Validate right offset is within bounds
                        if (right != 0 && right != 0xFFFF)
                        {
                            var rightOffsetBytes = right * 4;
                            if (rightOffsetBytes < directoryDataStream.Length)
                            {
                                treeNodes.Add(new TreeNodeInfo
                                {
                                    DirectoryData = currentTreeNode.DirectoryData,
                                    Offset = right,
                                    Path = currentTreeNode.Path
                                });
                                totalNodes++;
                            }
                        }

                        // Validate left offset is within bounds
                        if (left != 0 && left != 0xFFFF)
                        {
                            var leftOffsetBytes = left * 4;
                            if (leftOffsetBytes < directoryDataStream.Length)
                            {
                                treeNodes.Add(new TreeNodeInfo
                                {
                                    DirectoryData = currentTreeNode.DirectoryData,
                                    Offset = left,
                                    Path = currentTreeNode.Path
                                });
                                totalNodes++;
                            }
                        }

                        if ((attribute & 0x10) != 0)
                        {
                            if (size > 0)
                            {
                                var directorySectors = (size + Constants.XGD_SECTOR_SIZE - 1) / Constants.XGD_SECTOR_SIZE;
                                var directoryData = new byte[size];
                                for (var i = 0; i < directorySectors; i++)
                                {
                                    var currentDirectorySector = xgdInfo.BaseSector + sector + (uint)i;
                                    if (decoder.TryReadSector(currentDirectorySector, out var sectorData) == false)
                                    {
                                        return;
                                    }
                                    var offset = i * Constants.XGD_SECTOR_SIZE;
                                    var length = Math.Min(Constants.XGD_SECTOR_SIZE, size - offset);
                                    Array.Copy(sectorData, 0, directoryData, offset, length);
                                }

                                treeNodes.Add(new TreeNodeInfo
                                {
                                    DirectoryData = directoryData,
                                    Offset = 0,
                                    Path = System.IO.Path.Combine(currentTreeNode.Path, filename)
                                });
                                totalNodes++;
                                info(new FileInfo
                                {
                                    IsFile = false,
                                    Path = System.IO.Path.Combine(currentTreeNode.Path, filename),
                                    Filename = filename,
                                    Size = size,
                                    StartSector = (int)(xgdInfo.BaseSector + sector),
                                    EndSector = (int)(xgdInfo.BaseSector + sector + ((size + Constants.XGD_SECTOR_SIZE - 1) / Constants.XGD_SECTOR_SIZE) - 1),
                                    InSlices = "N/A"
                                });
                            }
                        }
                        else
                        {
                            if (size > 0)
                            {
                                var startSector = (int)(xgdInfo.BaseSector + sector);
                                var endSector = (int)(xgdInfo.BaseSector + sector + ((size + Constants.XGD_SECTOR_SIZE - 1) / Constants.XGD_SECTOR_SIZE) - 1);
                                info(new FileInfo
                                {
                                    IsFile = true,
                                    Path = currentTreeNode.Path,
                                    Filename = filename,
                                    Size = size,
                                    StartSector = startSector,
                                    EndSector = endSector,
                                    InSlices = "N/A"
                                });
                            }
                            else
                            {
                                info(new FileInfo
                                {
                                    IsFile = true,
                                    Path = currentTreeNode.Path,
                                    Filename = filename,
                                    Size = size,
                                    StartSector = -1,
                                    EndSector = -1,
                                    InSlices = "N/A"
                                });
                            }
                        }

                        if (progress != null)
                        {
                            progress(processedNodes / (float)totalNodes);
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Print(ex.ToString());
                throw;
            }
        }

        public static string GetChecksumFromContainer(ContainerReader containerReader, Action<float>? progress, CancellationToken cancellationToken)
        {
            if (progress != null)
            {
                progress(0);
            }

            if (containerReader.GetMountCount() == 0)
            {
                throw new Exception("Container not mounted.");
            }

            try
            {
                var decoder = containerReader.GetDecoder();
                using var hash = SHA256.Create();
                
                var totalSectors = decoder.TotalSectors();
                for (var i = 0u; i < totalSectors; i++)
                {
                    if (decoder.TryReadSector(i, out var buffer))
                    {
                        hash.TransformBlock(buffer, 0, buffer.Length, null, 0);
                    }
                    
                    if (progress != null)
                    {
                        progress(i / (float)totalSectors);
                    }
                    
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var sha256Hash = hash.Hash;
                if (sha256Hash == null)
                {
                    throw new ArgumentOutOfRangeException();
                }
                return BitConverter.ToString(sha256Hash).Replace("-", string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Print(ex.ToString());
                throw;
            }
        }

        public static void CompareContainers(ContainerReader containerReader1, ContainerReader containerReader2, Action<string> log, Action<float>? progress)
        {
            if (containerReader1.GetMountCount() == 0 || containerReader2.GetMountCount() == 0)
            {
                log("One or both containers are not mounted.");
                return;
            }

            try
            {
                var decoder1 = containerReader1.GetDecoder();
                var decoder2 = containerReader2.GetDecoder();
                var xgdInfo1 = decoder1.GetXgdInfo();
                var xgdInfo2 = decoder2.GetXgdInfo();

                if (xgdInfo1.BaseSector > 0)
                {
                    log("First contains a video partition, compare will ignore those sectors.");
                }

                if (xgdInfo2.BaseSector > 0)
                {
                    log("Second contains a video partition, compare will ignore those sectors.");
                }

                var totalSectors1 = decoder1.TotalSectors();
                var totalSectors2 = decoder2.TotalSectors();

                if (totalSectors1 - xgdInfo1.BaseSector != totalSectors2 - xgdInfo2.BaseSector)
                {
                    log("Expected sector counts do not match, assuming image could be trimmed.");
                }

                log("");
                log("Getting data sectors hash for first...");
                if (!containerReader1.TryGetDataSectors(out var dataSectors1))
                {
                    log("Failed to get data sectors from first container.");
                    return;
                }
                var dataSectors1Array = dataSectors1.ToArray();
                Array.Sort(dataSectors1Array);

                log("Calculating data sector hashes for first...");
                using var dataSectorsHash1 = SHA256.Create();
                for (var i = 0; i < dataSectors1Array.Length; i++)
                {
                    var dataSector1 = dataSectors1Array[i];
                    if (decoder1.TryReadSector(dataSector1, out var buffer))
                    {
                        dataSectorsHash1.TransformBlock(buffer, 0, buffer.Length, null, 0);
                    }
                    if (progress != null)
                    {
                        progress(i / (float)dataSectors1Array.Length * 0.5f);
                    }
                }
                dataSectorsHash1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var dataChecksum1 = dataSectorsHash1.Hash;
                if (dataChecksum1 == null)
                {
                    throw new ArgumentOutOfRangeException();
                }
                var dataSectorsHash1Result = BitConverter.ToString(dataChecksum1).Replace("-", string.Empty);

                log("Getting data sectors hash for second...");
                if (!containerReader2.TryGetDataSectors(out var dataSectors2))
                {
                    log("Failed to get data sectors from second container.");
                    return;
                }
                var dataSectors2Array = dataSectors2.ToArray();
                Array.Sort(dataSectors2Array);

                log("Calculating data sector hash for second...");
                using var dataSectorsHash2 = SHA256.Create();
                for (var i = 0; i < dataSectors2Array.Length; i++)
                {
                    var dataSector2 = dataSectors2Array[i];
                    if (decoder2.TryReadSector(dataSector2, out var buffer))
                    {
                        dataSectorsHash2.TransformBlock(buffer, 0, buffer.Length, null, 0);
                    }
                    if (progress != null)
                    {
                        progress(0.5f + i / (float)dataSectors2Array.Length * 0.5f);
                    }
                }
                dataSectorsHash2.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var dataChecksum2 = dataSectorsHash2.Hash;
                if (dataChecksum2 == null)
                {
                    throw new ArgumentOutOfRangeException();
                }
                var dataSectorsHash2Result = BitConverter.ToString(dataChecksum2).Replace("-", string.Empty);

                if (dataSectorsHash1Result == dataSectorsHash2Result)
                {
                    log("Data sectors match.");
                }
                else
                {
                    log("Data sectors do not match.");
                }

                log("");
                log($"First image data sectors range: {dataSectors1Array.First()} - {dataSectors1Array.Last()}");
                log($"Second image data sectors range: {dataSectors2Array.First()} - {dataSectors2Array.Last()}");
                log("");
            }
            catch (Exception ex)
            {
                log($"Error during comparison: {ex.Message}");
                System.Diagnostics.Debug.Print(ex.ToString());
            }
        }
    }
}
