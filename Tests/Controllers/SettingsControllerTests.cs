using API;
using API.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace API.Tests.Controllers;

public class SettingsControllerTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly TrangaSettings _settings;

    public SettingsControllerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"SettingsTest_{Guid.NewGuid()}");
        _settings = new TrangaSettings { AppData = _tmpDir };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, true);
    }

    private SettingsController CreateController()
    {
        var controller = new SettingsController(_settings);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public void GetSettings_ReturnsCurrentSettingsInstance()
    {
        var result = CreateController().GetSettings();

        var ok = Assert.IsType<Ok<TrangaSettings>>(result);
        Assert.Same(_settings, ok.Value);
    }

    [Fact]
    public void GetUserAgent_ReturnsCurrentUserAgent()
    {
        _settings.UserAgent = "TestAgent/1.0";

        var result = CreateController().GetUserAgent();

        var ok = Assert.IsType<Ok<string>>(result);
        Assert.Equal("TestAgent/1.0", ok.Value);
    }

    [Fact]
    public void SetUserAgent_UpdatesUserAgent()
    {
        CreateController().SetUserAgent("NewAgent/2.0");

        Assert.Equal("NewAgent/2.0", _settings.UserAgent);
    }

    [Fact]
    public void ResetUserAgent_RestoresDefaultUserAgent()
    {
        _settings.UserAgent = "CustomAgent/1.0";

        CreateController().ResetUserAgent();

        Assert.Equal(TrangaSettings.DefaultUserAgent, _settings.UserAgent);
    }

    [Fact]
    public void GetImageCompression_ReturnsCurrentLevel()
    {
        _settings.ImageCompression = 75;

        var result = CreateController().GetImageCompression();

        var ok = Assert.IsType<Ok<int>>(result);
        Assert.Equal(75, ok.Value);
    }

    [Fact]
    public void SetImageCompression_ValidLevel_UpdatesAndReturnsOk()
    {
        var result = CreateController().SetImageCompression(60);

        Assert.IsType<Ok>(result.Result);
        Assert.Equal(60, _settings.ImageCompression);
    }

    [Fact]
    public void SetImageCompression_LevelZero_ReturnsBadRequest()
    {
        var result = CreateController().SetImageCompression(0);

        Assert.IsType<BadRequest>(result.Result);
        Assert.Equal(40, _settings.ImageCompression); // unchanged from default
    }

    [Fact]
    public void SetImageCompression_LevelAbove100_ReturnsBadRequest()
    {
        var result = CreateController().SetImageCompression(101);

        Assert.IsType<BadRequest>(result.Result);
    }

    [Fact]
    public void GetBwImagesToggle_ReturnsCurrentState()
    {
        _settings.BlackWhiteImages = true;

        var result = CreateController().GetBwImagesToggle();

        var ok = Assert.IsType<Ok<bool>>(result);
        Assert.True(ok.Value);
    }

    [Fact]
    public void SetBwImagesToggle_UpdatesState()
    {
        CreateController().SetBwImagesToggle(true);

        Assert.True(_settings.BlackWhiteImages);
    }

    [Fact]
    public void GetCustomNamingScheme_ReturnsCurrentScheme()
    {
        _settings.ChapterNamingScheme = "%M - Ch.%C";

        var result = CreateController().GetCustomNamingScheme();

        var ok = Assert.IsType<Ok<string>>(result);
        Assert.Equal("%M - Ch.%C", ok.Value);
    }

    [Fact]
    public void SetCustomNamingScheme_UpdatesScheme()
    {
        CreateController().SetCustomNamingScheme("?V(Vol.%V/)%M - Ch.%C");

        Assert.Equal("?V(Vol.%V/)%M - Ch.%C", _settings.ChapterNamingScheme);
    }

    [Fact]
    public void GetDownloadLanguage_ReturnsCurrentLanguage()
    {
        _settings.DownloadLanguage = "jp";

        var result = CreateController().GetDownloadLanguage();

        var ok = Assert.IsType<Ok<string>>(result);
        Assert.Equal("jp", ok.Value);
    }

    [Fact]
    public void SetDownloadLanguage_UpdatesLanguage()
    {
        CreateController().SetDownloadLanguage("fr");

        Assert.Equal("fr", _settings.DownloadLanguage);
    }

    [Fact]
    public void SetFlareSolverrUrl_UpdatesUrl()
    {
        CreateController().SetFlareSolverrUrl("http://localhost:8191");

        Assert.Equal("http://localhost:8191", _settings.FlareSolverrUrl);
    }

    [Fact]
    public void ClearFlareSolverrUrl_SetsUrlToEmpty()
    {
        _settings.FlareSolverrUrl = "http://localhost:8191";

        CreateController().ClearFlareSolverrUrl();

        Assert.Equal(string.Empty, _settings.FlareSolverrUrl);
    }

    [Fact]
    public void SetMetron_PersistsCredentials()
    {
        CreateController().SetMetron(new API.Controllers.Requests.SetMetronRecord { Username = "u", Password = "p" });

        Assert.Equal("u", _settings.MetronUsername);
        Assert.Equal("p", _settings.MetronPassword);
    }

    [Fact]
    public void ClearMetron_EmptiesCredentials()
    {
        _settings.MetronUsername = "u";
        _settings.MetronPassword = "p";

        CreateController().ClearMetron();

        Assert.Equal(string.Empty, _settings.MetronUsername);
        Assert.Equal(string.Empty, _settings.MetronPassword);
    }

    [Fact]
    public void SetProwlarr_PersistsAndEnablesIndexer()
    {
        CreateController().SetProwlarr(new API.Controllers.Requests.SetProwlarrRecord { BaseUrl = "http://prowlarr:9696", ApiKey = "k" });

        Assert.Equal("http://prowlarr:9696", _settings.ProwlarrBaseUrl);
        Assert.Equal("k", _settings.ProwlarrApiKey);
        Assert.True(_settings.ProwlarrConfigured);
        Assert.True(_settings.IndexerConfigured);
    }

    [Fact]
    public void SetTorrentClient_PersistsAndEnablesClient()
    {
        CreateController().SetTorrentClient(new API.Controllers.Requests.SetTorrentClientRecord
        {
            BaseUrl = "http://qbittorrent:8080", Username = "admin", Password = "pw"
        });

        Assert.Equal("http://qbittorrent:8080", _settings.TorrentClientBaseUrl);
        Assert.Equal("admin", _settings.TorrentClientUsername);
        Assert.True(_settings.TorrentClientConfigured);
    }

    [Fact]
    public void ClearTorrentClient_DisablesClient()
    {
        _settings.TorrentClientBaseUrl = "http://qbittorrent:8080";

        CreateController().ClearTorrentClient();

        Assert.Equal(string.Empty, _settings.TorrentClientBaseUrl);
        Assert.False(_settings.TorrentClientConfigured);
    }
}
