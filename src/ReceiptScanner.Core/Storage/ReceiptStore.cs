using System.Globalization;
using System.Runtime.InteropServices;
using ReceiptScanner.Core.Imaging;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Core.Storage;

public sealed record SavedReceipt(string Folder, string BaseName, IReadOnlyList<string> Files);

/// <summary>
/// Puts finished receipts in their folder. Files are written under a temporary name and only take
/// their real name when complete, so a failed save can't leave a partial receipt behind (spec F6).
/// </summary>
public static class ReceiptStore
{
    private const string PartialSuffix = ".partial";

    public static SavedReceipt Save(
        RawImage image,
        PixelRegion region,
        string folder,
        OutputFormat format,
        DateTime timestamp,
        bool unverified = false)
    {
        Directory.CreateDirectory(folder);
        var name = string.Create(CultureInfo.InvariantCulture, $"receipt_{timestamp:yyyy-MM-dd_HH-mm-ss}") + (unverified ? "_UNVERIFIED" : "");
        var baseName = UniqueBaseName(folder, name);
        return new SavedReceipt(folder, baseName, WriteFiles(image, region, folder, baseName, format, replaceExisting: false));
    }

    /// <summary>Rewrites an existing receipt after a change such as rotation.</summary>
    public static SavedReceipt Replace(RawImage image, PixelRegion region, SavedReceipt existing, OutputFormat format)
    {
        var files = WriteFiles(image, region, existing.Folder, existing.BaseName, format, replaceExisting: true);
        return existing with { Files = files };
    }

    public static SavedReceipt Move(SavedReceipt receipt, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        var baseName = UniqueBaseName(targetFolder, receipt.BaseName);
        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var file in receipt.Files)
            {
                var to = Path.Combine(targetFolder, baseName + Path.GetExtension(file));
                File.Move(file, to, overwrite: false);
                moved.Add((file, to));
            }
        }
        catch
        {
            foreach (var (from, to) in moved)
            {
                File.Move(to, from, overwrite: false);
            }

            throw;
        }

        return new SavedReceipt(targetFolder, baseName, moved.Select(m => m.To).ToList());
    }

    /// <summary>Sends files to the Recycle Bin. If that fails, the files are left in place and this throws.</summary>
    public static void Recycle(SavedReceipt receipt)
    {
        foreach (var file in receipt.Files)
        {
            RecycleBin.Send(file);
        }
    }

    private static List<string> WriteFiles(
        RawImage image,
        PixelRegion region,
        string folder,
        string baseName,
        OutputFormat format,
        bool replaceExisting)
    {
        if (!image.IsIntact())
        {
            throw new InvalidDataException("The scan data is incomplete, so it wasn't saved.");
        }

        var extensions = format switch
        {
            OutputFormat.Pdf => new[] { ".pdf" },
            OutputFormat.Png => new[] { ".png" },
            _ => new[] { ".pdf", ".png" },
        };

        var partials = new List<(string Partial, string Final)>();
        try
        {
            foreach (var extension in extensions)
            {
                var final = Path.Combine(folder, baseName + extension);
                var partial = final + PartialSuffix;
                partials.Add((partial, final));
                using var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                if (extension == ".pdf")
                {
                    ReceiptEncoder.WritePdf(image, region, stream);
                }
                else
                {
                    ReceiptEncoder.WritePng(image, region, stream);
                }

                stream.Flush(flushToDisk: true);
            }

            foreach (var (partial, final) in partials)
            {
                File.Move(partial, final, overwrite: replaceExisting);

                // "Saved" must mean the file is really there, whatever the sync client or antivirus did.
                var written = new FileInfo(final);
                if (!written.Exists || written.Length == 0)
                {
                    throw new IOException($"{final} wasn't on disk after saving.");
                }
            }
        }
        catch
        {
            foreach (var (partial, _) in partials)
            {
                TryDelete(partial);
            }

            throw;
        }

        return partials.Select(p => p.Final).ToList();
    }

    private static string UniqueBaseName(string folder, string name)
    {
        var candidate = name;
        for (var n = 2; Exists(folder, candidate); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name}_{n}");
        }

        return candidate;
    }

    private static bool Exists(string folder, string baseName) =>
        File.Exists(Path.Combine(folder, baseName + ".pdf")) || File.Exists(Path.Combine(folder, baseName + ".png"));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .partial file isn't counted as a receipt.
        }
    }

    private static class RecycleBin
    {
        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        public static void Send(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                var operation = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = Path.GetFullPath(path) + "\0\0",
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
                };
                var result = SHFileOperationW(ref operation);
                if (result != 0 || operation.fAnyOperationsAborted)
                {
                    throw new IOException($"Windows couldn't move the file to the Recycle Bin (code {result}).");
                }

                return;
            }

            // Used only with the simulator on non-Windows systems.
            var trash = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trash", "files");
            Directory.CreateDirectory(trash);
            File.Move(path, Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path)));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
    }
}
