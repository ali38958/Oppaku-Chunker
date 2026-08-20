using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oppaku.Gui;

public static class SystemIconHelper
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string path, bool isDirectory)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Determine cache key
        string cacheKey = isDirectory ? path : Path.GetExtension(path);
        if (string.IsNullOrEmpty(cacheKey)) cacheKey = "unknown_file";

        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
        {
            return cachedIcon;
        }

        uint flags = SHGFI_ICON | SHGFI_SMALLICON;
        uint attributes = 0;

        if (isDirectory)
        {
            if (path == "This PC")
            {
                // Fallback to desktop icon or something generic for This PC
                path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }
            // For drives and specific folders, we must query the real path to get custom icons
        }
        else
        {
            // For files, we don't need to hit the disk, just use the extension
            flags |= SHGFI_USEFILEATTRIBUTES;
            path = "dummy" + cacheKey;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }

        SHFILEINFO shinfo = new SHFILEINFO();
        IntPtr res = SHGetFileInfo(path, attributes, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);

        if (res != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
        {
            try
            {
                ImageSource img = Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                
                img.Freeze(); // Essential for caching across threads and performance
                _iconCache[cacheKey] = img;
                return img;
            }
            finally
            {
                DestroyIcon(shinfo.hIcon);
            }
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
