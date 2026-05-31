using API.Acquirers;
using API.Indexers;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.TorrentClients;
using API.Workers.PeriodicWorkers;
using log4net;
using Microsoft.Extensions.DependencyInjection;

namespace API.Extensions;

/// <summary>
/// DI registration helpers. Keep Program.cs lean by grouping coherent registration blocks here.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the torrent-based chapter acquisition path (indexer, torrent client, acquirer,
    /// completion worker) when both <see cref="TrangaSettings.IndexerConfigured"/> and
    /// <see cref="TrangaSettings.TorrentClientConfigured"/> are true. Otherwise no-ops.
    /// </summary>
    public static IServiceCollection AddTorrentAcquisitionPath(this IServiceCollection services, TrangaSettings settings, ILog log)
    {
        if (!settings.IndexerConfigured || !settings.TorrentClientConfigured)
        {
            log.Info("Torrent acquisition path disabled (no indexer source or torrent client configured).");
            return services;
        }

        log.Info("Indexer source and torrent client are configured — registering torrent acquisition path.");

        // Indexer providers: manual (settings) and/or Prowlarr sync. Each yields Torznab indexers.
        if (settings.ManualIndexers.Count > 0)
            services.AddSingleton<IIndexerProvider>(sp =>
                new ConfiguredIndexerProvider(
                    new HttpClient(sp.GetRequiredService<RateLimitHandler>(), disposeHandler: false),
                    settings.ManualIndexers));

        if (settings.ProwlarrConfigured)
            services.AddSingleton<IIndexerProvider>(sp =>
                new ProwlarrIndexerProvider(
                    new HttpClient(sp.GetRequiredService<RateLimitHandler>(), disposeHandler: false),
                    settings.ProwlarrBaseUrl,
                    settings.ProwlarrApiKey,
                    settings.IndexerComicCategories));

        // Aggregate search surface over all providers.
        services.AddSingleton<IIndexerClient>(sp =>
            new AggregateIndexerSearch(sp.GetServices<IIndexerProvider>()));

        // The user-facing torrent-backed series source. Registering it as a SeriesSource makes it
        // appear in search + chapter discovery alongside the scrape connectors; its Kind=Torrent
        // routes its chapters through the torrent acquirer.
        services.AddSingleton<SeriesSource>(sp =>
            new IndexerBackedSeriesSource(sp.GetRequiredService<IIndexerClient>(), settings));

        services.AddSingleton<ITorrentClient>(sp =>
        {
            var rl = sp.GetRequiredService<RateLimitHandler>();
            return new QBittorrentClient(
                new HttpClient(rl, disposeHandler: false),
                settings.TorrentClientBaseUrl,
                settings.TorrentClientUsername,
                settings.TorrentClientPassword);
        });

        services.AddSingleton(_ => new ReleaseSelector
        {
            MinSeeders = settings.ReleaseMinSeeders,
            PreferredTokens = settings.ReleasePreferredTokens,
            BlockedTokens = settings.ReleaseBlockedTokens
        });

        services.AddSingleton<IChapterAcquirer>(sp =>
            new TorrentAcquirer(
                sp.GetRequiredService<IIndexerClient>(),
                sp.GetRequiredService<ITorrentClient>(),
                sp.GetRequiredService<ReleaseSelector>(),
                new TorrentAcquirerSettings(settings.TorrentStagingDirectory, settings.IndexerComicCategories)));

        services.AddSingleton<TorrentCompletionWorker>();

        return services;
    }
}
