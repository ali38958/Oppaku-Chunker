using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Oppaku.Core.Services;

public static class SparseFileHelper
{
    private const uint FSCTL_SET_SPARSE = 590020;
    private const uint FSCTL_SET_ZERO_DATA = 622632;

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

    public static void CreateSparseFile(string filePath, long sizeInBytes)
    {
        // Must use FileMode.Create to overwrite or create new
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        
        uint bytesReturned;
        bool result = DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);
        
        if (!result)
        {
            throw new IOException($"Failed to set sparse file attribute. Error code: {Marshal.GetLastWin32Error()}");
        }

        fs.SetLength(sizeInBytes);
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
            {
                throw new IOException($"Failed to set zero data range. Error code: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
