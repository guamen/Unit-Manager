using System.IO.Enumeration;
using FileViewer.Models;

namespace FileViewer.Services;

/// <summary>
/// Result of a file system search operation, including partial results if cancelled.
/// </summary>
public sealed record SearchExecutionResult(
    List<FileSystemItem> Items,
    int ScannedCount,
    bool WasCancelled);

/// <summary>
/// Reports search progress to the UI thread.
/// </summary>
public readonly record struct SearchProgressInfo(int ScannedCount, int MatchedCount);

/// <summary>
/// High-performance recursive file and folder scanner built on <see cref="FileSystemEnumerable{T}"/>.
/// </summary>
public sealed class FileSearchService
{
    private const int ProgressReportInterval = 2000;

    public Task<SearchExecutionResult> SearchAsync(
        SearchFilter filter,
        IProgress<SearchProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return Task.Run(() => ExecuteSearch(filter, progress, cancellationToken), CancellationToken.None);
    }

    private static SearchExecutionResult ExecuteSearch(
        SearchFilter filter,
        IProgress<SearchProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        string pattern = filter.SearchPattern.Trim();
        bool hasPattern = pattern.Length > 0;
        bool hasWildcards = hasPattern && (pattern.Contains('*') || pattern.Contains('?'));
        string[] extensions = ParseExtensions(filter.ExtensionFilter);
        bool hasExtensions = extensions.Length > 0;
        bool hasSizeFilter = filter.UseMinSize || filter.UseMaxSize;
        bool measureFolders = !filter.JunkFilesOnly && filter.TargetType != SearchTargetType.FilesOnly
            && !hasExtensions && !hasSizeFilter;

        DateTime createdFromStart = filter.CreatedFrom.Date;
        DateTime createdToEnd = filter.CreatedTo.Date.AddDays(1).AddTicks(-1);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.None,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var results = new List<FileSystemItem>();
        var folderSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var scannedFolders = new List<(string Path, string Parent)>();
        int scannedCount = 0;
        int matchedCount = 0;
        bool wasCancelled = false;

        var enumerable = new FileSystemEnumerable<FileSystemItem>(
            filter.RootPath,
            (ref FileSystemEntry entry) =>
            {
                string fileName = entry.FileName.ToString();
                bool isDirectory = entry.IsDirectory;
                string junkCategory = string.Empty;

                if (!isDirectory && filter.JunkFilesOnly)
                {
                    JunkFileClassifier.TryClassify(entry.FileName, entry.Directory, filter.JunkCategory, out junkCategory);
                }

                return new FileSystemItem
                {
                    Name = fileName,
                    FullPath = entry.ToFullPath(),
                    IsDirectory = isDirectory,
                    Extension = isDirectory ? string.Empty : Path.GetExtension(fileName).ToLowerInvariant(),
                    SizeBytes = isDirectory ? -1L : entry.Length,
                    CreatedAt = entry.CreationTimeUtc.LocalDateTime,
                    JunkCategory = junkCategory
                };
            },
            options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !measureFolders || (entry.Attributes & FileAttributes.ReparsePoint) == 0;
            },
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (measureFolders && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    string parent = entry.Directory.ToString();
                    if (entry.IsDirectory)
                        scannedFolders.Add((entry.ToFullPath(), parent));
                    else
                        folderSizes[parent] = folderSizes.GetValueOrDefault(parent) + entry.Length;
                }

                scannedCount++;
                if (scannedCount % ProgressReportInterval == 0)
                {
                    progress?.Report(new SearchProgressInfo(scannedCount, matchedCount));
                }

                if (!MatchesFilter(
                    ref entry,
                    filter,
                    pattern,
                    hasPattern,
                    hasWildcards,
                    extensions,
                    hasExtensions,
                    hasSizeFilter,
                    createdFromStart,
                    createdToEnd))
                {
                    return false;
                }

                matchedCount++;
                return true;
            }
        };

        try
        {
            foreach (FileSystemItem item in enumerable)
            {
                results.Add(item);
            }

            // Children are discovered after their parents, so fold their sizes in reverse order.
            for (int index = scannedFolders.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, parent) = scannedFolders[index];
                long size = folderSizes.GetValueOrDefault(path);
                folderSizes[path] = size;
                folderSizes[parent] = folderSizes.GetValueOrDefault(parent) + size;
            }
            foreach (FileSystemItem item in results)
            {
                if (item.IsDirectory && folderSizes.TryGetValue(item.FullPath, out long size))
                    item.SizeBytes = size;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCancelled = true;
        }

        progress?.Report(new SearchProgressInfo(scannedCount, results.Count));
        return new SearchExecutionResult(results, scannedCount, wasCancelled);
    }

    private static bool MatchesFilter(
        ref FileSystemEntry entry,
        SearchFilter filter,
        string pattern,
        bool hasPattern,
        bool hasWildcards,
        string[] extensions,
        bool hasExtensions,
        bool hasSizeFilter,
        DateTime createdFromStart,
        DateTime createdToEnd)
    {
        bool isDirectory = entry.IsDirectory;

        // 1. Target type filter (folders, files, or both)
        if ((filter.JunkFilesOnly || filter.TargetType == SearchTargetType.FilesOnly) && isDirectory)
        {
            return false;
        }

        if (!filter.JunkFilesOnly && filter.TargetType == SearchTargetType.FoldersOnly && !isDirectory)
        {
            return false;
        }

        // 1b. Junk files filter (applies only to files)
        if (filter.JunkFilesOnly &&
            !JunkFileClassifier.TryClassify(entry.FileName, entry.Directory, filter.JunkCategory, out _))
        {
            return false;
        }

        // 2. Name / pattern filter
        if (hasPattern)
        {
            bool nameMatches = hasWildcards
                ? FileSystemName.MatchesSimpleExpression(pattern, entry.FileName, ignoreCase: true)
                : MemoryExtensions.Contains(entry.FileName, pattern.AsSpan(), StringComparison.OrdinalIgnoreCase);

            if (!nameMatches)
            {
                return false;
            }
        }

        // 3. Extension filter (applies only to files)
        if (hasExtensions)
        {
            if (isDirectory)
            {
                return false;
            }

            ReadOnlySpan<char> entryExtension = Path.GetExtension(entry.FileName);
            bool extensionMatches = false;
            foreach (string ext in extensions)
            {
                if (entryExtension.Equals(ext.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    extensionMatches = true;
                    break;
                }
            }

            if (!extensionMatches)
            {
                return false;
            }
        }

        // 4. File size filter (applies only to files)
        if (hasSizeFilter)
        {
            if (isDirectory)
            {
                return false;
            }

            long length = entry.Length;
            if (filter.UseMinSize && length < filter.MinSizeBytes)
            {
                return false;
            }

            if (filter.UseMaxSize && length > filter.MaxSizeBytes)
            {
                return false;
            }
        }

        // 5. Creation date filter
        if (filter.UseCreatedFrom || filter.UseCreatedTo)
        {
            DateTime createdLocal = entry.CreationTimeUtc.LocalDateTime;
            if (filter.UseCreatedFrom && createdLocal < createdFromStart)
            {
                return false;
            }

            if (filter.UseCreatedTo && createdLocal > createdToEnd)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] ParseExtensions(string rawExtensions)
    {
        if (string.IsNullOrWhiteSpace(rawExtensions))
        {
            return [];
        }

        string[] tokens = rawExtensions.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = new List<string>(tokens.Length);

        foreach (string token in tokens)
        {
            string cleaned = token.Trim().TrimStart('*');
            if (cleaned.Length == 0)
            {
                continue;
            }

            if (!cleaned.StartsWith('.'))
            {
                cleaned = "." + cleaned;
            }

            if (cleaned.Length > 1 && !normalized.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(cleaned);
            }
        }

        return [.. normalized];
    }
}
