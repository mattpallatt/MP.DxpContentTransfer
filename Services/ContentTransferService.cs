using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DxpContentTransfer.Models;
using EPiServer;
using EPiServer.Core;
using EPiServer.SpecializedProperties;
using EPiServer.Web;
using EPiServer.Web.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DxpContentTransfer.Services;

public class ContentTransferService : IContentTransferService
{
    private readonly IDxpSettingsService _settingsService;
    private readonly IEnvironmentTokenService _tokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IContentLoader _contentLoader;
    private readonly IUrlResolver _urlResolver;
    private readonly ILogger<ContentTransferService> _logger;
    private readonly bool _logApiCalls;

    public ContentTransferService(
        IDxpSettingsService settingsService,
        IEnvironmentTokenService tokenService,
        IHttpClientFactory httpClientFactory,
        IContentLoader contentLoader,
        IUrlResolver urlResolver,
        ILogger<ContentTransferService> logger,
        IConfiguration configuration)
    {
        _settingsService = settingsService;
        _tokenService = tokenService;
        _httpClientFactory = httpClientFactory;
        _contentLoader = contentLoader;
        _urlResolver = urlResolver;
        _logger = logger;
        _logApiCalls = configuration.GetValue<bool>("DxpContentTransfer:LogApiCalls");
    }

    // ── Pre-check ─────────────────────────────────────────────────────────────

    public async Task<PreCheckResult> PreCheckAsync(
        string contentId,
        string targetEnvironmentName,
        bool includeChildren,
        bool overwriteMatchingIds)
    {
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);

        if (target == null || !target.IsConfigured)
            return new PreCheckResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        string targetToken;
        try { targetToken = await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new PreCheckResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        var contentRef = ParseContentReference(contentId);
        if (contentRef == ContentReference.EmptyReference)
            return new PreCheckResult { Success = false, ErrorMessage = $"Invalid content reference: {contentId}" };

        var result = new PreCheckResult { Success = true };
        // Maps sourceGuid → the GUID that item will use on target (same GUID for Create/Overwrite, new GUID for CreateNew).
        // Used so children in the same batch can find their parent without needing it to exist on target yet.
        var batchGuidMap = new Dictionary<Guid, Guid>();
        // Tracks GUIDs already scanned for dependencies so each asset/block is counted once.
        var depSeen = new HashSet<Guid>();

        foreach (var itemRef in CollectItems(contentRef, includeChildren))
        {
            var item = await BuildPreCheckItemAsync(itemRef, target, targetToken, overwriteMatchingIds, batchGuidMap);

            // Scan the page's blocks, media, and inline images for the hierarchical plan display.
            try
            {
                if (!ContentReference.IsNullOrEmpty(itemRef))
                {
                    var content = _contentLoader.Get<IContent>(itemRef);
                    if (content is PageData)
                    {
                        depSeen.Add(content.ContentGuid);
                        item.Dependencies = ScanContentDependencies(content, depSeen);
                    }
                }
            }
            catch { /* dependency scan is best-effort */ }

            result.Items.Add(item);
            if (item.ContentGuid != Guid.Empty)
            {
                var targetGuid = item.Action == PreCheckAction.CreateNew
                    ? (item.NewGuid ?? item.ContentGuid)
                    : item.ContentGuid;
                batchGuidMap[item.ContentGuid] = targetGuid;
            }
        }

        return result;
    }

