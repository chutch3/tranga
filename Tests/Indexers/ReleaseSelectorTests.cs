using API.Indexers;
using Xunit;

namespace API.Tests.Indexers;

public class ReleaseSelectorTests
{
    private static IndexerSearchResult R(string title, int seeders) =>
        new(title, "magnet:?xt=urn:btih:" + title.GetHashCode(), 1000, seeders, "ix");

    [Fact]
    public void SelectBest_ReturnsHighestSeeders_AmongValidReleases()
    {
        var selector = new ReleaseSelector { MinSeeders = 1, PreferredTokens = [], BlockedTokens = [] };

        var best = selector.SelectBest([R("a", 5), R("b", 20), R("c", 12)]);

        Assert.Equal("b", best?.Title);
    }

    [Fact]
    public void SelectBest_ExcludesBlockedTokens()
    {
        var selector = new ReleaseSelector { MinSeeders = 1, PreferredTokens = [], BlockedTokens = ["cbr"] };

        var best = selector.SelectBest([R("Saga 60.cbr", 100), R("Saga 60.cbz", 10)]);

        Assert.Equal("Saga 60.cbz", best?.Title);
    }

    [Fact]
    public void SelectBest_PrefersPreferredTokens_OverHigherSeeders()
    {
        var selector = new ReleaseSelector { MinSeeders = 1, PreferredTokens = ["cbz"], BlockedTokens = [] };

        var best = selector.SelectBest([R("Saga 60.unknown", 100), R("Saga 60.cbz", 10)]);

        Assert.Equal("Saga 60.cbz", best?.Title);
    }

    [Fact]
    public void SelectBest_ReturnsNull_WhenNoReleaseMeetsSeederFloor()
    {
        var selector = new ReleaseSelector { MinSeeders = 10, PreferredTokens = [], BlockedTokens = [] };

        var best = selector.SelectBest([R("a", 2), R("b", 5)]);

        Assert.Null(best);
    }

    [Fact]
    public void SelectBest_ReturnsNull_OnEmptyInput()
    {
        var selector = new ReleaseSelector();

        var best = selector.SelectBest([]);

        Assert.Null(best);
    }
}
