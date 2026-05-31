using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using log4net;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace API.Schema.NotificationsContext.NotificationConnectors;

[PrimaryKey("Name")]
public class NotificationConnector(string name, string url, Dictionary<string, string> headers, string httpMethod, string body)
{
    [StringLength(64)] public string Name { get; init; } = name;
    
    [StringLength(2048)] [Url] public string Url { get; internal set; } = url;

    [Required] public Dictionary<string, string> Headers { get; internal set; } = headers;

    [StringLength(8)] public string HttpMethod { get; internal set; } = httpMethod;

    [StringLength(4096)] public string Body { get; internal set; } = body;

    /// <summary>
    /// EF ONLY!!! EF Core's ConstructorBindingConvention cannot bind a
    /// <c>Dictionary&lt;string,string&gt;</c> parameter, so we provide an alternate constructor with
    /// only scalar parameters for EF to materialise rows. Headers is set via its property setter.
    /// </summary>
    internal NotificationConnector(string name, string url, string httpMethod, string body)
        : this(name, url, new Dictionary<string, string>(), httpMethod, body) { }

    [NotMapped] private readonly HttpClient Client = new()
    {
        DefaultRequestHeaders = { { "User-Agent", TrangaSettings.DefaultUserAgent } }
    };
    
    [JsonIgnore] protected ILog Log = LogManager.GetLogger(name);

    public void SendNotification(string title, string notificationText)
    {
        Log.InfoFormat("Sending notification: {0} - {1}", title, notificationText);
        string formattedUrl = FormatStr(Url, title, notificationText);
        string formattedBody = FormatStr(Body, title, notificationText);
        Dictionary<string, string> formattedHeaders = Headers.ToDictionary(h => h.Key, 
            h => FormatStr(h.Value, title, notificationText));

        HttpRequestMessage request = new(System.Net.Http.HttpMethod.Parse(HttpMethod), formattedUrl);
        foreach ((string key, string value) in formattedHeaders)
            request.Headers.Add(key, value);
        request.Content = new StringContent(formattedBody);
        request.Content.Headers.ContentType = new ("application/json");
        Log.DebugFormat("Request: {0}", request);

        HttpResponseMessage response = Client.Send(request);
        Log.DebugFormat("Response status code: {0} {1}", response.StatusCode, response.Content.ReadAsStringAsync().Result);
    }

    private static string FormatStr(string str, string title, string text)
    {
        StringBuilder sb = new (str);
        sb.Replace("%title", title);
        sb.Replace("%text", text);
            
        return sb.ToString();
    }

    public override string ToString() => $"{GetType().Name} {Name}";
}