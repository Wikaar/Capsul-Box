using System.Net;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// What the agent's own internet radio is playing: the song from the stream (ICY StreamTitle), its cover
/// (AMG Radio stations: amgradio.ru; others: iTunes search) and, until a cover is found, the station logo
/// as artwork for the player on the tablet.
/// </summary>
internal sealed class RadioNowPlaying : IDisposable
{
    internal sealed record Info(string StationId, string? Track, int ArtId);

    private static int _artSeq = 1_000_000; // above MediaMonitor's ids, so /api/art can tell them apart
    private const int MaxImageBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Func<DeckAction, CancellationToken, Task<RadioLogo?>> _logo;
    private readonly Func<bool> _online;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private string? _stationId, _target, _track;
    private AlbumArt? _art;
    private int _artId;

    static RadioNowPlaying() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public RadioNowPlaying(Func<DeckAction, CancellationToken, Task<RadioLogo?>> logo, Func<bool> online, HttpClient? http = null)
    {
        _logo = logo;
        _online = online;
        _http = http ?? CreateClient();
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CapsulBox/" + AppPaths.Version);
        return client;
    }

    public Info? Current { get { lock (_gate) return _stationId is null ? null : new(_stationId, _track, _artId); } }

    public AlbumArt? Art(int id) { lock (_gate) return id != 0 && id == _artId ? _art : null; }

