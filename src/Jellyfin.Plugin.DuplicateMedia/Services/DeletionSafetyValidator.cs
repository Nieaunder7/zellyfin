namespace Jellyfin.Plugin.DuplicateMedia.Services;

public static class DeletionSafetyValidator
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The media path is empty.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool PathsEqual(string first, string second)
    {
        return string.Equals(NormalizePath(first), NormalizePath(second), PathComparison);
    }

    public static bool IsInsideLibraryRoot(string filePath, IEnumerable<string> libraryRoots)
    {
        var normalizedFile = NormalizePath(filePath);
        foreach (var root in libraryRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var normalizedRoot = NormalizePath(root);
            var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
            if (normalizedFile.StartsWith(rootWithSeparator, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    public static HashSet<string> ValidateSelection(
        string productCode,
        string? confirmation,
        IEnumerable<string>? selectedItemIds,
        IEnumerable<string> groupItemIds)
    {
        var normalizedCode = productCode.Trim().ToUpperInvariant();
        if (!string.Equals(confirmation?.Trim(), normalizedCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Confirmation must exactly match the unique code.");
        }

        var groupIds = groupItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = selectedItemIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0)
        {
            throw new ArgumentException("Select at least one file to delete.");
        }

        if (selectedIds.Count >= groupIds.Count)
        {
            throw new ArgumentException("At least one file in the duplicate group must be kept.");
        }

        if (!selectedIds.IsSubsetOf(groupIds))
        {
            throw new ArgumentException("One or more selected items do not belong to this duplicate group.");
        }

        return selectedIds;
    }

    public static string GetBatchConfirmation(int groupCount) => $"영구삭제 {groupCount}";

    public static void ValidateBatchConfirmation(int groupCount, string? confirmation)
    {
        if (groupCount < 1)
        {
            throw new ArgumentException("Select at least one group.");
        }

        var expected = GetBatchConfirmation(groupCount);
        if (!string.Equals(confirmation?.Trim(), expected, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Confirmation must exactly match: {expected}");
        }
    }

    public static void ValidateSnapshot(
        string scannedPath,
        string indexedPath,
        long? scannedSizeBytes,
        DateTime scannedModifiedUtc,
        FileInfo currentFile,
        IEnumerable<string> libraryRoots,
        TimeSpan modifiedTolerance)
    {
        if (!PathsEqual(scannedPath, indexedPath))
        {
            throw new InvalidOperationException("The Jellyfin item path has changed since the scan.");
        }

        if (!IsInsideLibraryRoot(indexedPath, libraryRoots))
        {
            throw new InvalidOperationException("The file is outside the current Jellyfin library roots.");
        }

        if (!currentFile.Exists)
        {
            throw new InvalidOperationException("The media file no longer exists.");
        }

        if ((currentFile.Attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidOperationException("The selected path is not a file.");
        }

        if ((currentFile.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Symbolic links and reparse-point files are not deleted.");
        }

        if (scannedSizeBytes.HasValue && currentFile.Length != scannedSizeBytes.Value)
        {
            throw new InvalidOperationException("The file size has changed since the scan.");
        }

        var modifiedDifference = (currentFile.LastWriteTimeUtc - scannedModifiedUtc.ToUniversalTime()).Duration();
        if (modifiedDifference > modifiedTolerance)
        {
            throw new InvalidOperationException("The file modification time has changed since the scan.");
        }
    }
}
