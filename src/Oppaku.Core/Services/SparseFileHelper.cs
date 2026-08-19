using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Oppaku.Core.Services;

public static class SparseFileHelper
{
    private const uint FSCTL_SET_SPARSE = 590020;
    private const uint FSCTL_SET_ZERO_DATA = 622632;

    /// <summary>
    /// Number of bytes reserved at the end of the sparse file for embedded progress metadata.
    /// This zone sits at [ContentSize, ContentSize + MetadataReserve) and is stripped on finalise.
    /// </summary>
    public const int MetadataReserve = 4096;

    private const string MetadataMagic = "OPPAKU_PROGRESS_V2\0"; // fixed 19 bytes + null

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ZERO_DATA_INFORMATION
    {
        public long FileOffset;
        public long BeyondFinalZero;
    }

    /// <summary>
    /// Creates a sparse file pre-allocated to <paramref name="contentSize"/> + <see cref="MetadataReserve"/> bytes.
    /// The extra zone at the end is used to store embedded rebuild progress.
    /// </summary>
    public static void CreateSparseFile(string filePath, long contentSize)
    {
        long totalSize = contentSize + MetadataReserve;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        uint bytesReturned;
        bool result = DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);
        
        if (!result)
            throw new IOException($"Failed to set sparse file attribute. Error code: {Marshal.GetLastWin32Error()}");

        fs.SetLength(totalSize);
    }

    public static void SetZeroData(SafeFileHandle handle, long offset, long count)
    {
        var zeroData = new FILE_ZERO_DATA_INFORMATION
        {
            FileOffset = offset,
            BeyondFinalZero = offset + count
        };

        int size = Marshal.SizeOf(zeroData);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(zeroData, ptr, false);
            uint bytesReturned;
            bool result = DeviceIoControl(handle, FSCTL_SET_ZERO_DATA, ptr, (uint)size, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);
            
            if (!result)
                throw new IOException($"Failed to set zero data range. Error code: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Writes embedded progress JSON into the metadata zone at the end of the file.
    /// </summary>
    public static void WriteEmbeddedProgress(string filePath, RebuildProgress progress)
    {
        string json = JsonSerializer.Serialize(progress);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

        if (jsonBytes.Length + MetadataMagic.Length + 4 > MetadataReserve)
            throw new InvalidOperationException("Progress metadata exceeds reserved zone size.");

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        fs.Position = progress.ContentSize;

        using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        writer.Write(MetadataMagic);
        writer.Write(jsonBytes.Length);
        writer.Write(jsonBytes);
    }

    /// <summary>
    /// Reads the embedded progress JSON from the metadata zone.
    /// Returns null if no valid metadata is found.
    /// </summary>
    public static RebuildProgress? ReadEmbeddedProgress(string filePath, long contentSize)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= contentSize) return null;

        fs.Position = contentSize;
        using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        try
        {
            string magic = reader.ReadString();
            if (magic != MetadataMagic) return null;
            int len = reader.ReadInt32();
            if (len <= 0 || len > MetadataReserve) return null;
            byte[] jsonBytes = reader.ReadBytes(len);
            return JsonSerializer.Deserialize<RebuildProgress>(jsonBytes);
        }
        catch
        {
            return null;
        }
    }
}
