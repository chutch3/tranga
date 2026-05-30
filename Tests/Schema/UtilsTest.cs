using API;

namespace API.Tests.Schema;

public class UtilsTest
{
    /// <summary>Random whose Next(min,max) always returns the largest valid value (max-1).</summary>
    private sealed class MaxRandom : Random
    {
        public override int Next(int minValue, int maxValue) => maxValue - 1;
    }

    [Fact]
    public void RandomElement_CanReturnLastElement()
    {
        var items = new[] { "a", "b", "c" };

        // Using a Random that picks the largest index, the LAST element must be reachable.
        string picked = items.RandomElement(new MaxRandom());

        Assert.Equal("c", picked);
    }

    [Fact]
    public void RandomElement_ReturnsAnElementFromTheList()
    {
        var items = new[] { 10, 20, 30 };

        for (int i = 0; i < 50; i++)
            Assert.Contains(items.RandomElement(), items);
    }

    [Theory]
    [InlineData("https://localhost", "", "https://localhost")]
    [InlineData("https://localhost/", "", "https://localhost")]
    [InlineData("https://localhost", "wow", "https://localhost/wow")]
    [InlineData("https://localhost", "/wow", "https://localhost/wow")]
    [InlineData("https://localhost/", "wow", "https://localhost/wow")]
    [InlineData("https://localhost/", "/wow", "https://localhost/wow")]
    [InlineData("https://localhost/abc", "wow", "https://localhost/abc/wow")]
    [InlineData("https://localhost/abc", "/wow", "https://localhost/abc/wow")]
    [InlineData("https://localhost/abc/", "wow", "https://localhost/abc/wow")]
    [InlineData("https://localhost/abc/", "/wow", "https://localhost/abc/wow")]
    public void BuildUri_BuildsCorrectUri(string basePath, string relativePath, string fullPath)
    {
        Assert.Equal(new Uri(fullPath), Utils.BuildUri(basePath, relativePath));
    }
}