    // Scans a page or block's properties to build the nested dependency tree shown in the plan.
    // Uses IContentLoader (local, fast) so no API calls needed at pre-check time.
    private List<DependencyNode> ScanContentDependencies(IContent content, HashSet<Guid> seen)
    {
        var nodes = new List<DependencyNode>();
        if (content is not IContentData contentData) return nodes;

        foreach (var prop in contentData.Property)
        {
            if (prop is PropertyContentArea area && area.Value is ContentArea contentArea)
            {
                foreach (var areaItem in contentArea.Items)
                {
                    try
                    {
                        var child = _contentLoader.Get<IContent>(areaItem.ContentLink);
                        if (!seen.Add(child.ContentGuid)) continue;
                        if (child is IContentMedia)
                        {
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Image" });
                        }
                        else
                        {
                            var blockNode = new DependencyNode { Name = child.Name, NodeType = "Block" };
                            blockNode.Children = ScanContentDependencies(child, seen);
                            nodes.Add(blockNode);
                        }
                    }
                    catch { }
                }
            }
            else if (prop is PropertyContentReference cref && !ContentReference.IsNullOrEmpty(cref.ContentLink))
            {
                try
                {
                    var child = _contentLoader.Get<IContent>(cref.ContentLink);
                    if (child is IContentMedia && seen.Add(child.ContentGuid))
                        nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Image" });
                }
                catch { }
            }
            else if (prop is PropertyContentReferenceList crefList && crefList.Value is IList<ContentReference> crefListItems)
            {
                foreach (var refVal in crefListItems)
                {
                    try
                    {
                        var child = _contentLoader.Get<IContent>(refVal);
                        if (child is IContentMedia && seen.Add(child.ContentGuid))
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Image" });
                    }
                    catch { }
                }
            }
        }

        // Scan XHTML properties for actually-referenced inline images.
        // Use type-name detection + PropertyLongString.LongString to get the raw stored XHTML
        // without requiring an HTTP context (which XhtmlString.ToHtmlString() may need).
        var seenInlineUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _logger.LogDebug("Pre-check XHTML scan for '{Content}': scanning {Count} properties", content.Name, contentData.Property.Count);
        foreach (var prop in contentData.Property)
        {
            var typeName = prop.GetType().Name;
            if (!typeName.Contains("Xhtml", StringComparison.OrdinalIgnoreCase)) continue;

            string html = null;
            try
            {
                // Try value cast first
                if (prop.Value is XhtmlString xs)
                    html = xs.ToString();
                // Fallback: ToString() on the property itself (PropertyData.ToString() calls Value.ToString())
                if (string.IsNullOrEmpty(html))
                    html = prop.Value?.ToString();
                // Last resort: use reflection to read the underlying string field
                if (string.IsNullOrEmpty(html))
                {
                    var field = prop.GetType().GetField("_longString",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    html = field?.GetValue(prop) as string;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Pre-check XHTML read failed for '{Content}'.{Prop}({Type}): {Error}", content.Name, prop.Name, typeName, ex.Message);
            }

            _logger.LogDebug("Pre-check XHTML prop '{Content}'.{Prop}({Type}): {Len} chars", content.Name, prop.Name, typeName, html?.Length ?? -1);
            if (string.IsNullOrEmpty(html)) continue;

            foreach (Match m in Regex.Matches(html, @"src=""([^""]+)""", RegexOptions.IgnoreCase))
            {
                var src = m.Groups[1].Value;
                var srcPath = src.Split('?')[0];

                // EPiServer permanent link format: /link/{32hex}.aspx
                if (srcPath.StartsWith("/link/", StringComparison.OrdinalIgnoreCase) &&
                    srcPath.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                {
                    if (!seenInlineUrls.Add(srcPath)) continue;
                    try
                    {
                        var linked = _urlResolver.Route(new UrlBuilder(srcPath));
                        if (linked is IContentMedia media)
                            nodes.Add(new DependencyNode { Name = media.Name ?? srcPath, NodeType = "InlineImage" });
                    }
                    catch { }
                    continue;
                }

                if (!src.Contains("/contentassets/", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("/globalassets/",  StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("/EPiServer/",     StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seenInlineUrls.Add(srcPath)) continue;
                var filename = Path.GetFileName(srcPath.TrimEnd('/'));
                var versionIdx = filename.LastIndexOf(",,", StringComparison.Ordinal);
                if (versionIdx > 0) filename = filename[..versionIdx];
                if (!string.IsNullOrEmpty(filename))
                    nodes.Add(new DependencyNode { Name = filename, NodeType = "InlineImage" });
            }
        }

        return nodes;
    }

    private async Task<PreCheckItemResult> BuildPreCheckItemAsync(
        ContentReference contentRef,
        DxpEnvironmentConfig target,
        string targetToken,
        bool overwriteMatchingIds,
        Dictionary<Guid, Guid> batchGuidMap)
    {
        IContent content;
        try { content = _contentLoader.Get<IContent>(contentRef); }
        catch (Exception ex)
        {
            return new PreCheckItemResult
            {
                ContentId = contentRef.ToString(),
                ContentName = "?",
                Action = PreCheckAction.Unresolvable,
                Notes = $"Could not load source content: {ex.Message}"
            };
        }

        var guid = content.ContentGuid;
        var name = content.Name;
        var existsOnTarget = await ExistsOnTargetAsync(guid, target, targetToken);

        if (existsOnTarget && overwriteMatchingIds)
        {
            return new PreCheckItemResult
            {
                ContentId = contentRef.ToString(),
                ContentGuid = guid,
                ContentName = name,
                Action = PreCheckAction.Overwrite,
                Notes = "Exists on target — will be overwritten in place."
            };
        }

        // For CreateNew (exists but not overwriting) or Create (doesn't exist), resolve parent.
        // Check the batch first: if the direct parent is also being transferred we know its target GUID
        // without needing it to exist on target yet.
        Guid? parentGuid = null;
        string parentPath = null;
        Guid? directParentSourceGuid = null;
        if (!ContentReference.IsNullOrEmpty(content.ParentLink))
        {
            try { directParentSourceGuid = _contentLoader.Get<IContent>(content.ParentLink).ContentGuid; }
            catch { /* parent may not be loadable */ }
        }

        if (directParentSourceGuid.HasValue && batchGuidMap.TryGetValue(directParentSourceGuid.Value, out var batchParentTargetGuid))
        {
            parentGuid = batchParentTargetGuid;
            try { parentPath = _contentLoader.Get<IContent>(content.ParentLink).Name + " (being transferred)"; }
            catch { parentPath = "(parent being transferred)"; }
        }
        else
        {
            (parentGuid, parentPath) = await ResolveTargetParentAsync(content, target, targetToken);
        }

        // If parent not resolved by batch or GUID/URL walking, fall back to the site start page
        var usedRootFallback = false;
        if (!parentGuid.HasValue)
        {
            var (rootGuid, rootPath) = GetSiteRootFallback();
            parentGuid = rootGuid;
            parentPath = rootPath;
            usedRootFallback = true;
        }

        var isRootFallback = usedRootFallback;

        if (existsOnTarget)
        {
            return new PreCheckItemResult
            {
                ContentId = contentRef.ToString(),
                ContentGuid = guid,
                ContentName = name,
                Action = parentGuid.HasValue ? PreCheckAction.CreateNew : PreCheckAction.Unresolvable,
                NewGuid = parentGuid.HasValue ? Guid.NewGuid() : null,
                TargetParentGuid = parentGuid,
                TargetParentPath = parentPath,
                IsRootFallback = isRootFallback,
                Notes = parentGuid.HasValue
                    ? isRootFallback
                        ? "Exists on target — overwrite off. Parent not found, will create as new copy under site root (unpublished)."
                        : $"Exists on target — overwrite off, will create as new copy under '{parentPath}'."
                    : "Exists on target — overwrite off, but parent could not be resolved on target."
            };
        }

        return new PreCheckItemResult
        {
            ContentId = contentRef.ToString(),
            ContentGuid = guid,
            ContentName = name,
            Action = parentGuid.HasValue ? PreCheckAction.Create : PreCheckAction.Unresolvable,
            TargetParentGuid = parentGuid,
            TargetParentPath = parentPath,
            IsRootFallback = isRootFallback,
            Notes = parentGuid.HasValue
                ? isRootFallback
                    ? "Does not exist on target and parent not found — will be created under site root (unpublished)."
                    : $"Does not exist on target — will be created under '{parentPath}'."
                : "Does not exist on target and site root could not be resolved."
        };
    }

    private async Task<bool> ExistsOnTargetAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";
        LogRequest("GET (exists check)", url, targetToken);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        LogResponse("GET (exists check)", url, (int)response.StatusCode, body);

        // 401/403 means the content EXISTS but our identity can't read it — treat as present
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return true;

        return response.StatusCode == System.Net.HttpStatusCode.OK;
    }

    private async Task<(Guid? guid, string path)> ResolveTargetParentAsync(
        IContent content,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        var ancestors = BuildAncestorChain(content.ParentLink);

        // Phase 1: GUID-based — walk up source ancestry, return first that exists on target
        foreach (var ancestor in ancestors)
        {
            if (ancestor.ContentGuid == Guid.Empty) continue;
            if (await ExistsOnTargetAsync(ancestor.ContentGuid, target, targetToken))
                return (ancestor.ContentGuid, ancestor.Name);
        }

        // Phase 2: URL-based — resolve each ancestor's friendly URL and search on target
        foreach (var ancestor in ancestors)
        {
            try
            {
                var relativeUrl = _urlResolver.GetUrl(ancestor.ContentLink);
                if (string.IsNullOrWhiteSpace(relativeUrl) ||
                    relativeUrl == "/" ||
                    relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetGuid = await FindByUrlOnTargetAsync(relativeUrl, target, targetToken);
                if (targetGuid.HasValue)
                    return (targetGuid, $"{ancestor.Name} (matched by URL '{relativeUrl}')");
            }
            catch { /* URL resolution may not be supported for blocks/media */ }
        }

        return (null, null);
    }

    private List<IContent> BuildAncestorChain(ContentReference startRef)
    {
        var chain = new List<IContent>();
        var current = startRef;
        var visited = new HashSet<int>();

        while (!ContentReference.IsNullOrEmpty(current) &&
               current.ID != ContentReference.RootPage.ID &&
               visited.Add(current.ID))
        {
            try
            {
                var ancestor = _contentLoader.Get<IContent>(current);
                chain.Add(ancestor);
                current = ancestor.ParentLink;
            }
            catch { break; }
        }

        return chain; // nearest ancestor first
    }

    private async Task<Guid?> FindByUrlOnTargetAsync(string relativeUrl, DxpEnvironmentConfig target, string targetToken)
    {
        var client = _httpClientFactory.CreateClient();
        var apiUrl = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/content/?contentURL={Uri.EscapeDataString(relativeUrl)}";
        LogRequest("GET (URL lookup on target)", apiUrl, targetToken);
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        request.Headers.Add("Accept", "application/json");
        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        LogResponse("GET (URL lookup on target)", apiUrl, (int)response.StatusCode, json);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Response may be a single object or an array
        var element = root.ValueKind == JsonValueKind.Array
            ? (root.GetArrayLength() > 0 ? root[0] : (JsonElement?)null)
            : root;

        if (element is null) return null;

        if (element.Value.TryGetProperty("contentLink", out var cl) &&
            cl.TryGetProperty("guidValue", out var guidProp) &&
            Guid.TryParse(guidProp.GetString(), out var guid))
            return guid;

        return null;
    }

    // ── Transfer ──────────────────────────────────────────────────────────────

    public async Task<TransferResult> TransferAsync(
        string contentId,
        string targetEnvironmentName,
        bool includeChildren,
        string sourceEnvironmentName,
        string transferStatus = "Published",
        List<PreCheckItemResult> plan = null,
        Action onItemComplete = null)
    {
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);
        var source = ResolveEnvironment(settings, sourceEnvironmentName);

        if (target == null || !target.IsConfigured)
            return new TransferResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        if (source == null || !source.IsConfigured)
            return new TransferResult { Success = false, ErrorMessage = $"Source environment '{sourceEnvironmentName}' is not configured. Ensure credentials are saved in settings." };

        string targetToken;
        try { targetToken = await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new TransferResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        string sourceToken;
        try { sourceToken = await _tokenService.GetTokenAsync(source); }
        catch (Exception ex) { return new TransferResult { Success = false, ErrorMessage = $"Failed to authenticate with source environment: {ex.Message}" }; }

        var contentRef = ParseContentReference(contentId);
        if (contentRef == ContentReference.EmptyReference)
            return new TransferResult { Success = false, ErrorMessage = $"Invalid content reference: {contentId}" };

        // Index plan by contentId for O(1) lookup per item
        var planLookup = plan?.ToDictionary(p => p.ContentId, StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, PreCheckItemResult>();

        var result = new TransferResult { Success = true };

        foreach (var itemRef in CollectItems(contentRef, includeChildren))
        {
            planLookup.TryGetValue(itemRef.ToString(), out var planItem);
            var itemResult = await TransferSingleItemAsync(itemRef, source.BaseUrl, sourceToken, target, targetToken, planItem, transferStatus, onItemComplete);
            result.Items.Add(itemResult);
            if (!itemResult.Success)
                result.Success = false;
        }

        result.TransferredCount = result.Items.Count(i => i.Success);
        return result;
    }

    private async Task<TransferItemResult> TransferSingleItemAsync(
        ContentReference contentRef,
        string sourceBaseUrl,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken,
        PreCheckItemResult planItem,
        string transferStatus = "Published",
        Action onItemComplete = null)
    {
        IContent content;
        try { content = _contentLoader.Get<IContent>(contentRef); }
        catch (Exception ex)
        {
            return new TransferItemResult { ContentId = contentRef.ToString(), Success = false, ErrorMessage = $"Could not load content: {ex.Message}" };
        }

        var contentName = content.Name;
        var sourceGuid = content.ContentGuid;

        string sourceJson;
        try { sourceJson = await ReadFromSourceAsync(sourceGuid, sourceBaseUrl, sourceToken); }
        catch (Exception ex)
        {
            return new TransferItemResult { ContentId = contentRef.ToString(), ContentName = contentName, Success = false, ErrorMessage = $"Failed to read from source: {ex.Message}" };
        }

        // Determine target GUID (same as source unless this is a CreateNew scenario)
        var targetGuid = (planItem?.Action == PreCheckAction.CreateNew)
            ? (planItem.NewGuid ??= Guid.NewGuid())
            : sourceGuid;

        // Determine target parent GUID from plan; fall back to ancestry walk
        Guid targetParentGuid;
        if (planItem?.TargetParentGuid.HasValue == true)
        {
            targetParentGuid = planItem.TargetParentGuid.Value;
        }
        else if (planItem?.Action == PreCheckAction.Overwrite)
        {
            // Overwrite: item already exists — extract its current parent from source
            targetParentGuid = ExtractParentGuid(sourceJson) ?? Guid.Empty;
        }
        else
        {
            var (parentGuid, _) = await ResolveTargetParentAsync(content, target, targetToken);
            targetParentGuid = parentGuid ?? Guid.Empty;
        }

        var visited = new HashSet<Guid> { sourceGuid };

        try
        {
            var targetId = await TransferItemCoreAsync(
                sourceGuid, sourceJson, sourceBaseUrl, sourceToken,
                target, targetToken, targetGuid, targetParentGuid,
                (transferStatus == "CheckedOut") ? "CheckedOut" : "Published",
                visited, onItemComplete);

            return new TransferItemResult
            {
                ContentId = contentRef.ToString(),
                ContentName = contentName,
                Success = true,
                TargetContentId = targetId,
                TargetBaseUrl = target.BaseUrl
            };
        }
        catch (Exception ex)
        {
            onItemComplete?.Invoke();
            return new TransferItemResult { ContentId = contentRef.ToString(), ContentName = contentName, Success = false, ErrorMessage = $"Transfer failed: {ex.Message}" };
        }
    }

    // Core recursive transfer method — implements the stub → blocks → images → full cycle.
    //
    // Order of operations per item:
    //   1. Write a minimal stub so Optimizely auto-creates the "For This Page/Block" asset folder
    //   2. Recursively transfer every referenced block (depth-first) and upload standalone media
    //   3. Upload inline images from PropertyXhtmlString fields (asset folder now exists)
    //   4. Write the full content with correct XHTML URLs and injected target integer IDs
    //
    // This ordering guarantees that when we upload an inline image its parent asset folder
    // already exists (created by the stub in step 1), so we can use the item's own GUID as
    // the image parentLink without any pre-flight folder resolution.
    private async Task<int?> TransferItemCoreAsync(
        Guid sourceGuid,
        string sourceJson,
        string sourceBaseUrl,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken,
        Guid targetGuid,
        Guid targetParentGuid,
        string effectiveStatus,
        HashSet<Guid> visited,
        Action onItemComplete)
    {
        // ── Step 1: Blocks and global media ──────────────────────────────────
        // Process blocks depth-first (they must exist before the parent is written).
        // Global media (not in contentassets) can also be uploaded now because their
        // parent folder already exists on the target.
        // Local media (contentassets images) are deferred until after step 2 creates
        // the asset bucket for this item.
        var idMap = new Dictionary<Guid, int?>();
        var deferredLocalMedia = new List<(Guid guid, string json)>();

        foreach (var refGuid in ExtractContentReferenceGuids(sourceJson))
        {
            if (!visited.Add(refGuid))
            {
                var tid = await GetTargetContentIdAsync(refGuid, target, targetToken);
                if (tid.HasValue) idMap[refGuid] = tid;
                continue;
            }

            string refJson;
            try { refJson = await ReadFromSourceAsync(refGuid, sourceBaseUrl, sourceToken); }
            catch { visited.Remove(refGuid); continue; }

            bool isMedia, isBlock;
            try
            {
                using var d = JsonDocument.Parse(refJson);
                isMedia = IsMediaContent(d.RootElement);
                isBlock = !isMedia && IsBlockContent(d.RootElement);
            }
            catch { continue; }

            if (!isMedia && !isBlock)
            {
                var tid = await GetTargetContentIdAsync(refGuid, target, targetToken);
                if (tid.HasValue) idMap[refGuid] = tid;
                continue;
            }

            if (isMedia)
            {
                if (IsLocalContent(refJson))
                {
                    // Asset bucket doesn't exist yet — defer until after step 2
                    deferredLocalMedia.Add((refGuid, refJson));
                }
                else
                {
                    // Global media — parent folder already exists on target
                    var mediaParentGuid = ExtractParentGuid(refJson) ?? targetGuid;
                    var mediaStub = BuildMinimalAssetJson(refJson, mediaParentGuid);
                    try
                    {
                        idMap[refGuid] = (await WriteAssetToTargetAsync(refGuid, refJson, mediaStub, sourceToken, target, targetToken)).id;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not upload global media {Guid}: {Error}", refGuid, ex.Message);
                        if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                            idMap[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
                    }
                    onItemComplete?.Invoke();
                }
                continue;
            }

            // Block — depth-first; if it already exists on target, just wire the reference
            var isLocalBlock = IsLocalContent(refJson);
            Guid blockParentGuid;
            if (isLocalBlock)
            {
                blockParentGuid = targetGuid;
            }
            else
            {
                blockParentGuid = ExtractParentGuid(refJson) ?? targetGuid;
                if (blockParentGuid != targetGuid)
                    await EnsureContentParentAsync(blockParentGuid, sourceBaseUrl, sourceToken, target, targetToken, new HashSet<Guid> { refGuid });
            }

            if (await ExistsOnTargetAsync(refGuid, target, targetToken))
            {
                var existingId = await GetTargetContentIdAsync(refGuid, target, targetToken);
                if (existingId.HasValue) idMap[refGuid] = existingId;
                _logger.LogDebug("Block {Guid} already exists on target — using existing ID {Id}", refGuid, existingId);
                onItemComplete?.Invoke();
                continue;
            }

            try
            {
                idMap[refGuid] = await TransferItemCoreAsync(
                    refGuid, refJson, sourceBaseUrl, sourceToken,
                    target, targetToken,
                    refGuid, blockParentGuid,
                    effectiveStatus, visited, onItemComplete);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not transfer block {Guid}: {Error}", refGuid, ex.Message);
                if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                    idMap[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
                onItemComplete?.Invoke();
            }
        }

        // ── Step 2: Full PUT ──────────────────────────────────────────────────
        // Full content (not a stub) ensures all required properties are present.
        // Also causes Optimizely to auto-create the "For This Page/Block" asset bucket.
        // Local media IDs are not yet known — corrected in step 4 if any were deferred.
        var baseJson = StripReadOnlyProperties(sourceJson, preserveParentLink: false);
        baseJson = InjectParentLink(baseJson, targetParentGuid);
        if (idMap.Count > 0) baseJson = InjectTargetContentIds(baseJson, idMap);
        baseJson = InjectStatus(baseJson, effectiveStatus);
        baseJson = InjectSortIndex(sourceJson, baseJson);

        await WriteToTargetAsync(targetGuid, baseJson, target, targetToken);

        // ── Step 3: Local media + XHTML inline images ─────────────────────────
        // Asset bucket now exists. Upload deferred local media, then XHTML inline images.
        var postIdMap = new Dictionary<Guid, int?>();
        var xhtmlUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deferredMediaUrls = new Dictionary<Guid, string>();

        foreach (var (refGuid, refJson) in deferredLocalMedia)
        {
            var mediaStub = BuildMinimalAssetJson(refJson, targetGuid);
            try
            {
                var (mediaId, mediaRelUrl) = await WriteAssetToTargetAsync(refGuid, refJson, mediaStub, sourceToken, target, targetToken);
                postIdMap[refGuid] = mediaId;
                if (mediaRelUrl != null)
                    deferredMediaUrls[refGuid] = mediaRelUrl;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not upload local media {Guid}: {Error}", refGuid, ex.Message);
                if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                    postIdMap[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
            }
            onItemComplete?.Invoke();
        }

        var seenImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var srcUrl in ExtractXhtmlImageUrls(sourceJson))
        {
            var relPath = ToRelativePath(srcUrl);
            if (string.IsNullOrEmpty(relPath)) continue;
            var pathForLookup = relPath.Split('?')[0];
            if (!seenImagePaths.Add(pathForLookup)) continue;

            var isContentAsset  = pathForLookup.StartsWith("/contentassets/", StringComparison.OrdinalIgnoreCase);
            var isGlobalAsset   = pathForLookup.StartsWith("/globalassets/",  StringComparison.OrdinalIgnoreCase);
            var isEpiServer     = pathForLookup.StartsWith("/EPiServer/",      StringComparison.OrdinalIgnoreCase)
                               || pathForLookup.StartsWith("/episerver/",      StringComparison.OrdinalIgnoreCase);
            var isPermanentLink = pathForLookup.StartsWith("/link/",           StringComparison.OrdinalIgnoreCase)
                               && pathForLookup.EndsWith(".aspx",              StringComparison.OrdinalIgnoreCase);
            if (!isContentAsset && !isGlobalAsset && !isEpiServer && !isPermanentLink) continue;

            Guid? imageGuid = null;
            try
            {
                imageGuid = await FindByUrlOnSourceAsync(pathForLookup, sourceBaseUrl, sourceToken);
                if (!imageGuid.HasValue && (isEpiServer || isPermanentLink))
                    imageGuid = TryResolveLocalContentGuid(pathForLookup);
            }
            catch { continue; }

            if (!imageGuid.HasValue)
            {
                _logger.LogWarning("Could not resolve XHTML image URL to a GUID: {Url}", relPath);
                continue;
            }

            if (!visited.Add(imageGuid.Value))
            {
                // Image was already uploaded (e.g. as deferred local media in step 3a).
                // CMA GET returns "url": null for contentassets, so check our upload cache first,
                // then fall back to CDV which returns the real canonical path.
                string existingUrl = null;
                if (deferredMediaUrls.TryGetValue(imageGuid.Value, out var cachedUrl))
                    existingUrl = cachedUrl;
                else
                    existingUrl = await GetTargetContentUrlViaCdvAsync(imageGuid.Value, target, targetToken);
                if (existingUrl != null) xhtmlUrlMap[relPath] = existingUrl;
                continue;
            }

            string imgJson;
            try { imgJson = await ReadFromSourceAsync(imageGuid.Value, sourceBaseUrl, sourceToken); }
            catch { continue; }

            // Use the CDV canonical URL to determine where the image actually lives.
            // The XHTML src may be an EPiServer internal URL (/EPiServer/CMS/Content/globalassets/...)
            // which doesn't start with /globalassets/ — so we can't rely on the raw src path.
            // The CDV by-GUID response always returns the clean canonical path.
            var canonicalPath = await GetSourceContentUrlAsync(imageGuid.Value, sourceBaseUrl, sourceToken);
            var isActuallyGlobal = !string.IsNullOrEmpty(canonicalPath)
                && canonicalPath.StartsWith("/globalassets/", StringComparison.OrdinalIgnoreCase);

            Guid imgParentGuid;
            if (isActuallyGlobal)
            {
                var lastSlash = canonicalPath.LastIndexOf('/');
                var folderPath = lastSlash > 0 ? canonicalPath[..(lastSlash + 1)] : "/globalassets/";
                imgParentGuid = await EnsureGlobalAssetFolderPathAsync(folderPath, sourceBaseUrl, sourceToken, target, targetToken) ?? targetGuid;
            }
            else
            {
                imgParentGuid = targetGuid;
            }

            try
            {
                var imgStub = BuildMinimalAssetJson(imgJson, imgParentGuid);
                var (_, targetRelUrl) = await WriteAssetToTargetAsync(imageGuid.Value, imgJson, imgStub, sourceToken, target, targetToken);
                if (targetRelUrl == null)
                    targetRelUrl = await GetTargetContentUrlViaCdvAsync(imageGuid.Value, target, targetToken);
                if (targetRelUrl != null)
                {
                    xhtmlUrlMap[relPath] = targetRelUrl;
                    _logger.LogDebug("XHTML image {Guid}: {Src} → {Tgt}", imageGuid.Value, relPath, targetRelUrl);
                }
                onItemComplete?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not upload XHTML image {Guid}: {Error}", imageGuid.Value, ex.Message);
                onItemComplete?.Invoke();
            }
        }

        // ── Step 4: Second PUT if anything was uploaded post-step-2 ──────────
        // onItemComplete fires here (after all work for this item is truly done).
        if (postIdMap.Count > 0 || xhtmlUrlMap.Count > 0)
        {
            var patchedJson = baseJson;
            if (postIdMap.Count > 0) patchedJson = InjectTargetContentIds(patchedJson, postIdMap);
            if (xhtmlUrlMap.Count > 0) patchedJson = RewriteXhtmlUrls(patchedJson, sourceBaseUrl, xhtmlUrlMap);
            var result = await WriteToTargetAsync(targetGuid, patchedJson, target, targetToken);
            onItemComplete?.Invoke();
            return result;
        }

        onItemComplete?.Invoke();
        return await GetTargetContentIdAsync(targetGuid, target, targetToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<ContentReference> CollectItems(ContentReference root, bool includeChildren)
    {
        var queue = new Queue<ContentReference>();
        var ordered = new List<ContentReference>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            ordered.Add(current);

            if (includeChildren)
            {
                foreach (var child in _contentLoader.GetChildren<IContent>(current).OrderBy(c => c.ContentLink.ID))
                    queue.Enqueue(child.ContentLink);
            }
        }

        return ordered;
    }

    private void LogRequest(string description, string url, string token, string requestBody = null)
    {
        var method = description.Split(' ')[0]; // e.g. "GET" or "PUT"
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($">>> {method} {url}");
        sb.AppendLine($"    Purpose: {description}");
        if (!string.IsNullOrEmpty(requestBody))
        {
            sb.AppendLine("    Content-Type: application/json");
            sb.AppendLine(requestBody);
        }
        _logger.LogDebug("{HttpRequest}", sb.ToString().TrimEnd());
    }

    private void LogResponse(string description, string url, int statusCode, string responseBody)
    {
        _logger.LogDebug("<<< {Status} {Description} {Url}\n{ResponseBody}", statusCode, description, url, responseBody);
    }

    private async Task<string> ReadFromSourceAsync(Guid guid, string sourceBaseUrl, string sourceToken)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{sourceBaseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";
        LogRequest("GET", url, sourceToken);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        LogResponse("GET", url, (int)response.StatusCode, body);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
        return body;
    }

    private static readonly string[] ReadOnlyProperties =
    [
        "existingLanguages", "masterLanguage", "saved", "created", "changed",
        "contentLink", "parentLink", "url", "routeSegment", "previewUrl",
        "publishedVersion", "statusReasons", "editUrl"
    ];

    private static string StripReadOnlyProperties(string json, bool preserveParentLink = false)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        foreach (var key in ReadOnlyProperties)
        {
            if (preserveParentLink && key == "parentLink") continue;
            node.Remove(key);
        }
        // Strip environment-specific integer ids from every content reference throughout
        // the document. Integer ids differ per environment; the target resolves by guidValue.
        StripContentReferenceIds(node);
        return node.ToJsonString();
    }

    private static readonly string[] ContentRefEnvFields = ["id", "workId", "url", "providerName", "expanded"];

    private static void StripContentReferenceIds(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("guidValue"))
            {
                // Keep only guidValue. id/workId are environment-specific integers;
                // url/providerName/expanded are source-server values that confuse the target.
                foreach (var field in ContentRefEnvFields)
                    obj.Remove(field);
            }
            foreach (var child in obj.Select(kvp => kvp.Value).ToList())
                if (child != null) StripContentReferenceIds(child);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) StripContentReferenceIds(item);
        }
    }

    // Strips properties of type PropertyBlob — server-managed blobs (thumbnail, etc.)
    // that are auto-generated on upload and cannot be written via the API.
    private static string StripBlobProperties(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        var toRemove = node
            .Where(kvp =>
                kvp.Value is JsonObject obj &&
                obj["propertyDataType"]?.GetValue<string>() == "PropertyBlob")
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in toRemove)
            node.Remove(key);
        return node.ToJsonString();
    }

    private static string InjectStatus(string json, string status)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        node["status"] = status;
        return node.ToJsonString();
    }

    // Reads SortIndex from the local CMS (via the content ID in the source JSON) and injects
    // it into the PUT body. The CMA GET response does not include sortIndex so it must be added
    // explicitly; the CMA PUT then applies it to the created/updated item.
    private string InjectSortIndex(string sourceJson, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(sourceJson);
            if (!doc.RootElement.TryGetProperty("contentLink", out var cl)) return json;
            if (!cl.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number) return json;
            var content = _contentLoader.Get<IContent>(new ContentReference(idProp.GetInt32()));

            // PageSortIndex is the built-in EPiServer property name for the sort index.
            // Fall back to reflection (SortIndex C# property) for types that expose it directly.
            int sortVal = 0;
            if (content.Property["PageSortIndex"]?.Value is int psi)
                sortVal = psi;
            if (sortVal == 0)
            {
                var prop = content.GetType().GetProperty("SortIndex");
                if (prop?.PropertyType == typeof(int) && prop.GetValue(content) is int si)
                    sortVal = si;
            }
            if (sortVal == 0) return json;

            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return json;
            node["sortIndex"] = sortVal;
            return node.ToJsonString();
        }
        catch { }
        return json;
    }

    private static string InjectParentLink(string json, Guid parentGuid)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        node["parentLink"] = new JsonObject { ["guidValue"] = parentGuid.ToString() };
        return node.ToJsonString();
    }

    // Walks the JSON tree and, for every content-reference object whose guidValue is in
    // targetIdMap, injects the corresponding target integer id + workId so the Content
    // Management API can bind the property (guidValue alone is not enough for media refs).
    private static string InjectTargetContentIds(string json, Dictionary<Guid, int?> targetIdMap)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return json;
        InjectContentIds(node, targetIdMap);
        return node.ToJsonString();
    }

    private static void InjectContentIds(JsonNode node, Dictionary<Guid, int?> targetIdMap)
    {
        if (node is JsonObject obj)
        {
            if (obj["guidValue"] is JsonValue guidVal)
            {
                try
                {
                    if (Guid.TryParse(guidVal.GetValue<string>(), out var guid) &&
                        targetIdMap.TryGetValue(guid, out var targetId) &&
                        targetId.HasValue)
                    {
                        obj["id"] = targetId.Value;
                        obj["workId"] = 0;
                    }
                }
                catch { }
            }
            foreach (var child in obj.Select(kvp => kvp.Value).ToList())
                if (child != null) InjectContentIds(child, targetIdMap);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) InjectContentIds(item, targetIdMap);
        }
    }

