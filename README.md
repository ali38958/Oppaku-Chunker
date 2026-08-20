<div align="center">
  <img src="assets/logo.ico" alt="Oppaku Logo" width="120" />

  # Oppaku
  
  **A cryptographic, sparse-file based chunking utility for transporting massive files via tiny storage devices.**

</div>

## 📖 Overview

Oppaku is a powerful file-chunking utility designed to solve a very specific problem: moving incredibly large files (or massive folders) between computers using storage devices (like USBs) that are smaller than the file itself. 

Unlike traditional file splitters that require all parts to be present before reassembling, Oppaku leverages **Sparse File** technology. It immediately creates the massive target file on the destination machine and allows you to inject chunks into it *in any order, one by one*.

## ✨ Why is Oppaku Unique?

- 🧩 **Sequential Injection**: You don't need all chunks on the target machine at once. You can move Chunk 1 with a small USB, inject it, delete it from the USB, go back for Chunk 2, and repeat.
- 📦 **Solid Archives (V2)**: Instead of just splitting files, Oppaku can now create `.oppaku-archive` solid archives that bundle multiple files into one highly compressed payload.
- 🗜️ **Brotli Compression & AES-256**: Archives can be heavily compressed (up to 'Extreme' level) using Brotli algorithms and securely encrypted via AES-256 CBC.
- 🚀 **Zero-Storage Streaming**: When chunking large folders, Oppaku utilizes a custom `VirtualFolderStream` to calculate hashes and slice chunks on the fly directly from the source directory, without creating intermediate temp files.
- 🔒 **Cryptographic Verification**: The original file's SHA-256 hash is embedded directly into the chunk's metadata. When you are done rebuilding, Oppaku mathematically guarantees your final file is a bit-for-bit perfect match.
- 💻 **Dual Interfaces**: Includes both a beautiful, native Windows WPF application and a fully featured cross-platform Terminal CLI.

## 🛠️ Tech Stack

- **Framework**: .NET 10
- **Architecture**: Core library (`Oppaku.Core`) shared between `Oppaku.Gui` (WPF) and `Oppaku.Cli` (Console)
- **Language**: C#

## 🚀 Getting Started

Follow these steps to run Oppaku on your machine.

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/ali38958/Oppaku.git
   cd Oppaku
   ```

2. **Run the GUI Application**
   ```bash
   dotnet run --project src/Oppaku.Gui
   ```

3. **Run the CLI Application**
   ```bash
   dotnet run --project src/Oppaku.Cli
   ```

### 📦 Building Standalone Executables

If you want to build a single `.exe` file that you can share with computers that don't even have .NET installed, run the following command:

**Build GUI:**
```bash
dotnet publish src\Oppaku.Gui -c Release -r win-x64 --self-contained
```
*(The executable will be located at `src\Oppaku.Gui\bin\Release\net10.0-windows\win-x64\publish\Oppaku.exe`)*

**Build CLI:**
```bash
dotnet publish src\Oppaku.Cli -c Release -r win-x64 --self-contained
```
*(The executable will be located at `src\Oppaku.Cli\bin\Release\net10.0\win-x64\publish\oppaku.exe`)*

## 📁 Project Structure

```text
src/
├── Oppaku.Core/    # Shared engine: Hashing, Chunking, Rebuilding, Cryptography
├── Oppaku.Gui/     # WPF graphical user interface
└── Oppaku.Cli/     # Command-line interface wrapper
tests/
└── Oppaku.Tests/   # xUnit test suite for the core engine
assets/             # Application icons and branding
```

## 📄 Documentation

For full instructions on how to extract, rebuild, and finalise files using both the GUI and the CLI commands, please see the [how_to_use.txt](how_to_use.txt) guide included in the repository.

## 📄 License

This project is licensed under the MIT License.

## 👤 Author

**Muhammad Ali**  
[GitHub Profile](https://github.com/ali38958) | [Project Repository](https://github.com/ali38958/Oppaku)