    /// <summary>Follows the station that plays now (null = none): starts or stops the background lookup.</summary>
    public void Follow(DeckAction? playing)
    {
        lock (_gate)
        {
            if (playing?.Id == _stationId && playing?.Target == _target) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _stationId = playing?.Id;
            _target = playing?.Target;
            _track = null;
            _art = null;
            _artId = 0;
            if (playing is null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var action = playing;
            _ = Task.Run(() => RunAsync(action, token));
        }
    }

    private async Task RunAsync(DeckAction action, CancellationToken ct)
    {
        try
        {
            await ShowLogoAsync(action, ct);
            string? lastTrack = null, coverFor = null;
            int coverTries = 0;
            while (!ct.IsCancellationRequested)
            {
                string? track = null;
                try { track = TrackText.Clean(await ReadIcyTitleAsync(action.Target, ct), action.Label); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { /* stream without metadata or unreachable: keep showing the station */ }

                if (track != lastTrack)
                {
                    lastTrack = track;
                    coverFor = null;
                    coverTries = 0;
                    lock (_gate) { if (ct.IsCancellationRequested) return; _track = track; }
                    if (track is null) await ShowLogoAsync(action, ct);
                    StateSignal.Poke();
                }
                // the AMG list can lag the stream by a few seconds: retry a few times per song
                if (track is not null && coverFor != track && coverTries < 5 && _online())
                {
                    coverTries++;
                    if (await FindCoverAsync(action, track, ct) is { } cover)
                    {
                        coverFor = track;
                        SetArt(cover, ct);
                    }
                    else if (coverTries == 1) await ShowLogoAsync(action, ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(coverFor == track || track is null ? 12 : 6), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void SetArt(AlbumArt art, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested) return;
            _art = art;
            _artId = Interlocked.Increment(ref _artSeq);
        }
        StateSignal.Poke();
    }

    private async Task ShowLogoAsync(DeckAction action, CancellationToken ct)
    {
        RadioLogo? logo = null;
        try { logo = await _logo(action, ct); } catch (OperationCanceledException) { throw; } catch { }
        if (logo is not { IsFallback: false } found)
        {
            lock (_gate) { if (!ct.IsCancellationRequested) { _art = null; _artId = 0; } }
            StateSignal.Poke();
            return;
        }
        SetArt(new AlbumArt(found.Bytes, found.Mime), ct);
    }

    // ---------- ICY ----------

    /// <summary>Opens the stream with "Icy-MetaData: 1" and reads the first metadata block (StreamTitle).</summary>
    internal async Task<string?> ReadIcyTitleAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || HlsPlaylist.IsHlsUrl(uri)) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        if (!resp.Headers.TryGetValues("icy-metaint", out var values) || !int.TryParse(values.FirstOrDefault(), out var metaint)
            || metaint <= 0 || metaint > 512 * 1024) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var audio = new byte[Math.Min(metaint, 64 * 1024)];
        for (int block = 0; block < 3; block++)
        {
            for (int left = metaint; left > 0;)
            {
                int read = await stream.ReadAsync(audio.AsMemory(0, Math.Min(left, audio.Length)), cts.Token);
                if (read <= 0) return null;
                left -= read;
            }
            int lengthByte = stream.ReadByte();
            if (lengthByte < 0) return null;
            if (lengthByte == 0) continue; // no change in this block
            var meta = new byte[lengthByte * 16];
            await stream.ReadExactlyAsync(meta, cts.Token);
            return ParseStreamTitle(meta);
        }
        return null;
    }

    /// <summary>StreamTitle from an ICY metadata block; UTF-8, or Windows-1251 for older Russian servers.</summary>
    internal static string? ParseStreamTitle(byte[] meta)
    {
        int end = Array.IndexOf(meta, (byte)0);
        var bytes = end >= 0 ? meta.AsSpan(0, end) : meta.AsSpan();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(1251).GetString(bytes); }
        const string key = "StreamTitle='";
        int start = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += key.Length;
        int stop = text.IndexOf("';", start, StringComparison.Ordinal);
        if (stop < 0) stop = text.LastIndexOf('\'');
        return stop > start ? text[start..stop] : null;
    }

    // ---------- cover ----------

    private async Task<AlbumArt?> FindCoverAsync(DeckAction action, string track, CancellationToken ct)
    {
        try
        {
            string? url = null;
            if (AmgRadio.LooksLikeAmg(action) && await AmgRadio.LiveAsync(_http, action, ct) is { } live
                && TrackText.Same(live.Track, track) && !string.IsNullOrWhiteSpace(live.Cover))
                url = live.Cover;
            url ??= await ITunesCoverAsync(track, ct);
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
            return await DownloadImageAsync(uri, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>Album art from the iTunes catalogue for "Artist – Title" (600 px); only if the artist matches.</summary>
    private async Task<string?> ITunesCoverAsync(string track, CancellationToken ct)
    {
        var (artist, title) = TrackText.Split(track);
        if (artist is null || title is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var term = Uri.EscapeDataString($"{artist} {title}");
        using var resp = await _http.GetAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=5", cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
        foreach (var r in results.EnumerateArray())
        {
            var foundArtist = r.TryGetProperty("artistName", out var a) ? a.GetString() : null;
            var art = r.TryGetProperty("artworkUrl100", out var u) ? u.GetString() : null;
            if (art is null || !TrackText.SameArtist(foundArtist, artist)) continue;
            return art.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
        }
        return null;
    }

    private async Task<AlbumArt?> DownloadImageAsync(Uri uri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength is > MaxImageBytes) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buf, cts.Token)) > 0)
        {
            if (ms.Length + read > MaxImageBytes) return null;
            ms.Write(buf, 0, read);
        }
        var bytes = ms.ToArray();
        return ImageMime(bytes) is { } mime ? new AlbumArt(bytes, mime) : null;
    }

    internal static string? ImageMime(byte[] b)
    {
        if (b.Length < 12) return null;
        if (b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    public void Dispose()
    {
        Follow(null);
        _http.Dispose();
    }
}

/// <summary>Cleaning and comparing "Artist – Title" texts from streams and catalogues. Pure, unit tested.</summary>
internal static class TrackText
{
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "unknown - unknown", "unknown artist", "untitled", "no title", "none", "null", "n/a", "na",
        "live", "on air", "onair", "stream", "radio", "advert", "advertisement", "werbung", "реклама",
        "adbreak", "ad break", "commercial", "-", "--",
    };
    private static readonly char[] Edges = [' ', '-', '–', '—', '|', '·', '/', '*', '_', '~', ':'];

    public static string? Clean(string? raw, string? station)
    {
        if (raw is null) return null;
        var s = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(Edges);
        if (s.Length < 2 || !s.Any(char.IsLetter) || Junk.Contains(s)) return null;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(station) && Norm(s) == Norm(station)) return null;
        return s.Replace(" - ", " – ", StringComparison.Ordinal);
    }

    public static (string? Artist, string? Title) Split(string track)
    {
        foreach (var sep in new[] { " – ", " - ", " — " })
        {
            int i = track.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0 && i + sep.Length < track.Length) return (track[..i].Trim(), track[(i + sep.Length)..].Trim());
        }
        return (null, null);
    }

    public static string Norm(string? s) =>
        new((s ?? "").ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());

    public static bool Same(string? a, string? b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length < 4 || y.Length < 4) return false;
        return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }

