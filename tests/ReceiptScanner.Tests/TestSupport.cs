using System.IO.Compression;
using System.Text;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class Images
{
    public const byte Paper = 245;

    /// <summary>Gray image at 100 dpi with its pixels set by a function.</summary>
    public static RawImage Gray(string dir, int width, int height, Func<int, int, byte> pixel, int dpi = 100)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".raw");
        var data = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[(y * width) + x] = pixel(x, y);
            }
        }

        File.WriteAllBytes(path, data);
        return new RawImage(path, width, height, 1, dpi);
    }

    public static RawImage Rgb(string dir, int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel, int dpi = 100)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".raw");
        var data = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var i = ((y * width) + x) * 3;
                data[i] = r;
                data[i + 1] = g;
                data[i + 2] = b;
            }
        }

        File.WriteAllBytes(path, data);
        return new RawImage(path, width, height, 3, dpi);
    }

    /// <summary>Reverses the "Up" row filter that PNG and PDF both use here.</summary>
    public static byte[] Unfilter(byte[] filtered, int rowBytes)
    {
        var rows = filtered.Length / (rowBytes + 1);
        var output = new byte[rows * rowBytes];
        for (var y = 0; y < rows; y++)
        {
            var from = y * (rowBytes + 1);
            Assert.Equal(2, filtered[from]);
            for (var i = 0; i < rowBytes; i++)
            {
                var up = y == 0 ? 0 : output[((y - 1) * rowBytes) + i];
                output[(y * rowBytes) + i] = (byte)(filtered[from + 1 + i] + up);
            }
        }

        return output;
    }

    public static byte[] Inflate(byte[] data)
    {
        using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>Writes a shell script that acts as the worker, so the process handling can be tested without Windows.</summary>
internal static class FakeWorker
{
    public static string Create(string dir, string script)
    {
        var path = Path.Combine(dir, "fake-worker.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n", new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
