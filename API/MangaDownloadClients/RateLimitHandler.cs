using System.Net;
using System.Threading.RateLimiting;
using log4net;

namespace API.MangaDownloadClients;

public class RateLimitHandler : DelegatingHandler
{
    private ILog Log { get; } = LogManager.GetLogger(typeof(RateLimitHandler));
    private readonly PartitionedRateLimiter<HttpRequestMessage> _limiter;

    public RateLimitHandler(TrangaSettings settings) : this(settings, new HttpClientHandler())
    {
    }

    /// <summary>
    /// Testable constructor: allows injecting the inner handler (the external HTTP boundary) and
    /// overriding the rate so behaviour can be verified without real network calls.
    /// </summary>
    internal RateLimitHandler(TrangaSettings settings, HttpMessageHandler innerHandler,
        int? requestsPerMinute = null, int queueLimit = 2000) : base(innerHandler)
    {
        // Calculate tokens per minute. Default is 90.
        int rpm = requestsPerMinute ?? (settings.UserAgent.Equals(TrangaSettings.DefaultUserAgent)
            ? int.Min(Constants.RequestsPerMinute, 90)
            : Constants.RequestsPerMinute);

        // Partition by host so that politeness limits are enforced PER site. A global bucket would let
        // a bulk download from one connector starve requests to every other connector.
        _limiter = PartitionedRateLimiter.Create<HttpRequestMessage, string>(request =>
            RateLimitPartition.GetTokenBucketLimiter(
                request.RequestUri?.Host ?? string.Empty,
                _ => new TokenBucketRateLimiterOptions
                {
                    AutoReplenishment = true,
                    // Large QueueLimit to handle batches of chapters (18 chapters * 50 images = 900)
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    // Replenish every minute to avoid integer division issues with per-second replenishment
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    TokenLimit = rpm,
                    TokensPerPeriod = rpm
                }));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Log.DebugFormat("Requesting lease {0}", request.RequestUri);

        // Wait for a token from the host's bucket.
        // If the queue is full or cancellation happens, this throws.
        using RateLimitLease lease = await _limiter.AcquireAsync(request, permitCount: 1, cancellationToken);

        Log.DebugFormat("Acquired lease {0}", request.RequestUri);

        if (lease.IsAcquired)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        Log.WarnFormat("Rate limit lease NOT acquired for {0}", request.RequestUri);
        return new(HttpStatusCode.TooManyRequests);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _limiter.Dispose();
        base.Dispose(disposing);
    }
}
