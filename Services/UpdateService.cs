using System.Net;
using System.Text.Json;

namespace FileViewer.Services;

internal sealed record AvailableRelease(Version Version, bool HasInstaller, bool HasSetup)
{
    public Uri ZipUrl => new(UpdateService.BuildUri, $"Unit-Manager-{Version}-win-x64.zip");
    public Uri InstallerUrl => new(UpdateService.BuildUri,
        $"Unit-Manager-{Version}-win-x64{(HasSetup ? "-Setup.exe" : ".msi")}");
}

internal static class UpdateService
{
    internal static readonly Uri BuildUri = new("https://raw.githubusercontent.com/guamen/Unit-Manager/main/Build/");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Version AssemblyVersion = typeof(UpdateService).Assembly.GetName().Version!;
    public static Version CurrentVersion { get; } = new(AssemblyVersion.Major, AssemblyVersion.Minor, AssemblyVersion.Build);

    public static async Task<AvailableRelease?> GetLatestAsync()
    {
        using var response = await Client.GetAsync(new Uri(BuildUri, "latest.json"));
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (manifest.RootElement.ValueKind != JsonValueKind.Object ||
            !manifest.RootElement.TryGetProperty("version", out var field) ||
            field.ValueKind != JsonValueKind.String ||
            !Version.TryParse(field.GetString(), out var version) || version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException("The repository returned an invalid release version.");
        }

        bool hasInstaller = manifest.RootElement.TryGetProperty("installer", out var installer)
            && installer.ValueKind == JsonValueKind.True;
        bool hasSetup = manifest.RootElement.TryGetProperty("setup", out var setup)
            && setup.ValueKind == JsonValueKind.True;
        return new AvailableRelease(version, hasInstaller || hasSetup, hasSetup);
    }
}
