using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using log4net;
using System.Collections.Generic;
using System.Linq; // For OrderBy
using System.Text.Json;
using System.Text;
using System.Threading;

using API.Acquirers;

namespace API.MangaConnectors;

public class AsuraComic : SeriesSource
{
    private readonly TrangaSettings _settings;
    private readonly RateLimitHandler _rateLimitHandler;

    public AsuraComic(TrangaSettings settings, RateLimitHandler rateLimitHandler) : base("AsuraComic", ["en"], ["asuracomic.net"], "https://asuracomic.net/images/logo.webp", settings)
    {
        _settings = settings;
        _rateLimitHandler = rateLimitHandler;
        this.downloadClient = new HttpDownloadClient(rateLimitHandler, settings);
    }

    public override AcquisitionKind Kind => AcquisitionKind.ImageList;

    public override async Task<(Series, SourceId<Series>)[]> SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        string sanitizedTitle = string.Join(' ', Regex.Matches(mangaSearchName, @"[A-Za-z]+").Where(m => m.Value.Length > 0)).ToLowerInvariant();
        string requestUrl = $"https://asuracomic.net/series?name={HttpUtility.UrlEncode(sanitizedTitle)}";
        using HttpResponseMessage response = await downloadClient.MakeRequest(requestUrl, RequestType.Default);

        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Request failed or no HTML retrieved");
            return [];
        }

        string html = await response.Content.ReadAsStringAsync();
        Log.DebugFormat("Search HTML length: {0}", html.Length);
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//a[starts-with(@href, 'series/')]"); // Match v1 XPath
        Log.DebugFormat("Found {0} series nodes in search HTML", nodes?.Count ?? 0);
        if (nodes is null || nodes.Count < 1)
        {
            Log.Error("No series links found");
            return [];
        }

        HashSet<string> seenUrls = new(); // Dedup URLs
        List<(Series, SourceId<Series>)> mangas = new();
        foreach (HtmlNode node in nodes)
        {
            string href = node.GetAttributeValue("href", "");
            if (!string.IsNullOrEmpty(href))
            {
                // Fix: Ensure leading / for relative hrefs (match v1 TrimStart logic but add / if missing)
                if (!href.StartsWith("/"))
                    href = "/" + href;
                string fullUrl = $"https://asuracomic.net{href}";
                if (seenUrls.Add(fullUrl))
                {
                    Log.DebugFormat("Fetching from {0}", fullUrl); // Debug URL
                    (Series, SourceId<Series>)? manga = await GetMangaFromUrl(fullUrl);
                    if (manga.HasValue)
                    {
                        mangas.Add(manga.Value);
                        Log.DebugFormat("Added manga from {0}", fullUrl);
                    }
                    else
                    {
                        Log.WarnFormat("Failed to parse manga from {0}", fullUrl); // Debug fails
                    }
                }
            }
        }

        Log.InfoFormat("Search '{0}' yielded {1} results.", mangaSearchName, mangas.Count);
        return mangas.DistinctBy(r => r.Item1.Key).ToArray(); // Dedup by manga Key
    }

   public override async Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string url)
    {
        Log.InfoFormat("Fetching manga from URL: {0}", url);
        // Robust regex: Capture full slug before optional UID
        Match urlMatch = Regex.Match(url, @"https?://(?:www\.)?asuracomic\.net/series/(?<coreSlug>[^-]+(?:-[^-]+)*?)(?:-(?<uid>[a-f0-9]{8}))?$");
        if (!urlMatch.Success)
            return null;

        string coreSlug = urlMatch.Groups["coreSlug"].Value;
        string storedUrl = $"https://asuracomic.net/series/{coreSlug}-*";  // Stable wildcard

        // Fetch once using full url (no double fetch)
        using HttpResponseMessage response = await downloadClient.MakeRequest(url, RequestType.MangaInfo);
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to retrieve manga page");
            return null;
        }

        string html = await response.Content.ReadAsStringAsync();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        return ParseMangaFromHtml(doc, coreSlug, storedUrl);
    }

    public override async Task<(Series, SourceId<Series>)?> GetMangaFromId(string mangaIdOnSite)
    {
        string url = $"https://asuracomic.net/series/{mangaIdOnSite}";
        using HttpResponseMessage response = await downloadClient.MakeRequest(url, RequestType.MangaInfo);
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to retrieve manga page");
            return null;
        }

        string html = await response.Content.ReadAsStringAsync();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        return ParseMangaFromHtml(doc, mangaIdOnSite, url); // Use full slug as ID
    }

    private (Series, SourceId<Series>) ParseMangaFromHtml(HtmlDocument doc, string mangaIdOnSite, string url)
    {
        // Title with cleanup (kept for robustness, but simple decode to match original)
        HtmlNode? titleNode = doc.DocumentNode.SelectSingleNode("//title");
        string rawTitle = Regex.Match(titleNode?.InnerText ?? mangaIdOnSite, @"(.*) - Asura Scans").Groups[1].Value.Trim();
        string cleanTitle = HtmlEntity.DeEntitize(rawTitle).Trim(); // Simple decode like original

        // Cover
        HtmlNode? coverNode = doc.DocumentNode.SelectSingleNode("//img[@alt='poster']");
        string coverUrl = coverNode?.GetAttributeValue("src", "") ?? "";
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = $"https://asuracomic.net{coverUrl}";

        // Description
        HtmlNode? descNode = doc.DocumentNode.SelectSingleNode("//h3[starts-with(text(),'Synopsis')]/../span");
        string description = HtmlEntity.DeEntitize(descNode?.InnerText ?? "").Trim();

        // Tags
        HtmlNodeCollection? genreNodes = doc.DocumentNode.SelectNodes("//h3[text()='Genres']/../div/button");
        List<SeriesTag> tags = genreNodes?.Select(b => new SeriesTag(HtmlEntity.DeEntitize(b.InnerText.Trim()))).ToList() ?? [];

        // Status
        HtmlNode? statusNode = doc.DocumentNode.SelectSingleNode("//h3[text()='Status']/../h3[2]");
        string rawStatus = HtmlEntity.DeEntitize(statusNode?.InnerText ?? "").ToLowerInvariant().Trim();
        SeriesReleaseStatus releaseStatus = rawStatus switch
        {
            "ongoing" or "season end" => SeriesReleaseStatus.Continuing,
            "hiatus" => SeriesReleaseStatus.OnHiatus,
            "completed" => SeriesReleaseStatus.Completed,
            "dropped" => SeriesReleaseStatus.Cancelled,
            _ => SeriesReleaseStatus.Unreleased
        };

        // Authors/Artists
        HtmlNodeCollection? authorNodes = doc.DocumentNode.SelectNodes("//h3[text()='Author']/../h3[not(text()='Author' or text()='_')]");
        HtmlNodeCollection? artistNodes = doc.DocumentNode.SelectNodes("//h3[text()='Artist']/../h3[not(text()='Artist' or text()='_')]");
        List<Author> authors = authorNodes?.Select(a => new Author(HtmlEntity.DeEntitize(a.InnerText.Trim()))).ToList() ?? [];
        if (artistNodes is not null)
            authors.AddRange(artistNodes.Select(a => new Author(HtmlEntity.DeEntitize(a.InnerText.Trim()))));

        // Year
        HtmlNode? firstChapterNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href, 'chapter/1')]/../following-sibling::h3");
        uint? year = null;
        if (firstChapterNode?.InnerText is { } firstText && firstText.Contains(" "))
        {
            string datePart = firstText.Split(' ').Last();
            uint.TryParse(datePart, out uint parsedYear);
            year = parsedYear > 0 ? parsedYear : null;
        }

        List<AltTitle> altTitles = new();
        List<Link> links = new();
        // Match original constructor (null language for consistent Key)
        Series manga = new(cleanTitle, description, coverUrl, releaseStatus, authors, tags, links, altTitles, null, 0f, year, null);

        // Use mangaIdOnSite for ID (core slug, consistent)
        SourceId<Series> mcId = new(manga, this, mangaIdOnSite, url);
        manga.SourceIds.Add(mcId);

        return (manga, mcId);
    }

    public override async Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> manga, string? language = null)
    {
        Log.InfoFormat("Fetching chapters for: {0}", manga.IdOnConnectorSite);

        string baseSlug = manga.WebsiteUrl ?? manga.IdOnConnectorSite;
        if (baseSlug.Contains("series/"))
            baseSlug = baseSlug.Substring(baseSlug.IndexOf("series/") + 7);

        string websiteUrl = manga.WebsiteUrl ?? $"https://asuracomic.net/series/{baseSlug}";

        using HttpResponseMessage response = await downloadClient.MakeRequest(websiteUrl, RequestType.Default);
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load chapters page");
            return [];
        }

        string html = await response.Content.ReadAsStringAsync();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Early Access detection from JSON
        Dictionary<string, bool> earlyAccessDict = new();
        HtmlNode? scriptNode = doc.DocumentNode.SelectSingleNode("//script[contains(text(), 'is_early_access')]");
        if (scriptNode != null)
        {
            string scriptText = scriptNode.InnerText.Replace("\\", "");
            Match? jsonMatch = Regex.Match(scriptText, @"""chapters"":\s*\[(.*?)\],\s*""comic""", RegexOptions.Singleline);
            if (jsonMatch.Success)
            {
                string chaptersJson = "[" + jsonMatch.Groups[1].Value.Trim().Trim(',') + "]";
                try
                {
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(chaptersJson);
                    JsonElement jsonChapters = JsonSerializer.Deserialize<JsonElement>(jsonBytes);
                    if (jsonChapters.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement chapter in jsonChapters.EnumerateArray())
                        {
                            if (chapter.TryGetProperty("name", out JsonElement nameProp))
                            {
                                string chNum = nameProp.ValueKind == JsonValueKind.Number
                                    ? nameProp.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    : nameProp.GetString() ?? "";

                                if (!string.IsNullOrEmpty(chNum) &&
                                    chapter.TryGetProperty("is_early_access", out JsonElement eaProp) &&
                                    (eaProp.ValueKind == JsonValueKind.True ||
                                     (eaProp.ValueKind == JsonValueKind.String && eaProp.GetString()?.ToLower() == "true")))
                                {
                                    earlyAccessDict[chNum] = true;
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Silent - JSON issues are non-fatal
                }
            }
        }

        // Extract chapters from page
        HtmlNodeCollection? chapterNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/chapter/')]");
        if (chapterNodes is null)
            return [];

        HashSet<string> seenHrefs = new();
        List<(Chapter, SourceId<Chapter>)> chapters = new();

        foreach (HtmlNode node in chapterNodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
            if (string.IsNullOrEmpty(href) || !seenHrefs.Add(href))
                continue;

            if (!href.StartsWith("/"))
                href = "/" + href;

            string fullUrl = href.Contains("/chapter/")
                ? $"https://asuracomic.net/series/{baseSlug}{href.Substring(href.IndexOf("/chapter/"))}"
                : $"https://asuracomic.net/series/{baseSlug}{href}";

            string text = node.InnerText.Trim();

            // Get chapter number - supports decimals
            string chapterNumber;
            Match hrefMatch = Regex.Match(href, @"/chapter/([\d\.]+)");
            if (hrefMatch.Success)
            {
                chapterNumber = hrefMatch.Groups[1].Value;
            }
            else
            {
                Match textMatch = Regex.Match(text, @"Chapter\s*([\d\.]+)", RegexOptions.IgnoreCase);
                if (!textMatch.Success)
                    continue;
                chapterNumber = textMatch.Groups[1].Value;
            }

            if (earlyAccessDict.ContainsKey(chapterNumber))
                continue;

            string? title = null;
            string chapterStr = $"Chapter {chapterNumber}";
            int titleIndex = text.IndexOf(chapterStr, StringComparison.OrdinalIgnoreCase);
            if (titleIndex >= 0)
            {
                string rawTitle = text.Substring(titleIndex + chapterStr.Length).TrimStart('-', ':', ' ', '\t');
                title = string.IsNullOrEmpty(rawTitle) ? null : HtmlEntity.DeEntitize(rawTitle);
            }

            int? volumeNumber = null;
            Match volMatch = Regex.Match(text, @"(?:[Vv]olume|[Vv]ol\.?|[Ss]eason|[Ss]\.?)\s*([\d]+)", RegexOptions.IgnoreCase);
            if (volMatch.Success && int.TryParse(volMatch.Groups[1].Value, out int parsedVol))
            {
                volumeNumber = parsedVol;
            }

            Chapter ch = new(manga.Obj, chapterNumber, volumeNumber, title);
            string coreSlug = baseSlug.Replace("-*", "");
            string uniqueChapterId = $"{coreSlug}-{chapterNumber.Replace(".", "_")}";
            SourceId<Chapter> mcId = new(ch, this, uniqueChapterId, fullUrl);
            ch.SourceIds.Add(mcId);
            chapters.Add((ch, mcId));
        }

        Log.InfoFormat("Found {0} chapters for {1}", chapters.Count, manga.Obj.Name);
        return chapters.OrderBy(c => c.Item1, new Chapter.ChapterComparer()).ToArray();
    }

    internal override async Task<string[]> GetChapterImageUrls(SourceId<Chapter> chapterId)
    {
        Log.InfoFormat("Getting Chapter Image-Urls: {0}", chapterId.Obj);
        if (chapterId.WebsiteUrl is null)
        {
            Log.Error("Chapter URL is null");
            return [];
        }

        string? referrer = null;
        if (chapterId.Obj.ParentManga.SourceIds is not null && chapterId.Obj.ParentManga.SourceIds.Any())
        {
            referrer = chapterId.Obj.ParentManga.SourceIds
                .FirstOrDefault(id => id.MangaConnectorName == this.Name)?.WebsiteUrl;
        }

        // Sync wrapper for async MakeRequest
        ChromiumDownloadClient chromium = new(_settings, _rateLimitHandler);
        try
        {
            using HttpResponseMessage response = await chromium.MakeRequest(chapterId.WebsiteUrl!, RequestType.Default, referrer);

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                Log.Error("Failed to load chapter page with Chromium");
                return [];
            }

            string html = await response.Content.ReadAsStringAsync();
            HtmlDocument doc = new();
            doc.LoadHtml(html);

            Log.DebugFormat("HTML snippet (first 500 chars): {0}", doc.DocumentNode.OuterHtml.Substring(0, Math.Min(500, doc.DocumentNode.OuterHtml.Length)));

            HtmlNodeCollection? imageNodes = doc.DocumentNode.SelectNodes("//img[contains(@alt, 'chapter page') or contains(@alt, 'Chapter') or @class='page-break']");
            if (imageNodes is null || imageNodes.Count == 0)
            {
                Log.Warn("No chapter page images found");
                return [];
            }

            string[] imageUrls = imageNodes
                .Select(i =>
                {
                    string src = i.GetAttributeValue("src", "");
                    if (string.IsNullOrEmpty(src))
                        src = i.GetAttributeValue("data-src", "");
                    if (!src.StartsWith("http"))
                        src = "https://asuracomic.net" + src;
                    return src;
                })
                .Where(u => !string.IsNullOrEmpty(u))
                .ToArray();

            Log.InfoFormat("Found {0} images for chapter {1}", imageUrls.Length, chapterId.Obj);
            return imageUrls;
        }
        finally
        {
            chromium.DisposeAsync().AsTask();  // Sync dispose
        }
    }
}
