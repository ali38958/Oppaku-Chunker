using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Oppaku.Core.Services;

public class VirtualFolderStream : Stream
{
    private const string Magic = "OPPAKDIR";
    private readonly string _sourceDir;
    private readonly long _totalLength;
    private readonly byte[] _headerBytes;
    
    private readonly List<(string Path, long Size, long StartOffset)> _fileEntries = new();
    
    private long _position;
    private FileStream? _currentFileStream;
    private int _currentFileIndex = -1;

    public VirtualFolderStream(string sourceDir)
    {
        _sourceDir = sourceDir;
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        
        writer.Write(Magic);
        writer.Write(files.Length);
        
        foreach (var file in files)
        {
            string relPath = Path.GetRelativePath(sourceDir, file);
            var fi = new FileInfo(file);
            writer.Write(relPath);
            writer.Write(fi.Length);
        }
        
        writer.Flush();
        _headerBytes = ms.ToArray();
        long currentOffset = _headerBytes.Length;
        
        foreach (var file in files)
        {
            var fi = new FileInfo(file);
            _fileEntries.Add((file, fi.Length, currentOffset));
            currentOffset += fi.Length;
        }
        
        _totalLength = currentOffset;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _totalLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _totalLength) return 0;

        int totalBytesRead = 0;

        while (count > 0 && _position < _totalLength)
        {
            if (_position < _headerBytes.Length)
            {
                int bytesToRead = (int)Math.Min(count, _headerBytes.Length - _position);
                Array.Copy(_headerBytes, _position, buffer, offset, bytesToRead);
                _position += bytesToRead;
                offset += bytesToRead;
                count -= bytesToRead;
                totalBytesRead += bytesToRead;
            }
            else
            {
                EnsureCorrectFileStreamIsOpen();

                if (_currentFileStream == null)
                    break;

                int bytesToRead = (int)Math.Min(count, _currentFileStream.Length - _currentFileStream.Position);
                int bytesRead = _currentFileStream.Read(buffer, offset, bytesToRead);
                
                if (bytesRead == 0)
                {
                    // Reached EOF unexpectedly early, should not happen since we size check, but just in case
                    _position++;
                    continue;
                }

                _position += bytesRead;
                offset += bytesRead;
                count -= bytesRead;
                totalBytesRead += bytesRead;
            }
        }

        return totalBytesRead;
    }

    private void EnsureCorrectFileStreamIsOpen()
    {
        if (_position < _headerBytes.Length || _position >= _totalLength)
        {
            CloseCurrentFile();
            return;
        }

        int targetIndex = -1;
        // Optimization: if we are at the end of the current file, just move to the next
        if (_currentFileIndex >= 0 && _currentFileIndex < _fileEntries.Count)
        {
            var entry = _fileEntries[_currentFileIndex];
            if (_position >= entry.StartOffset && _position < entry.StartOffset + entry.Size)
            {
                targetIndex = _currentFileIndex;
            }
        }
        
        if (targetIndex == -1)
        {
            for (int i = 0; i < _fileEntries.Count; i++)
            {
                var entry = _fileEntries[i];
                if (_position >= entry.StartOffset && _position < entry.StartOffset + entry.Size)
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == -1)
        {
            CloseCurrentFile();
            return;
        }

        if (targetIndex != _currentFileIndex)
        {
            CloseCurrentFile();
            _currentFileIndex = targetIndex;
            var entry = _fileEntries[_currentFileIndex];
            _currentFileStream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        }

        long targetFileOffset = _position - _fileEntries[_currentFileIndex].StartOffset;
        if (_currentFileStream!.Position != targetFileOffset)
        {
            _currentFileStream.Position = targetFileOffset;
        }
    }

    private void CloseCurrentFile()
    {
        _currentFileStream?.Dispose();
        _currentFileStream = null;
        _currentFileIndex = -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _totalLength + offset,
            _ => throw new ArgumentException("Invalid seek origin")
        };

        if (newPosition < 0 || newPosition > _totalLength)
            throw new ArgumentOutOfRangeException(nameof(offset));

        _position = newPosition;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseCurrentFile();
        }
        base.Dispose(disposing);
    }
}
