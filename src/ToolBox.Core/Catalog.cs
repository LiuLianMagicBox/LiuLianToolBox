using System.Globalization;
using System.Text.Json;

namespace LiuLian.ToolBox;

public sealed class Catalog
{
    public static string CultureTag(string? language) => language switch
    {
        "EN" => "en",
        "ZH_CN" => "zh-CN",
        _ => "zh-TW"
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Tables =
        new[] { "zh-TW", "zh-CN", "en" }.ToDictionary(tag => tag, tag =>
        {
            using var stream = typeof(Catalog).Assembly.GetManifestResourceStream($"LiuLian.ToolBox.Locales.{tag}.json")!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        });

    private readonly CultureInfo culture;
    public Catalog(string? language) => culture = CultureInfo.GetCultureInfo(CultureTag(language));
    public string this[string key] => Tables[culture.Name].TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
        ? value : Tables["zh-TW"].GetValueOrDefault(key) ?? Tables["zh-TW"]["Failed"];
    public string Format(string key, params object?[] args) => string.Format(culture, this[key], args);

    public static IEnumerable<string> Menu(Snapshot? state)
    {
        yield return "Home";
        yield return "Current";
        yield return "Remote";
        yield return "Bot";
        if (state?.Idle is not null) yield return "Idle";
        if (state?.Mailbox is not null) yield return "Mailbox";
        yield return "Plugins";
        yield return "Info";
    }
}
