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

    public override (Manga, MangaConnectorId<Manga>)[] SearchManga(string mangaSearchName)
    {
        Log.Debug("Searching Manga on all enabled connectors:");
        MangaConnector[] enabledConnectors = GetConnectors().Where(c => c.Enabled).ToArray();
        Log.Debug(string.Join(", ", enabledConnectors.Select(c => c.Name)));

        Task<(Manga, MangaConnectorId<Manga>)[]>[] tasks =
            enabledConnectors.Select(c => new Task<(Manga, MangaConnectorId<Manga>)[]>(() => c.SearchManga(mangaSearchName))).ToArray();
        foreach (Task<(Manga, MangaConnectorId<Manga>)[]> task in tasks)
            task.Start();

        do
        {
            Thread.Sleep(500);
            Log.DebugFormat("Waiting for search to finish: {0}", tasks.Count(t => !t.IsCompleted));
        } while (tasks.Any(t => !t.IsCompleted));

        (Manga, MangaConnectorId<Manga>)[] ret = tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : []).SelectMany(i => i).ToArray();
        Log.DebugFormat("Got {0} results.", ret.Length);
        return ret;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromUrl(string url)
    {
        MangaConnector? mc = GetConnectors().FirstOrDefault(c => c.UrlMatchesConnector(url));
        return mc?.GetMangaFromUrl(url) ?? null;
    }

    public override (Manga, MangaConnectorId<Manga>)? GetMangaFromId(string mangaIdOnSite)
    {
        return null;
    }

    public override (Chapter, MangaConnectorId<Chapter>)[] GetChapters(MangaConnectorId<Manga> mangaId,
        string? language = null)
    {
        MangaConnector? mangaConnector = GetConnectors().FirstOrDefault(c => c.Name.Equals(mangaId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (mangaConnector is null) return [];
        return mangaConnector.GetChapters(mangaId, language);
    }

    internal override string[] GetChapterImageUrls(MangaConnectorId<Chapter> chapterId)
    {
        MangaConnector? mangaConnector = GetConnectors().FirstOrDefault(c => c.Name.Equals(chapterId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (mangaConnector is null) return [];
        return mangaConnector.GetChapterImageUrls(chapterId);
    }
}
