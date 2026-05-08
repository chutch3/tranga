using System.Runtime.InteropServices;
using API.Workers;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace API;

public class TrangaSettings
{
    private static string ComputeDefaultAppData() =>
        Environment.GetEnvironmentVariable("APP_DATA") ??
        (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? (bool.Parse(Environment.GetEnvironmentVariable("DEBUG") ?? "false") ? "./debug" : "/usr/share")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    // This property will be saved to and loaded from settings.json
    public string AppData { get; set; } = ComputeDefaultAppData();

    [JsonIgnore] public int Port => int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "6531");
    [JsonIgnore] public bool Debug => bool.Parse(Environment.GetEnvironmentVariable("DEBUG") ?? "false");

    [JsonIgnore] public string WorkingDirectory => Path.Join(AppData, "tranga-api");
    [JsonIgnore] public string SettingsFilePath => Path.Join(WorkingDirectory, "settings.json");
    [JsonIgnore] public string CoverImageCache => Path.Join(WorkingDirectory, "imageCache");
    [JsonIgnore] public string CoverImageCacheOriginal => Path.Join(CoverImageCache, "original");
    [JsonIgnore] public string CoverImageCacheLarge => Path.Join(CoverImageCache, "large");
    [JsonIgnore] public string CoverImageCacheMedium => Path.Join(CoverImageCache, "medium");
    [JsonIgnore] public string CoverImageCacheSmall => Path.Join(CoverImageCache, "small");

    public string DefaultDownloadLocation => Environment.GetEnvironmentVariable("DOWNLOAD_LOCATION") ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "/Manga" : Path.Join(Directory.GetCurrentDirectory(), "Manga"));
    [JsonIgnore] internal static readonly string DefaultUserAgent = $"Tranga/2.0 ({Enum.GetName(Environment.OSVersion.Platform)}; {(Environment.Is64BitOperatingSystem ? "x64" : "")})";
    public string UserAgent { get; set; } = DefaultUserAgent;
    public int ImageCompression{ get; set; } = 40;
    public bool BlackWhiteImages { get; set; } = false;
    public string FlareSolverrUrl { get; set; } = Environment.GetEnvironmentVariable("FLARESOLVERR_URL") ?? string.Empty;
    /// <summary>
    /// Placeholders:
    /// %M Obj Name
    /// %V Volume
    /// %C Chapter
    /// %T Title
    /// %A Author (first in list)
    /// %I Chapter Internal ID
    /// %i Obj Internal ID
    /// %Y Year (Obj)
    ///
    /// ?_(...) replace _ with a value from above:
    /// Everything inside the braces will only be added if the value of %_ is not null
    /// </summary>
    public string ChapterNamingScheme { get; set; } = "%M - ?V(Vol.%V )Ch.%C?T( - %T)";
    public int WorkCycleTimeoutMs { get; set; } = 20000;

    public string DownloadLanguage { get; set; } = "en";

    public int MaxConcurrentDownloads { get; set; } = (int)Math.Max(Environment.ProcessorCount * 0.75, 1); // Minimum of 1 Tasks, maximum of 0.75 per Core

    public int MaxConcurrentWorkers { get; set; } = Math.Max(Environment.ProcessorCount, 4); // Minimum of 4 Tasks, maximum of 1 per Core

    public VolumeResolutionStrategy VolumeResolutionStrategy { get; set; } = VolumeResolutionStrategy.ExactThenGuess;

    public int VolumeResolutionParallelism { get; set; } = 3;

    public LibraryRefreshSetting LibraryRefreshSetting { get; set; } = LibraryRefreshSetting.AfterMangaFinished;

    public int RefreshLibraryWhileDownloadingEveryMinutes { get; set; } = 10;

    /// <summary>
    /// Names of <see cref="API.MangaConnectors.MangaConnector"/> instances that have been explicitly disabled.
    /// Connectors absent from this set are considered enabled (opt-out model).
    /// </summary>
    public HashSet<string> DisabledConnectors { get; set; } = [];

    public TrangaSettings()
    {
        // WorkingDirectory is created by the AppData setter when AppData is overridden
        // (e.g. via object initializer). For the default path it is created in Save().
    }

    public static TrangaSettings Load()
    {
        // 1. Use the "Safety Net" logic to find where the file SHOULD be
        string discoveryPath = Path.Join(ComputeDefaultAppData(), "tranga-api", "settings.json");

        if (!File.Exists(discoveryPath))
        {
            var defaults = new TrangaSettings();
            // Defaults already has AppData set to _defaultAppData
            defaults.Save();
            return defaults;
        }

        // 2. Load the file. If the file has a different "AppData" inside it,
        // the json deserializer will overwrite the default value.
        var json = File.ReadAllText(discoveryPath);
        return JsonConvert.DeserializeObject<TrangaSettings>(json, new StringEnumConverter())
               ?? new TrangaSettings();
    }

    public void Save()
    {
        lock (this) // Good practice now that it's a shared reference in DI
        {
            Directory.CreateDirectory(WorkingDirectory);
            File.WriteAllText(SettingsFilePath, JsonConvert.SerializeObject(this, Formatting.Indented, new StringEnumConverter()));
        }
    }

    public void SetUserAgent(string value)
    {
        this.UserAgent = value;
        Save();
    }

    public void UpdateImageCompression(int value)
    {
        this.ImageCompression = value;
        Save();
    }

    public void SetBlackWhiteImageEnabled(bool enabled)
    {
        this.BlackWhiteImages = enabled;
        Save();
    }

    public void SetChapterNamingScheme(string scheme)
    {
        this.ChapterNamingScheme = scheme;
        Save();
    }

    public void SetFlareSolverrUrl(string url)
    {
        this.FlareSolverrUrl = url;
        Save();
    }

    public void SetDownloadLanguage(string language)
    {
        this.DownloadLanguage = language;
        Save();
    }

    public void SetMaxConcurrentDownloads(int value)
    {
        this.MaxConcurrentDownloads = value;
        Save();
    }

    public void SetMaxConcurrentWorkers(int value)
    {
        this.MaxConcurrentWorkers = value;
        Save();
    }

    public void SetLibraryRefreshSetting(LibraryRefreshSetting setting)
    {
        this.LibraryRefreshSetting = setting;
        Save();
    }

    public void SetRefreshLibraryWhileDownloadingEveryMinutes(int value)
    {
        this.RefreshLibraryWhileDownloadingEveryMinutes = value;
        Save();
    }

    /// <summary>
    /// Persists the enabled/disabled state for a connector by name.
    /// </summary>
    public void SetConnectorEnabled(string connectorName, bool enabled)
    {
        if (enabled)
            DisabledConnectors.Remove(connectorName);
        else
            DisabledConnectors.Add(connectorName);
        Save();
    }

    /// <summary>
    /// Applies the persisted <see cref="DisabledConnectors"/> set to a collection of connector instances.
    /// Call this once at startup after the DI container is built.
    /// </summary>
    public void ApplyDisabledConnectors(IEnumerable<MangaConnectors.MangaConnector> connectors)
    {
        foreach (var connector in connectors)
            connector.Enabled = !DisabledConnectors.Contains(connector.Name);
    }
}
