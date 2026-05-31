using API.Schema.MangaContext;
using Microsoft.Extensions.DependencyInjection;

namespace API.MangaConnectors;

public class Global : MangaConnector
{
    private readonly IServiceProvider _serviceProvider;

    public Global(TrangaSettings settings, IServiceProvider serviceProvider) : base("Global", ["all"], [""], "https://avatars.githubusercontent.com/u/13404778", settings)
    {
        _serviceProvider = serviceProvider;
    }

    private IEnumerable<MangaConnector> GetConnectors() =>
        _serviceProvider.GetServices<MangaConnector>().Where(c => c.Name != "Global");

        public override async Task<(Series, MangaConnectorId<Series>)[]> SearchManga(string mangaSearchName)
    {
        Log.Debug("Searching Series on all enabled connectors:");
        MangaConnector[] enabledConnectors = GetConnectors().Where(c => c.Enabled).ToArray();
        Log.Debug(string.Join(", ", enabledConnectors.Select(c => c.Name)));

        Task<(Series, MangaConnectorId<Series>)[]>[] tasks =
            enabledConnectors.Select(c => c.SearchManga(mangaSearchName)).ToArray();
        
        await Task.WhenAll(tasks);

        (Series, MangaConnectorId<Series>)[] ret = tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : [])
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

    public override async Task<(Series, MangaConnectorId<Series>)?> GetMangaFromUrl(string url)
    {
        MangaConnector? mc = GetConnectors().FirstOrDefault(c => c.UrlMatchesConnector(url));
        return mc is not null ? await mc.GetMangaFromUrl(url) : null;
    }

    public override async Task<(Series, MangaConnectorId<Series>)?> GetMangaFromId(string mangaIdOnSite)
    {
        return null;
    }

    public override async Task<(Chapter, MangaConnectorId<Chapter>)[]> GetChapters(MangaConnectorId<Series> mangaId,
        string? language = null)
    {
        MangaConnector? mangaConnector = GetConnectors().FirstOrDefault(c => c.Name.Equals(mangaId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (mangaConnector is null) return [];
        return await mangaConnector.GetChapters(mangaId, language);
    }

    internal override async Task<string[]> GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        MangaConnector? mangaConnector = GetConnectors().FirstOrDefault(c => c.Name.Equals(chapterId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (mangaConnector is null) return [];
        return await mangaConnector.GetChapterImageUrls(chapterId);
    }
}
