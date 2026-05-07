using System.IO.Compression;
using API;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
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

    private MangaContext CreateMangaContext(DbContextOptions<MangaContext> options) => new(options);

    private IServiceScope CreateScope(MangaContext mangaContext)
    {
        var actionsContext = new ActionsContext(
            new DbContextOptionsBuilder<ActionsContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(MangaContext))).Returns(mangaContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
        return scope.Object;
    }

    // Downloads the first two pages of a MangaDex chapter into a cbz at destPath,
    // mirroring how DownloadChapterFromMangaconnectorWorker names pages (0.jpg, 1.jpg, ...).
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

    // Berserk: the real MangaDex API has full volume data.
    // This test verifies the live resolver returns the correct chapter→volume mapping
    // without involving the worker or a database — the resolver is the thing being integration-tested.
    [Fact]
    public async Task Berserk_MangaDexResolver_ReturnsCorrectVolumeMapping()
    {
        const string berserkUuid = "801513ba-a712-498c-8f57-cae55b38cc92";

        var library = new FileLibrary(_tempDir, "Integration Library");
        var manga = new Manga("Berserk", "Dark fantasy", "url", MangaReleaseStatus.Continuing,
            [], [], [], [], library);
        manga.MangaConnectorIds.Add(
            new MangaConnectorId<Manga>(manga, "MangaDex", berserkUuid, null));

        var resolver = new MangaDexVolumeResolver(_httpClient);
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.True(map.TryGetValue("1", out int vol), "Chapter '1' should be in the MangaDex volume map");
        Assert.Equal(5, vol);
    }

    // One Punch-Man is DMCA'd on MangaDex — the resolver returns an empty map.
    // The color heuristic must take over. We download Chainsaw Man chapter 1 images
    // (UUID: 73af4d8d-1532-4a72-b1b9-8f4e5cd295c9) as stand-in content: its first page
    // is a color splash (avgDiff ≈ 131) so the heuristic fires and assigns volume 1.
    [Fact]
    public async Task OnePunchMan_DmcaOnMangaDex_ColorHeuristicAssignsVolume()
    {
        const string opmMangaDexUuid = "d8a959f7-648e-4c8d-8f23-f1f3f8e129f3";
        const string chainmanChapter1Uuid = "73af4d8d-1532-4a72-b1b9-8f4e5cd295c9";

        string dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        FileLibrary library;
        Manga manga;

        using (var setupDb = CreateMangaContext(dbOptions))
        {
            library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            manga = new Manga("One Punch-Man", "Superhero comedy", "url",
                MangaReleaseStatus.Continuing, [], [], [], [], library);
            manga.MangaConnectorIds.Add(
                new MangaConnectorId<Manga>(manga, "MangaDex", opmMangaDexUuid, null));
            setupDb.Mangas.Add(manga);
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
        var resolver = new MangaDexVolumeResolver(_httpClient);
        var worker = new ResolveMissingVolumesWorker(
            settings, Enumerable.Empty<MangaConnector>(), resolver);
        await worker.DoWork(CreateScope(workerDb));

        using var queryDb = CreateMangaContext(dbOptions);
        var result = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal(1, result.VolumeNumber);
    }
}
