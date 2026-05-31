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

public class WeebCentral : SeriesSource
{
    public WeebCentral(TrangaSettings settings, RateLimitHandler rateLimitHandler) : base("WeebCentral", new[] { "en" }, new[] { "weebcentral.com" }, "https://weebcentral.com/static/images/brand.png", settings)
    {
        this.downloadClient = new HttpDownloadClient(rateLimitHandler, settings);
    }

    public override AcquisitionKind Kind => AcquisitionKind.ImageList;

    public override async Task<(Series, SourceId<Series>)[]> SearchManga(string mangaSearchName)
    {
        Log.InfoFormat("Searching: {0}", mangaSearchName);
        string sanitizedTitle = string.Join(' ', Regex.Matches(mangaSearchName, @"[A-Za-z]+").Where(m => m.Value.Length > 0)).ToLowerInvariant();
        string requestUrl = $"https://weebcentral.com/search/data?limit=32&offset=0&text={HttpUtility.UrlEncode(sanitizedTitle)}&sort=Best+Match&order=Ascending&official=Any&display_mode=Minimal%20Display";
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

        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes("//a[contains(@href, 'series/')]");
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
                string fullUrl = $"{href}";
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
        Match urlMatch = Regex.Match(url, @"https?://(?:www\.)?weebcentral\.com/series/(?<uniqueId>[^/]+)/(?<coreSlug>[^/]+)");
        if (!urlMatch.Success)
            return null;

        string coreSlug = urlMatch.Groups["uniqueId"].Value;
        string storedUrl = $"https://weebcentral.com/series/{coreSlug}";  // Stable wildcard

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
        string url = $"https://weebcentral.com/series/{mangaIdOnSite}";
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
        string rawTitle = titleNode?.InnerText ?? mangaIdOnSite;

		Match m = Regex.Match(rawTitle,@"^(.*?)\s*\|\s*Weeb.*$",RegexOptions.IgnoreCase);

		string cleanTitle = m.Success ? m.Groups[1].Value.Trim() : rawTitle;
		cleanTitle = HtmlEntity.DeEntitize(cleanTitle); // Simple decode like original

        // Cover
        HtmlNode? coverNode = doc.DocumentNode.SelectSingleNode("//img[contains(@alt, 'cover')]");
        string coverUrl = coverNode?.GetAttributeValue("src", "") ?? "";
        if (!string.IsNullOrEmpty(coverUrl) && !coverUrl.StartsWith("http"))
            coverUrl = $"https://temp.compsci88.com{coverUrl}";

        // Description
        HtmlNode? descNode = doc.DocumentNode.SelectSingleNode("//strong[starts-with(text(),'Description')]/../p");
        string description = HtmlEntity.DeEntitize(descNode?.InnerText ?? "").Trim();

        // Tags
        HtmlNodeCollection? genreNodes = doc.DocumentNode.SelectNodes("//strong[starts-with(text(),'Tag')]/../span");
        List<SeriesTag> tags = genreNodes?.Select(b => new SeriesTag(HtmlEntity.DeEntitize(b.InnerText.Trim()))).ToList() ?? [];

        // Status
        HtmlNode? statusNode = doc.DocumentNode.SelectSingleNode("//strong[starts-with(text(),'Status')]/../a");
        string rawStatus = HtmlEntity.DeEntitize(statusNode?.InnerText ?? "").ToLowerInvariant().Trim();
        SeriesReleaseStatus releaseStatus = rawStatus switch
        {
            "ongoing" => SeriesReleaseStatus.Continuing,
            "hiatus" => SeriesReleaseStatus.OnHiatus,
            "completed" => SeriesReleaseStatus.Completed,
            "canceled" => SeriesReleaseStatus.Cancelled,
            _ => SeriesReleaseStatus.Unreleased
        };

        // Authors
        HtmlNodeCollection? authorNodes = doc.DocumentNode.SelectNodes("//strong[starts-with(text(),'Author')]/../span");
        List<Author> authors = authorNodes?.Select(a => new Author(HtmlEntity.DeEntitize(a.InnerText.Trim()))).ToList() ?? [];

        // Year
        HtmlNode? firstChapterNode = doc.DocumentNode.SelectSingleNode("//strong[starts-with(text(),'Released: ')]/../span");
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

        string baseSlug = manga.IdOnConnectorSite;
        if (baseSlug.Contains("series/"))
            baseSlug = baseSlug.Substring(baseSlug.IndexOf("series/") + 7);

        string websiteUrl = $"https://weebcentral.com/series/{baseSlug}/full-chapter-list";

        using HttpResponseMessage response = await downloadClient.MakeRequest(websiteUrl, RequestType.Default);
        if (!response.IsSuccessStatusCode)
        {
            Log.Error("Failed to load chapters page");
            return [];
        }

        string html = await response.Content.ReadAsStringAsync();
        HtmlDocument doc = new();
        doc.LoadHtml(html);

        // Extract chapters from page
        HtmlNodeCollection? chapterNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/chapters/')]");
        if (chapterNodes is null)
            return [];

        List<(Chapter, SourceId<Chapter>)> chapters = new();

        foreach (HtmlNode node in chapterNodes)
        {
            string href = node.GetAttributeValue("href", "").Trim();
			string text = node.SelectSingleNode(".//span[@class='']").InnerText.Trim();

			// Get volume/season number - if applicable
			int? volumeNumber = null;
			Match volMatch = Regex.Match(text, @"^(?:volume|vol\.?|season|s\.?)\s*([\d]+)", RegexOptions.IgnoreCase);
			if (volMatch.Success)
			{
				if (int.TryParse(volMatch.Groups[1].Value, out int parsedVolume))
					volumeNumber = parsedVolume;
				else
					Log.Warn($"Failed to parse volume number: {volMatch.Groups[1].Value}");
			}

            // Get chapter number - supports decimals
            string chapterNumber;
			Match chMatch = Regex.Match(text, @"(?:chapter|ch\.?)\s*([\d]+(?:\.\d+)?)", RegexOptions.IgnoreCase);
			if (chMatch.Success)
				chapterNumber = chMatch.Groups[1].Value;
			else
			{
				// If "chapter" or "ch" is not found, take the last number in the string
				MatchCollection numberMatches = Regex.Matches(text, @"\d+(\.\d+)?");
				if (numberMatches.Count > 0)
				{
					chapterNumber = numberMatches.Last().Value;
					Log.Warn($"Unknown chapter format detected. Using last number in string: {chapterNumber}");
				}
				else
				{
					// For everything else, log and continue
					Log.Warn($"Unknown chapter format ignored: {text}");
					continue;
				}
			}

            string? title = null;

            Chapter ch = new(manga.Obj, chapterNumber, volumeNumber, title);
			string chapterIdOnSite = new Uri(href).Segments.Last();
			string canonicalChapterUrl = $"https://weebcentral.com/chapters/{chapterIdOnSite}";
            SourceId<Chapter> mcId = new(ch, this, chapterIdOnSite, canonicalChapterUrl);
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

		return await GetChapterImageUrlsAsync(chapterId, referrer);
	}

	private async Task<string[]> GetChapterImageUrlsAsync(SourceId<Chapter> chapterId, string? referrer)
	{
		using HttpResponseMessage response = await downloadClient.MakeRequest(chapterId.WebsiteUrl!, RequestType.Default, referrer);

		if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
		{
			Log.Error("Failed to load chapter page with Chromium");
			return [];
		}

		string html = await response.Content.ReadAsStringAsync();

		HtmlDocument doc = new();
		doc.LoadHtml(html);

		HtmlNodeCollection? imageNodes = doc.DocumentNode.SelectNodes("//img[starts-with(@alt, 'Page')]");

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
				return src;
			})
			.Where(u => !string.IsNullOrEmpty(u))
			.ToArray();

		Log.InfoFormat("Found {0} images for chapter {1}", imageUrls.Length, chapterId.Obj);
		return imageUrls;
	}
}
