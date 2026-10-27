# XboxToolkit

A comprehensive .NET library for working with Xbox 360 and Xbox Original game containers, executables, and metadata.

## Overview

XboxToolkit provides a complete set of tools for reading, extracting, and creating Xbox game container formats. The library supports multiple container types, XEX file processing, and marketplace metadata integration.

## Features

### Container Support
- **ISO Containers**: Read Xbox Original and Xbox 360 ISO files
- **CCI Containers**: Read and decode compressed container images (LZ4 compressed)
- **GOD Containers**: Read Game on Demand (GOD) container formats
- **Multi-slice Support**: Automatic handling of split container files

### XEX File Processing
- Extract metadata from XEX executables
- Decrypt retail and devkit XEX files
- Decompress compressed XEX data
- Extract game information (Title ID, Media ID, version, regions)
- Extract thumbnails and localized strings
- Parse XDBF (Xbox Data Base Format) structures
- Extract XSRC (Xbox Source) XML data

### Container Building
- Create ISO files from folder structures
- Support for both Xbox Original and Xbox 360 ISO formats
- Progress reporting for long operations

### Marketplace Integration
- Fetch game metadata from Xbox Marketplace
- Download game images and thumbnails
- Parse marketplace XML data

### Image Processing & Texture Conversion
- **XPR Texture Support**: Convert Xbox texture files (XPR) to JPEG format
- **DDS Support**: Convert DirectDraw Surface (DDS) files to PNG format
- **DXT Compression**: Support for DXT1, DXT3, and DXT5 texture compression formats
- **Automatic Format Detection**: Automatically detects and converts various image formats

### Xbox Original (XBE) Support
- Extract and replace images from XBE executables (logo, title, save images)
- Extract certificate information from XBE files
- Replace certificate information in XBE files
- Modify XBE title images

### BIOS Logo Processing
- Decode Xbox BIOS boot logos to PNG format
- Encode PNG images to Xbox BIOS logo format
- Support for custom boot logo creation

### Additional Features
- XGD (Xbox Game Disc) information extraction
- Sector-level decoding and reading
- Cross-platform native library support (Windows, Linux, macOS)

## Installation

### NuGet Package

```bash
Install-Package XboxToolkit
```

The package includes native libraries for Windows, Linux, and macOS, each for x64 and arm64.

## Requirements

- .NET 6.0 or higher
- Native libraries are included in the NuGet package

## Usage Examples

### Reading an ISO Container

```csharp
using XboxToolkit;

// Auto-detect container type
if (ContainerUtility.TryAutoDetectContainerType("game.iso", out var containerReader))
{
    if (containerReader.TryMount())
    {
        // Extract files from container
        ContainerUtility.ExtractFilesFromContainer(containerReader, "output_folder");
        
        containerReader.Dismount();
        containerReader.Dispose();
    }
}
```

### Reading a CCI Container

```csharp
var cciReader = new CCIContainerReader("game.cci");
if (cciReader.TryMount())
{
    var decoder = cciReader.GetDecoder();
    // Use decoder to read sectors...
    cciReader.Dismount();
    cciReader.Dispose();
}
```

### Extracting XEX Metadata

```csharp
byte[] xexData = File.ReadAllBytes("default.xex");
if (XexUtility.TryExtractXexMetaData(xexData, out var metaData))
{
    Console.WriteLine($"Title: {metaData.TitleName}");
    Console.WriteLine($"Title ID: {metaData.TitleId:X8}");
    Console.WriteLine($"Publisher: {metaData.Publisher}");
    Console.WriteLine($"Developer: {metaData.Developer}");
    Console.WriteLine($"Version: {metaData.Version}");
}
```

### Creating an ISO from Folder

```csharp
// Create Xbox 360 ISO
ContainerUtility.ConvertFolderToISO(
    "input_folder",
    ISOFormat.Xbox360,
    "output.iso",
    splitPoint: 0,
    progress: (percent) => Console.WriteLine($"Progress: {percent:P}")
);
```

### Fetching Marketplace Metadata

```csharp
uint titleId = 0x41560817; // Example Title ID
string marketplaceUrl = MarketplaceUtility.GetMarketPlaceUrl(titleId);
// Use the URL to fetch additional metadata
```

### Converting XPR Textures

```csharp
byte[] xprData = File.ReadAllBytes("texture.xpr");
if (XprUtility.ConvertXprToJpeg(xprData, out var jpegData))
{
    File.WriteAllBytes("texture.jpg", jpegData);
}
```

### Converting DDS Files

```csharp
byte[] ddsData = File.ReadAllBytes("texture.dds");
if (XprUtility.ConvertDdsToPng(ddsData, out var pngData))
{
    File.WriteAllBytes("texture.png", pngData);
}
```

### Working with XBE Files

```csharp
byte[] xbeData = File.ReadAllBytes("default.xbe");

// Extract certificate information
if (XbeUtility.TryGetXbeCert(xbeData, out var cert))
{
    Console.WriteLine($"Title: {cert.TitleName}");
    Console.WriteLine($"Title ID: {cert.TitleId:X8}");
}

// Extract title image
if (XbeUtility.TryGetXbeImage(xbeData, XbeUtility.ImageType.TitleImage, out var imageData))
{
    File.WriteAllBytes("title_image.png", imageData);
}

// Replace title image
byte[] newImage = File.ReadAllBytes("new_title.png");
XbeUtility.TryReplaceXbeTitleImage(xbeData, newImage);
```

### Processing BIOS Logos

```csharp
var biosLogo = new BiosLogoUtility();

// Decode BIOS logo to PNG
byte[] logoData = File.ReadAllBytes("bios_logo.bin");
biosLogo.DecodeLogoImage(logoData, out var pngData);
File.WriteAllBytes("bios_logo.png", pngData);

// Encode PNG to BIOS logo format
byte[] pngImage = File.ReadAllBytes("custom_logo.png");
biosLogo.EncodeLogoImage(pngImage, width: 100, height: 17, out var encodedLogo);
File.WriteAllBytes("custom_bios_logo.bin", encodedLogo);
```

## Supported Formats

### Container Formats
- **ISO**: Xbox Original and Xbox 360 disc images
- **CCI**: Compressed Container Image (LZ4 compressed)
- **GOD**: Game on Demand container format

### Executable Formats
- **XEX**: Xbox 360 executable format
- **XBE**: Xbox Original executable format

### Image & Texture Formats
- **XPR**: Xbox texture format (converts to JPEG)
- **DDS**: DirectDraw Surface format (converts to PNG)
- **DXT1/DXT3/DXT5**: Compressed texture formats
- **PNG/JPEG**: Standard image formats for output

### Data Formats
- **XDBF**: Xbox Data Base Format
- **XSRC**: Xbox Source XML format
- **BIOS Logo**: Xbox BIOS boot logo format

## Project Structure

The main library is located in the `XboxToolkit` directory. This project focuses on the core library functionality and does not include test projects.

## License

This project is licensed under the GPL-3.0-only License.

## Authors

- **EqUiNoX** - Team Resurgent

## Repository

- **GitHub**: [https://github.com/Team-Resurgent/XboxToolkit](https://github.com/Team-Resurgent/XboxToolkit)

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Acknowledgments

This library uses the following dependencies:
- **K4os.Compression.LZ4** (v1.3.8) - LZ4 compression support
- **LibDeflate.NET** (v1.19.0) - GZIP decompression support
- **SixLabors.ImageSharp** (v3.1.12) - Image processing and format conversion
