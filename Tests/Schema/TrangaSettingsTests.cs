using API;
using Newtonsoft.Json;
using Xunit;

namespace API.Tests.Schema;

public class TrangaSettingsTests
{
    [Fact]
    public void NewSettings_ShouldInitializeWithSmartDefaults()
    {
        var settings = new TrangaSettings();

        // If running locally on Linux, it should be /usr/share or ./debug
        Assert.NotNull(settings.AppData);
        Assert.Equal(40, settings.ImageCompression);
        Assert.Equal("en", settings.DownloadLanguage);
    }

    [Fact]
    public void Cors_DefaultsToAllowAnyOrigin()
    {
        var settings = new TrangaSettings();

        Assert.Empty(settings.CorsAllowedOrigins);
        Assert.True(settings.CorsAllowAnyOrigin);
    }

    [Fact]
    public void Cors_WhenOriginsConfigured_DoesNotAllowAnyOrigin()
    {
        var settings = new TrangaSettings { CorsAllowedOrigins = ["https://my-frontend.test"] };

        Assert.False(settings.CorsAllowAnyOrigin);
        Assert.Contains("https://my-frontend.test", settings.CorsAllowedOrigins);
    }

    [Fact]
    public void TorrentPath_DefaultsToDisabled()
    {
        var settings = new TrangaSettings();

        Assert.False(settings.IndexerConfigured);
        Assert.False(settings.TorrentClientConfigured);
    }

    [Fact]
    public void TorrentPath_BecomesEnabled_WhenProwlarrSyncAndClientConfigured()
    {
        var settings = new TrangaSettings
        {
            ProwlarrBaseUrl = "http://prowlarr:9696",
            ProwlarrApiKey = "secret",
            TorrentClientBaseUrl = "http://qbittorrent:8080",
            TorrentClientUsername = "admin",
            TorrentClientPassword = "p"
        };

        Assert.True(settings.ProwlarrConfigured);
        Assert.True(settings.IndexerConfigured);
        Assert.True(settings.TorrentClientConfigured);
    }

    [Fact]
    public void Indexer_BecomesConfigured_WithManualIndexersAlone_NoProwlarr()
    {
        var settings = new TrangaSettings
        {
            ManualIndexers = [new API.Indexers.ManualIndexerConfig("Tracker", "http://t.test/api", "k", [8000])]
        };

        Assert.False(settings.ProwlarrConfigured);
        Assert.True(settings.IndexerConfigured); // manual indexers are sufficient — not coupled to Prowlarr
    }

    [Fact]
    public void TorrentStagingDirectory_LivesUnderWorkingDirectory()
    {
        var settings = new TrangaSettings { AppData = "/tmp/x" };

        Assert.Equal("/tmp/x/tranga-api/torrent-staging", settings.TorrentStagingDirectory);
    }

    [Fact]
    public void WorkingDirectory_ShouldReflectCustomAppData()
    {
        var settings = new TrangaSettings { AppData = "/tmp/custom_manga" };

        // This confirms that changing AppData correctly flows down to the sub-paths
        Assert.Equal("/tmp/custom_manga/tranga-api", settings.WorkingDirectory);
        Assert.Equal("/tmp/custom_manga/tranga-api/settings.json", settings.SettingsFilePath);
    }

    [Fact]
    public void Serialization_ShouldRespectCustomPaths()
    {
        var original = new TrangaSettings { AppData = "/mnt/nas/tranga" };

        var json = JsonConvert.SerializeObject(original);
        var deserialized = JsonConvert.DeserializeObject<TrangaSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("/mnt/nas/tranga", deserialized.AppData);
    }

    [Fact]
    public void Setters_ShouldTriggerSaveWhenUsingMethods()
    {
        // Note: If you still have the "Set..." methods in the class,
        // you can verify they work as expected.
        // Since we are using DI, we usually prefer setting properties directly
        // and calling .Save() once, but testing the methods is good too.

        var settings = new TrangaSettings { AppData = "./test_save" };
        Directory.CreateDirectory(settings.WorkingDirectory);

        settings.SetDownloadLanguage("jp");

        Assert.Equal("jp", settings.DownloadLanguage);
        Assert.True(File.Exists(settings.SettingsFilePath));

        // Cleanup
        Directory.Delete("./test_save", true);
    }

    [Fact]
    public void DefaultPath_ShouldBeLinuxStandard_WhenOnLinux()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
        {
            var settings = new TrangaSettings();
            // This ensures your "Safety Net" is exactly what you expect for Swarm
            bool isExpectedPath = settings.AppData == "/usr/share" || settings.AppData == "./debug";
            Assert.True(isExpectedPath);
        }
    }
}
