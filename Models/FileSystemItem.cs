using System.Globalization;

namespace FileViewer.Models;

/// <summary>
/// Represents a file or folder discovered during a file system search.
/// </summary>
public sealed class FileSystemItem
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public string ItemTypeDisplay => IsDirectory ? "Folder" : "File";

    public required string Extension { get; init; }

    /// <summary>
    /// Size in bytes, including scanned descendants for folders, or -1 when unavailable.
    /// </summary>
    public required long SizeBytes { get; set; }

    public string SizeDisplay => FormatBytes(SizeBytes);

    public required DateTime CreatedAt { get; init; }

    public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string JunkCategory { get; init; } = string.Empty;

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size:0.##} {units[unitIndex]}";
    }
}
