using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Установленная игра Steam (из appmanifest_*.acf).</summary>
public sealed record SteamGame(string Id, string Name, long LastPlayed, long SizeBytes);

/// <summary>
/// Capsul Box: библиотека Steam на этом компьютере — установленные игры, их обложки и запуск. Читает только локальные
/// файлы Steam (libraryfolders.vdf и appmanifest_*.acf), ключ Web API не нужен. Обложка — из кэша Steam, а если её
/// там нет, с CDN Steam (один раз, потом из памяти).
/// </summary>
public sealed partial class SteamLibrary(ILogger<SteamLibrary> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly object _lock = new();
    private IReadOnlyList<SteamGame> _games = [];
    private DateTime _readAt = DateTime.MinValue;
    private readonly Dictionary<string, (byte[] Data, string Type)?> _covers = [];

    /// <summary>Игры по времени последнего запуска (сначала недавние). Список перечитывается не чаще раза в минуту.</summary>
    public IReadOnlyList<SteamGame> Games()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _readAt < TimeSpan.FromMinutes(1)) return _games;
            _readAt = DateTime.UtcNow;
            try { _games = Read(SteamPath()); }
            catch (Exception e) { log.LogDebug("steam library: {Msg}", e.Message); }
            return _games;
        }
    }

    /// <summary>Запуск игры через steam://rungameid; только игры из списка установленных.</summary>
    public string? Launch(string? id)
    {
        if (id is null || !Games().Any(g => g.Id == id)) return "Игра не найдена в библиотеке";
        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + id) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) { return "Steam не запустил игру: " + e.Message; }
    }

    public async Task<(byte[] Data, string Type)?> CoverAsync(string? id, CancellationToken ct)
    {
        if (id is null || !AppId().IsMatch(id) || !Games().Any(g => g.Id == id)) return null;
        lock (_lock) if (_covers.TryGetValue(id, out var known)) return known;

        (byte[], string)? cover = null;
        if (LocalCover(SteamPath(), id) is { } file)
        {
            try { cover = (await File.ReadAllBytesAsync(file, ct), file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg"); }
            catch (IOException) { }
        }
        if (cover is null)
        {
            foreach (var url in new[] { $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg", $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg" })
            {
                try
                {
                    using var r = await Http.GetAsync(url, ct);
                    if (r.IsSuccessStatusCode) { cover = (await r.Content.ReadAsByteArrayAsync(ct), "image/jpeg"); break; }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            }
        }
        lock (_lock) { if (_covers.Count > 300) _covers.Clear(); _covers[id] = cover; }
        return cover;
    }

    // ---------- чтение файлов Steam ----------

    internal static string? SteamPath()
    {
#if WINDOWS
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(pf) ? pf : null;
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam"), Path.Combine(home, "Library", "Application Support", "Steam") })
            if (Directory.Exists(p)) return p;
        return null;
#endif
    }

    internal static IReadOnlyList<SteamGame> Read(string? steam)
    {
        if (steam is null) return [];
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(LibraryPaths(File.ReadAllText(vdf)));

        var games = new Dictionary<string, SteamGame>();
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try { if (ParseManifest(File.ReadAllText(acf)) is { } g) games[g.Id] = g; }
                catch (IOException) { }
            }
        }
        return games.Values.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Папки библиотек из libraryfolders.vdf ("path" "D:\\SteamLibrary").</summary>
    internal static IEnumerable<string> LibraryPaths(string vdf) =>
        PathPair().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    /// <summary>Одна игра из appmanifest_*.acf; инструменты и среды выполнения Steam пропускаются.</summary>
    internal static SteamGame? ParseManifest(string acf)
    {
        string? Get(string key) => Regex.Match(acf, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        var id = Get("appid");
        var name = Get("name");
        if (id is null || !AppId().IsMatch(id) || string.IsNullOrWhiteSpace(name)) return null;
        if (Skip().IsMatch(name)) return null;
        long.TryParse(Get("LastPlayed"), out var last);
        long.TryParse(Get("SizeOnDisk"), out var size);
        return new SteamGame(id, name.Trim(), last, size);
    }

    /// <summary>Обложка в кэше Steam: старый вид appcache/librarycache/&lt;id&gt;_library_600x900.jpg и новый — папка &lt;id&gt;/.</summary>
    internal static string? LocalCover(string? steam, string id)
    {
        if (steam is null) return null;
        var cache = Path.Combine(steam, "appcache", "librarycache");
        var old = Path.Combine(cache, id + "_library_600x900.jpg");
        if (File.Exists(old)) return old;
        var dir = Path.Combine(cache, id);
        if (!Directory.Exists(dir)) return null;
        try
        {
            return Directory.EnumerateFiles(dir, "library_600x900.jpg", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(dir, "header.jpg", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (IOException) { return null; }
    }

    [GeneratedRegex("^[0-9]{1,10}$")]
    private static partial Regex AppId();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathPair();

    [GeneratedRegex(@"^(Steamworks Common Redistributables|Steam Linux Runtime|Proton |SteamVR)", RegexOptions.IgnoreCase)]
    private static partial Regex Skip();
}
