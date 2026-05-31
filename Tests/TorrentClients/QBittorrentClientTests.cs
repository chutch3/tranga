using System.Net;
using System.Text;
using API;
using API.TorrentClients;
using Xunit;

namespace API.Tests.TorrentClients;

public class QBittorrentClientTests
{
    /// <summary>
    /// Drives the FakeHttpMessageHandler with per-endpoint responders so each test can wire only
    /// the qBittorrent endpoints it actually exercises.
    /// </summary>
    private sealed class Router
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();

        public HttpResponseMessage Handle(HttpRequestMessage req)
        {
            Requests.Add(req);
            string path = req.RequestUri!.AbsolutePath;
            return Routes.TryGetValue(path, out var fn)
                ? fn(req)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static (QBittorrentClient client, Router router, HttpClient http) Build()
    {
        var router = new Router();
        var http = new HttpClient(new FakeHttpMessageHandler(router.Handle));
        var client = new QBittorrentClient(http, baseUrl: "http://qbittorrent.test:8080", username: "u", password: "p");
        return (client, router, http);
    }

    private static HttpResponseMessage Ok(string body = "Ok.") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Add_LogsInThenPostsAdd_ReturnsTag()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => Ok("Ok.");
        router.Routes["/api/v2/torrents/add"]  = _ => Ok("Ok.");

        string? result = await client.Add("magnet:?xt=urn:btih:abc", "/downloads", tag: "chap-1", CancellationToken.None);

        Assert.Equal("chap-1", result);
        // Two requests: login, then add.
        Assert.Equal("/api/v2/auth/login", router.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/api/v2/torrents/add",  router.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Add_ReturnsNull_OnAuthFailure()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        string? result = await client.Add("magnet:?xt=urn:btih:abc", "/downloads", "tag", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatus_ReturnsDownloading_WhenProgressLessThanOne()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => Ok();
        router.Routes["/api/v2/torrents/info"] = _ => Json("""
            [{"hash":"abc","progress":0.42,"state":"downloading","save_path":"/downloads","tags":"chap-1"}]
            """);

        var status = await client.GetStatus("chap-1", CancellationToken.None);

        var dl = Assert.IsType<TorrentStatus.Downloading>(status);
        Assert.Equal(0.42, dl.Progress, precision: 2);
    }

    [Fact]
    public async Task GetStatus_ReturnsCompleted_WhenProgressIsOne()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => Ok();
        router.Routes["/api/v2/torrents/info"] = _ => Json("""
            [{"hash":"abc","progress":1.0,"state":"uploading","save_path":"/downloads/saga60","tags":"chap-1"}]
            """);

        var status = await client.GetStatus("chap-1", CancellationToken.None);

        var done = Assert.IsType<TorrentStatus.Completed>(status);
        Assert.Equal("/downloads/saga60", done.SavePath);
    }

    [Fact]
    public async Task GetStatus_ReturnsErrored_OnErrorState()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => Ok();
        router.Routes["/api/v2/torrents/info"] = _ => Json("""
            [{"hash":"abc","progress":0.1,"state":"error","save_path":"","tags":"chap-1"}]
            """);

        var status = await client.GetStatus("chap-1", CancellationToken.None);

        Assert.IsType<TorrentStatus.Errored>(status);
    }

    [Fact]
    public async Task GetStatus_ReturnsNull_WhenNoMatchingTorrent()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => Ok();
        router.Routes["/api/v2/torrents/info"] = _ => Json("[]");

        var status = await client.GetStatus("chap-missing", CancellationToken.None);

        Assert.Null(status);
    }

    [Fact]
    public async Task Remove_ResolvesHashFromInfoThenDeletes()
    {
        var (client, router, _) = Build();
        router.Routes["/api/v2/auth/login"] = _ => Ok();
        router.Routes["/api/v2/torrents/info"] = _ => Json("""
            [{"hash":"abc123","progress":1.0,"state":"uploading","save_path":"/x","tags":"chap-1"}]
            """);
        HttpRequestMessage? deleteRequest = null;
        router.Routes["/api/v2/torrents/delete"] = req => { deleteRequest = req; return Ok(); };

        await client.Remove("chap-1", deleteData: true, CancellationToken.None);

        Assert.NotNull(deleteRequest);
        string body = await deleteRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("hashes=abc123", body);
        Assert.Contains("deleteFiles=true", body);
    }
}
