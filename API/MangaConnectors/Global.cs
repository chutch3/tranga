using API.Schema.SeriesContext;
using Microsoft.Extensions.DependencyInjection;

using API.Acquirers;

namespace API.MangaConnectors;

public class Global : SeriesSource
{
    private readonly IServiceProvider _serviceProvider;

    public Global(TrangaSettings settings, IServiceProvider serviceProvider) : base("Global", ["all"], [""], "https://avatars.githubusercontent.com/u/13404778", settings)
    {
        _serviceProvider = serviceProvider;
    }

    private IEnumerable<SeriesSource> GetConnectors() =>
        _serviceProvider.GetServices<SeriesSource>().Where(c => c.Name != "Global");

    public override AcquisitionKind Kind => AcquisitionKind.ImageList;

        public override async Task<(Series, SourceId<Series>)[]> SearchManga(string mangaSearchName)
    {
        Log.Debug("Searching Series on all enabled connectors:");
        SeriesSource[] enabledConnectors = GetConnectors().Where(c => c.Enabled).ToArray();
        Log.Debug(string.Join(", ", enabledConnectors.Select(c => c.Name)));

        Task<(Series, SourceId<Series>)[]>[] tasks =
            enabledConnectors.Select(c => c.SearchManga(mangaSearchName)).ToArray();
        
        await Task.WhenAll(tasks);

        (Series, SourceId<Series>)[] ret = tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : [])
            .SelectMany(i => i)
            .OrderByDescending(m =>
            {
                var connector = GetConnectors().FirstOrDefault(c => c.Name == m.Item2.MangaConnectorName);
                if (connector == null) return -1;
                if (connector.SupportedLanguages.Contains(Settings.DownloadLanguage) || connector.SupportedLanguages.Contains("all")) return 1;
                return 0;
            })
            .ToArray();
        Log.DebugFormat("Got {0} results.", ret.Length);
        return ret;
    }

    public override async Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string url)
    {
        SeriesSource? mc = GetConnectors().FirstOrDefault(c => c.UrlMatchesConnector(url));
        return mc is not null ? await mc.GetMangaFromUrl(url) : null;
    }

    public override async Task<(Series, SourceId<Series>)?> GetMangaFromId(string mangaIdOnSite)
    {
        return null;
    }

    public override async Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> mangaId,
        string? language = null)
    {
        SeriesSource? seriesSource = GetConnectors().FirstOrDefault(c => c.Name.Equals(mangaId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (seriesSource is null) return [];
        return await seriesSource.GetChapters(mangaId, language);
    }

    internal override async Task<string[]> GetChapterImageUrls(SourceId<Chapter> chapterId)
    {
        SeriesSource? seriesSource = GetConnectors().FirstOrDefault(c => c.Name.Equals(chapterId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (seriesSource is null) return [];
        return await seriesSource.GetChapterImageUrls(chapterId);
    }
}
