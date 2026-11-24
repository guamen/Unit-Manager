namespace FileViewer.Models;

/// <summary>
/// Specifies which file system entry types should be included in search results.
/// </summary>
public enum SearchTargetType
{
    Both = 0,
    FilesOnly = 1,
    FoldersOnly = 2
}

/// <summary>
/// Units supported for file size filtering.
/// </summary>
public enum SizeUnit
{
    Bytes = 0,
    KB = 1,
    MB = 2,
    GB = 3
}

/// <summary>
/// Encapsulates all user-specified criteria for scanning a drive.
/// </summary>
public sealed class SearchFilter
{
    public string RootPath { get; init; } = @"C:\";

    public string SearchPattern { get; init; } = string.Empty;

    public SearchTargetType TargetType { get; init; } = SearchTargetType.Both;

    public string ExtensionFilter { get; init; } = string.Empty;

    public bool UseMinSize { get; init; }

    public long MinSizeBytes { get; init; }

    public bool UseMaxSize { get; init; }

    public long MaxSizeBytes { get; init; }

    public bool UseCreatedFrom { get; init; }

    public DateTime CreatedFrom { get; init; }

    public bool UseCreatedTo { get; init; }

    public DateTime CreatedTo { get; init; }

    public bool JunkFilesOnly { get; init; }

    public JunkFileCategory JunkCategory { get; init; } = JunkFileCategory.All;

    /// <summary>
    /// Converts a size value in the selected unit to bytes.
    /// </summary>
    public static long ToBytes(decimal value, SizeUnit unit)
    {
        decimal multiplier = unit switch
        {
            SizeUnit.Bytes => 1m,
            SizeUnit.KB => 1024m,
            SizeUnit.MB => 1024m * 1024m,
            SizeUnit.GB => 1024m * 1024m * 1024m,
            _ => 1m
        };

        decimal result = value * multiplier;
        if (result >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return result <= 0 ? 0L : decimal.ToInt64(decimal.Truncate(result));
    }
}
