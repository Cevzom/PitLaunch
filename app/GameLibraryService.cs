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
    // Installer metadata is not trusted enough to justify an unbounded recursive disk walk.
    // Search only inside a validated game folder, never cross reparse points, and stop after a
    // generous number of candidates. A normal game has tens of executables, not thousands.
    private const int MaxExecutableSearchDepth = 3;
    private const int MaxExecutableCandidates = 2000;
    private const int MaxExecutableDirectories = 512;
    private const int MaxXboxGameFolders = 1000;

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
                     .Concat(ScanSafely(ScanEpic, "Epic"))
                     .Concat(ScanSafely(ScanLauncherRegistrations, "EA / Ubisoft / Battle.net / iRacing"))
                     .Concat(ScanSafely(ScanXboxLibraries, "Xbox")))
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

    /// <summary>
    /// EA app, Ubisoft Connect, Battle.net and iRacing all register their installed titles with
    /// Windows even though their private launcher databases use different formats. Reading the
    /// normal uninstall records keeps this scanner read-only and resilient across launcher updates.
    /// </summary>
    private static IEnumerable<GameEntry> ScanLauncherRegistrations()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey? root = OpenRegistry(hive, view,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (string keyName in SafeSubKeyNames(root))
                {
                    using RegistryKey? item = OpenSubKey(root, keyName);
                    if (item is null) continue;
                    string name = RegistryText(item, "DisplayName");
                    string publisher = RegistryText(item, "Publisher");
                    string location = NormalizeInstallLocation(RegistryText(item, "InstallLocation"));
                    string icon = RegistryText(item, "DisplayIcon");
                    string source = LauncherSource(name, publisher, location);
                    if (source.Length == 0 || name.Length == 0 || IsNotAGame(name)) continue;

                    bool safeLocation = IsSafeExecutableSearchRoot(location);
                    string? guessed = safeLocation
                        ? PickExecutable(location, name, Path.GetFileName(location))
                        : null;
                    // Known titles (notably iRacing) often register a launcher icon even though the
                    // long-running simulation executable is present below InstallLocation.
                    string? executable = IsKnownTitle(name) ? guessed : ExistingExecutable(icon) ?? guessed;
                    if (executable is null || !seen.Add(executable)) continue;
                    yield return new GameEntry(name, executable, source);
                }
            }
        }
    }

    /// <summary>Xbox PC games are installed below the user-selected XboxGames folder on each drive.</summary>
    private static IEnumerable<GameEntry> ScanXboxLibraries()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string library;
            try
            {
                if (drive.DriveType is not DriveType.Fixed and not DriveType.Removable) continue;
                if (!drive.IsReady) continue;
                library = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            }
            catch { continue; }
            foreach (GameEntry game in ScanXboxLibraryRoot(library)) yield return game;
        }
    }

    internal static IEnumerable<GameEntry> ScanXboxLibraryRoot(string library)
    {
        if (!Directory.Exists(library) || !IsSafeExecutableSearchRoot(library)) yield break;
        foreach (string gameFolder in SafeDirectories(library))
        {
            string content = Path.Combine(gameFolder, "Content");
            if (!Directory.Exists(content)) content = gameFolder;
            string name = Path.GetFileName(gameFolder);
            if (string.IsNullOrWhiteSpace(name) || IsNotAGame(name)) continue;
            string? executable = PickExecutable(content, name, name);
            if (executable is not null) yield return new GameEntry(name, executable, "Xbox");
        }
    }

    internal static string LauncherSource(string name, string publisher, string location)
    {
        string haystack = name + " " + publisher + " " + location;
        if (haystack.Contains("iRacing", StringComparison.OrdinalIgnoreCase)) return "iRacing";
        if (haystack.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft Connect";
        if (haystack.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (haystack.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ||
            publisher.Equals("EA", StringComparison.OrdinalIgnoreCase) ||
            location.Contains("EA Games", StringComparison.OrdinalIgnoreCase)) return "EA app";
        return string.Empty;
    }

    private static RegistryKey? OpenRegistry(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(path);
        }
        catch { return null; }
    }

    private static RegistryKey? OpenSubKey(RegistryKey root, string name)
    {
        try { return root.OpenSubKey(name); }
        catch { return null; }
    }

    private static string[] SafeSubKeyNames(RegistryKey root)
    {
        try { return root.GetSubKeyNames(); }
        catch { return []; }
    }

    private static string RegistryText(RegistryKey key, string name)
    {
        try { return key.GetValue(name)?.ToString()?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    internal static string? ExistingExecutable(string displayIcon)
    {
        string? value = DisplayIconPath(displayIcon);
        if (value is null) return null;
        try
        {
            string fileName = Path.GetFileNameWithoutExtension(value);
            return File.Exists(value) &&
                   Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                   !IsExecutableNoise(fileName)
                ? value
                : null;
        }
        catch { return null; }
    }

    internal static string? DisplayIconPath(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string value = displayIcon.Trim();
        if (value.StartsWith('"'))
        {
            int closingQuote = value.IndexOf('"', 1);
            if (closingQuote < 2) return null;
            value = value[1..closingQuote];
        }
        else
        {
            int comma = value.LastIndexOf(',');
            if (comma > 2 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma];
            value = value.Trim().Trim('"');
        }

        try
        {
            value = Environment.ExpandEnvironmentVariables(value);
            return Path.GetFullPath(value);
        }
        catch { return null; }
    }

    internal static string NormalizeInstallLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return string.Empty;
        try
        {
            string value = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
            return Path.GetFullPath(value);
        }
        catch { return string.Empty; }
    }

    internal static bool IsSafeExecutableSearchRoot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            string fullPath = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = (Path.GetPathRoot(fullPath) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Length == 0 || fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (Directory.Exists(fullPath) &&
                (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (IsSameOrDescendant(fullPath, windows)) return false;

            string[] exactBroadRoots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            ];
            return !exactBroadRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Any(path =>
                fullPath.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
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
        if (!Directory.Exists(folder) || !IsSafeExecutableSearchRoot(folder)) return null;

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
            foreach (string path in EnumerateExecutablesBounded(folder, "*.exe"))
            {
                string file = Path.GetFileNameWithoutExtension(path);
                if (IsExecutableNoise(file)) continue;
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
        if (!IsSafeExecutableSearchRoot(folder)) return null;
        return EnumerateExecutablesBounded(folder, fileName).FirstOrDefault();
    }

    private static string Simplify(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsKnownTitle(string name) =>
        KnownExecutables.Any(item => name.Contains(item.Title, StringComparison.OrdinalIgnoreCase));

    private static bool IsExecutableNoise(string fileName) =>
        ExecutableNoise.Any(noise => fileName.Contains(noise, StringComparison.OrdinalIgnoreCase)) ||
        fileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    internal static bool IsNotAGame(string name) =>
        name.Contains("redistributable", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DirectX", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("anti-cheat", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("anticheat", StringComparison.OrdinalIgnoreCase) ||
        Simplify(name) is "eaapp" or "eadesktop" or "ubisoftconnect" or "ubisoftgamelauncher" or
            "battlenet" or "blizzardbattlenet" or "iracingservice";

    private static bool IsSameOrDescendant(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent)) return false;
        string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikeSimRacing(string name) =>
        SimRacingTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeFiles(string folder, string pattern)
    {
        try { return Directory.EnumerateFiles(folder, pattern); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            EnumerationOptions options = new()
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };
            return Directory.EnumerateDirectories(folder, "*", options).Take(MaxXboxGameFolders).ToList();
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> EnumerateExecutablesBounded(string root, string pattern)
    {
        List<string> files = [];
        Queue<(string Directory, int Depth)> pending = [];
        pending.Enqueue((root, 0));
        int visitedDirectories = 0;
        EnumerationOptions direct = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        while (pending.Count > 0 && visitedDirectories < MaxExecutableDirectories &&
               files.Count < MaxExecutableCandidates)
        {
            (string directory, int depth) = pending.Dequeue();
            visitedDirectories++;
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, pattern, direct))
                {
                    files.Add(file);
                    if (files.Count >= MaxExecutableCandidates) break;
                }
                if (depth >= MaxExecutableSearchDepth) continue;
                foreach (string child in Directory.EnumerateDirectories(directory, "*", direct))
                {
                    if (pending.Count + visitedDirectories >= MaxExecutableDirectories) break;
                    pending.Enqueue((child, depth + 1));
                }
            }
            catch
            {
                // A protected launcher subfolder should not hide the rest of the library.
            }
        }

        return files;
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
