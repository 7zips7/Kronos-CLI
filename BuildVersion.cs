using System.Text.Json;
using System.Text.RegularExpressions;

namespace KronosCLI;

internal sealed record BuildVersion(string Text, int Major, int Minor, string Source)
{
    public const string ProxyMinimum = "30.10";

    static readonly Regex Pattern = new(@"Release-((\d+)\.(\d+)[\d.]*(?:-CL-\d+)?)", RegexOptions.Compiled);

    public bool UsesProxy => Major > 30 || (Major == 30 && Minor >= 10);

    public override string ToString() => Text;

    public static string CloudContentPath(Options options) =>
        Path.Combine(options.BuildRoot, "Cloud", "cloudcontent.json");

    public static BuildVersion? Read(Options options)
    {
        try
        {
            string path = CloudContentPath(options);
            if (!File.Exists(path)) return null;

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("BuildVersion", out var value) || value.ValueKind != JsonValueKind.String)
                return null;

            var match = Pattern.Match(value.GetString()!);
            if (!match.Success) return null;

            return new BuildVersion(match.Groups[1].Value,
                int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), path);
        }
        catch
        {
            return null;
        }
    }
}
