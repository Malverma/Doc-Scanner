using System.Globalization;
using System.IO.Compression;
using System.Text;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Core.Imaging;

/// <summary>
/// Lossless PNG and PDF writers that stream row by row, so memory use stays small however long the
/// receipt is. Both formats use the same "Up" row filter and zlib data.
/// </summary>
public static class ReceiptEncoder
{
    public static void WritePng(RawImage image, PixelRegion region, Stream output)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)region.Width);
        WriteBigEndian(header, 4, (uint)region.Height);
        header[8] = 8;
        header[9] = image.BytesPerPixel == 1 ? (byte)0 : (byte)2;
        WriteChunk(output, "IHDR", header);

        var pixelsPerMeter = (uint)Math.Round(image.Dpi / 0.0254);
        var density = new byte[9];
        WriteBigEndian(density, 0, pixelsPerMeter);
        WriteBigEndian(density, 4, pixelsPerMeter);
        density[8] = 1;
        WriteChunk(output, "pHYs", density);

        using (var chunks = new ChunkWriteStream(output, "IDAT"))
        {
            WriteFilteredData(image, region, chunks);
        }

        WriteChunk(output, "IEND", []);
    }

    public static void WritePdf(RawImage image, PixelRegion region, Stream output)
    {
        var offsets = new long[7];
        var position = new CountingStream(output);

        WriteAscii(position, "%PDF-1.5\n%âãÏÓ\n");

        var pageWidth = Points(region.Width, image.Dpi);
        var pageHeight = Points(region.Height, image.Dpi);
        var content = $"q {pageWidth} 0 0 {pageHeight} 0 0 cm /Im0 Do Q\n";

        void WriteObject(int number, string body)
        {
            offsets[number] = position.Written;
            WriteAscii(position, $"{number} 0 obj\n{body}\nendobj\n");
        }

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] " +
            "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>");
        WriteObject(4, $"<< /Length {content.Length} >>\nstream\n{content}endstream");

        offsets[5] = position.Written;
        var colorSpace = image.BytesPerPixel == 1 ? "/DeviceGray" : "/DeviceRGB";
        WriteAscii(position,
            "5 0 obj\n" +
            $"<< /Type /XObject /Subtype /Image /Width {region.Width} /Height {region.Height} " +
            $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /FlateDecode " +
            $"/DecodeParms << /Predictor 12 /Colors {image.BytesPerPixel} /BitsPerComponent 8 /Columns {region.Width} >> " +
            "/Length 6 0 R >>\nstream\n");
        var dataStart = position.Written;
        WriteFilteredData(image, region, position);
        var dataLength = position.Written - dataStart;
        WriteAscii(position, "\nendstream\nendobj\n");
        WriteObject(6, dataLength.ToString(CultureInfo.InvariantCulture));

        var xref = position.Written;
        var table = new StringBuilder("xref\n0 7\n0000000000 65535 f \n");
        for (var i = 1; i <= 6; i++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offsets[i]:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        WriteAscii(position, table.ToString());
    }

    private static string Points(int pixels, int dpi) =>
        (pixels * 72d / dpi).ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteFilteredData(RawImage image, PixelRegion region, Stream output)
    {
        var channels = image.BytesPerPixel;
        var rowBytes = region.Width * channels;
        var full = new byte[image.Width * channels];
        var previous = new byte[rowBytes];
        var filtered = new byte[rowBytes + 1];
        filtered[0] = 2; // PNG filter type "Up"

        using var handle = File.OpenHandle(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        using var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
        for (var y = region.Top; y < region.Bottom; y++)
        {
            ImageAnalysis.ReadRow(handle, image, y, full);
            var start = region.Left * channels;
            for (var i = 0; i < rowBytes; i++)
            {
                var current = full[start + i];
                filtered[i + 1] = (byte)(current - previous[i]);
                previous[i] = current;
            }

            zlib.Write(filtered);
        }
    }

    private static void WriteAscii(Stream stream, string text) => stream.Write(Encoding.Latin1.GetBytes(text));

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32.Update(Crc32.Start, typeBytes);
        crc = Crc32.Update(crc, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, Crc32.Finish(crc));
        output.Write(crcBytes);
    }

    private static class Crc32
    {
        public const uint Start = 0xFFFFFFFF;
        private static readonly uint[] Table = BuildTable();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }

    /// <summary>Splits written bytes into PNG chunks of a fixed maximum size.</summary>
    private sealed class ChunkWriteStream(Stream inner, string type) : WriteOnlyStream
    {
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _used;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(buffer.Length, _buffer.Length - _used);
                buffer[..take].CopyTo(_buffer.AsSpan(_used));
                _used += take;
                buffer = buffer[take..];
                if (_used == _buffer.Length)
                {
                    Flush();
                }
            }
        }

        public override void Flush()
        {
            if (_used > 0)
            {
                WriteChunk(inner, type, _buffer.AsSpan(0, _used));
                _used = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(Stream inner) : WriteOnlyStream
    {
        public long Written { get; private set; }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Written += buffer.Length;
        }

        public override void Flush() => inner.Flush();
    }

    private abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
