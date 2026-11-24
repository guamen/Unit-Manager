namespace FileViewer.Models;

/// <summary>
/// Categories of junk or residual files that can be targeted during a search.
/// </summary>
public enum JunkFileCategory
{
    All = 0,
    DumpsAndCrashes = 1,
    TemporaryFiles = 2,
    BackupsAndFragments = 3,
    LogsAndDiagnostics = 4,
    CacheAndResiduals = 5
}
