using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using static DxpContentTransfer.Services.JsonVisitors;

namespace DxpContentTransfer.Services;

// Pure XHTML (PropertyXhtmlString) processing — extraction and remapping of the inline assets and
// content-block fragments the editor bakes into rich-text markup. No I/O, no engine state, so it is
// unit-tested directly (see tests/DxpContentTransfer.Tests/XhtmlHelperTests). The recursive transfer
// engine in ContentTransferService drives these; the resolution/upload of what they surface stays there.
internal static class XhtmlProcessor
{
    // Extracts all inline-asset URLs (img src + a href) from PropertyXhtmlString fields in a document.
    internal static List<string> ExtractXhtmlImageUrls(string json)
    {
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            CollectXhtmlUrls(doc.RootElement, urls);
        }
        catch { }
        return urls;
    }

    private static void CollectXhtmlUrls(JsonElement element, List<string> urls) =>
        WalkJsonElements(element, obj =>
        {
            if (obj.TryGetProperty("propertyDataType", out var pdt) &&
                pdt.GetString() == "PropertyXhtmlString" &&
                obj.TryGetProperty("value", out var val) &&
                val.ValueKind == JsonValueKind.String)
                CollectAssetUrls(val.GetString() ?? "", urls);
        });

    // Both <img src> and <a href> can point at internal assets (images, video, documents). Collect
    // both in document order; the transfer loop filters to actual media before upload. Parsed via
    // HtmlAgilityPack rather than regex so attribute quoting/order/whitespace can't slip an asset past.
    private static void CollectAssetUrls(string html, List<string> urls)
    {
        if (string.IsNullOrEmpty(html)) return;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var nodes = doc.DocumentNode.SelectNodes("//*[@src] | //*[@href]");
        if (nodes == null) return;
        foreach (var node in nodes)
        {
            var src = node.GetAttributeValue("src", null);
            if (!string.IsNullOrEmpty(src)) urls.Add(src);
            var href = node.GetAttributeValue("href", null);
            if (!string.IsNullOrEmpty(href)) urls.Add(href);
        }
    }

    // Extracts inline content-block references from every PropertyXhtmlString in a document. The
    // editor stores them as <div class="epi-contentfragment" data-contentguid data-contentlink
    // data-contentname …> nodes — the guid lives only in the HTML string, so the generic guidValue
    // walker never sees them.
    internal static List<(Guid Guid, int ContentLink, string Name)> ExtractXhtmlContentFragments(string json)
    {
        var list = new List<(Guid, int, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            WalkJsonElements(doc.RootElement, obj =>
            {
                if (obj.TryGetProperty("propertyDataType", out var pdt) &&
                    pdt.GetString() == "PropertyXhtmlString" &&
                    obj.TryGetProperty("value", out var val) &&
                    val.ValueKind == JsonValueKind.String)
                    list.AddRange(ParseContentFragments(val.GetString()));
            });
        }
        catch { }
        return list;
    }

    // Pulls (guid, integer id, name) out of each epi-contentfragment div. Parsed via HtmlAgilityPack
    // so attribute order, quoting and whitespace are handled by the DOM rather than a brittle regex.
    internal static IEnumerable<(Guid Guid, int ContentLink, string Name)> ParseContentFragments(string html)
    {
        var results = new List<(Guid, int, string)>();
        if (string.IsNullOrEmpty(html)) return results;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var divs = doc.DocumentNode.SelectNodes("//div");
        if (divs == null) return results;

        foreach (var div in divs)
        {
            var cls = div.GetAttributeValue("class", "");
            if (!cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("epi-contentfragment"))
                continue;
            // The guid is the only required attribute; without a valid one there's nothing to transfer.
            if (!Guid.TryParse(div.GetAttributeValue("data-contentguid", null), out var guid)) continue;
            var contentLink = int.TryParse(div.GetAttributeValue("data-contentlink", null), out var id) ? id : 0;
            var name = div.GetAttributeValue("data-contentname", null);
            results.Add((guid, contentLink, name));
        }
        return results;
    }

    // Strips the source environment origin (scheme+host) from all image src URLs inside
    // PropertyXhtmlString values so that relative paths (/contentassets/... /globalassets/...)
    // resolve correctly on any target environment.
    // xhtmlUrlMap (optional): source relative path → target relative path. Applied after stripping
    // the origin so that bucket-GUID mismatches between environments are fixed up.
    internal static string RewriteXhtmlUrls(string json, string sourceBaseUrl,
        Dictionary<string, string> xhtmlUrlMap = null, Dictionary<int, int> contentIdMap = null,
        Dictionary<int, int> blockIdMap = null)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        try
        {
            var origin = new Uri(sourceBaseUrl).GetLeftPart(UriPartial.Authority);
            RewriteXhtmlNodes(node, origin, xhtmlUrlMap, contentIdMap, blockIdMap);
        }
        catch { }
        return node.ToJsonString();
    }

    private static void RewriteXhtmlNodes(JsonNode node, string origin,
        Dictionary<string, string> xhtmlUrlMap = null, Dictionary<int, int> contentIdMap = null,
        Dictionary<int, int> blockIdMap = null) =>
        WalkJsonObjects(node, obj =>
        {
            if (obj["propertyDataType"]?.GetValue<string>() != "PropertyXhtmlString" ||
                obj["value"] is not JsonValue val)
                return;
            try
            {
                var html = val.GetValue<string>() ?? "";
                if (html.Contains(origin, StringComparison.OrdinalIgnoreCase))
                    html = html.Replace(origin, "", StringComparison.OrdinalIgnoreCase);
                if (xhtmlUrlMap != null)
                    foreach (var (src, tgt) in xhtmlUrlMap)
                        if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                            html = html.Replace(src, tgt, StringComparison.OrdinalIgnoreCase);
                // Remap the environment-specific ",,{id}" content id embedded in editor media URLs.
                // Bounded by a non-digit lookahead so ",,105" never matches inside ",,1050".
                if (contentIdMap != null)
                    foreach (var (sourceId, targetId) in contentIdMap)
                        html = Regex.Replace(html, $",,{sourceId}(?=\\D|$)", $",,{targetId}");
                // Remap the environment-specific integer id on inline content-block fragments. The
                // closing quote delimits the value, so data-contentlink="118" never matches "1180".
                if (blockIdMap != null)
                    foreach (var (sourceId, targetId) in blockIdMap)
                        html = html.Replace($"data-contentlink=\"{sourceId}\"", $"data-contentlink=\"{targetId}\"");
                obj["value"] = html;
            }
            catch { }
        });

    // Converts an absolute URL to its path component, or returns the input unchanged if
    // it is already relative.
    internal static string ToRelativePath(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.PathAndQuery;
        return url.StartsWith('/') ? url : null;
    }

    private const string EditModeContentPrefix = "/EPiServer/CMS/Content";

    // Turns an XHTML <img src> into a path we can resolve back to a content GUID. Editor markup
    // commonly stores internal edit-mode URLs such as
    //   /EPiServer/CMS/Content/globalassets/en/foo/bar.jpg,,108%3Fepieditmode=false
    // so we: URL-decode (the query is often %3F-encoded), drop the query/fragment, strip the
    // ",,<version>" suffix, and rewrite the edit-mode prefix to its friendly form (/globalassets/…).
    internal static string NormalizeInlineImagePath(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return relPath;
        var p = Uri.UnescapeDataString(relPath);
        var q = p.IndexOfAny(['?', '#']);
        if (q >= 0) p = p[..q];
        var v = p.IndexOf(",,", StringComparison.Ordinal);
        if (v >= 0) p = p[..v];
        if (p.StartsWith(EditModeContentPrefix, StringComparison.OrdinalIgnoreCase))
            p = p[EditModeContentPrefix.Length..];
        return p;
    }
}