    public static bool SameArtist(string? found, string? wanted)
    {
        string x = Norm(found), y = Norm(wanted);
        if (x.Length < 2 || y.Length < 2) return false;
        return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }
}

/// <summary>
/// Stations of the AMG Radio holding (Ретро Хит, Русский Рок, Русская Волна, …): amgradio.ru lists them all with
/// logo, current track and cover in one JSON (<c>/?live=1</c>).
/// </summary>
internal static class AmgRadio
{
    internal sealed record Station(string Key, string Name, string Logo, string Track, string Cover);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<Station> _cached = [];
    private static long _cachedAt;

    public static bool LooksLikeAmg(DeckAction action)
    {
        var t = (action.Target + " " + action.Icon).ToLowerInvariant();
        return t.Contains("volna.top") || t.Contains("amgradio") || t.Contains("amg-radio");
    }

    public static async Task<Station?> LiveAsync(HttpClient http, DeckAction action, CancellationToken ct, long maxAgeMs = 10_000) =>
        Match(action, await ListAsync(http, ct, maxAgeMs));

    public static async Task<IReadOnlyList<Station>> ListAsync(HttpClient http, CancellationToken ct, long maxAgeMs)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = Environment.TickCount64;
            if (_cached.Count > 0 && now - _cachedAt < maxAgeMs) return _cached;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var json = await http.GetStringAsync("https://amgradio.ru/?live=1", cts.Token);
                var fresh = Parse(json);
                if (fresh.Count > 0) { _cached = fresh; _cachedAt = now; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            return _cached;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Parses the <c>/?live=1</c> JSON. Pure, unit tested.</summary>
    public static IReadOnlyList<Station> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("stations", out var stations) || stations.ValueKind != JsonValueKind.Object) return [];
        string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return stations.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .Select(p => new Station(p.Name, Str(p.Value, "name"), Str(p.Value, "logo"), Str(p.Value, "track"), Str(p.Value, "cover")))
            .ToArray();
    }

    /// <summary>By stream address first (retro.volna.top/Retro → "retro"), then the longest AMG name in the label. Pure.</summary>
    public static Station? Match(DeckAction action, IReadOnlyList<Station> list)
    {
        if (list.Count == 0) return null;
        var words = new List<string>();
        if (Uri.TryCreate(action.Target?.Trim(), UriKind.Absolute, out var uri))
        {
            words.Add(uri.Host.Split('.')[0].ToLowerInvariant());
            var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null) words.Add(last.Split('.')[0].ToLowerInvariant());
        }
        if (list.FirstOrDefault(s => words.Contains(s.Key.ToLowerInvariant())) is { } byStream) return byStream;
        var label = TrackText.Norm(action.Label);
        return list.Where(s => TrackText.Norm(s.Name).Length >= 3 && label.Contains(TrackText.Norm(s.Name), StringComparison.Ordinal))
            .OrderByDescending(s => TrackText.Norm(s.Name).Length).FirstOrDefault();
    }
}
