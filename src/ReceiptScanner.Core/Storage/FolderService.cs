namespace ReceiptScanner.Core.Storage;

public sealed record FolderEntry(string Name, string Path, int ReceiptCount, bool HasSubfolders);

public sealed class FolderNameException(string message) : Exception(message);

/// <summary>Folder browsing and editing under the base folder. Nothing outside it is ever touched.</summary>
public sealed class FolderService(string baseFolder)
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public string BaseFolder { get; } = Path.GetFullPath(baseFolder);

    public bool IsInsideBase(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = BaseFolder.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<FolderEntry> List(string folder)
    {
        EnsureInside(folder);
        return new DirectoryInfo(folder)
            .EnumerateDirectories()
            .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !d.Name.StartsWith('.'))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new FolderEntry(d.Name, d.FullName, CountReceipts(d.FullName), HasSubfolders(d)))
            .ToList();
    }

    /// <summary>Counts receipts, not files, so a saved PDF and PNG pair counts once.</summary>
    public static int CountReceipts(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(IsReceiptFile)
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public FolderEntry Create(string parent, string name)
    {
        EnsureInside(parent);
        var clean = ValidateName(name);
        var path = Path.Combine(parent, clean);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new FolderNameException($"\"{clean}\" already exists here.");
        }

        Directory.CreateDirectory(path);
        return new FolderEntry(clean, path, 0, false);
    }

    public string Rename(string folder, string newName)
    {
        EnsureInside(folder);
        if (IsBase(folder))
        {
            throw new FolderNameException("The base folder can't be renamed here.");
        }

        var clean = ValidateName(newName);
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        var target = Path.Combine(parent, clean);
        var onlyCaseChanges = string.Equals(Path.GetFullPath(folder), target, StringComparison.OrdinalIgnoreCase);
        if (!onlyCaseChanges && (Directory.Exists(target) || File.Exists(target)))
        {
            throw new FolderNameException($"\"{clean}\" already exists here.");
        }

        Directory.Move(folder, target);
        return target;
    }

    /// <summary>Only an empty folder can be deleted here, so receipts can't be lost by accident.</summary>
    public void DeleteEmpty(string folder)
    {
        EnsureInside(folder);
        if (IsBase(folder))
        {
            throw new FolderNameException("The base folder can't be deleted.");
        }

        if (Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new FolderNameException("Only empty folders can be deleted. Move its contents out first.");
        }

        Directory.Delete(folder);
    }

    /// <summary>Returns folders from the base down to the given folder, for the breadcrumb.</summary>
    public IReadOnlyList<(string Name, string Path)> Breadcrumb(string folder)
    {
        EnsureInside(folder);
        var parts = new List<(string, string)>();
        var current = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        while (!IsBase(current))
        {
            parts.Insert(0, (Path.GetFileName(current), current));
            current = Path.GetDirectoryName(current)!;
        }

        var baseName = Path.GetFileName(BaseFolder.TrimEnd(Path.DirectorySeparatorChar));
        parts.Insert(0, (string.IsNullOrEmpty(baseName) ? BaseFolder : baseName, BaseFolder));
        return parts;
    }

    public static string ValidateName(string name)
    {
        var clean = name.Trim().TrimEnd('.');
        if (clean.Length == 0)
        {
            throw new FolderNameException("Enter a folder name.");
        }

        if (clean.Length > 100)
        {
            throw new FolderNameException("That name is too long. Use 100 characters or fewer.");
        }

        if (clean.IndexOfAny(InvalidNameChars) >= 0 || clean.Any(char.IsControl))
        {
            throw new FolderNameException("Folder names can't contain < > : \" / \\ | ? or *.");
        }

        var stem = clean.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem))
        {
            throw new FolderNameException($"Windows reserves the name \"{stem}\".");
        }

        return clean;
    }

    private static bool IsReceiptFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSubfolders(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateDirectories()
                .Any(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !d.Name.StartsWith('.'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsBase(string path) =>
        string.Equals(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
            BaseFolder.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void EnsureInside(string path)
    {
        if (!IsInsideBase(path))
        {
            throw new InvalidOperationException("That folder is outside the base folder.");
        }
    }
}
