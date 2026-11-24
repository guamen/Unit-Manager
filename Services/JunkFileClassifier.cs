using FileViewer.Models;

namespace FileViewer.Services;

/// <summary>
/// Classifies files as junk/residual files (dumps, temporary files, backups, logs, cache)
/// using allocation-free span matching for high-speed drive scans.
/// </summary>
public static class JunkFileClassifier
{
    private static readonly string[] DumpExtensions =
    [
        ".dmp", ".mdmp", ".hdmp", ".kdmp", ".wer", ".stackdump", ".crash"
    ];

    private static readonly string[] TempExtensions =
    [
        ".tmp", ".temp", ".$$$"
    ];

    private static readonly string[] BackupExtensions =
    [
        ".bak", ".old", ".orig", ".wbk", ".xlk", ".chk", ".bkp", ".backup"
    ];

    private static readonly string[] LogExtensions =
    [
        ".log", ".etl", ".err"
    ];

    private static readonly string[] CacheExtensions =
    [
        ".cache", ".swp", ".swo", ".swn", ".part", ".crdownload", ".partial", ".gid", ".fts"
    ];

    private static readonly string[] CacheExactNames =
    [
        "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", ".DS_Store"
    ];

    public static string GetCategoryDisplayName(JunkFileCategory category) => category switch
    {
        JunkFileCategory.All => "All (Dumps, Temporary Files, etc.)",
        JunkFileCategory.DumpsAndCrashes => "Crash dumps and errors",
        JunkFileCategory.TemporaryFiles => "Temporary files",
        JunkFileCategory.BackupsAndFragments => "Backups and fragments",
        JunkFileCategory.LogsAndDiagnostics => "Logs and diagnostics",
        JunkFileCategory.CacheAndResiduals => "Cache and residual files",
        _ => "All"
    };

    public static string GetCategoryHint(JunkFileCategory category) => category switch
    {
        JunkFileCategory.All =>
            "Finds dumps (.dmp, .mdmp, .wer), temporary files (.tmp, ~$*, Temp), backups (.bak, .old), logs (.log, .etl), and cache files (Thumbs.db, .cache).",
        JunkFileCategory.DumpsAndCrashes =>
            "Finds .dmp, .mdmp, .hdmp, .kdmp, .wer, .stackdump, .crash, hs_err_pid*.log, *.dmp.txt, and Minidump, CrashDumps, or WER folders.",
        JunkFileCategory.TemporaryFiles =>
            "Finds .tmp, .temp, .$$$, .~*, ~$*, .~lock.*#, and files in Windows\\Temp or AppData\\Local\\Temp.",
        JunkFileCategory.BackupsAndFragments =>
            "Finds .bak, .old, .orig, .wbk, .xlk, .chk, .bkp, .backup, and backup files ending in ~.",
        JunkFileCategory.LogsAndDiagnostics =>
            "Finds .log, .etl, and .err log and diagnostic files.",
        JunkFileCategory.CacheAndResiduals =>
            "Finds Thumbs.db, .DS_Store, ._*, .cache, .swp/.swo, incomplete downloads (.part, .crdownload, .partial), and INetCache folders.",
        _ => string.Empty
    };

    public static bool TryClassify(
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<char> directory,
        JunkFileCategory requestedCategory,
        out string matchedCategoryLabel)
    {
        if (fileName.IsEmpty)
        {
            matchedCategoryLabel = string.Empty;
            return false;
        }

        ReadOnlySpan<char> extension = Path.GetExtension(fileName);

        if (requestedCategory == JunkFileCategory.All)
        {
            if (IsDumpOrCrash(fileName, extension, directory))
            {
                matchedCategoryLabel = "Dump / Error";
                return true;
            }

            if (IsTemporaryFile(fileName, extension, directory))
            {
                matchedCategoryLabel = "Temporary";
                return true;
            }

            if (IsBackupOrFragment(fileName, extension))
            {
                matchedCategoryLabel = "Backup / Fragment";
                return true;
            }

            if (IsLogOrDiagnostic(extension))
            {
                matchedCategoryLabel = "Log / Diagnostics";
                return true;
            }

            if (IsCacheOrResidual(fileName, extension, directory))
            {
                matchedCategoryLabel = "Cache / Residual";
                return true;
            }

            matchedCategoryLabel = string.Empty;
            return false;
        }

        switch (requestedCategory)
        {
            case JunkFileCategory.DumpsAndCrashes when IsDumpOrCrash(fileName, extension, directory):
                matchedCategoryLabel = "Dump / Error";
                return true;

            case JunkFileCategory.TemporaryFiles when IsTemporaryFile(fileName, extension, directory):
                matchedCategoryLabel = "Temporary";
                return true;

            case JunkFileCategory.BackupsAndFragments when IsBackupOrFragment(fileName, extension):
                matchedCategoryLabel = "Backup / Fragment";
                return true;

            case JunkFileCategory.LogsAndDiagnostics when IsLogOrDiagnostic(extension):
                matchedCategoryLabel = "Log / Diagnostics";
                return true;

            case JunkFileCategory.CacheAndResiduals when IsCacheOrResidual(fileName, extension, directory):
                matchedCategoryLabel = "Cache / Residual";
                return true;

            default:
                matchedCategoryLabel = string.Empty;
                return false;
        }
    }

