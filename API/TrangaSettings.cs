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

    public string DefaultDownloadLocation => Environment.GetEnvironmentVariable("DOWNLOAD_LOCATION") ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "/Series" : Path.Join(Directory.GetCurrentDirectory(), "Series"));
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
    /// Names of <see cref="API.MangaConnectors.SeriesSource"/> instances that have been explicitly disabled.
    /// Connectors absent from this set are considered enabled (opt-out model).
    /// </summary>
    public HashSet<string> DisabledConnectors { get; set; } = [];

    /// <summary>
    /// Origins permitted for browser CORS requests. Empty (the default) preserves the historical
    /// "allow any origin" behaviour; populate it to restrict the API to specific frontends.
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = [];

    /// <summary>True when no explicit origins are configured, i.e. any origin is allowed.</summary>
    [JsonIgnore] public bool CorsAllowAnyOrigin => CorsAllowedOrigins.Length == 0;

    // ---------- Indexer (Prowlarr) ----------
    // Indexers follow the *arr model: a list of Torznab/Newznab endpoints. You can ADD them by hand
    // (ManualIndexers) and/or let Prowlarr SYNC them (Prowlarr* below). Prowlarr is one source of
    // indexers, not the indexer itself.

    /// <summary>Manually-configured Torznab/Newznab indexers.</summary>
    public List<API.Indexers.ManualIndexerConfig> ManualIndexers { get; set; } = [];

    /// <summary>Base URL of a Prowlarr instance to sync indexers from (e.g. http://prowlarr:9696). Empty disables Prowlarr sync.</summary>
    public string ProwlarrBaseUrl { get; set; } = "";
    /// <summary>Prowlarr API key (X-Api-Key header).</summary>
    public string ProwlarrApiKey { get; set; } = "";
    /// <summary>Comic category IDs applied to indexer searches. Default 8000 = Comics (Newznab convention).</summary>
    public int[] IndexerComicCategories { get; set; } = [8000];

    [JsonIgnore] public bool ProwlarrConfigured =>
        !string.IsNullOrWhiteSpace(ProwlarrBaseUrl) && !string.IsNullOrWhiteSpace(ProwlarrApiKey);

    /// <summary>True when at least one indexer source (manual or Prowlarr sync) is configured.</summary>
    [JsonIgnore] public bool IndexerConfigured => ManualIndexers.Count > 0 || ProwlarrConfigured;

    // ---------- Torrent client (qBittorrent) ----------
    /// <summary>Base URL of the torrent client's Web API (e.g. http://qbittorrent:8080). Empty disables torrents.</summary>
    public string TorrentClientBaseUrl { get; set; } = "";
    public string TorrentClientUsername { get; set; } = "";
    public string TorrentClientPassword { get; set; } = "";

    /// <summary>Directory the torrent client downloads into; the completion worker moves files out of here.</summary>
    [JsonIgnore] public string TorrentStagingDirectory => Path.Join(WorkingDirectory, "torrent-staging");

    [JsonIgnore] public bool TorrentClientConfigured => !string.IsNullOrWhiteSpace(TorrentClientBaseUrl);

    // ---------- Metron metadata (metron.cloud) ----------
    /// <summary>Metron account username (HTTP Basic auth). Empty disables Metron lookups.</summary>
    public string MetronUsername { get; set; } = "";
    /// <summary>Metron account password (HTTP Basic auth).</summary>
    public string MetronPassword { get; set; } = "";

    // ---------- Release selection (v1: simple scoring) ----------
    public int ReleaseMinSeeders { get; set; } = 2;
    public string[] ReleasePreferredTokens { get; set; } = ["cbz"];
    public string[] ReleaseBlockedTokens { get; set; } = ["cbr", "pdf"];

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

    public void SetMetronCredentials(string username, string password)
    {
        this.MetronUsername = username;
        this.MetronPassword = password;
        Save();
    }

    public void SetProwlarr(string baseUrl, string apiKey)
    {
        this.ProwlarrBaseUrl = baseUrl;
        this.ProwlarrApiKey = apiKey;
        Save();
    }

    public void SetTorrentClient(string baseUrl, string username, string password)
    {
        this.TorrentClientBaseUrl = baseUrl;
        this.TorrentClientUsername = username;
        this.TorrentClientPassword = password;
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
    public void ApplyDisabledConnectors(IEnumerable<MangaConnectors.SeriesSource> connectors)
    {
        foreach (var connector in connectors)
            connector.Enabled = !DisabledConnectors.Contains(connector.Name);
    }
}
