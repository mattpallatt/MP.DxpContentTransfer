using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DxpContentTransfer.Services;

// Thin transport wrapper over the Optimizely Content Management (CMA) and
// Content Delivery (CDV) REST endpoints. Centralises URL building, bearer auth,
// the send, and request/response logging so callers only deal with status + body.
// Every per-request HTTP method in ContentTransferService used to inline this
// boilerplate; it now lives here once.
public sealed class CmaClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CmaClient> _logger;

    public CmaClient(IHttpClientFactory httpClientFactory, ILogger<CmaClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // For the few callers that need to drive the request themselves (binary download,
    // multipart upload) rather than the simple JSON send path above.
    public HttpClient CreateClient() => _httpClientFactory.CreateClient();

    public static string ManagementUrl(string baseUrl, Guid guid) =>
        $"{baseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";

    public static string DeliveryUrl(string baseUrl, Guid guid) =>
        $"{baseUrl.TrimEnd('/')}/api/episerver/v3.0/content/{guid}";

    public static string DeliveryByUrl(string baseUrl, string contentUrl) =>
        $"{baseUrl.TrimEnd('/')}/api/episerver/v3.0/content/?contentURL={Uri.EscapeDataString(contentUrl)}";

    // GET the CMA representation of a content item by GUID. Pass `language` (e.g. "es") to read a
    // specific language branch via the Accept-Language header — the CMA returns the master branch
    // when it's omitted.
    public Task<CmaResponse> GetManagementAsync(string baseUrl, string token, Guid guid, string purpose, string language = null) =>
        SendAsync(HttpMethod.Get, ManagementUrl(baseUrl, guid), token, purpose, acceptLanguage: language);

    // GET the CDV representation of a content item by GUID (returns clean canonical urls).
    public Task<CmaResponse> GetDeliveryAsync(string baseUrl, string token, Guid guid, string purpose) =>
        SendAsync(HttpMethod.Get, DeliveryUrl(baseUrl, guid), token, purpose, accept: "application/json");

    // GET a content item by its relative URL via the CDV endpoint.
    public Task<CmaResponse> GetByUrlAsync(string baseUrl, string token, string contentUrl, string purpose) =>
        SendAsync(HttpMethod.Get, DeliveryByUrl(baseUrl, contentUrl), token, purpose, accept: "application/json");

    // PUT a JSON body to the CMA management endpoint for a GUID.
    public Task<CmaResponse> PutManagementAsync(string baseUrl, string token, Guid guid, string json, string purpose) =>
        SendAsync(HttpMethod.Put, ManagementUrl(baseUrl, guid), token, purpose,
            content: new StringContent(json, Encoding.UTF8, "application/json"), requestBody: json);

    // DELETE a content item by GUID.
    public Task<CmaResponse> DeleteManagementAsync(string baseUrl, string token, Guid guid, string purpose) =>
        SendAsync(HttpMethod.Delete, ManagementUrl(baseUrl, guid), token, purpose);

    // Multipart PUT of an in-memory binary asset (content JSON + raw bytes). Used for the tiny probe
    // image that forces Optimizely to create a content's "For This Page/Block" asset folder.
    public async Task<CmaResponse> PutAssetBytesAsync(string baseUrl, string token, Guid guid, string json, byte[] bytes, string fileName, string mimeType, string purpose)
    {
        var client = _httpClientFactory.CreateClient();
        var url = ManagementUrl(baseUrl, guid);
        LogRequest(HttpMethod.Put, url, purpose, json);

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(json, Encoding.UTF8, "application/json"), "content");
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        multipart.Add(filePart, "file", fileName);
        request.Content = multipart;

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("<<< {Status} PUT (asset) ({Purpose}) {Url}\n{Body}", (int)response.StatusCode, purpose, url, body);
        return new CmaResponse(response.StatusCode, body);
    }

    public async Task<CmaResponse> SendAsync(
        HttpMethod method, string url, string token, string purpose,
        HttpContent content = null, string accept = null, string requestBody = null, string acceptLanguage = null)
    {
        var client = _httpClientFactory.CreateClient();
        LogRequest(method, url, purpose, requestBody);

        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept != null) request.Headers.Add("Accept", accept);
        if (!string.IsNullOrEmpty(acceptLanguage)) request.Headers.Add("Accept-Language", acceptLanguage);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("<<< {Status} {Method} ({Purpose}) {Url}\n{Body}",
            (int)response.StatusCode, method.Method, purpose, url, body);
        return new CmaResponse(response.StatusCode, body);
    }

    private void LogRequest(HttpMethod method, string url, string purpose, string requestBody)
    {
        if (string.IsNullOrEmpty(requestBody))
        {
            _logger.LogDebug(">>> {Method} ({Purpose}) {Url}", method.Method, purpose, url);
            return;
        }
        _logger.LogDebug(">>> {Method} ({Purpose}) {Url}\n    Content-Type: application/json\n{Body}",
            method.Method, purpose, url, requestBody);
    }
}

public readonly record struct CmaResponse(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}
