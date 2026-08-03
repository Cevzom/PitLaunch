using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PitLaunch;

internal sealed record GameEntry(string Name, string ExecutablePath, string Source)
{
    /// <summary>What game detection actually matches on.</summary>
    public string ProcessName => GameDetectionService.NormalizeProcessName(ExecutablePath);

    /// <summary>Sim titles float to the top of the list, since that is what this app is for.</summary>
    public bool IsSimRacing => GameLibraryService.LooksLikeSimRacing(Name);
}

/// <summary>
/// Finds installed games so a setup can watch for one without anybody typing a process name.
/// Reads the launchers' own install records rather than trawling the disk.
/// </summary>
internal sealed class GameLibraryService
{
    private static readonly string[] SimRacingTerms =
    [
        "assetto", "iracing", "rfactor", "automobilista", "raceroom", "beamng", "dirt", "wrc",
        "f1 2", "f1 manager", "forza", "gran turismo", "le mans", "project cars", "richard burns",
        "wreckfest", "grid", "nascar", "motogp", "ride ", "truck simulator", "rennsport"
    ];

    /// <summary>
    /// Titles where the obvious executable is a launcher that exits once the game starts.
    /// Watching the launcher would switch the setup back mid-session, so these are pinned to the
    /// process that actually stays running. Only applied when the file is really there.
    /// </summary>
    private static readonly (string Title, string Executable)[] KnownExecutables =
    [
        ("assetto corsa competizione", "AC2-Win64-Shipping.exe"),
        ("assetto corsa evo", "AssettoCorsaEVO.exe"),
        ("assetto corsa", "acs.exe"),
        ("beamng.drive", "BeamNG.drive.x64.exe"),
        ("grand theft auto v", "GTA5.exe"),
        ("iracing", "iRacingSim64DX11.exe")
    ];

    // Things that live beside a game but are never the game.
    private static readonly string[] ExecutableNoise =
    [
        "unins", "uninstall", "crashhandler", "crashreport", "crashpad", "redist", "vcredist",
        "dxsetup", "directx", "setup", "installer", "install", "anticheat", "eac", "battleye",
        "be_service", "launcher_", "helper", "diag", "benchmark", "editor", "server", "dedicated",
        "activation", "cleanup", "repair", "report", "updater", "update", "notification",
        "launcher"
    ];

    public List<GameEntry> Scan()
    {
        Dictionary<string, GameEntry> games = new(StringComparer.OrdinalIgnoreCase);

        foreach (GameEntry entry in ScanSafely(ScanSteam, "Steam")
                     .Concat(ScanSafely(ScanEpic, "Epic")))
        {
            // Key on the executable so the same game found twice does not appear twice.
            games[entry.ExecutablePath] = entry;
        }

        return games.Values
            .OrderByDescending(game => game.IsSimRacing)
            .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<GameEntry> ScanSafely(Func<IEnumerable<GameEntry>> scan, string source)
    {
        try
        {
            return scan().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Game scan ({source}) failed: {ex.Message}");
            return [];
        }
    }

    private static IEnumerable<GameEntry> ScanSteam()
    {
        string? steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrWhiteSpace(steamPath)) yield break;
        steamPath = steamPath.Replace('/', '\\');

        foreach (string library in SteamLibraries(steamPath))
        {
            string apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;

            foreach (string manifest in SafeFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(manifest, Encoding.UTF8); }
                catch { continue; }

                string? name = MatchValue(text, "name");
                string? installDir = MatchValue(text, "installdir");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) continue;
                if (IsNotAGame(name)) continue;

                string folder = Path.Combine(apps, "common", installDir);
                string? exe = PickExecutable(folder, name, installDir);
                if (exe is null) continue;

                yield return new GameEntry(name, exe, "Steam");
            }
        }
    }

    private static IEnumerable<string> SteamLibraries(string steamPath)
    {
        yield return steamPath;
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf, Encoding.UTF8); }
        catch { yield break; }

        foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
        {
            string path = match.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static IEnumerable<GameEntry> ScanEpic()
    {
        string manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;

        foreach (string file in SafeFiles(manifests, "*.item"))
        {
            string? name = null;
            string? exe = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                JsonElement root = document.RootElement;
                name = Text(root, "DisplayName");
                string? location = Text(root, "InstallLocation");
                string? launch = Text(root, "LaunchExecutable");
                if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(launch))
                {
                    string candidate = Path.GetFullPath(Path.Combine(location, launch.Replace('/', '\\')));
                    if (File.Exists(candidate)) exe = candidate;
                }
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name) || exe is null || IsNotAGame(name)) continue;
            yield return new GameEntry(name, exe, "Epic");
        }
    }

    private static string? Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Picks the executable a player would actually launch. Prefers a name resembling the game,
    /// then the largest remaining file, because installers and anti-cheat helpers sit alongside it.
    /// </summary>
    private static string? PickExecutable(string folder, string gameName, string installDir)
    {
        if (!Directory.Exists(folder)) return null;

        // A pinned executable for a known title always wins over the guesswork below.
        foreach ((string title, string executable) in KnownExecutables)
        {
            if (!gameName.Contains(title, StringComparison.OrdinalIgnoreCase)) continue;
            string? pinned = FindFile(folder, executable);
            if (pinned is not null) return pinned;
            break;
        }

        List<FileInfo> candidates = [];
        try
        {
            EnumerationOptions options = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            foreach (string path in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                string file = Path.GetFileNameWithoutExtension(path);
                if (ExecutableNoise.Any(noise => file.Contains(noise, StringComparison.OrdinalIgnoreCase))) continue;
                try { candidates.Add(new FileInfo(path)); } catch { }
            }
        }
        catch
        {
            return null;
        }

        if (candidates.Count == 0) return null;

        string wantedA = Simplify(gameName);
        string wantedB = Simplify(installDir);
        return candidates
            .OrderByDescending(file =>
            {
                string simple = Simplify(Path.GetFileNameWithoutExtension(file.Name));
                if (simple.Length == 0) return 0;
                if (simple == wantedA || simple == wantedB) return 3;
                if (wantedA.StartsWith(simple, StringComparison.Ordinal) ||
                    wantedB.StartsWith(simple, StringComparison.Ordinal) ||
                    simple.StartsWith(wantedA, StringComparison.Ordinal)) return 2;
                return 0;
            })
            .ThenByDescending(file => file.Length)
            .First()
            .FullName;
    }

    private static string? FindFile(string folder, string fileName)
    {
        try
        {
            EnumerationOptions options = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(folder, fileName, options).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string Simplify(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsNotAGame(string name) =>
        name.Contains("redistributable", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DirectX", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeSimRacing(string name) =>
        SimRacingTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try { return Directory.EnumerateFiles(folder, pattern); }
        catch { return []; }
    }

    private static string? MatchValue(string text, string key)
    {
        Match match = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>Prints what the scanner sees, so "my game is not listed" can be answered with facts.</summary>
internal static class GameScanReport
{
    public static int Run(string outputPath)
    {
        AppLog.SuppressWrites();
        try
        {
            List<GameEntry> games = new GameLibraryService().Scan();
            var payload = new
            {
                scanned = DateTimeOffset.Now,
                count = games.Count,
                games = games.Select(game => new
                {
                    game.Name,
                    game.Source,
                    process = game.ProcessName,
                    path = game.ExecutablePath,
                    simRacing = game.IsSimRacing
                })
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                AppPaths.EnsureDataDirectory();
                outputPath = Path.Combine(AppPaths.DataDirectory, "game-scan.json");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? AppContext.BaseDirectory);
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            return 0;
        }
        catch
        {
            return 2;
        }
    }
}
