using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DuplicateMedia.Services;

public sealed class ProductCodeExtractor
{
    private const int RegexTimeoutMilliseconds = 250;
    private static readonly string[] DomainSuffixes = [".com", ".net", ".org", ".tv", ".co.kr"];

    public Regex CreateRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Product-code pattern cannot be empty.", nameof(pattern));
        }

        return new Regex(
            pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds));
    }

    public IReadOnlyList<string> Extract(Regex regex, IEnumerable<string?> sources)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (Match match in regex.Matches(source))
            {
                if (HasDomainSuffix(source, match.Index + match.Length))
                {
                    continue;
                }

                var prefix = match.Groups["prefix"].Value;
                var number = match.Groups["number"].Value;
                if (prefix.Length == 0 || number.Length == 0)
                {
                    continue;
                }

                results.Add($"{prefix.ToUpperInvariant()}-{number}");
            }
        }

        return results.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasDomainSuffix(string source, int suffixIndex)
    {
        var suffix = source.AsSpan(suffixIndex);
        foreach (var domain in DomainSuffixes)
        {
            if (suffix.StartsWith(domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
