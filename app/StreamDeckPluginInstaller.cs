using System.Diagnostics;
using System.Net.Http;

namespace PitLaunch;

/// <summary>
/// Gives normal users a one-click path from PitLaunch to Elgato's package installer. Development
/// builds use the package in this checkout; installed builds fetch the identically named asset from
/// the latest GitHub Release, so the desktop installer does not have to carry two copies of it.
/// </summary>
internal static class StreamDeckPluginInstaller
{
    internal const string PackageName = "com.cevzom.pitlaunch.streamDeckPlugin";
    internal const string DownloadUrl =
        "https://github.com/Cevzom/PitLaunch/releases/latest/download/" + PackageName;

    private const long MaximumPackageBytes = 20L * 1024 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<bool> OpenInstallerAsync(CancellationToken cancellationToken = default)
    {
        string? developmentPackage = FindDevelopmentPackage();
        string packagePath = developmentPackage ?? await DownloadLatestAsync(cancellationToken);
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = packagePath,
            UseShellExecute = true
        });
        if (process is null)
            throw new InvalidOperationException("Windows could not open the Stream Deck plugin installer.");

        return developmentPackage is null;
    }

    private static async Task<string> DownloadLatestAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, DownloadUrl);
        using HttpResponseMessage response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaximumPackageBytes)
            throw new InvalidDataException("The Stream Deck plugin download was unexpectedly large.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "PitLaunch",
            "StreamDeck",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string packagePath = Path.Combine(directory, PackageName);

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(
            packagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaximumPackageBytes)
            {
                throw new InvalidDataException("The Stream Deck plugin download was unexpectedly large.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0) throw new InvalidDataException("The Stream Deck plugin download was empty.");
        return packagePath;
    }

    private static string? FindDevelopmentPackage()
    {
        string candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "integrations", "stream-deck", "dist", PackageName));
        return File.Exists(candidate) ? candidate : null;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PitLaunch/{AppInfo.Version}");
        return client;
    }
}
