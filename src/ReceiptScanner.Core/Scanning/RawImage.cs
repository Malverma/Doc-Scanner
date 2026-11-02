namespace ReceiptScanner.Core.Scanning;

/// <summary>
/// Uncompressed top-down pixels on disk with no row padding. 1 byte per pixel is gray and 3 is RGB.
/// A file is used because a 72 in scan at 600 dpi is about 660 MB.
/// </summary>
public sealed record RawImage(string Path, int Width, int Height, int BytesPerPixel, int Dpi)
{
    public long ExpectedLength => (long)Width * Height * BytesPerPixel;

    public double HeightMillimeters => Height * 25.4 / Dpi;

    public double WidthMillimeters => Width * 25.4 / Dpi;

    /// <summary>True only if the file has exactly the bytes the dimensions call for.</summary>
    public bool IsIntact()
    {
        var info = new FileInfo(Path);
        return Width > 0 && Height > 0 && Dpi > 0 && BytesPerPixel is 1 or 3 && info.Exists && info.Length == ExpectedLength;
    }
}

/// <summary>A rectangle of a raw image, in pixels. Right and Bottom are exclusive.</summary>
public readonly record struct PixelRegion(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public static PixelRegion Full(RawImage image) => new(0, 0, image.Width, image.Height);
}
