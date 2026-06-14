using System.Net.Http.Headers;
using System.Text.Json;
using DxpContentTransfer.Models;
using Microsoft.Extensions.Logging;

namespace DxpContentTransfer.Services;

public sealed class EnvironmentHealthService : IEnvironmentHealthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EnvironmentHealthService> _logger;

    public EnvironmentHealthService(IHttpClientFactory httpClientFactory, ILogger<EnvironmentHealthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<EnvironmentHealthResult> CheckAsync(DxpEnvironmentConfig config)
    {
        if (config == null || !config.IsConfigured)
            return EnvironmentHealthResult.Fail("Enter Base URL, Client Key and Client Secret first.");
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out _))
            return EnvironmentHealthResult.Fail("Base URL must be an absolute URL, e.g. https://example.com.");

        var client = _httpClientFactory.CreateClient();

        // 1. OAuth2 client_credentials token — the definitive credentials + scope check. A failure
        //    here is the CORS-500 / bad-secret / token-endpoint-missing class of problem.
        string token, grantedScope;
        try
        {
            var tokenUrl = $"{config.BaseUrl.TrimEnd('/')}/api/episerver/connect/token";
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = config.ClientKey,
                ["client_secret"] = config.ClientSecret,
                ["scope"] = "epi_content_management"
            };
            using var resp = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return EnvironmentHealthResult.Fail($"Token request failed ({(int)resp.StatusCode}). {SummariseError(body)}");

            using var doc = JsonDocument.Parse(body);
            token = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            grantedScope = doc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Health check token step failed for {Env}: {Error}", config.Name, ex.Message);
            return EnvironmentHealthResult.Fail($"Could not reach the token endpoint at {config.BaseUrl.TrimEnd('/')}/api/episerver/connect/token — {ex.Message}");
        }

        if (string.IsNullOrEmpty(token))
            return EnvironmentHealthResult.Fail("The token endpoint responded but returned no access_token.");

        // Some servers echo the granted scope; if present and missing our scope, that's the 403 root cause.
        if (!string.IsNullOrEmpty(grantedScope) &&
            !grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("epi_content_management"))
            return EnvironmentHealthResult.Fail(
                $"Authenticated, but the 'epi_content_management' scope was not granted (got '{grantedScope}'). Add it to the OpenID Connect application's allowed scopes.");

        // 2. Confirm the Content Management API responds to an authenticated call. We GET a throwaway
        //    GUID, which a healthy CMA answers with 404 (content-not-found) — exactly as it does during
        //    a real transfer. So a 404 here is SUCCESS, not failure. The only thing it can't tell us by
        //    status alone is "registered API said not-found" vs "no such route" — both 404. We separate
        //    those by the response shape: a real API emits a JSON/problem body, a bare framework 404 doesn't.
        //    (We never hard-fail on a 404: a working environment produces one.)
        int status;
        string contentType, probeBody;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CmaClient.ManagementUrl(config.BaseUrl, Guid.NewGuid()));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await client.SendAsync(req);
            status = (int)resp.StatusCode;
            contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            probeBody = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return EnvironmentHealthResult.Fail($"Token acquired, but the Content Management API call failed — {ex.Message}");
        }

        _logger.LogDebug("Health check CMA probe for {Env}: {Status} content-type='{ContentType}'", config.Name, status, contentType);

        if (status == 401)
            return EnvironmentHealthResult.Fail("Token acquired, but the Content Management API rejected it (401).");
        if (status == 403)
            return EnvironmentHealthResult.Fail("Token acquired, but it is not authorized for the Content Management API (403). Check the 'epi_content_management' scope on the application.");
        if (status is >= 200 and < 300)
            return EnvironmentHealthResult.Pass("Connection OK — token granted, and the Content Management API is reachable.");
        if (status == 404)
        {
            // Registered API not-found (JSON body) ⇒ confident OK. Bare framework 404 ⇒ still OK
            // (token + scope are proven), but flag the small chance the API isn't registered.
            var apiAnswered = contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(probeBody);
            return apiAnswered
                ? EnvironmentHealthResult.Pass("Connection OK — token granted with the content-management scope, and the Content Management API is reachable.")
                : EnvironmentHealthResult.Pass("Token and scope OK. The probe returned a bare 404 — almost certainly fine, but if transfers fail with 404, confirm services.AddContentManagementApi(...) is registered on this environment.");
        }
        return EnvironmentHealthResult.Fail($"Token OK, but the Content Management API returned {status}.");
    }

    private static bool LooksLikeJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var c = body.TrimStart()[0];
        return c is '{' or '[';
    }

    private static string SummariseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No response body.";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            if (!string.IsNullOrEmpty(error) || !string.IsNullOrEmpty(desc))
                return string.Join(": ", new[] { error, desc }.Where(s => !string.IsNullOrEmpty(s)));
        }
        catch { /* not JSON — fall through to the trimmed raw body */ }
        return body.Length > 200 ? body[..200] + "…" : body;
    }
}
