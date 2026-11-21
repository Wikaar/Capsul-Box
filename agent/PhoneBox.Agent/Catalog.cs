using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>A page template: a ready-made widget layout the settings UI (and migration) can start from.</summary>
public sealed class TemplateDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Desc { get; set; } = "";
    public List<WidgetConfig> Widgets { get; set; } = [];
}

/// <summary>The widget types and page templates shipped with the agent (widgets.json, templates.json).</summary>
public static class Catalog
{
    public const int Cols = 12;
    public const int Rows = 6;

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static readonly string WidgetsJson = Read("widgets.json");
    public static readonly string TemplatesJson = Read("templates.json");

    /// <summary>Capsul Box: виджеты, которые предлагает редактор (остальные PhoneBox-виджеты скрыты, но код и старые
    /// настройки с ними продолжают работать).</summary>
    public static readonly IReadOnlySet<string> CapsulWidgets = new HashSet<string>
    {
        "clock", "weather", "forecast", "media", "volume", "stat", "gauge", "chart", "meters", "gpu", "topprocs", "processes",
        "shortcuts", "button", "discord", "discord-controls", "steam", "steam-activity", "steam-library", "batteries", "battery", "label", "timer", "stack",
        "audio-mixer", "app-volume", "device-volume", "audio-outputs", "microphone", "media-remote", "media-seek",
        "power-controls", "system-keys", "hotkeys", "macro-pad", "push",
    };

    /// <summary>Каталог для редактора: только виджеты Capsul Box и шаблоны cb-*.</summary>
    public static readonly string EditorWidgetsJson = Filter(WidgetsJson, e => CapsulWidgets.Contains(e.GetProperty("type").GetString()!));
    public static readonly string EditorTemplatesJson = Filter(TemplatesJson, e => e.GetProperty("id").GetString()!.StartsWith("cb-", StringComparison.Ordinal));

    private static string Filter(string json, Func<JsonElement, bool> keep)
    {
        using var doc = JsonDocument.Parse(json);
        return "[" + string.Join(",", doc.RootElement.EnumerateArray().Where(keep).Select(e => e.GetRawText())) + "]";
    }

    public static readonly IReadOnlyList<TemplateDef> Templates =
        JsonSerializer.Deserialize<List<TemplateDef>>(TemplatesJson, Opts) ?? [];

    private static readonly Dictionary<string, (int MinW, int MinH)> Sizes = LoadSizes();

    public static bool IsWidget(string type) => Sizes.ContainsKey(type);

    public static (int MinW, int MinH) MinSize(string type) => Sizes.TryGetValue(type, out var s) ? s : (1, 1);

    public static TemplateDef? Template(string? id) => id is null ? null : Templates.FirstOrDefault(t => t.Id == id);

    private static Dictionary<string, (int, int)> LoadSizes()
    {
        using var doc = JsonDocument.Parse(WidgetsJson);
        return doc.RootElement.EnumerateArray().ToDictionary(
            w => w.GetProperty("type").GetString()!,
            w => (w.GetProperty("minW").GetInt32(), w.GetProperty("minH").GetInt32()));
    }

    private static string Read(string name)
    {
        using var s = AppPaths.Resource(name) ?? throw new InvalidOperationException($"missing resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
