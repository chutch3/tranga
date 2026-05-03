using API;
using Newtonsoft.Json;
using Xunit;

namespace Tests;

public class TrangaSettingsTests
{
    [Fact]
    public void NewSettings_ShouldInitializeWithSmartDefaults()
    {
        // Arrange & Act
        var settings = new TrangaSettings();

        // Assert - Verify the "Safety Net" logic worked
        // If running locally on Linux, it should be /usr/share or ./debug
        Assert.NotNull(settings.AppData);
        Assert.Equal(40, settings.ImageCompression);
        Assert.Equal("en", settings.DownloadLanguage);
    }

    [Fact]
    public void WorkingDirectory_ShouldReflectCustomAppData()
    {
        // Arrange
        var settings = new TrangaSettings { AppData = "/tmp/custom_manga" };

        // Act & Assert
        // This confirms that changing AppData correctly flows down to the sub-paths
        Assert.Equal("/tmp/custom_manga/tranga-api", settings.WorkingDirectory);
        Assert.Equal("/tmp/custom_manga/tranga-api/settings.json", settings.SettingsFilePath);
    }

    [Fact]
    public void Serialization_ShouldRespectCustomPaths()
    {
        // Arrange
        var original = new TrangaSettings { AppData = "/mnt/nas/tranga" };

        // Act
        var json = JsonConvert.SerializeObject(original);
        var deserialized = JsonConvert.DeserializeObject<TrangaSettings>(json);

        // Assert
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