    private async Task<int?> GetTargetContentIdAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";
        LogRequest("GET (get target ID)", url, targetToken);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        LogResponse("GET (get target ID)", url, (int)response.StatusCode, body);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("contentLink", out var cl) &&
                cl.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.Number)
                return idProp.GetInt32();
        }
        catch { }
        return null;
    }

    // Resolves an EPiServer-internal or routed URL to a content GUID using the local CMS router.
    // Used for /EPiServer/CMS/Content/... paths that the CMS editor stores as permanent links.
    private Guid? TryResolveLocalContentGuid(string relPath)
    {
        try
        {
            var content = _urlResolver.Route(new UrlBuilder(relPath));
            if (content != null) return content.ContentGuid;
        }
        catch (Exception ex) { _logger.LogDebug("Local URL resolve failed for {Path}: {Error}", relPath, ex.Message); }
        return null;
    }

    // Fetches the content URL from the target environment after an asset has been written.
    // Used when the PUT response doesn't include a url field (so xhtmlUrlMap can still be populated).
    private async Task<string> GetTargetContentUrlAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";
        LogRequest("GET (fetch target URL after upload)", url, targetToken);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        LogResponse("GET (fetch target URL after upload)", url, (int)response.StatusCode, body);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("url", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String)
            {
                var fullUrl = urlProp.GetString() ?? "";
                if (Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri))
                    return uri.PathAndQuery;
                if (fullUrl.StartsWith('/')) return fullUrl;
            }
        }
        catch { }
        return null;
    }

    // Uses the Content Delivery API (not CMA) to resolve a canonical relative URL for content on the target.
    // CMA GET returns "url": null for contentassets images; CDV returns the real path via contentLink.url.
    private async Task<string> GetTargetContentUrlViaCdvAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var client = _httpClientFactory.CreateClient();
        var apiUrl = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/content/{guid}";
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);
        request.Headers.Add("Accept", "application/json");
        LogRequest("GET (CDV — resolve target asset URL)", apiUrl, targetToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        LogResponse("GET (CDV — resolve target asset URL)", apiUrl, (int)response.StatusCode, body);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("contentLink", out var cl) &&
                cl.TryGetProperty("url", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String)
            {
                var fullUrl = urlProp.GetString();
                if (string.IsNullOrEmpty(fullUrl)) return null;
                return Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : fullUrl;
            }
        }
        catch { }
        return null;
    }

    // Resolves the correct parent folder GUID on the TARGET for a globalassets image.
    // Reads parentLink.url from the source image JSON, extracts the relative path, looks it up
    // on the target, and creates any missing intermediate folders if needed.
    private async Task<Guid?> ResolveGlobalAssetFolderOnTargetAsync(
        string imgJson,
        DxpEnvironmentConfig target, string targetToken,
        string sourceBaseUrl, string sourceToken)
    {
        try
        {
            string parentUrl;
            using (var doc = JsonDocument.Parse(imgJson))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("parentLink", out var pl) || pl.ValueKind != JsonValueKind.Object) return null;
                if (!pl.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String) return null;
                parentUrl = urlProp.GetString();
            }
            if (string.IsNullOrEmpty(parentUrl)) return null;

            var relPath = Uri.TryCreate(parentUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : parentUrl;
            if (!relPath.EndsWith('/')) relPath += '/';
            if (!relPath.StartsWith("/globalassets/", StringComparison.OrdinalIgnoreCase)) return null;

            // Try direct lookup first; if not found, create the full folder path
            return await FindByUrlOnTargetAsync(relPath, target, targetToken)
                ?? await EnsureGlobalAssetFolderPathAsync(relPath, sourceBaseUrl, sourceToken, target, targetToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ResolveGlobalAssetFolderOnTargetAsync failed: {Error}", ex.Message);
        }
        return null;
    }

    // Walks the globalassets path segment-by-segment on the target, creating any folder that
    // doesn't exist yet. Returns the GUID of the deepest (innermost) folder.
    private async Task<Guid?> EnsureGlobalAssetFolderPathAsync(
        string relFolderPath,
        string sourceBaseUrl, string sourceToken,
        DxpEnvironmentConfig target, string targetToken)
    {
        // "/globalassets/events/conference/" → ["globalassets", "events", "conference"]
        var parts = relFolderPath.Trim('/').Split('/');

        // Resolve the globalassets root folder on the target (must already exist)
        var currentPath = "/globalassets/";
        var currentGuid = await FindByUrlOnTargetAsync(currentPath, target, targetToken);
        if (!currentGuid.HasValue)
        {
            _logger.LogWarning("Could not find /globalassets/ root on target — cannot create folder path {Path}", relFolderPath);
            return null;
        }

        // Walk each segment after "globalassets"
        for (int i = 1; i < parts.Length; i++)
        {
            var segment = parts[i];
            if (string.IsNullOrEmpty(segment)) continue;
            var nextPath = currentPath + segment + "/";

            var nextGuid = await FindByUrlOnTargetAsync(nextPath, target, targetToken);
            if (nextGuid.HasValue)
            {
                currentGuid = nextGuid;
                currentPath = nextPath;
                continue;
            }

            // Folder missing on target — look up its content type from source, then create it
            string leafContentType = "ContentFolder";
            var sourceFolderGuid = await FindByUrlOnSourceAsync(nextPath, sourceBaseUrl, sourceToken);
            if (sourceFolderGuid.HasValue)
            {
                try
                {
                    var folderJson = await ReadFromSourceAsync(sourceFolderGuid.Value, sourceBaseUrl, sourceToken);
                    using var doc = JsonDocument.Parse(folderJson);
                    if (doc.RootElement.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.Array)
                    {
                        var last = ct.EnumerateArray().LastOrDefault();
                        if (last.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(last.GetString()))
                            leafContentType = last.GetString();
                    }
                }
                catch { /* use default */ }
            }

            var newFolderGuid = Guid.NewGuid();
            var folderCreateJson = new JsonObject
            {
                ["parentLink"] = new JsonObject { ["guidValue"] = currentGuid.Value.ToString("D") },
                ["name"] = segment,
                ["status"] = "Published",
                ["contentType"] = new JsonArray(leafContentType)
            }.ToJsonString();

            _logger.LogDebug("Creating missing globalassets folder '{Segment}' at {Path} (parent={Parent})", segment, nextPath, currentGuid.Value);
            try
            {
                await WriteToTargetAsync(newFolderGuid, folderCreateJson, target, targetToken);
                currentGuid = newFolderGuid;
                currentPath = nextPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to create globalassets folder '{Segment}': {Error} — uploading to parent instead", segment, ex.Message);
                // currentGuid stays as the parent — best effort
            }
        }

        return currentGuid;
    }


    // Returns the canonical relative URL of a content item on the source, using the CDV by-GUID endpoint.
    // e.g. for an EPiServer internal image URL the CDV returns "/globalassets/events/image.jpg".
    private async Task<string> GetSourceContentUrlAsync(Guid guid, string sourceBaseUrl, string sourceToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var apiUrl = $"{sourceBaseUrl.TrimEnd('/')}/api/episerver/v3.0/content/{guid}";
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
            request.Headers.Add("Accept", "application/json");
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("contentLink", out var cl) &&
                cl.TryGetProperty("url", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String)
            {
                var fullUrl = urlProp.GetString();
                if (string.IsNullOrEmpty(fullUrl)) return null;
                return Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : fullUrl;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    // Looks up a content item GUID on the SOURCE environment by its relative URL.
    // Used for /globalassets/ paths where the GUID cannot be extracted directly.
    private async Task<Guid?> FindByUrlOnSourceAsync(string relativeUrl, string sourceBaseUrl, string sourceToken)
    {
        var client = _httpClientFactory.CreateClient();
        var apiUrl = $"{sourceBaseUrl.TrimEnd('/')}/api/episerver/v3.0/content/?contentURL={Uri.EscapeDataString(relativeUrl)}";
        LogRequest("GET (URL lookup on source)", apiUrl, sourceToken);
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
        request.Headers.Add("Accept", "application/json");
        var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        LogResponse("GET (URL lookup on source)", apiUrl, (int)response.StatusCode, json);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("XHTML URL lookup returned {Status} for {Url}", (int)response.StatusCode, relativeUrl);
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var element = root.ValueKind == JsonValueKind.Array
            ? (root.GetArrayLength() > 0 ? root[0] : (JsonElement?)null)
            : root;
        if (element is null) return null;
        if (element.Value.TryGetProperty("contentLink", out var cl) &&
            cl.TryGetProperty("guidValue", out var gp) &&
            Guid.TryParse(gp.GetString(), out var guid))
            return guid;
        _logger.LogWarning("XHTML URL lookup response had no guidValue for {Url}", relativeUrl);
        return null;
    }

    // Extracts all image src attribute values from PropertyXhtmlString fields in a JSON document.
    private static List<string> ExtractXhtmlImageUrls(string json)
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

    private static void CollectXhtmlUrls(JsonElement element, List<string> urls)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("propertyDataType", out var pdt) &&
                pdt.GetString() == "PropertyXhtmlString" &&
                element.TryGetProperty("value", out var val) &&
                val.ValueKind == JsonValueKind.String)
            {
                var html = val.GetString() ?? "";
                foreach (Match m in Regex.Matches(html, @"src=""([^""]+)"""))
                    urls.Add(m.Groups[1].Value);
            }
            foreach (var prop in element.EnumerateObject())
                CollectXhtmlUrls(prop.Value, urls);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectXhtmlUrls(item, urls);
        }
    }

    // Strips the source environment origin (scheme+host) from all image src URLs inside
    // PropertyXhtmlString values so that relative paths (/contentassets/... /globalassets/...)
    // resolve correctly on any target environment.
    // xhtmlUrlMap (optional): source relative path → target relative path. Applied after stripping
    // the origin so that bucket-GUID mismatches between environments are fixed up.
    private static string RewriteXhtmlUrls(string json, string sourceBaseUrl, Dictionary<string, string> xhtmlUrlMap = null)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        try
        {
            var origin = new Uri(sourceBaseUrl).GetLeftPart(UriPartial.Authority);
            RewriteXhtmlNodes(node, origin, xhtmlUrlMap);
        }
        catch { }
        return node.ToJsonString();
    }

    private static void RewriteXhtmlNodes(JsonNode node, string origin, Dictionary<string, string> xhtmlUrlMap = null)
    {
        if (node is JsonObject obj)
        {
            if (obj["propertyDataType"]?.GetValue<string>() == "PropertyXhtmlString" &&
                obj["value"] is JsonValue val)
            {
                try
                {
                    var html = val.GetValue<string>() ?? "";
                    if (html.Contains(origin, StringComparison.OrdinalIgnoreCase))
                        html = html.Replace(origin, "", StringComparison.OrdinalIgnoreCase);
                    if (xhtmlUrlMap != null)
                        foreach (var (src, tgt) in xhtmlUrlMap)
                            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                                html = html.Replace(src, tgt, StringComparison.OrdinalIgnoreCase);
                    obj["value"] = html;
                }
                catch { }
            }
            foreach (var child in obj.Select(kvp => kvp.Value).ToList())
                if (child != null) RewriteXhtmlNodes(child, origin, xhtmlUrlMap);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) RewriteXhtmlNodes(item, origin, xhtmlUrlMap);
        }
    }

    // Converts an absolute URL to its path component, or returns the input unchanged if
    // it is already relative.
    private static string ToRelativePath(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.PathAndQuery;
        return url.StartsWith('/') ? url : null;
    }

    private static List<Guid> ExtractContentReferenceGuids(string json)
    {
        var guids = new List<Guid>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            CollectGuids(doc.RootElement, guids, new HashSet<Guid>());
        }
        catch { /* malformed JSON — skip */ }
        return guids;
    }

    private static void CollectGuids(JsonElement element, List<Guid> guids, HashSet<Guid> seen)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == "guidValue" &&
                    prop.Value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(prop.Value.GetString(), out var guid) &&
                    seen.Add(guid))
                    guids.Add(guid);

                CollectGuids(prop.Value, guids, seen);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectGuids(item, guids, seen);
        }
    }

    private async Task<(int? id, string targetRelUrl)> WriteAssetToTargetAsync(
        Guid guid,
        string originalJson,
        string cleanJson,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        var binaryUrl = GetAssetBinaryUrl(originalJson);

        if (binaryUrl == null)
            return (await WriteToTargetAsync(guid, cleanJson, target, targetToken), null);

        var client = _httpClientFactory.CreateClient();
        LogRequest("GET (binary download)", binaryUrl, sourceToken);
        var dlRequest = new HttpRequestMessage(HttpMethod.Get, binaryUrl);
        dlRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
        var dlResponse = await client.SendAsync(dlRequest, HttpCompletionOption.ResponseHeadersRead);
        _logger.LogDebug("<<< {Status} GET (binary download) {Url} — {Bytes} bytes", (int)dlResponse.StatusCode, binaryUrl, dlResponse.Content.Headers.ContentLength ?? -1);

        if (!dlResponse.IsSuccessStatusCode)
        {
            var dlBody = await dlResponse.Content.ReadAsStringAsync();
            LogResponse("GET (binary download)", binaryUrl, (int)dlResponse.StatusCode, dlBody);
            _logger.LogWarning("Could not download binary for asset {Guid} from {Url} ({Status}) — transferring metadata only", guid, binaryUrl, (int)dlResponse.StatusCode);
            return (await WriteToTargetAsync(guid, cleanJson, target, targetToken), null);
        }

        using var binaryStream = new MemoryStream();
        await dlResponse.Content.CopyToAsync(binaryStream);
        var mimeType = GetAssetMimeType(originalJson) ?? dlResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = GetAssetFileName(originalJson) ?? guid.ToString();

        // Multipart PUT: "content" part = minimal JSON, "file" part = binary.
        // Field name "file" confirmed working against EPiServer.ContentManagementApi v3.12.7.
        var url = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";
        var activeJson = cleanJson;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            _logger.LogDebug(">>> PUT {Url}\n    Purpose: PUT (multipart) — uploading asset binary + metadata to target\n    Content-Type: multipart/form-data\n[content part]\n{Json}\n[file part] name={FileName} mimeType={Mime} size={Bytes}",
                url, activeJson, fileName, mimeType, binaryStream.Length);
            var putRequest = new HttpRequestMessage(HttpMethod.Put, url);
            putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetToken);

            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(activeJson, Encoding.UTF8, "application/json"), "content");
            binaryStream.Position = 0;
            var binaryPart = new StreamContent(binaryStream);
            binaryPart.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            multipart.Add(binaryPart, "file", fileName);
            putRequest.Content = multipart;

            var response = await client.SendAsync(putRequest);
            var responseBody = await response.Content.ReadAsStringAsync();
            LogResponse("PUT (multipart)", url, (int)response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    int? id = null;
                    string targetRelUrl = null;
                    if (doc.RootElement.TryGetProperty("contentLink", out var cl) &&
                        cl.TryGetProperty("id", out var idProp) &&
                        idProp.ValueKind == JsonValueKind.Number)
                        id = idProp.GetInt32();
                    if (doc.RootElement.TryGetProperty("url", out var urlProp) &&
                        urlProp.ValueKind == JsonValueKind.String)
                    {
                        var fullUrl = urlProp.GetString() ?? "";
                        targetRelUrl = Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri)
                            ? uri.PathAndQuery
                            : (fullUrl.StartsWith('/') ? fullUrl : null);
                    }
                    return (id, targetRelUrl);
                }
                catch { }
                return (null, null);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var unknownProp = ExtractPropertyNotFoundName(responseBody);
                if (unknownProp != null)
                {
                    _logger.LogDebug("Stripping unknown asset property '{Prop}' from {Guid} and retrying", unknownProp, guid);
                    activeJson = StripNamedProperty(activeJson, unknownProp);
                    continue;
                }
            }

            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        throw new HttpRequestException("Asset write failed after stripping multiple unknown properties");
    }

    private static string GetAssetBinaryUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!IsMediaContent(root)) return null;
            if (root.TryGetProperty("language", out var lang) &&
                lang.TryGetProperty("link", out var link) &&
                link.ValueKind == JsonValueKind.String)
                return link.GetString();
        }
        catch { }
        return null;
    }

    // Ensures a content item (typically a shared folder) exists on target, creating it from
    // source if not. Recurses up the ancestry so the full folder chain is created bottom-up.
    // `seen` prevents re-entering GUIDs already in progress (guards against loops).
    private async Task EnsureContentParentAsync(
        Guid parentGuid,
        string sourceBaseUrl,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken,
        HashSet<Guid> seen)
    {
        if (seen != null && seen.Contains(parentGuid)) return;
        if (await ExistsOnTargetAsync(parentGuid, target, targetToken)) return;

        seen ??= new HashSet<Guid>();
        seen.Add(parentGuid);

        try
        {
            var json = await ReadFromSourceAsync(parentGuid, sourceBaseUrl, sourceToken);
            // Ensure this item's own parent exists first (depth-first up the tree)
            var grandparentGuid = ExtractParentGuid(json);
            if (grandparentGuid.HasValue)
                await EnsureContentParentAsync(grandparentGuid.Value, sourceBaseUrl, sourceToken, target, targetToken, seen);
            var cleanJson = StripReadOnlyProperties(json, preserveParentLink: true);
            await WriteToTargetAsync(parentGuid, cleanJson, target, targetToken);
            _logger.LogDebug("Created missing parent {Guid} on target", parentGuid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not create missing parent {Guid} on target: {Error}", parentGuid, ex.Message);
        }
    }

    private static Guid? ExtractParentGuid(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("parentLink", out var pl) &&
                pl.TryGetProperty("guidValue", out var gv) &&
                Guid.TryParse(gv.GetString(), out var guid))
                return guid;
        }
        catch { }
        return null;
    }

    private static string ExtractPropertyNotFoundName(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) &&
                code.GetString() == "PropertyNotFound" &&
                root.TryGetProperty("detail", out var detail))
            {
                var msg = detail.GetString() ?? "";
                var start = msg.IndexOf('\'') + 1;
                var end = msg.IndexOf('\'', start);
                if (start > 0 && end > start) return msg[start..end];
            }
        }
        catch { }
        return null;
    }

    private static string StripNamedProperty(string json, string propertyName)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        node.Remove(propertyName);
        return node.ToJsonString();
    }

    // Returns true if the content is page-local (url contains /contentassets/).
    // Local content goes under the page's own "For This Page" bucket.
    // Global content (/globalassets/ or no URL) preserves its source parentLink.
    private static bool IsLocalContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("url", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String)
                return (urlProp.GetString() ?? "").Contains("/contentassets/", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return false; // no URL → treat as global, preserve parentLink
    }

    // Block detection: we treat anything that is NOT media and NOT a page as block-like.
    // Checking for the literal string "Block" in contentType is too narrow — Shared Blocks
    // and custom shared content types don't always carry "Block" in their type hierarchy,
    // so they would silently fall through to the page-reference path and skip XHTML scanning.
    private static bool IsBlockContent(JsonElement root) =>
        !IsMediaContent(root) && !IsPageContent(root);

    private static bool IsPageContent(JsonElement root)
    {
        if (root.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.Array)
            foreach (var item in ct.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), "Page", StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static bool IsMediaContent(JsonElement root)
    {
        if (root.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.Array)
            foreach (var item in ct.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), "Media", StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }


    // Builds the minimal JSON body for a page or block stub — just enough for Optimizely to create
    // the item and auto-generate its "For This Page/Block" asset folder on the target.
    private static string BuildMinimalContentJson(string sourceJson, Guid parentGuid, string status = "CheckedOut")
    {
        string name = null;
        string language = null;
        var contentTypes = new JsonArray();
        try
        {
            using var doc = JsonDocument.Parse(sourceJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var nameProp))
                name = nameProp.GetString();
            if (root.TryGetProperty("language", out var langProp) && langProp.ValueKind == JsonValueKind.Object &&
                langProp.TryGetProperty("name", out var langName))
                language = langName.GetString();
            if (root.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.Array)
                foreach (var item in ct.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is string s)
                        contentTypes.Add(s);
        }
        catch { }

        var obj = new JsonObject
        {
            ["parentLink"] = new JsonObject { ["guidValue"] = parentGuid.ToString("D") },
            ["name"] = name ?? "content",
            ["status"] = status,
            ["contentType"] = contentTypes
        };
        if (!string.IsNullOrEmpty(language))
            obj["language"] = new JsonObject { ["name"] = language };
        return obj.ToJsonString();
    }

    // Builds the minimal JSON body that the Content Management API needs to create/update an asset.
    // Full source metadata causes 400s; only parentLink, the leaf contentType, name and status are required.
    private static string BuildMinimalAssetJson(string sourceJson, Guid parentGuid, string status = "Published")
    {
        string name = null;
        string leafContentType = null;
        try
        {
            using var doc = JsonDocument.Parse(sourceJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var nameProp))
                name = nameProp.GetString();
            if (root.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.Array)
            {
                // Take the last (most-specific) type — e.g. ["Image","Media","ImageFile"] → "ImageFile"
                foreach (var item in ct.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        leafContentType = item.GetString();
            }
        }
        catch { }

        var obj = new JsonObject
        {
            ["parentLink"] = new JsonObject { ["guidValue"] = parentGuid.ToString("D") },
            ["name"] = name ?? "asset",
            ["status"] = status
        };
        if (!string.IsNullOrEmpty(leafContentType))
            obj["contentType"] = new JsonArray(leafContentType);

        return obj.ToJsonString();
    }

    private static string GetAssetMimeType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mimeType", out var mime) &&
                mime.TryGetProperty("value", out var val) &&
                val.ValueKind == JsonValueKind.String)
                return val.GetString();
        }
        catch { }
        return null;
    }

    private static string GetAssetFileName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
                return name.GetString();
        }
        catch { }
        return null;
    }

    private async Task<int?> WriteToTargetAsync(Guid guid, string contentJson, DxpEnvironmentConfig target, string token)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{target.BaseUrl.TrimEnd('/')}/api/episerver/v3.0/contentmanagement/{guid}";
        var activeJson = contentJson;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            LogRequest("PUT", url, token, activeJson);
            var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(activeJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            LogResponse("PUT", url, (int)response.StatusCode, body);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("contentLink", out var cl) &&
                        cl.TryGetProperty("id", out var idProp) &&
                        idProp.ValueKind == JsonValueKind.Number)
                        return idProp.GetInt32();
                }
                catch { }
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var unknownProp = ExtractPropertyNotFoundName(body);
                if (unknownProp != null)
                {
                    _logger.LogDebug("Stripping unknown property '{Prop}' from {Guid} and retrying", unknownProp, guid);
                    activeJson = StripNamedProperty(activeJson, unknownProp);
                    continue;
                }
            }

            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
        }

        throw new HttpRequestException("Write failed after stripping multiple unknown properties");
    }

    private (Guid? guid, string path) GetSiteRootFallback()
    {
        try
        {
            var startRef = SiteDefinition.Current?.StartPage ?? ContentReference.StartPage;
            if (ContentReference.IsNullOrEmpty(startRef)) return (null, null);
            var startPage = _contentLoader.Get<IContent>(startRef);
            return (startPage.ContentGuid, $"{startPage.Name} (site root)");
        }
        catch { return (null, null); }
    }

    private bool IsSiteRootGuid(Guid guid)
    {
        try
        {
            var startRef = SiteDefinition.Current?.StartPage ?? ContentReference.StartPage;
            if (ContentReference.IsNullOrEmpty(startRef)) return false;
            return _contentLoader.Get<IContent>(startRef).ContentGuid == guid;
        }
        catch { return false; }
    }

    private static DxpEnvironmentConfig ResolveEnvironment(DxpTransferSettings settings, string name) =>
        name?.ToLowerInvariant() switch
        {
            "integration" => settings.Integration,
            "preproduction" => settings.Preproduction,
            "production" => settings.Production,
            _ => null
        };

    private static ContentReference ParseContentReference(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ContentReference.EmptyReference;

        var parts = id.Split('_', ':');
        if (int.TryParse(parts[0], out var contentId))
            return new ContentReference(contentId);

        return ContentReference.EmptyReference;
    }
}
