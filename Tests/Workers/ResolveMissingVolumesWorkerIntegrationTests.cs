using System.Collections.Concurrent;
using System.IO.Compression;
using API;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Services;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace API.Tests.Workers;

[Trait("Category", "Integration")]
public class ResolveMissingVolumesWorkerIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _httpClient = new();
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"TrangaIntegration_{Guid.NewGuid()}");

    public Task InitializeAsync()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Tranga-Integration-Tests/1.0");
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        return Task.CompletedTask;
    }

    private SeriesContext CreateMangaContext(DbContextOptions<SeriesContext> options) => new(options);

    private IServiceScope CreateScope(SeriesContext mangaContext)
    {
        var actionsContext = new ActionsContext(
            new DbContextOptionsBuilder<ActionsContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(SeriesContext))).Returns(mangaContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
        return scope.Object;
    }

    private static readonly Mock<IMangaDexSearchService> MockSearchService = new();

    static ResolveMissingVolumesWorkerIntegrationTests()
    {
        MockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>());
        MockSearchService
            .Setup(s => s.GetChapterToVolumeMapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());
    }

    private ResolveMissingVolumesForMangaWorker MakePoolWorker(string mangaKey, TrangaSettings settings, IMangaDexVolumeResolver resolver) =>
        new(new ConcurrentQueue<string>([mangaKey]), settings, resolver, MockSearchService.Object);

    // Downloads the first two pages of a MangaDex chapter into a cbz at destPath.
    private async Task DownloadMangaDexChapterAsCbz(string mangadexChapterId, string destPath)
    {
        var serverJson = JObject.Parse(
            await _httpClient.GetStringAsync(
                $"https://api.mangadex.org/at-home/server/{mangadexChapterId}"));

        string baseUrl = serverJson["baseUrl"]!.ToString();
        string hash = serverJson["chapter"]!["hash"]!.ToString();
        var pages = serverJson["chapter"]!["data"]!.ToObject<string[]>()!;

        using var zip = ZipFile.Open(destPath, ZipArchiveMode.Create);
        for (int i = 0; i < Math.Min(pages.Length, 2); i++)
        {
            var bytes = await _httpClient.GetByteArrayAsync(
                $"{baseUrl}/data/{hash}/{pages[i]}");
            using var entry = zip.CreateEntry($"{i}.jpg").Open();
            await entry.WriteAsync(bytes);
        }
    }

    // Berserk ch "1" → volume 5 per MangaDex aggregate.
    [Fact]
    public async Task Berserk_ExactOnlyStrategy_WorkerPersistsVolumeToDatabase()
    {
        const string berserkUuid = "801513ba-a712-498c-8f57-cae55b38cc92";
        string dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName).Options;

        string mangaKey;
        using (var setupDb = CreateMangaContext(dbOptions))
        {
            var library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            var manga = new Series("Berserk", "Dark fantasy", "url", SeriesReleaseStatus.Continuing,
                [], [], [], [], library);
            manga.SourceIds.Add(
                new SourceId<Series>(manga, "MangaDex", berserkUuid, null));
            setupDb.Series.Add(manga);
            setupDb.Chapters.Add(new Chapter(manga, "1", null, "Black Swordsman")
                { Downloaded = true, FileName = "berserk_ch1.cbz" });
            await setupDb.SaveChangesAsync();
            mangaKey = manga.Key;
        }

        using var workerDb = CreateMangaContext(dbOptions);
        var settings = new TrangaSettings
            { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly, AppData = _tempDir };
        await MakePoolWorker(mangaKey, settings, new MangaDexVolumeResolver(_httpClient))
            .DoWork(CreateScope(workerDb));

        using var queryDb = CreateMangaContext(dbOptions);
        var result = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal(5, result.VolumeNumber);
    }

    // Berserk ch "0.01" on MangaDex → stored as "0.1" by Chapter constructor.
    // The resolver must normalize its keys the same way so TryGetValue succeeds.
    [Fact]
    public async Task Berserk_ChapterWithLeadingZeroDecimal_NormalizationPipelineResolvesVolume()
    {
        const string berserkUuid = "801513ba-a712-498c-8f57-cae55b38cc92";
        string dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName).Options;

        string mangaKey;
        using (var setupDb = CreateMangaContext(dbOptions))
        {
            var library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            var manga = new Series("Berserk", "Dark fantasy", "url", SeriesReleaseStatus.Continuing,
                [], [], [], [], library);
            manga.SourceIds.Add(
                new SourceId<Series>(manga, "MangaDex", berserkUuid, null));
            setupDb.Series.Add(manga);
            // Constructor normalizes "0.01" → "0.1"
            setupDb.Chapters.Add(new Chapter(manga, "0.01", null, "The Black Swordsman")
                { Downloaded = true, FileName = "berserk_ch001.cbz" });
            await setupDb.SaveChangesAsync();
            mangaKey = manga.Key;
        }

        using var workerDb = CreateMangaContext(dbOptions);
        var settings = new TrangaSettings
            { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly, AppData = _tempDir };
        await MakePoolWorker(mangaKey, settings, new MangaDexVolumeResolver(_httpClient))
            .DoWork(CreateScope(workerDb));

        using var queryDb = CreateMangaContext(dbOptions);
        var result = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "0.1");
        Assert.Equal(1, result.VolumeNumber);
    }

    // Verify the live resolver returns the correct chapter→volume mapping without involving the worker.
    [Fact]
    public async Task Berserk_MangaDexResolver_ReturnsCorrectVolumeMapping()
    {
        const string berserkUuid = "801513ba-a712-498c-8f57-cae55b38cc92";

        var library = new FileLibrary(_tempDir, "Integration Library");
        var manga = new Series("Berserk", "Dark fantasy", "url", SeriesReleaseStatus.Continuing,
            [], [], [], [], library);
        manga.SourceIds.Add(
            new SourceId<Series>(manga, "MangaDex", berserkUuid, null));

        var resolver = new MangaDexVolumeResolver(_httpClient);
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.True(map.TryGetValue("1", out int vol), "Chapter '1' should be in the MangaDex volume map");
        Assert.Equal(5, vol);
    }

    // One Punch-Man is DMCA'd on MangaDex — resolver returns empty, color heuristic takes over.
    [Fact]
    public async Task OnePunchMan_DmcaOnMangaDex_ColorHeuristicAssignsVolume()
    {
        const string opmMangaDexUuid = "d8a959f7-648e-4c8d-8f23-f1f3f8e129f3";
        const string chainmanChapter1Uuid = "73af4d8d-1532-4a72-b1b9-8f4e5cd295c9";

        string dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        FileLibrary library;
        Series manga;

        using (var setupDb = CreateMangaContext(dbOptions))
        {
            library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            manga = new Series("One Punch-Man", "Superhero comedy", "url",
                SeriesReleaseStatus.Continuing, [], [], [], [], library);
            manga.SourceIds.Add(
                new SourceId<Series>(manga, "MangaDex", opmMangaDexUuid, null));
            setupDb.Series.Add(manga);
            setupDb.Chapters.Add(new Chapter(manga, "1", null, "Punch 1")
                { Downloaded = true, FileName = "chap1.cbz" });
            await setupDb.SaveChangesAsync();
        }

        string mangaDir = Path.Combine(_tempDir, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        await DownloadMangaDexChapterAsCbz(chainmanChapter1Uuid, Path.Combine(mangaDir, "chap1.cbz"));

        using var workerDb = CreateMangaContext(dbOptions);
        var settings = new TrangaSettings
            { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess, AppData = _tempDir };
        await MakePoolWorker(manga.Key, settings, new MangaDexVolumeResolver(_httpClient))
            .DoWork(CreateScope(workerDb));

        using var queryDb = CreateMangaContext(dbOptions);
        var result = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal(1, result.VolumeNumber);
    }
}
