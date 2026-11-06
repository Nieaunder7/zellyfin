using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.SeekStatistics.Data;

/// <summary>Matches local videos to a saved, local performer catalog without contacting external sites.</summary>
public sealed class PerformerFilmographyIndex
{
    private static readonly HashSet<string> RuruTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "루루", "아이자와", "逢沢るる", "Ruru", "Aizawa"
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _cachePath;
    private readonly object _gate = new();
    private DateTime _cachedModifiedUtc = DateTime.MinValue;
    private PerformerCode[] _cachedCodes = [];

    public PerformerFilmographyIndex(IServerConfigurationManager configurationManager)
    {
        var dataPath = configurationManager.ApplicationPaths.DataPath;
        // Jellyfin stores its database under runtime/data/data; the saved catalog is under runtime/worker.
        var runtimeRoot = Directory.GetParent(Directory.GetParent(dataPath)?.FullName ?? dataPath)?.FullName ?? dataPath;
        _cachePath = Path.Combine(runtimeRoot, "worker", "scene-analysis", "filmography", "ruru-aizawa.json");
    }

    public static bool IsRuruTerm(string term) => RuruTerms.Contains(term);

    public int RuruCodeCount => GetRuruCodes().Length;

    public PerformerCode? MatchRuru(string name, string? path)
    {
        var haystack = name + " " + Path.GetFileNameWithoutExtension(path ?? "");
        foreach (var code in GetRuruCodes())
        {
            if (haystack.Contains(code.Prefix, StringComparison.OrdinalIgnoreCase)
                && code.Pattern.IsMatch(haystack)) return code;
        }
        return null;
    }

    private PerformerCode[] GetRuruCodes()
    {
        DateTime modifiedUtc;
        try
        {
            if (!File.Exists(_cachePath)) return [];
            modifiedUtc = File.GetLastWriteTimeUtc(_cachePath);
        }
        catch (IOException)
        {
            return [];
        }
        lock (_gate)
        {
            if (modifiedUtc == _cachedModifiedUtc) return _cachedCodes;
            try
            {
                var cache = JsonSerializer.Deserialize<FilmographyCache>(File.ReadAllText(_cachePath), JsonOptions);
                var codes = (cache?.Sources ?? [])
                    .SelectMany(source => source.Codes ?? []).Select(code => (Code: code, Source: "AVBom"))
                    .Concat((cache?.UserReportedAdditionalCodes ?? []).Select(code => (Code: code, Source: "사용자 추가")))
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Code))
                    .DistinctBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => CreateCode(entry.Code, entry.Source))
                    .Where(entry => entry is not null)
                    .Cast<PerformerCode>()
                    .ToArray();
                _cachedCodes = codes;
                _cachedModifiedUtc = modifiedUtc;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return _cachedCodes;
            }
            return _cachedCodes;
        }
    }

    private static PerformerCode? CreateCode(string code, string source)
    {
        var segments = Regex.Matches(code, @"[A-Za-z]+|\d+").Select(match => match.Value).ToArray();
        var prefix = segments.FirstOrDefault(segment => char.IsLetter(segment[0]));
        if (prefix is null || segments.Length < 2) return null;
        var body = string.Join(@"[\W_]*", segments.Select(segment =>
            char.IsDigit(segment[0]) ? "0*" + segment.TrimStart('0').PadLeft(1, '0') : Regex.Escape(segment)));
        return new PerformerCode(code, source, prefix,
            new Regex(@"(?<![A-Za-z0-9])" + body + @"(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private sealed class FilmographyCache
    {
        public FilmographySource[] Sources { get; set; } = [];
        public string[] UserReportedAdditionalCodes { get; set; } = [];
    }

    private sealed class FilmographySource
    {
        public string[] Codes { get; set; } = [];
    }
}

public sealed record PerformerCode(string Code, string Source, string Prefix, Regex Pattern);
