using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.SeriesContext;
using API.Services;
using API.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Tests.Controllers;

public class MetadataSourceControllerAniListTests
{
    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private MetadataSourceController CreateController(
        SeriesContext ctx,
        IMangaDexSearchService? mangaDexService = null,
        IAniListSearchService? aniListService = null)
    {
        var mockMangaDex = mangaDexService ?? new Mock<IMangaDexSearchService>().Object;
        var mockAniList = aniListService ?? new Mock<IAniListSearchService>().Object;
        var mockWorkerQueue = new Mock<IWorkerQueue>();
        var controller = new MetadataSourceController(ctx, mockMangaDex, mockAniList, mockWorkerQueue.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static API.Schema.SeriesContext.Series MakeTestManga(string name = "Test Series")
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task GetCandidates_WithSourceAniList_CallsAniListServiceNotMangaDex()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var mockMangaDex = new Mock<IMangaDexSearchService>();
        var mockAniList = new Mock<IAniListSearchService>();
        mockAniList.Setup(s => s.SearchAsync("Berserk", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AniListSearchResult
                {
                    AniListId = 12345,
                    Title = "Berserk",
                    Author = "Kentaro Miura",
                    ChapterCount = 364,
                    VolumeCount = 41
                }
            ]);

        var controller = CreateController(ctx, mockMangaDex.Object, mockAniList.Object);
        var result = await controller.GetMetadataSourceCandidates(manga.Key, "Berserk", "anilist");

        var ok = Assert.IsType<Ok<List<MetadataSourceCandidate>>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.NotEmpty(ok.Value);
        Assert.Equal("12345", ok.Value[0].ExternalId);
        Assert.Equal("Berserk", ok.Value[0].Title);

        // MangaDex must NOT have been called
        mockMangaDex.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // AniList must have been called
        mockAniList.Verify(s => s.SearchAsync("Berserk", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCandidates_WithSourceMangadex_CallsMangaDexNotAniList()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("One Piece");
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var mockMangaDex = new Mock<IMangaDexSearchService>();
        mockMangaDex.Setup(s => s.SearchAsync("One Piece", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MangaDexSearchResult
                {
                    MangaDexId = "abc-123",
                    Title = "One Piece",
                    Author = "Oda",
                    ChapterCount = 1100
                }
            ]);
        var mockAniList = new Mock<IAniListSearchService>();

        var controller = CreateController(ctx, mockMangaDex.Object, mockAniList.Object);
        var result = await controller.GetMetadataSourceCandidates(manga.Key, "One Piece", "mangadex");

        var ok = Assert.IsType<Ok<List<MetadataSourceCandidate>>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.NotEmpty(ok.Value);

        // AniList must NOT have been called
        mockAniList.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // MangaDex must have been called
        mockMangaDex.Verify(s => s.SearchAsync("One Piece", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetCandidates_WithNoSourceParam_DefaultsToMangaDex()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Naruto");
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var mockMangaDex = new Mock<IMangaDexSearchService>();
        mockMangaDex.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var mockAniList = new Mock<IAniListSearchService>();

        var controller = CreateController(ctx, mockMangaDex.Object, mockAniList.Object);
        // Call with no source (default)
        var result = await controller.GetMetadataSourceCandidates(manga.Key, "Naruto");

        mockMangaDex.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        mockAniList.Verify(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCandidates_WithSourceAniList_UnknownManga_ReturnsNotFound()
    {
        using var ctx = CreateContext();

        var mockAniList = new Mock<IAniListSearchService>();
        var controller = CreateController(ctx, aniListService: mockAniList.Object);
        var result = await controller.GetMetadataSourceCandidates("nonexistent", "Anything", "anilist");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task GetCandidates_WithSourceAniList_ReturnsScoresAndExternalIdAsString()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var mockAniList = new Mock<IAniListSearchService>();
        mockAniList.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AniListSearchResult
                {
                    AniListId = 99,
                    Title = "Berserk",
                    Author = null,
                    ChapterCount = 364,
                    VolumeCount = 41
                }
            ]);

        var controller = CreateController(ctx, aniListService: mockAniList.Object);
        var result = await controller.GetMetadataSourceCandidates(manga.Key, "Berserk", "anilist");

        var ok = Assert.IsType<Ok<List<MetadataSourceCandidate>>>(result.Result);
        Assert.Single(ok.Value!);
        Assert.Equal("99", ok.Value[0].ExternalId);
        Assert.True(ok.Value[0].Score > 0f, "Score must be positive");
        Assert.NotNull(ok.Value[0].MatchReasons);
    }
}
