using System.Text.RegularExpressions;

namespace API.Indexers;

/// <summary>Decomposition of a torrent/usenet release title into series + issue + year.</summary>
public record ParsedRelease(string SeriesTitle, string? IssueNumber, int? Year);

/// <summary>
/// Best-effort parser for comic release titles. Indexers return release names like
/// "Saga 060 (2024) (Digital) (Zone-Empire)"; the torrent-backed source needs the series name and
/// issue number out of them. Heuristic and intentionally simple — covers the common cases; the
/// long tail is discovered by use.
/// </summary>
public static class ReleaseTitleParser
{
    private static readonly Regex YearRx = new(@"\((19|20)\d{2}\)", RegexOptions.Compiled);
    private static readonly Regex TagRx = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);

    // Trailing issue token: optional #/vol/v/chapter/ch prefix, optional leading zeros, digits with
    // an optional decimal, anchored to the end of the (tag-stripped) title.
    private static readonly Regex IssueRx = new(
        @"(?:#|vol\.?|v\.?|chapter|ch\.?)?\s*0*(\d{1,5}(?:\.\d+)?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ParsedRelease Parse(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new ParsedRelease("", null, null);

        int? year = null;
        Match ym = YearRx.Match(title);
        if (ym.Success)
            year = int.Parse(ym.Value.Trim('(', ')'));

        // Strip all (...) and [...] tag groups, collapse whitespace.
        string cleaned = WhitespaceRx.Replace(TagRx.Replace(title, " "), " ").Trim();

        string? issue = null;
        string seriesTitle = cleaned;
        Match im = IssueRx.Match(cleaned);
        if (im.Success && im.Groups[1].Success)
        {
            issue = NormalizeIssue(im.Groups[1].Value);
            seriesTitle = cleaned[..im.Index].Trim().TrimEnd('-', ':', '–').Trim();
        }

        if (string.IsNullOrWhiteSpace(seriesTitle))
            seriesTitle = cleaned; // never return an empty series

        return new ParsedRelease(seriesTitle, issue, year);
    }

    private static string NormalizeIssue(string raw)
    {
        // "060" -> "60", "60.5" -> "60.5", "100" -> "100". Matches Chapter's chapter-number contract.
        if (raw.Contains('.'))
        {
            string[] parts = raw.Split('.');
            return $"{int.Parse(parts[0])}.{parts[1]}";
        }
        return int.Parse(raw).ToString();
    }
}
