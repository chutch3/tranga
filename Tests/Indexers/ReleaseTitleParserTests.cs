using API.Indexers;
using Xunit;

namespace API.Tests.Indexers;

public class ReleaseTitleParserTests
{
    [Theory]
    // title                                                  | series              | issue  | year
    [InlineData("Saga 060 (2024) (Digital) (Zone-Empire)",      "Saga",               "60",   2024)]
    [InlineData("The Walking Dead 100 (2012)",                  "The Walking Dead",   "100",  2012)]
    [InlineData("Batman (2016) 050",                            "Batman",             "50",   2016)]
    [InlineData("Invincible Compendium 001 (2011)",             "Invincible Compendium", "1", 2011)]
    [InlineData("Saga #60",                                     "Saga",               "60",   null)]
    [InlineData("Monstress Vol. 1 (2016)",                      "Monstress",          "1",    2016)]
    [InlineData("Saga 60.5 (2024)",                             "Saga",               "60.5", 2024)]
    [InlineData("Some Ongoing Comic",                           "Some Ongoing Comic", null,   null)]
    public void Parse_ExtractsSeriesIssueYear(string title, string expectedSeries, string? expectedIssue, int? expectedYear)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal(expectedSeries, parsed.SeriesTitle);
        Assert.Equal(expectedIssue, parsed.IssueNumber);
        Assert.Equal(expectedYear, parsed.Year);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptySeries()
    {
        var parsed = ReleaseTitleParser.Parse("");
        Assert.Equal("", parsed.SeriesTitle);
        Assert.Null(parsed.IssueNumber);
    }

    [Fact]
    public void Parse_IssueIsValidChapterNumber_AcceptedByChapterCtor()
    {
        // The parsed issue string must satisfy Chapter's chapter-number contract.
        var parsed = ReleaseTitleParser.Parse("Saga 060 (2024)");
        Assert.Equal("60", parsed.IssueNumber);
    }
}
