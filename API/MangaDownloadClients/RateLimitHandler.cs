using System.Net;
using System.Threading.RateLimiting;
using log4net;

namespace API.MangaDownloadClients;

public class RateLimitHandler : DelegatingHandler
{
    private ILog Log { get; } = LogManager.GetLogger(typeof(RateLimitHandler));
    private readonly RateLimiter _limiter;

    public RateLimitHandler(TrangaSettings settings) : base(new HttpClientHandler())
    {
        // Calculate tokens per minute. Default is 90.
        int requestsPerMinute = settings.UserAgent.Equals(TrangaSettings.DefaultUserAgent) 
            ? int.Min(Constants.RequestsPerMinute, 90) 
            : Constants.RequestsPerMinute;

        _limiter = new TokenBucketRateLimiter(new()
        {
            AutoReplenishment = true,
            // Increase QueueLimit to handle large batches of chapters (18 chapters * 50 images = 900)
            QueueLimit = 2000, 
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            // Replenish every minute to avoid integer division issues with per-second replenishment
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            TokenLimit = requestsPerMinute,
            TokensPerPeriod = requestsPerMinute
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Log.DebugFormat("Requesting lease {0}", request.RequestUri);
        
        // Wait for a token from the bucket.
        // If the queue is full or cancellation happens, this throws.
        using RateLimitLease lease = await _limiter.AcquireAsync(permitCount: 1, cancellationToken);
        
        Log.DebugFormat("Acquired lease {0}", request.RequestUri);

        if (lease.IsAcquired)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        Log.WarnFormat("Rate limit lease NOT acquired for {0}", request.RequestUri);
        return new(HttpStatusCode.TooManyRequests);
    }
}
