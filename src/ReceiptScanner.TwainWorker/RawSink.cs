using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>
/// Writes TWAIN memory strips straight to disk. This process is 32-bit, so it can't assume a 660 MB
/// image would fit in memory.
/// </summary>
internal sealed class RawSink : IDisposable
{
    private readonly FileStream _stream;
    private byte[] _rowBuffer = [];
    private int _width;
    private int _bytesPerPixel;
    private bool _invert;
    private int _expectedRows;

    public RawSink(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
    }

    public string Path { get; }

    public int Dpi { get; private set; }

    public long RowsWritten { get; private set; }

    public bool Started { get; private set; }

    public void Start(TWImageInfo info, PixelFlavor flavor, int requestedDpi)
    {
        if (info.BitsPerPixel is not (8 or 24))
        {
            throw new NotSupportedException($"The driver is sending {info.BitsPerPixel}-bit images. Only 8-bit gray and 24-bit color are supported.");
        }

        if (info.Compression != CompressionType.None)
        {
            throw new NotSupportedException($"The driver is sending compressed data ({info.Compression}).");
        }

        _width = info.ImageWidth;
        _bytesPerPixel = info.BitsPerPixel / 8;
        _expectedRows = info.ImageLength;
        _invert = flavor == PixelFlavor.Vanilla && _bytesPerPixel == 1;
        _rowBuffer = new byte[_width * _bytesPerPixel];
        Dpi = info.XResolution > 0 ? (int)Math.Round(info.XResolution) : requestedDpi;
        Started = true;
    }

    public void WriteStrip(TWImageMemXfer info, byte[] data)
    {
        if (info.XOffset != 0)
        {
            throw new NotSupportedException("The driver is sending image tiles, which aren't supported.");
        }

        var stride = _rowBuffer.Length;
        if (info.BytesPerRow < stride)
        {
            throw new InvalidDataException($"The driver sent rows of {info.BytesPerRow} bytes, but {stride} were expected.");
        }

        var rows = (int)info.Rows;
        for (var r = 0; r < rows; r++)
        {
            var from = (long)r * info.BytesPerRow;
            if (from + stride > data.Length)
            {
                throw new InvalidDataException("The driver sent fewer bytes than it reported.");
            }

            Buffer.BlockCopy(data, (int)from, _rowBuffer, 0, stride);
            if (_invert)
            {
                for (var i = 0; i < stride; i++)
                {
                    _rowBuffer[i] = (byte)(255 - _rowBuffer[i]);
                }
            }

            var rowIndex = (long)info.YOffset + r;
            _stream.Position = rowIndex * stride;
            _stream.Write(_rowBuffer);
            RowsWritten = Math.Max(RowsWritten, rowIndex + 1);
        }
    }

    /// <summary>Returns the finished image, or null if fewer rows arrived than the driver announced.</summary>
    public RawImage? Finish()
    {
        if (!Started || RowsWritten == 0)
        {
            return null;
        }

        if (_expectedRows > 0 && RowsWritten < _expectedRows)
        {
            return null;
        }

        _stream.SetLength(RowsWritten * _rowBuffer.Length);
        _stream.Flush(flushToDisk: true);
        return new RawImage(Path, _width, (int)RowsWritten, _bytesPerPixel, Dpi);
    }

    public void Dispose() => _stream.Dispose();
}