    private static bool IsDumpOrCrash(
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<char> extension,
        ReadOnlySpan<char> directory)
    {
        if (MatchesAnyExtension(extension, DumpExtensions))
        {
            return true;
        }

        if (fileName.EndsWith(".dmp.txt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((fileName.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase) ||
             fileName.StartsWith("replay_pid", StringComparison.OrdinalIgnoreCase)) &&
            extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsSubpath(directory, @"\Minidump")
            || ContainsSubpath(directory, @"\CrashDumps")
            || ContainsSubpath(directory, @"\Microsoft\Windows\WER");
    }

    private static bool IsTemporaryFile(
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<char> extension,
        ReadOnlySpan<char> directory)
    {
        if (MatchesAnyExtension(extension, TempExtensions))
        {
            return true;
        }

        if (extension.StartsWith(".~", StringComparison.Ordinal))
        {
            return true;
        }

        if (fileName.StartsWith("~$", StringComparison.Ordinal))
        {
            return true;
        }

        if (fileName.StartsWith(".~lock.", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith("#", StringComparison.Ordinal))
        {
            return true;
        }

        return ContainsSubpath(directory, @"\Windows\Temp")
            || ContainsSubpath(directory, @"\AppData\Local\Temp")
            || ContainsSubpath(directory, @"\Local Settings\Temp");
    }

    private static bool IsBackupOrFragment(ReadOnlySpan<char> fileName, ReadOnlySpan<char> extension)
    {
        if (MatchesAnyExtension(extension, BackupExtensions))
        {
            return true;
        }

        return fileName.Length > 1 && fileName.EndsWith("~", StringComparison.Ordinal);
    }

    private static bool IsLogOrDiagnostic(ReadOnlySpan<char> extension)
    {
        return MatchesAnyExtension(extension, LogExtensions);
    }

    private static bool IsCacheOrResidual(
        ReadOnlySpan<char> fileName,
        ReadOnlySpan<char> extension,
        ReadOnlySpan<char> directory)
    {
        if (MatchesAnyExtension(extension, CacheExtensions))
        {
            return true;
        }

        foreach (string exactName in CacheExactNames)
        {
            if (fileName.Equals(exactName.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (fileName.Length > 2 && fileName.StartsWith("._", StringComparison.Ordinal))
        {
            return true;
        }

        return ContainsSubpath(directory, @"\AppData\Local\Microsoft\Windows\INetCache")
            || ContainsSubpath(directory, @"\Temporary Internet Files");
    }

    private static bool MatchesAnyExtension(ReadOnlySpan<char> extension, string[] candidates)
    {
        if (extension.IsEmpty)
        {
            return false;
        }

        foreach (string candidate in candidates)
        {
            if (extension.Equals(candidate.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSubpath(ReadOnlySpan<char> directory, string subpath)
    {
        if (directory.Length < subpath.Length)
        {
            return false;
        }

        int searchStart = 0;
        while (searchStart <= directory.Length - subpath.Length)
        {
            int relativeIndex = directory[searchStart..].IndexOf(subpath.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (relativeIndex < 0)
            {
                return false;
            }

            int matchEnd = searchStart + relativeIndex + subpath.Length;
            if (matchEnd == directory.Length ||
                directory[matchEnd] == Path.DirectorySeparatorChar ||
                directory[matchEnd] == Path.AltDirectorySeparatorChar)
            {
                return true;
            }

            searchStart += relativeIndex + 1;
        }

        return false;
    }
}
