using API.Acquirers;
using API.Indexers;
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
            log.Info("Torrent acquisition path disabled (indexer or torrent client not configured).");
            return services;
        }

        log.Info("Indexer and torrent client are configured — registering torrent acquisition path.");

        services.AddSingleton<IIndexerClient>(sp =>
        {
            var rl = sp.GetRequiredService<RateLimitHandler>();
            return new ProwlarrClient(
                new HttpClient(rl, disposeHandler: false),
                settings.IndexerBaseUrl,
                settings.IndexerApiKey);
        });

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
