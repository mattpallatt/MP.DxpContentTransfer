using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DxpContentTransfer.Models;
using EPiServer;
using EPiServer.Core;
using EPiServer.Core.Html.StringParsing;
using EPiServer.DataAbstraction;
using EPiServer.SpecializedProperties;
using EPiServer.Web;
using EPiServer.Web.Routing;
using Microsoft.Extensions.Logging;
using static DxpContentTransfer.Services.JsonVisitors;

namespace DxpContentTransfer.Services;

public class ContentTransferService : IContentTransferService
{
    private readonly IDxpSettingsService _settingsService;
    private readonly IEnvironmentTokenService _tokenService;
    private readonly CmaClient _cma;
    private readonly IContentLoader _contentLoader;
    private readonly IUrlResolver _urlResolver;
    private readonly IContentTypeRepository _contentTypeRepository;
    private readonly ILogger<ContentTransferService> _logger;

    // A single CMA PUT can require several retries as we strip unknown/required properties one at
    // a time off the body; this bounds that loop so a persistent server error can't spin forever.
    private const int MaxWriteAttempts = 25;

    // Bumped whenever behaviour changes, and logged at the start of every pre-check/transfer so the
    // running build can be confirmed from the logs. Keep in sync with the package version.
    private const string BuildMarker = "0.7.1 (pinned-master picker + environment indicator)";

    public ContentTransferService(
        IDxpSettingsService settingsService,
        IEnvironmentTokenService tokenService,
        CmaClient cma,
        IContentLoader contentLoader,
        IUrlResolver urlResolver,
        IContentTypeRepository contentTypeRepository,
        ILogger<ContentTransferService> logger)
    {
        _settingsService = settingsService;
        _tokenService = tokenService;
        _cma = cma;
        _contentLoader = contentLoader;
        _urlResolver = urlResolver;
        _contentTypeRepository = contentTypeRepository;
        _logger = logger;
    }

    // ── Pre-check ─────────────────────────────────────────────────────────────

    public async Task<PreCheckResult> PreCheckAsync(
        string contentId,
        string targetEnvironmentName,
        bool includeChildren,
        bool overwriteMatchingIds)
    {
        _logger.LogInformation("DXP Content Transfer pre-check — build {Build}", BuildMarker);
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);

        if (target == null || !target.IsConfigured)
            return new PreCheckResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        var contentRef = ParseContentReference(contentId);
        if (contentRef == ContentReference.EmptyReference)
            return new PreCheckResult { Success = false, ErrorMessage = $"Invalid content reference: {contentId}" };

        // Pre-load ALL IContentLoader/IUrlResolver data synchronously BEFORE the first await.
        // IDatabaseExecutor is not thread-safe; any await can resume on a different thread pool thread.
        var batchGuidMap = new Dictionary<Guid, Guid>();
        // Source GUIDs of batch items whose action is not Overwrite (i.e. being created fresh).
        // Any descendant of these must also be created fresh, even if its GUID exists elsewhere on target.
        var batchForcedNew = new HashSet<Guid>();
        var depSeen = new HashSet<Guid>();
        var (siteRootGuid, siteRootPath) = GetSiteRootFallback();

        var itemContexts = CollectItems(contentRef, includeChildren)
            .Select(itemRef =>
            {
                IContent content = null;
                try { if (!ContentReference.IsNullOrEmpty(itemRef)) content = _contentLoader.Get<IContent>(itemRef, LanguageSelector.AutoDetect(true)); }
                catch (Exception ex) { _logger.LogDebug("Pre-check: could not load content {Ref}: {Error}", itemRef, ex.Message); }

                Guid? directParentSourceGuid = null;
                string directParentName = null;
                if (content != null && !ContentReference.IsNullOrEmpty(content.ParentLink))
                {
                    try
                    {
                        var parent = _contentLoader.Get<IContent>(content.ParentLink, LanguageSelector.AutoDetect(true));
                        directParentSourceGuid = parent.ContentGuid;
                        directParentName = parent.Name;
                    }
                    catch (Exception ex) { _logger.LogDebug("Pre-check: could not load parent of {Ref}: {Error}", itemRef, ex.Message); }
                }

                var ancestorsWithUrls = new List<(IContent ancestor, string url)>();
                if (content != null)
                {
                    foreach (var a in BuildAncestorChain(content.ParentLink))
                    {
                        string url = null;
                        try { url = _urlResolver.GetUrl(a.ContentLink); }
                        catch { }
                        ancestorsWithUrls.Add((a, url));
                    }
                }

                var deps = new List<DependencyNode>();
                if (content is PageData)
                {
                    depSeen.Add(content.ContentGuid);
                    deps = ScanContentDependencies(content, depSeen);
                }

                return (itemRef, content, directParentSourceGuid, directParentName, ancestorsWithUrls, deps);
            })
            .ToList();

        // The languages the selected content exists in — read here (before the first await) since
        // ILocalizable.ExistingLanguages is DB-backed. Union across all items, friendly name + locale.
        var availableLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in itemContexts)
            if (ctx.content is ILocalizable localizable && localizable.ExistingLanguages != null)
                foreach (var culture in localizable.ExistingLanguages)
                    if (culture != null && !string.IsNullOrEmpty(culture.Name))
                        availableLanguages[culture.Name] = culture.EnglishName;

        // All IContentLoader work done — now safe to await.
        string targetToken;
        try { targetToken = await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new PreCheckResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        var result = new PreCheckResult
        {
            Success = true,
            AvailableLanguages = availableLanguages
                .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new LanguageOption { Code = kv.Key, DisplayName = kv.Value })
                .ToList()
        };

        foreach (var (itemRef, content, directParentSourceGuid, directParentName, ancestorsWithUrls, deps) in itemContexts)
        {
            var item = await BuildPreCheckItemAsync(
                itemRef, content,
                directParentSourceGuid, directParentName, ancestorsWithUrls,
                siteRootGuid, siteRootPath,
                target, targetToken, overwriteMatchingIds, batchGuidMap, batchForcedNew);

            item.Dependencies = deps;
            result.Items.Add(item);

            if (item.ContentGuid != Guid.Empty)
            {
                var targetGuid = item.Action == PreCheckAction.CreateNew
                    ? (item.NewGuid ?? item.ContentGuid)
                    : item.ContentGuid;
                batchGuidMap[item.ContentGuid] = targetGuid;

                // Not an in-place overwrite → children cannot exist in context on the target
                if (item.Action != PreCheckAction.Overwrite)
                    batchForcedNew.Add(item.ContentGuid);
            }
        }

        return result;
    }

    // Scans a page, block, or inline PropertyBlock's properties to build the nested dependency
    // tree shown in the plan. Accepts IContentData so it can recurse into PropertyBlock values
    // (inline blocks have no GUID and don't implement IContent).
    // Uses IContentLoader (local, fast) so no API calls needed at pre-check time.
    private List<DependencyNode> ScanContentDependencies(IContentData contentData, HashSet<Guid> seen)
    {
        var nodes = new List<DependencyNode>();
        if (contentData == null) return nodes;

        foreach (var prop in contentData.Property)
        {
            if (prop is PropertyContentArea area && area.Value is ContentArea contentArea)
            {
                foreach (var areaItem in contentArea.Items)
                {
                    try
                    {
                        if (IsSystemContentReference(areaItem.ContentLink)) continue;
                        var child = _contentLoader.Get<IContent>(areaItem.ContentLink, LanguageSelector.AutoDetect(true));
                        if (!seen.Add(child.ContentGuid)) continue;
                        if (child is IContentMedia)
                        {
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = GetMediaNodeType(child.Name), ContentGuid = child.ContentGuid.ToString("D") });
                        }
                        else if (child is PageData)
                        {
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Page", ContentGuid = child.ContentGuid.ToString("D") });
                        }
                        else
                        {
                            var blockNode = new DependencyNode { Name = child.Name, NodeType = "Block", ContentGuid = child.ContentGuid.ToString("D") };
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
                    if (IsSystemContentReference(cref.ContentLink)) continue;
                    if (IsSystemPropertyName(prop.Name)) continue;
                    var child = _contentLoader.Get<IContent>(cref.ContentLink, LanguageSelector.AutoDetect(true));
                    if (!seen.Add(child.ContentGuid)) continue;
                    if (child is IContentMedia)
                        nodes.Add(new DependencyNode { Name = child.Name, NodeType = GetMediaNodeType(child.Name), ContentGuid = child.ContentGuid.ToString("D") });
                    else if (child is PageData)
                        nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Page", ContentGuid = child.ContentGuid.ToString("D") });
                }
                catch { }
            }
            else if (prop is PropertyContentReferenceList crefList && crefList.Value is IList<ContentReference> crefListItems)
            {
                foreach (var refVal in crefListItems)
                {
                    try
                    {
                        if (IsSystemContentReference(refVal)) continue;
                        var child = _contentLoader.Get<IContent>(refVal, LanguageSelector.AutoDetect(true));
                        if (!seen.Add(child.ContentGuid)) continue;
                        if (child is IContentMedia)
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = GetMediaNodeType(child.Name), ContentGuid = child.ContentGuid.ToString("D") });
                        else if (child is PageData)
                            nodes.Add(new DependencyNode { Name = child.Name, NodeType = "Page", ContentGuid = child.ContentGuid.ToString("D") });
                    }
                    catch { }
                }
            }
            else if (prop.Value is BlockData inlineBlock)
            {
                // PropertyBlock — inline block embedded directly in the property (no separate GUID).
                // Recurse to surface any media or content refs it contains.
                try
                {
                    var innerNodes = ScanContentDependencies(inlineBlock, seen);
                    nodes.AddRange(innerNodes);
                }
                catch { }
            }
        }

        // Scan XHTML properties for actually-referenced inline assets (img src + a href).
        // Use type-name detection + PropertyLongString.LongString to get the raw stored XHTML
        // without requiring an HTTP context (which XhtmlString.ToHtmlString() may need).
        var seenInlineUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentName = (contentData as IContent)?.Name ?? "inline block";
        _logger.LogDebug("Pre-check XHTML scan for '{Content}': scanning {Count} properties", contentName, contentData.Property.Count);
        foreach (var prop in contentData.Property)
        {
            var typeName = prop.GetType().Name;
            if (!typeName.Contains("Xhtml", StringComparison.OrdinalIgnoreCase)) continue;

            var xhtml = prop.Value as XhtmlString;
            string html = null;
            try
            {
                // Try value cast first
                if (xhtml != null)
                    html = xhtml.ToString();
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
                _logger.LogDebug("Pre-check XHTML read failed for '{Content}'.{Prop}({Type}): {Error}", contentName, prop.Name, typeName, ex.Message);
            }

            _logger.LogDebug("Pre-check XHTML prop '{Content}'.{Prop}({Type}): {Len} chars", contentName, prop.Name, typeName, html?.Length ?? -1);

            // Inline content blocks (epi-contentfragment divs). XhtmlString.ToString()/the raw string
            // doesn't reliably carry the fragment divs (anchors survive, fragments don't), but the
            // parsed fragment list always does — so walk that directly rather than scraping markup.
            if (xhtml != null)
                foreach (var fragment in xhtml.Fragments)
                {
                    if (fragment is not ContentFragment cf || cf.ContentGuid == Guid.Empty) continue;
                    if (!seenInlineUrls.Add("frag:" + cf.ContentGuid.ToString("D"))) continue;
                    string fragName = null;
                    try { fragName = cf.GetContent()?.Name; } catch { }
                    nodes.Add(new DependencyNode
                    {
                        Name = fragName ?? cf.ContentGuid.ToString("D"),
                        NodeType = "InlineBlock",
                        ContentGuid = cf.ContentGuid.ToString("D")
                    });
                }

            if (string.IsNullOrEmpty(html)) continue;

            foreach (Match m in Regex.Matches(html, @"(?:src|href)=""([^""]+)""", RegexOptions.IgnoreCase))
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
        IContent content,
        Guid? directParentSourceGuid,
        string directParentName,
        List<(IContent ancestor, string url)> ancestorsWithUrls,
        Guid? siteRootGuid,
        string siteRootPath,
        DxpEnvironmentConfig target,
        string targetToken,
        bool overwriteMatchingIds,
        Dictionary<Guid, Guid> batchGuidMap,
        HashSet<Guid> batchForcedNew)
    {
        if (content == null)
        {
            return new PreCheckItemResult
            {
                ContentId = contentRef.ToString(),
                ContentName = "?",
                Action = PreCheckAction.Unresolvable,
                Notes = "Could not load source content."
            };
        }

        var guid = content.ContentGuid;
        var name = content.Name;

        // All IContentLoader/IUrlResolver data was pre-loaded by the caller — safe to await immediately.
        var existsOnTarget = await ExistsOnTargetAsync(guid, target, targetToken);

        // If the direct parent is in the batch and is being created fresh (not overwritten in-place),
        // this item cannot exist on the target in the correct context — even if its GUID matches
        // something elsewhere. Treat it as new so it gets created under its proper parent.
        if (existsOnTarget && directParentSourceGuid.HasValue && batchForcedNew.Contains(directParentSourceGuid.Value))
            existsOnTarget = false;

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

        // Check the batch first: if the direct parent is also being transferred we know its target GUID
        // without needing it to exist on target yet.
        Guid? parentGuid = null;
        string parentPath = null;

        if (directParentSourceGuid.HasValue && batchGuidMap.TryGetValue(directParentSourceGuid.Value, out var batchParentTargetGuid))
        {
            parentGuid = batchParentTargetGuid;
            parentPath = (directParentName ?? "(parent being transferred)") + " (being transferred)";
        }
        else
        {
            (parentGuid, parentPath) = await ResolveTargetParentAsync(ancestorsWithUrls, target, targetToken);
        }

        var isRootFallback = false;
        if (!parentGuid.HasValue)
        {
            parentGuid = siteRootGuid;
            parentPath = siteRootPath;
            isRootFallback = true;
        }

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
        var r = await _cma.GetManagementAsync(target.BaseUrl, targetToken, guid, "exists check");
        // 401/403 means the content EXISTS but our identity can't read it — treat as present
        if (r.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        return r.Status == HttpStatusCode.OK;
    }

    private async Task<(Guid? guid, string path)> ResolveTargetParentAsync(
        List<(IContent ancestor, string url)> ancestorsWithUrls,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        // Phase 1: GUID-based — walk up source ancestry, return first that exists on target
        foreach (var (ancestor, _) in ancestorsWithUrls)
        {
            if (ancestor.ContentGuid == Guid.Empty) continue;
            if (await ExistsOnTargetAsync(ancestor.ContentGuid, target, targetToken))
                return (ancestor.ContentGuid, ancestor.Name);
        }

        // Phase 2: URL-based — URLs were pre-computed by the caller before any await
        foreach (var (ancestor, relativeUrl) in ancestorsWithUrls)
        {
            if (string.IsNullOrWhiteSpace(relativeUrl) ||
                relativeUrl == "/" ||
                relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                continue;

            var targetGuid = await FindByUrlOnTargetAsync(relativeUrl, target, targetToken);
            if (targetGuid.HasValue)
                return (targetGuid, $"{ancestor.Name} (matched by URL '{relativeUrl}')");
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
                var ancestor = _contentLoader.Get<IContent>(current, LanguageSelector.AutoDetect(true));
                chain.Add(ancestor);
                current = ancestor.ParentLink;
            }
            catch { break; }
        }

        return chain; // nearest ancestor first
    }

    // Looks up a content item GUID by its relative URL via the CDV endpoint. Source and target
    // lookups are identical apart from which environment they hit, so they share this one body.
    private async Task<Guid?> FindByUrlAsync(string baseUrl, string token, string relativeUrl, string purpose)
    {
        var r = await _cma.GetByUrlAsync(baseUrl, token, relativeUrl, purpose);
        // A not-found here is normal control flow (e.g. probing whether a folder exists), so this
        // stays at Debug rather than Warning to avoid log noise.
        if (!r.IsSuccess)
        {
            _logger.LogDebug("URL lookup returned {Status} for {Url}", (int)r.Status, relativeUrl);
            return null;
        }
        return ExtractGuidFromContentResponse(r.Body, relativeUrl);
    }

    private Task<Guid?> FindByUrlOnTargetAsync(string relativeUrl, DxpEnvironmentConfig target, string targetToken) =>
        FindByUrlAsync(target.BaseUrl, targetToken, relativeUrl, "URL lookup on target");

    // Extracts contentLink.guidValue from a CDV content response that may be a single object or
    // an array (the by-URL endpoint returns an array when multiple matches exist).
    private Guid? ExtractGuidFromContentResponse(string json, string relativeUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var element = root.ValueKind == JsonValueKind.Array
                ? (root.GetArrayLength() > 0 ? root[0] : (JsonElement?)null)
                : root;
            if (element is null) return null;
            if (element.Value.TryGetProperty("contentLink", out var cl) &&
                cl.TryGetProperty("guidValue", out var guidProp) &&
                Guid.TryParse(guidProp.GetString(), out var guid))
                return guid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not parse content response for {Url}: {Error}", relativeUrl, ex.Message);
        }
        _logger.LogDebug("URL lookup response had no guidValue for {Url}", relativeUrl);
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
        Action onItemComplete = null,
        IReadOnlyCollection<string> selectedLanguages = null)
    {
        _logger.LogInformation("DXP Content Transfer starting — build {Build}", BuildMarker);
        var settings = _settingsService.Get();
        var target = ResolveEnvironment(settings, targetEnvironmentName);
        var source = ResolveEnvironment(settings, sourceEnvironmentName);

        if (target == null || !target.IsConfigured)
            return new TransferResult { Success = false, ErrorMessage = $"Target environment '{targetEnvironmentName}' is not configured." };

        if (source == null || !source.IsConfigured)
            return new TransferResult { Success = false, ErrorMessage = $"Source environment '{sourceEnvironmentName}' is not configured. Ensure credentials are saved in settings." };

        var contentRef = ParseContentReference(contentId);
        if (contentRef == ContentReference.EmptyReference)
            return new TransferResult { Success = false, ErrorMessage = $"Invalid content reference: {contentId}" };

        // Pre-load ALL IContentLoader/IUrlResolver data synchronously BEFORE the first await.
        var itemContexts = CollectItems(contentRef, includeChildren)
            .Select(itemRef =>
            {
                IContent content = null;
                try { content = _contentLoader.Get<IContent>(itemRef, LanguageSelector.AutoDetect(true)); }
                catch (Exception ex) { _logger.LogDebug("Transfer: could not load content {Ref}: {Error}", itemRef, ex.Message); }

                string contentName = null;
                Guid sourceGuid = Guid.Empty;
                var ancestorsWithUrls = new List<(IContent ancestor, string url)>();

                if (content != null)
                {
                    contentName = content.Name;
                    sourceGuid = content.ContentGuid;

                    foreach (var a in BuildAncestorChain(content.ParentLink))
                    {
                        string url = null;
                        try { url = _urlResolver.GetUrl(a.ContentLink); }
                        catch { }
                        ancestorsWithUrls.Add((a, url));
                    }
                }

                return (itemRef, content, contentName, sourceGuid, ancestorsWithUrls);
            })
            .ToList();

        // Pre-load the culture-specific (translatable) property names per content type, on the HTTP
        // thread — PropertyDefinition access is DB-backed, same affinity rule as the content pre-load
        // above. The background transfer uses this to build language-branch payloads (which must omit
        // culture-invariant properties) without touching IContentLoader/IContentTypeRepository.
        var cultureSpecificByType = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ct in _contentTypeRepository.List())
            {
                var specific = ct.PropertyDefinitions
                    .Where(pd => pd.LanguageSpecific)
                    .Select(pd => pd.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (specific.Count > 0) cultureSpecificByType[ct.Name] = specific;
            }
        }
        catch (Exception ex) { _logger.LogWarning("Could not pre-load culture-specific property map: {Error}", ex.Message); }

        // An image content-type name for the asset-folder probe (uploading a tiny image makes Optimizely
        // create+attach a content's "For This Page" folder, which page-local blocks must be parented by).
        // Discovered on the HTTP thread; the target must have the same content types deployed.
        string imageTypeName = null;
        try
        {
            var modelTypes = _contentTypeRepository.List().Where(ct => ct.ModelType != null).ToList();
            imageTypeName = modelTypes.FirstOrDefault(ct => typeof(IContentImage).IsAssignableFrom(ct.ModelType))?.Name
                         ?? modelTypes.FirstOrDefault(ct => typeof(ImageData).IsAssignableFrom(ct.ModelType))?.Name;
            _logger.LogDebug("Asset-folder probe image type: {Type}", imageTypeName ?? "(none found)");
        }
        catch (Exception ex) { _logger.LogDebug("Could not resolve an image content type for asset-folder probing: {Error}", ex.Message); }

        var languagePlan = new LanguagePlan
        {
            Selected = selectedLanguages == null ? null : new HashSet<string>(selectedLanguages, StringComparer.OrdinalIgnoreCase),
            CultureSpecificByType = cultureSpecificByType,
            ImageTypeName = imageTypeName
        };

        // All IContentLoader work done — now safe to await.
        string targetToken;
        try { targetToken = await _tokenService.GetTokenAsync(target); }
        catch (Exception ex) { return new TransferResult { Success = false, ErrorMessage = $"Failed to authenticate with target environment: {ex.Message}" }; }

        string sourceToken;
        try { sourceToken = await _tokenService.GetTokenAsync(source); }
        catch (Exception ex) { return new TransferResult { Success = false, ErrorMessage = $"Failed to authenticate with source environment: {ex.Message}" }; }

        var planLookup = plan?.ToDictionary(p => p.ContentId, StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, PreCheckItemResult>();

        var result = new TransferResult { Success = true };

        var deferredPatches = new List<(Guid guid, string property, string json)>();

        foreach (var ctx in itemContexts)
        {
            planLookup.TryGetValue(ctx.itemRef.ToString(), out var planItem);
            var itemResult = await TransferSingleItemAsync(
                ctx.itemRef, ctx.content, ctx.contentName, ctx.sourceGuid,
                ctx.ancestorsWithUrls,
                source.BaseUrl, sourceToken, target, targetToken,
                planItem, transferStatus, onItemComplete, deferredPatches, languagePlan);
            result.Items.Add(itemResult);
            if (!itemResult.Success)
                result.Success = false;
        }

        // Second pass: re-apply any properties that were stripped because the referenced
        // content did not yet exist on the target when the page was first written.
        if (deferredPatches.Count > 0)
        {
            // Build a map from target GUID → result item so we can annotate defaults used.
            var targetGuidToResult = new Dictionary<Guid, TransferItemResult>();
            for (int i = 0; i < itemContexts.Count && i < result.Items.Count; i++)
            {
                var ctx = itemContexts[i];
                var ri = result.Items[i];
                if (!ri.Success) continue;
                planLookup.TryGetValue(ctx.itemRef.ToString(), out var pi);
                var tg = (pi?.Action == PreCheckAction.CreateNew) ? (pi.NewGuid ?? ctx.sourceGuid) : ctx.sourceGuid;
                if (tg != Guid.Empty) targetGuidToResult[tg] = ri;
            }

            _logger.LogInformation("Applying {Count} deferred property patch(es) for forward content references", deferredPatches.Count);
            foreach (var grp in deferredPatches.GroupBy(p => p.guid))
            {
                try
                {
                    var currentJson = await ReadFromTargetAsync(grp.Key, target, targetToken);
                    if (currentJson == null)
                    {
                        _logger.LogWarning("Deferred patch: could not read {Guid} from target — skipping", grp.Key);
                        continue;
                    }
                    var stripped = StripReadOnlyProperties(currentJson, preserveParentLink: true);
                    var node = JsonNode.Parse(stripped)?.AsObject();
                    if (node == null) continue;

                    targetGuidToResult.TryGetValue(grp.Key, out var resultItem);
                    var defaultsUsed = new List<string>();

                    foreach (var (_, property, json) in grp)
                    {
                        // Check if the referenced content now exists on target
                        var refGuid = ExtractReferencedContentGuid(json);
                        var refExists = !refGuid.HasValue || await ExistsOnTargetAsync(refGuid.Value, target, targetToken);

                        if (refExists)
                        {
                            try
                            {
                                // Inject target integer IDs for every GUID in this property so
                                // Optimizely can resolve the reference. Without the integer id, a
                                // GUID-only reference fails with InvalidContent even when the
                                // content exists on target (EPiServer CMA v3 requirement).
                                var patchJson = json;
                                var patchGuids = ExtractContentReferenceGuids(json);
                                if (patchGuids.Count > 0)
                                {
                                    var patchIdMap = new Dictionary<Guid, int?>();
                                    foreach (var rg in patchGuids)
                                    {
                                        var tid = await GetTargetContentIdAsync(rg, target, targetToken);
                                        if (tid.HasValue) patchIdMap[rg] = tid;
                                    }
                                    if (patchIdMap.Count > 0)
                                        patchJson = InjectTargetContentIds(patchJson, patchIdMap);
                                }
                                node[property] = JsonNode.Parse(patchJson);
                            }
                            catch { _logger.LogWarning("Deferred patch: could not parse property '{Prop}' for {Guid}", property, grp.Key); }
                        }
                        else
                        {
                            // Referenced content still absent — create an automatic placeholder
                            var fallbackGuid = await GetFallbackReferenceGuidAsync(
                                json, refGuid, grp.Key, source.BaseUrl, sourceToken, target, targetToken);
                            if (!string.IsNullOrEmpty(fallbackGuid))
                            {
                                var fallbackNode = BuildFallbackPropertyValue(json, fallbackGuid);
                                if (fallbackNode != null)
                                {
                                    node[property] = fallbackNode;
                                    defaultsUsed.Add(property);
                                    _logger.LogDebug("Deferred patch: property '{Prop}' on {Guid} set to placeholder {Fallback}", property, grp.Key, fallbackGuid);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Deferred patch: property '{Prop}' on {Guid} references missing content and no placeholder could be created — skipping", property, grp.Key);
                            }
                        }
                    }

                    var patchedContent = await RelinkContentLinksAsync(node.ToJsonString(), source.BaseUrl, target, targetToken);
                    patchedContent = ReplaceSourceDomain(patchedContent, source.BaseUrl, target.BaseUrl);
                    await WriteToTargetAsync(grp.Key, patchedContent, target, targetToken);
                    _logger.LogInformation("Deferred patch applied for {Guid}: [{Props}]", grp.Key, string.Join(", ", grp.Select(p => p.property)));

                    if (defaultsUsed.Count > 0 && resultItem != null)
                        resultItem.DefaultedProperties.AddRange(defaultsUsed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Deferred patch failed for {Guid}: {Error}", grp.Key, ex.Message);
                }
            }
        }

        result.TransferredCount = result.Items.Count(i => i.Success);
        return result;
    }

    private async Task<TransferItemResult> TransferSingleItemAsync(
        ContentReference contentRef,
        IContent content,
        string contentName,
        Guid sourceGuid,
        List<(IContent ancestor, string url)> ancestorsWithUrls,
        string sourceBaseUrl,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken,
        PreCheckItemResult planItem,
        string transferStatus = "Published",
        Action onItemComplete = null,
        List<(Guid guid, string property, string json)> deferredPatches = null,
        LanguagePlan languagePlan = null)
    {
        // content, contentName, sourceGuid, ancestorsWithUrls are all pre-loaded by caller.
        if (content == null)
            return new TransferItemResult { ContentId = contentRef.ToString(), Success = false, ErrorMessage = "Could not load content." };

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
            var (parentGuid, _) = await ResolveTargetParentAsync(ancestorsWithUrls, target, targetToken);
            targetParentGuid = parentGuid ?? Guid.Empty;
        }

        var visited = new HashSet<Guid> { sourceGuid };
        var failedDependencyGuids = new List<string>();

        try
        {
            var targetId = await TransferItemCoreAsync(
                sourceGuid, sourceJson, sourceBaseUrl, sourceToken,
                target, targetToken, targetGuid, targetParentGuid,
                (transferStatus == "CheckedOut") ? "CheckedOut" : "Published",
                visited, onItemComplete, deferredPatches, failedDependencyGuids, languagePlan);

            return new TransferItemResult
            {
                ContentId = contentRef.ToString(),
                ContentName = contentName,
                Success = true,
                TargetContentId = targetId,
                TargetBaseUrl = target.BaseUrl,
                FailedDependencyGuids = failedDependencyGuids
            };
        }
        catch (Exception ex)
        {
            onItemComplete?.Invoke();
            return new TransferItemResult { ContentId = contentRef.ToString(), ContentName = contentName, Success = false, ErrorMessage = $"Transfer failed: {ex.Message}", FailedDependencyGuids = failedDependencyGuids };
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
        Action onItemComplete,
        List<(Guid guid, string property, string json)> deferredPatches = null,
        List<string> failedDependencyGuids = null,
        LanguagePlan languagePlan = null)
    {
        // ── Step 1: Blocks and global media ──────────────────────────────────
        // Process blocks depth-first (they must exist before the parent is written).
        // Global media (not in contentassets) can also be uploaded now because their
        // parent folder already exists on the target.
        // Local media (contentassets images) are deferred until after step 2 creates
        // the asset bucket for this item.
        var idMap = new Dictionary<Guid, int?>();
        var deferredLocalMedia = new List<(Guid guid, string json)>();
        var deferredLocalBlocks = new List<(Guid guid, string json)>();

        // Inline content blocks (epi-contentfragment divs) reference a block by guid *inside* the
        // XHTML string; their guids are surfaced for the step-4 data-contentlink remap (and, inside
        // ProcessReferencedDependenciesAsync, folded into the transfer set).
        var inlineFragments = XhtmlProcessor.ExtractXhtmlContentFragments(sourceJson);

        await ProcessReferencedDependenciesAsync(
            sourceJson, sourceBaseUrl, sourceToken, target, targetToken,
            targetGuid, effectiveStatus, visited, idMap, deferredLocalMedia, deferredLocalBlocks,
            onItemComplete, deferredPatches, failedDependencyGuids, languagePlan);

        // ── Step 2: Full PUT ──────────────────────────────────────────────────
        // Full content (not a stub) ensures all required properties are present.
        // Also causes Optimizely to auto-create the "For This Page/Block" asset bucket.
        // Local media IDs are not yet known — corrected in step 4 if any were deferred.
        var baseJson = StripReadOnlyProperties(sourceJson, preserveParentLink: false);
        baseJson = InjectParentLink(baseJson, targetParentGuid);
        if (idMap.Count > 0) baseJson = InjectTargetContentIds(baseJson, idMap);
        baseJson = InjectStatus(baseJson, effectiveStatus);
        // Resolve link-property hrefs to their correct target URLs before the generic domain swap,
        // because path prefixes (e.g. /mattpage/) can differ between environments.
        baseJson = await RelinkContentLinksAsync(baseJson, sourceBaseUrl, target, targetToken);
        baseJson = ReplaceSourceDomain(baseJson, sourceBaseUrl, target.BaseUrl);

        await WriteToTargetAsync(targetGuid, baseJson, target, targetToken, deferredPatches, sourceBaseUrl, sourceToken);

        // ── Step 3: Local media + XHTML inline images ─────────────────────────
        // Asset bucket now exists. Upload deferred local media, then XHTML inline images.
        var postIdMap = new Dictionary<Guid, int?>();
        var xhtmlUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Editor inline-image URLs embed the source content's integer id as ",,{id}". That id is
        // environment-specific, so remap it to the target id once the media exists on the target.
        var xhtmlContentIdMap = new Dictionary<int, int>();
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
                await CaptureFolderMappingAsync(refGuid, refJson, target, targetToken, languagePlan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not upload local media {Guid}: {Error}", refGuid, ex.Message);
                failedDependencyGuids?.Add(refGuid.ToString("D"));
                if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                    postIdMap[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
            }
            onItemComplete?.Invoke();
        }

        // Local media has now mapped the owner's content-asset folder, so the deferred page-local
        // blocks can be transferred into it. Their ids feed the step-4 re-PUT so the ContentArea binds.
        if (deferredLocalBlocks.Count > 0)
            foreach (var (blockGuid, blockId) in await TransferDeferredLocalBlocksAsync(
                deferredLocalBlocks, sourceBaseUrl, sourceToken, target, targetToken,
                targetGuid, effectiveStatus, visited, onItemComplete, deferredPatches, failedDependencyGuids, languagePlan))
                postIdMap[blockGuid] = blockId;

        var seenImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var srcUrl in XhtmlProcessor.ExtractXhtmlImageUrls(sourceJson))
        {
            var relPath = XhtmlProcessor.ToRelativePath(srcUrl);
            if (string.IsNullOrEmpty(relPath)) continue;
            // relPath is kept as-is for the rewrite map key (it matches the raw src in the HTML);
            // pathForLookup is normalised purely for resolving the src back to a content GUID.
            var pathForLookup = XhtmlProcessor.NormalizeInlineImagePath(relPath);
            if (string.IsNullOrEmpty(pathForLookup) || !seenImagePaths.Add(pathForLookup)) continue;

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
                // Fall back to the local CMS router for any path the CDV can't resolve — it
                // handles permanent links and edit-mode/friendly content URLs the API won't.
                if (!imageGuid.HasValue)
                    imageGuid = TryResolveLocalContentGuid(pathForLookup);
                // Last resort: resolve the ",,{id}" content id embedded in editor media URLs.
                if (!imageGuid.HasValue)
                    imageGuid = TryResolveByEmbeddedContentId(relPath);
            }
            catch { continue; }

            if (!imageGuid.HasValue)
            {
                _logger.LogWarning("Could not resolve XHTML asset URL to a GUID: {Url}", relPath);
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
                // Same fallback as the upload branch: the source canonical path equals the target URL.
                if (string.IsNullOrEmpty(existingUrl))
                    existingUrl = await GetSourceContentUrlAsync(imageGuid.Value, sourceBaseUrl, sourceToken);
                if (!string.IsNullOrEmpty(existingUrl)) xhtmlUrlMap[relPath] = existingUrl;
                await RecordInlineImageIdRemapAsync(relPath, imageGuid.Value, target, targetToken, xhtmlContentIdMap);
                continue;
            }

            string imgJson;
            try { imgJson = await ReadFromSourceAsync(imageGuid.Value, sourceBaseUrl, sourceToken); }
            catch { continue; }

            // An <a href> can point at a page/block (in edit mode), not just an asset. Only media
            // gets uploaded here; page/block links are left to ordinary content-link relinking.
            if (GetAssetBinaryUrl(imgJson) == null)
            {
                _logger.LogDebug("Inline link {Guid} is not a media asset — skipping upload", imageGuid.Value);
                continue;
            }

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
                // The source canonical path equals the target URL for global/page-local media (the
                // routeSegment is preserved and globalassets/contentassets paths match across
                // environments), so fall back to it when the target URL can't be read back —
                // otherwise the rewrite is skipped and the editor's edit-mode src is left in place.
                if (string.IsNullOrEmpty(targetRelUrl)) targetRelUrl = canonicalPath;
                if (!string.IsNullOrEmpty(targetRelUrl))
                {
                    xhtmlUrlMap[relPath] = targetRelUrl;
                    _logger.LogDebug("XHTML asset {Guid}: {Src} → {Tgt}", imageGuid.Value, relPath, targetRelUrl);
                }
                else
                {
                    _logger.LogWarning("Uploaded XHTML asset {Guid} but could not determine its target URL to rewrite the markup", imageGuid.Value);
                }
                await RecordInlineImageIdRemapAsync(relPath, imageGuid.Value, target, targetToken, xhtmlContentIdMap);
                onItemComplete?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not upload XHTML asset {Guid}: {Error}", imageGuid.Value, ex.Message);
                failedDependencyGuids?.Add(imageGuid.Value.ToString("D"));
                onItemComplete?.Invoke();
            }
        }

        // Inline content-block fragments embed the source block's integer id as data-contentlink="{id}".
        // The block guid is environment-stable (we preserve it on transfer) so the div's data-contentguid
        // already matches; only the integer id needs remapping to the target — otherwise it points at
        // whatever unrelated content happens to hold that id on the target.
        var xhtmlBlockIdMap = new Dictionary<int, int>();
        foreach (var frag in inlineFragments)
        {
            if (frag.ContentLink <= 0 || xhtmlBlockIdMap.ContainsKey(frag.ContentLink)) continue;
            int? targetId = idMap.TryGetValue(frag.Guid, out var mapped) ? mapped : null;
            targetId ??= await GetTargetContentIdAsync(frag.Guid, target, targetToken);
            if (targetId.HasValue && targetId.Value != frag.ContentLink)
                xhtmlBlockIdMap[frag.ContentLink] = targetId.Value;
        }

        // ── Step 4: Second PUT if anything was uploaded post-step-2 ──────────
        int? masterTargetId;
        if (postIdMap.Count > 0 || xhtmlUrlMap.Count > 0 || xhtmlContentIdMap.Count > 0 || xhtmlBlockIdMap.Count > 0)
        {
            var patchedJson = baseJson;
            if (postIdMap.Count > 0) patchedJson = InjectTargetContentIds(patchedJson, postIdMap);
            if (xhtmlUrlMap.Count > 0 || xhtmlContentIdMap.Count > 0 || xhtmlBlockIdMap.Count > 0)
                patchedJson = XhtmlProcessor.RewriteXhtmlUrls(patchedJson, sourceBaseUrl, xhtmlUrlMap, xhtmlContentIdMap, xhtmlBlockIdMap);
            masterTargetId = await WriteToTargetAsync(targetGuid, patchedJson, target, targetToken, deferredPatches, sourceBaseUrl, sourceToken);
        }
        else
        {
            masterTargetId = await GetTargetContentIdAsync(targetGuid, target, targetToken);
        }

        // ── Step 5: Other language branches ──────────────────────────────────
        // The master language is now on target. Create/update every *other* language this item has
        // (subject to the user's selection) as a culture-specific branch payload. onItemComplete fires
        // after this, so the item is only marked done once all its languages are written.
        await WriteLanguageBranchesAsync(
            sourceGuid, sourceJson, targetGuid, targetParentGuid, effectiveStatus,
            sourceBaseUrl, sourceToken, target, targetToken,
            languagePlan, visited, idMap, xhtmlUrlMap, xhtmlContentIdMap, xhtmlBlockIdMap, deferredPatches, failedDependencyGuids);

        onItemComplete?.Invoke();
        return masterTargetId;
    }

    // Transfers the blocks and media a document references into idMap (depth-first; blocks recurse so
    // their own languages/dependencies follow). Drives both the master pass and each language branch —
    // for a branch this picks up dependencies the master never had (e.g. a block only in a culture-
    // specific ContentArea). visited dedups against everything transferred so far this item, so shared
    // dependencies are wired (id looked up) rather than re-done. Inline content-block guids baked into
    // XHTML are folded in too, since the guidValue walker can't see them.
    private async Task ProcessReferencedDependenciesAsync(
        string json,
        string sourceBaseUrl, string sourceToken,
        DxpEnvironmentConfig target, string targetToken,
        Guid targetGuid, string effectiveStatus,
        HashSet<Guid> visited, Dictionary<Guid, int?> idMap,
        List<(Guid guid, string json)> deferredLocalMedia,
        List<(Guid guid, string json)> deferredLocalBlocks,
        Action onItemComplete,
        List<(Guid guid, string property, string json)> deferredPatches,
        List<string> failedDependencyGuids,
        LanguagePlan languagePlan)
    {
        var refGuids = ExtractContentReferenceGuids(json);
        foreach (var frag in XhtmlProcessor.ExtractXhtmlContentFragments(json))
            if (frag.Guid != Guid.Empty && !refGuids.Contains(frag.Guid))
                refGuids.Add(frag.Guid);

        foreach (var refGuid in refGuids)
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
                    // Asset bucket may not exist yet (master pass) — defer; the caller uploads it
                    // once the bucket is created. For a branch the bucket already exists, so the
                    // caller flushes immediately.
                    deferredLocalMedia.Add((refGuid, refJson));
                }
                else
                {
                    // Global media — ensure its parent folder exists on target before uploading.
                    // Globalassets folder GUIDs are usually identical across DXP environments, but
                    // sub-folders may be missing if the folder tree was never fully synced.
                    var mediaParentGuid = ExtractParentGuid(refJson) ?? targetGuid;
                    if (mediaParentGuid != targetGuid && !await ExistsOnTargetAsync(mediaParentGuid, target, targetToken))
                    {
                        // Try to resolve the canonical path and recreate the folder hierarchy
                        var canonicalUrl = await GetSourceContentUrlAsync(refGuid, sourceBaseUrl, sourceToken);
                        if (!string.IsNullOrEmpty(canonicalUrl) && canonicalUrl.StartsWith("/globalassets/", StringComparison.OrdinalIgnoreCase))
                        {
                            var lastSlash = canonicalUrl.LastIndexOf('/');
                            var folderPath = lastSlash > 0 ? canonicalUrl[..(lastSlash + 1)] : "/globalassets/";
                            mediaParentGuid = await EnsureGlobalAssetFolderPathAsync(folderPath, sourceBaseUrl, sourceToken, target, targetToken) ?? targetGuid;
                        }
                        else
                        {
                            await EnsureContentParentAsync(mediaParentGuid, sourceBaseUrl, sourceToken, target, targetToken, new HashSet<Guid> { refGuid });
                        }
                    }
                    var mediaStub = BuildMinimalAssetJson(refJson, mediaParentGuid);
                    try
                    {
                        idMap[refGuid] = (await WriteAssetToTargetAsync(refGuid, refJson, mediaStub, sourceToken, target, targetToken)).id;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not upload global media {Guid}: {Error}", refGuid, ex.Message);
                        failedDependencyGuids?.Add(refGuid.ToString("D"));
                        if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                            idMap[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
                    }
                    onItemComplete?.Invoke();
                }
                continue;
            }

            // Page-local blocks belong in the owner's "For This Page/Block" content-asset folder, whose
            // target GUID isn't known until a sibling local asset has been uploaded (the page must exist
            // first). Defer them; the caller transfers them once local media has populated the folder map.
            if (IsLocalContent(refJson))
            {
                deferredLocalBlocks.Add((refGuid, refJson));
                continue;
            }

            // Shared/global block — its parent is real content we can ensure exists, so transfer now,
            // depth-first. Always re-transfer (even if it already exists on target): skipping means
            // stale data — links/URLs/property changes on source would never propagate. PUT is idempotent.
            var blockParentGuid = ExtractParentGuid(refJson) ?? targetGuid;
            if (blockParentGuid != targetGuid)
                await EnsureContentParentAsync(blockParentGuid, sourceBaseUrl, sourceToken, target, targetToken, new HashSet<Guid> { refGuid });
            try
            {
                idMap[refGuid] = await TransferItemCoreAsync(
                    refGuid, refJson, sourceBaseUrl, sourceToken,
                    target, targetToken,
                    refGuid, blockParentGuid,
                    effectiveStatus, visited, onItemComplete, deferredPatches, failedDependencyGuids, languagePlan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not transfer block {Guid}: {Error}", refGuid, ex.Message);
                failedDependencyGuids?.Add(refGuid.ToString("D"));
                if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                    idMap[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
                onItemComplete?.Invoke();
            }
        }
    }

    // Transfers page-local blocks that were deferred until the owner's content-asset folder became known
    // (after a sibling local asset mapped it, or via a probe). Each goes into that folder so the editor's
    // "For This Page" tab shows it. Results are returned for the caller to inject into the owner's
    // ContentArea references.
    private async Task<Dictionary<Guid, int?>> TransferDeferredLocalBlocksAsync(
        List<(Guid guid, string json)> deferredLocalBlocks,
        string sourceBaseUrl, string sourceToken, DxpEnvironmentConfig target, string targetToken,
        Guid ownerTargetGuid, string effectiveStatus, HashSet<Guid> visited,
        Action onItemComplete, List<(Guid guid, string property, string json)> deferredPatches,
        List<string> failedDependencyGuids, LanguagePlan languagePlan)
    {
        var ids = new Dictionary<Guid, int?>();
        foreach (var (refGuid, refJson) in deferredLocalBlocks)
        {
            var sourceFolder = ExtractParentGuid(refJson);
            Guid blockParentGuid;
            if (sourceFolder.HasValue && languagePlan != null && languagePlan.FolderMap.TryGetValue(sourceFolder.Value, out var mapped))
                blockParentGuid = mapped;
            else
                blockParentGuid = await ResolveAssetFolderGuidAsync(ownerTargetGuid, target, targetToken, languagePlan) ?? ownerTargetGuid;

            try
            {
                ids[refGuid] = await TransferItemCoreAsync(
                    refGuid, refJson, sourceBaseUrl, sourceToken,
                    target, targetToken,
                    refGuid, blockParentGuid,
                    effectiveStatus, visited, onItemComplete, deferredPatches, failedDependencyGuids, languagePlan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not transfer local block {Guid}: {Error}", refGuid, ex.Message);
                failedDependencyGuids?.Add(refGuid.ToString("D"));
                if (await ExistsOnTargetAsync(refGuid, target, targetToken))
                    ids[refGuid] = await GetTargetContentIdAsync(refGuid, target, targetToken);
            }
        }
        return ids;
    }

    // Records source-folder → target-folder for a just-transferred page-local media item. Optimizely
    // routes page-local media into the owner's real content-asset folder, so the target item's
    // parentLink reveals that folder's generated GUID. Page-local blocks (not routed) reuse this map
    // to land in the same folder as their sibling assets.
    private async Task CaptureFolderMappingAsync(Guid mediaGuid, string sourceMediaJson, DxpEnvironmentConfig target, string targetToken, LanguagePlan languagePlan)
    {
        if (languagePlan == null) return;
        var sourceFolder = ExtractParentGuid(sourceMediaJson);
        if (!sourceFolder.HasValue || languagePlan.FolderMap.ContainsKey(sourceFolder.Value)) return;
        try
        {
            var targetFolder = ExtractParentGuid(await ReadFromTargetAsync(mediaGuid, target, targetToken));
            if (targetFolder.HasValue)
            {
                languagePlan.FolderMap[sourceFolder.Value] = targetFolder.Value;
                _logger.LogDebug("Mapped content-asset folder {Source} → {Target}", sourceFolder.Value, targetFolder.Value);
            }
        }
        catch (Exception ex) { _logger.LogDebug("Could not map content-asset folder for {Guid}: {Error}", mediaGuid, ex.Message); }
    }

    // A 1×1 transparent PNG, uploaded purely to make Optimizely create a content's asset folder.
    private static readonly byte[] ProbePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // Resolves (creating if necessary) the target GUID of an owner's "For This Page/Block" content-asset
    // folder, where page-local blocks must live. The CMA doesn't expose this folder and its GUID is
    // generated on the target, so we discover it the way Optimizely itself does: uploading a tiny image
    // to the owner makes the CMA call GetOrCreateAssetFolder and route the image into the folder, whose
    // GUID is then the image's parentLink. We read that, delete the probe image (the folder it created
    // stays, linked to the owner), and cache per owner so it's probed at most once.
    private async Task<Guid?> ResolveAssetFolderGuidAsync(Guid ownerGuid, DxpEnvironmentConfig target, string token, LanguagePlan languagePlan)
    {
        if (languagePlan == null) return null;
        if (languagePlan.AssetFolderCache.TryGetValue(ownerGuid, out var cached)) return cached;

        Guid? folder = null;
        if (string.IsNullOrEmpty(languagePlan.ImageTypeName))
            _logger.LogWarning("No image content type available to resolve the asset folder for {Owner}; page-local blocks will be parented by the owner", ownerGuid);
        else
        {
            var probeGuid = Guid.NewGuid();
            try
            {
                var probeJson = new JsonObject
                {
                    ["name"] = "__dxp-assetfolder-probe.png",
                    ["contentType"] = new JsonArray(languagePlan.ImageTypeName),
                    ["parentLink"] = new JsonObject { ["guidValue"] = ownerGuid.ToString() },
                    ["status"] = "Published"
                }.ToJsonString();

                var resp = await _cma.PutAssetBytesAsync(target.BaseUrl, token, probeGuid, probeJson, ProbePng, "__dxp-assetfolder-probe.png", "image/png", "asset-folder probe");
                if (resp.IsSuccess)
                {
                    var parent = ExtractParentGuid(resp.Body);
                    if (parent.HasValue && parent.Value != ownerGuid) folder = parent;
                    else _logger.LogWarning("Asset-folder probe for {Owner} was not routed to a folder (parent {Parent}); page-local blocks will be parented by the owner", ownerGuid, parent);
                    await _cma.DeleteManagementAsync(target.BaseUrl, token, probeGuid, "remove asset-folder probe");
                }
                else
                {
                    _logger.LogWarning("Asset-folder probe for {Owner} failed ({Status}): {Body}", ownerGuid, (int)resp.Status, resp.Body);
                }
            }
            catch (Exception ex) { _logger.LogWarning("Asset-folder probe for {Owner} errored: {Error}", ownerGuid, ex.Message); }
        }

        languagePlan.AssetFolderCache[ownerGuid] = folder;
        return folder;
    }

    // Writes every non-master language branch of an item that the user kept selected. Each branch is a
    // culture-specific payload (BuildLanguageBranchJson). Before writing, the branch's own referenced
    // dependencies are transferred (so a block/image only present in a non-master branch is created),
    // then the payload reuses the accumulated dependency ids and the master pass's inline-asset rewrites.
    // A failure on one branch is logged and skipped, never failing the master transfer.
    private async Task WriteLanguageBranchesAsync(
        Guid sourceGuid, string masterJson, Guid targetGuid, Guid targetParentGuid, string effectiveStatus,
        string sourceBaseUrl, string sourceToken, DxpEnvironmentConfig target, string targetToken,
        LanguagePlan languagePlan, HashSet<Guid> visited, Dictionary<Guid, int?> idMap,
        Dictionary<string, string> xhtmlUrlMap, Dictionary<int, int> xhtmlContentIdMap, Dictionary<int, int> xhtmlBlockIdMap,
        List<(Guid guid, string property, string json)> deferredPatches, List<string> failedDependencyGuids)
    {
        var master = ExtractContentLanguage(masterJson) ?? ExtractMasterLanguage(masterJson);
        var branches = ExtractExistingLanguages(masterJson)
            .Where(l => !string.Equals(l, master, StringComparison.OrdinalIgnoreCase))
            .Where(l => languagePlan?.Includes(l) ?? true)
            .ToList();
        if (branches.Count == 0) return;

        var cultureSpecific = languagePlan?.CultureSpecific(ExtractConcreteContentTypeName(masterJson)) ?? new HashSet<string>();

        foreach (var lang in branches)
        {
            try
            {
                var branchJson = await ReadFromSourceAsync(sourceGuid, sourceBaseUrl, sourceToken, lang);

                // Transfer dependencies unique to this branch (e.g. a block only in a culture-specific
                // ContentArea) that the master pass didn't see. The asset bucket already exists (master
                // wrote it), so branch-local media is uploaded immediately rather than deferred.
                // onItemComplete is not passed — branch-only dependencies aren't counted in the plan.
                var branchLocalMedia = new List<(Guid guid, string json)>();
                var branchLocalBlocks = new List<(Guid guid, string json)>();
                await ProcessReferencedDependenciesAsync(
                    branchJson, sourceBaseUrl, sourceToken, target, targetToken,
                    targetGuid, effectiveStatus, visited, idMap, branchLocalMedia, branchLocalBlocks,
                    onItemComplete: null, deferredPatches, failedDependencyGuids, languagePlan);
                foreach (var (mediaGuid, mediaJson) in branchLocalMedia)
                {
                    try
                    {
                        idMap[mediaGuid] = (await WriteAssetToTargetAsync(mediaGuid, mediaJson, BuildMinimalAssetJson(mediaJson, targetGuid), sourceToken, target, targetToken)).id;
                        await CaptureFolderMappingAsync(mediaGuid, mediaJson, target, targetToken, languagePlan);
                    }
                    catch (Exception ex) { _logger.LogWarning("Could not upload branch media {Guid}: {Error}", mediaGuid, ex.Message); failedDependencyGuids?.Add(mediaGuid.ToString("D")); }
                }
                // Branch-only local blocks: now that media has mapped the folder, transfer them into it.
                foreach (var (blockGuid, blockId) in await TransferDeferredLocalBlocksAsync(
                    branchLocalBlocks, sourceBaseUrl, sourceToken, target, targetToken,
                    targetGuid, effectiveStatus, visited, null, deferredPatches, failedDependencyGuids, languagePlan))
                    idMap[blockGuid] = blockId;

                var payload = BuildLanguageBranchJson(branchJson, cultureSpecific, targetParentGuid, effectiveStatus);
                if (idMap.Count > 0) payload = InjectTargetContentIds(payload, idMap);
                payload = await RelinkContentLinksAsync(payload, sourceBaseUrl, target, targetToken);
                payload = ReplaceSourceDomain(payload, sourceBaseUrl, target.BaseUrl);
                if (xhtmlUrlMap.Count > 0 || xhtmlContentIdMap.Count > 0 || xhtmlBlockIdMap.Count > 0)
                    payload = XhtmlProcessor.RewriteXhtmlUrls(payload, sourceBaseUrl, xhtmlUrlMap, xhtmlContentIdMap, xhtmlBlockIdMap);
                await WriteToTargetAsync(targetGuid, payload, target, targetToken, deferredPatches, sourceBaseUrl, sourceToken);
                _logger.LogInformation("Transferred '{Lang}' branch of {Guid}", lang, sourceGuid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not transfer '{Lang}' branch of {Guid}: {Error}", lang, sourceGuid, ex.Message);
            }
        }
    }

    // The concrete (leaf) content type name from a CMA document's "contentType" array, used to look up
    // its culture-specific property set. The array runs base → derived, so the last entry is concrete.
    private static string ExtractConcreteContentTypeName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.Array)
            {
                string last = null;
                foreach (var item in ct.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) last = item.GetString();
                return last;
            }
        }
        catch { }
        return null;
    }

    // Transfer-wide language settings, computed once on the HTTP thread and threaded through the
    // recursive transfer (instance state would race across concurrent jobs on the scoped service).
    private sealed class LanguagePlan
    {
        // null = transfer every language each item has; otherwise the (case-insensitive) set chosen.
        public IReadOnlySet<string> Selected { get; init; }
        public IReadOnlyDictionary<string, HashSet<string>> CultureSpecificByType { get; init; }
            = new Dictionary<string, HashSet<string>>();

        // A media content-type name (discovered on the HTTP thread) used for the asset-folder probe,
        // plus a per-transfer cache of owner GUID → its resolved "For This Page/Block" folder GUID.
        public string ImageTypeName { get; init; }
        public Dictionary<Guid, Guid?> AssetFolderCache { get; } = new();

        // Source content-asset folder GUID → the target folder it maps to, learned as local media is
        // transferred (Optimizely routes page-local media into the owner's real folder, revealing its
        // generated target GUID). Page-local *blocks* — which Optimizely does NOT route — reuse this to
        // land in the same folder as their sibling assets.
        public Dictionary<Guid, Guid> FolderMap { get; } = new();

        public bool Includes(string language) =>
            Selected == null || (language != null && Selected.Contains(language));

        public HashSet<string> CultureSpecific(string typeName) =>
            typeName != null && CultureSpecificByType.TryGetValue(typeName, out var set)
                ? set : new HashSet<string>();
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
                // Custom page types from external assemblies may not be registered in this project.
                // Optimizely falls back to loading them as ContentData rather than PageData, so
                // .OfType<PageData>() silently discards them. Exclude known non-page types instead.
                using var e = _contentLoader.GetChildren<IContent>(current, LanguageSelector.AutoDetect(true)).GetEnumerator();
                var pageChildren = new List<(ContentReference contentRef, int sortIndex)>();
                while (true)
                {
                    bool moved;
                    IContent child = null;
                    try { moved = e.MoveNext(); if (moved) child = e.Current; }
                    catch { break; }
                    if (!moved) break;
                    if (child is BlockData || child is IContentMedia || child is ContentFolder) continue;
                    if (child is IVersionable versionable && versionable.Status != VersionStatus.Published) continue;
                    var sortIdx = 0;
                    try { var psi = child.Property["PageSortIndex"]?.Value; if (psi != null) sortIdx = Convert.ToInt32(psi); } catch { }
                    pageChildren.Add((child.ContentLink, sortIdx));
                }
                foreach (var (childRef, _) in pageChildren.OrderBy(t => t.sortIndex))
                    queue.Enqueue(childRef);
            }
        }

        return ordered;
    }

    private async Task<string> ReadFromSourceAsync(Guid guid, string sourceBaseUrl, string sourceToken, string language = null)
    {
        var r = await _cma.GetManagementAsync(sourceBaseUrl, sourceToken, guid, "read source", language);
        if (!r.IsSuccess)
            throw new HttpRequestException($"HTTP {(int)r.Status}: {r.Body}");
        return r.Body;
    }

    private static readonly string[] ReadOnlyProperties =
    [
        "existingLanguages", "masterLanguage", "saved", "created", "changed",
        "contentLink", "parentLink", "url", "routeSegment", "previewUrl",
        "publishedVersion", "statusReasons", "editUrl"
    ];

    internal static string StripReadOnlyProperties(string json, bool preserveParentLink = false)
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

    // ── JSON tree visitors (WalkJsonObjects / WalkJsonElements / MutateJsonStrings) live in
    //    JsonVisitors.cs, imported via `using static` so the calls below read unqualified.

    private static void StripContentReferenceIds(JsonNode node) =>
        WalkJsonObjects(node, obj =>
        {
            // For content-reference objects keep only guidValue. id/workId are environment-specific
            // integers; url/providerName/expanded are source-server values that confuse the target.
            if (obj.ContainsKey("guidValue"))
                foreach (var field in ContentRefEnvFields)
                    obj.Remove(field);
        });

    internal static string InjectStatus(string json, string status)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        node["status"] = status;
        return node.ToJsonString();
    }

    internal static string InjectParentLink(string json, Guid parentGuid)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        node["parentLink"] = new JsonObject { ["guidValue"] = parentGuid.ToString() };
        return node.ToJsonString();
    }

    // ── Multilingual ───────────────────────────────────────────────────────────
    // Reads the language codes a content item exists in from its CMA JSON ("existingLanguages"),
    // in the order the CMA returns them. Empty if the field is absent.
    internal static List<string> ExtractExistingLanguages(string json) =>
        ExtractLanguageNames(json, "existingLanguages");

    // The branch language of a CMA document ("language.name"), or null.
    internal static string ExtractContentLanguage(string json) =>
        ExtractLanguageObjectName(json, "language");

    // The master/default language of a content item ("masterLanguage.name"), or null.
    internal static string ExtractMasterLanguage(string json) =>
        ExtractLanguageObjectName(json, "masterLanguage");

    private static List<string> ExtractLanguageNames(string json, string arrayProp)
    {
        var langs = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(arrayProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                        n.GetString() is { Length: > 0 } code)
                        langs.Add(code);
        }
        catch { }
        return langs;
    }

    private static string ExtractLanguageObjectName(string json, string objProp)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(objProp, out var o) && o.ValueKind == JsonValueKind.Object &&
                o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                return n.GetString();
        }
        catch { }
        return null;
    }

    // Builds the write payload for a NON-master language branch. The Optimizely CMA treats language
    // variants as separate objects that share the culture-invariant properties, so a branch write
    // must send ONLY the culture-specific (translatable) property values — including invariant ones
    // is what triggers a 409 Conflict. We keep the system fields (name, language, contentType, status)
    // and every property whose name is in cultureSpecificNames, drop the rest, and bind the parent by
    // guid. Source environment-specific ref ids are stripped (StripReadOnlyProperties).
    internal static string BuildLanguageBranchJson(string branchJson, ISet<string> cultureSpecificNames, Guid parentGuid, string status)
    {
        var node = JsonNode.Parse(StripReadOnlyProperties(branchJson))?.AsObject();
        if (node == null) return branchJson;

        foreach (var key in node.Select(kvp => kvp.Key).ToList())
            if (node[key] is JsonObject prop && prop.ContainsKey("propertyDataType") &&
                !cultureSpecificNames.Contains(key))
                node.Remove(key);

        node["parentLink"] = new JsonObject { ["guidValue"] = parentGuid.ToString() };
        if (!string.IsNullOrEmpty(status)) node["status"] = status;
        return node.ToJsonString();
    }

    // Walks the JSON tree and, for every content-reference object whose guidValue is in
    // targetIdMap, injects the corresponding target integer id + workId so the Content
    // Management API can bind the property (guidValue alone is not enough for media refs).
    internal static string InjectTargetContentIds(string json, Dictionary<Guid, int?> targetIdMap)
    {
        var node = JsonNode.Parse(json);
        if (node == null) return json;
        InjectContentIds(node, targetIdMap);
        return node.ToJsonString();
    }

    private static void InjectContentIds(JsonNode node, Dictionary<Guid, int?> targetIdMap) =>
        WalkJsonObjects(node, obj =>
        {
            if (obj["guidValue"] is not JsonValue guidVal) return;
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
        });

    private async Task<int?> GetTargetContentIdAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var r = await _cma.GetManagementAsync(target.BaseUrl, targetToken, guid, "get target ID");
        return r.IsSuccess ? ExtractContentLinkId(r.Body) : null;
    }

    // Extracts contentLink.id (the environment-specific integer id) from a CMA response body.
    private static int? ExtractContentLinkId(string body)
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

    // Records a ",,{sourceId}" → ",,{targetId}" remap for an inline image whose URL carries the
    // source content's integer id. The id is environment-specific, so the markup must be updated
    // to the target id (the GUID is preserved across environments, so look the target id up by it).
    private async Task RecordInlineImageIdRemapAsync(
        string relPath, Guid imageGuid, DxpEnvironmentConfig target, string targetToken, Dictionary<int, int> map)
    {
        var m = Regex.Match(relPath ?? "", @",,(\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var sourceId) || map.ContainsKey(sourceId))
            return;
        var targetId = await GetTargetContentIdAsync(imageGuid, target, targetToken);
        if (targetId.HasValue && targetId.Value != sourceId)
            map[sourceId] = targetId.Value;
    }

    // EPiServer embeds the content id in editor media URLs as ",,{id}" (e.g. image.jpg,,108).
    // When URL-based resolution fails, load that id locally (the gadget runs on the source) to
    // get the GUID. Runs on the IContentLoader, consistent with the other local lookups here.
    private Guid? TryResolveByEmbeddedContentId(string path)
    {
        var m = Regex.Match(path ?? "", @",,(\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var id)) return null;
        try
        {
            var content = _contentLoader.Get<IContent>(new ContentReference(id), LanguageSelector.AutoDetect(true));
            if (content != null && content.ContentGuid != Guid.Empty) return content.ContentGuid;
        }
        catch (Exception ex) { _logger.LogDebug("Could not resolve embedded content id {Id}: {Error}", id, ex.Message); }
        return null;
    }

    // Uses the Content Delivery API (not CMA) to resolve a canonical relative URL for content on the target.
    // CMA GET returns "url": null for contentassets images; CDV returns the real path via contentLink.url.
    private async Task<string> GetTargetContentUrlViaCdvAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var r = await _cma.GetDeliveryAsync(target.BaseUrl, targetToken, guid, "CDV resolve target asset URL");
        return r.IsSuccess ? ExtractContentLinkUrlPath(r.Body) : null;
    }

    // Extracts contentLink.url from a CDV response and returns its absolute path component.
    private static string ExtractContentLinkUrlPath(string body)
    {
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
        var r = await _cma.GetDeliveryAsync(sourceBaseUrl, sourceToken, guid, "CDV resolve source content URL");
        return r.IsSuccess ? ExtractContentLinkUrlPath(r.Body) : null;
    }

    // Looks up a content item GUID on the SOURCE environment by its relative URL.
    // Used for /globalassets/ paths where the GUID cannot be extracted directly.
    private Task<Guid?> FindByUrlOnSourceAsync(string relativeUrl, string sourceBaseUrl, string sourceToken) =>
        FindByUrlAsync(sourceBaseUrl, sourceToken, relativeUrl, "URL lookup on source");

    // Inline-asset URL + content-fragment extraction (ExtractXhtmlImageUrls /
    // ExtractXhtmlContentFragments / ParseContentFragments) lives in XhtmlProcessor.cs.

    // Replaces the source environment origin (scheme+host) with the target origin in every
    // string value throughout the JSON document. Handles plain URL properties (PropertyUrl,
    // PropertyLongString containing links) as well as any other field that may store an
    // absolute URL pointing back at the source environment.
    internal static string ReplaceSourceDomain(string json, string sourceBaseUrl, string targetBaseUrl)
    {
        try
        {
            var sourceOrigin = new Uri(sourceBaseUrl).GetLeftPart(UriPartial.Authority);
            var targetOrigin = new Uri(targetBaseUrl).GetLeftPart(UriPartial.Authority);
            if (string.IsNullOrEmpty(sourceOrigin) ||
                string.Equals(sourceOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase))
                return json;
            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return json;
            ReplaceOriginInNodes(node, sourceOrigin, targetOrigin);
            return node.ToJsonString();
        }
        catch { return json; }
    }

    private static void ReplaceOriginInNodes(JsonNode node, string sourceOrigin, string targetOrigin) =>
        MutateJsonStrings(node, str =>
            !string.IsNullOrEmpty(str) && str.Contains(sourceOrigin, StringComparison.OrdinalIgnoreCase)
                ? str.Replace(sourceOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase)
                : str);

    // Resolves internal link URLs in PropertyLinkCollection items and PropertyUrl properties
    // to their correct target-environment URLs. Simple domain replacement is not sufficient
    // because source environments can have path prefixes (e.g. /mattpage/) that don't exist
    // on target. For each href/value containing the source origin we:
    //   1. Look up the page by its contentLink.guidValue via the target CDV (most reliable).
    //   2. Fall back to finding the page by its source URL path on target.
    //   3. Last resort: plain domain swap.
    // Must be called before ReplaceSourceDomain so the source origin is still detectable.
    private async Task<string> RelinkContentLinksAsync(
        string json,
        string sourceBaseUrl,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        try
        {
            var sourceOrigin = new Uri(sourceBaseUrl).GetLeftPart(UriPartial.Authority);
            var targetOrigin = new Uri(target.BaseUrl).GetLeftPart(UriPartial.Authority);
            if (string.IsNullOrEmpty(sourceOrigin) ||
                string.Equals(sourceOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase))
                return json;
            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return json;
            await RelinkUrlsInNodeAsync(node, sourceOrigin, targetOrigin, target, targetToken);
            return node.ToJsonString();
        }
        catch { return json; }
    }

    private async Task RelinkUrlsInNodeAsync(
        JsonNode node,
        string sourceOrigin,
        string targetOrigin,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        if (node is JsonObject obj)
        {
            // PropertyLinkCollection item: object with an "href" string containing the source origin
            if (obj["href"] is JsonValue hrefVal && hrefVal.TryGetValue(out string href) &&
                !string.IsNullOrEmpty(href) &&
                href.Contains(sourceOrigin, StringComparison.OrdinalIgnoreCase))
            {
                obj["href"] = await ResolveToTargetUrlAsync(
                    href, obj["contentLink"] as JsonObject, sourceOrigin, targetOrigin, target, targetToken);
            }

            // PropertyUrl: { "value": "https://source/...", "propertyDataType": "PropertyUrl" }
            if (obj["propertyDataType"] is JsonValue pdtVal &&
                string.Equals(pdtVal.GetValue<string>(), "PropertyUrl", StringComparison.OrdinalIgnoreCase) &&
                obj["value"] is JsonValue urlVal && urlVal.TryGetValue(out string urlStr) &&
                !string.IsNullOrEmpty(urlStr) &&
                urlStr.Contains(sourceOrigin, StringComparison.OrdinalIgnoreCase))
            {
                obj["value"] = await ResolveToTargetUrlAsync(
                    urlStr, null, sourceOrigin, targetOrigin, target, targetToken);
            }

            foreach (var child in obj.Select(kvp => kvp.Value).ToList())
                if (child != null) await RelinkUrlsInNodeAsync(child, sourceOrigin, targetOrigin, target, targetToken);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) await RelinkUrlsInNodeAsync(item, sourceOrigin, targetOrigin, target, targetToken);
        }
    }

    // Resolves a single source URL to its correct target URL.
    // Priority: GUID lookup → path lookup on target → domain swap.
    private async Task<string> ResolveToTargetUrlAsync(
        string sourceUrl,
        JsonObject contentLinkNode,
        string sourceOrigin,
        string targetOrigin,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        // 1. GUID-based CDV lookup — survives URL-segment differences between environments
        if (contentLinkNode != null &&
            Guid.TryParse(contentLinkNode["guidValue"]?.GetValue<string>(), out var linkedGuid))
        {
            var targetUrl = await GetTargetContentUrlViaCdvAsync(linkedGuid, target, targetToken);
            if (!string.IsNullOrEmpty(targetUrl))
            {
                var result = targetUrl.StartsWith('/') ? targetOrigin + targetUrl : targetUrl;
                _logger.LogDebug("Relinked (GUID): {Src} → {Tgt}", sourceUrl, result);
                return result;
            }
        }

        // 2. URL-path lookup on target
        var relPath = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var srcUri)
            ? srcUri.PathAndQuery : null;
        if (!string.IsNullOrEmpty(relPath) && relPath != "/")
        {
            var foundGuid = await FindByUrlOnTargetAsync(relPath, target, targetToken);
            if (foundGuid.HasValue)
            {
                var targetUrl = await GetTargetContentUrlViaCdvAsync(foundGuid.Value, target, targetToken);
                if (!string.IsNullOrEmpty(targetUrl))
                {
                    var result = targetUrl.StartsWith('/') ? targetOrigin + targetUrl : targetUrl;
                    _logger.LogDebug("Relinked (URL path): {Src} → {Tgt}", sourceUrl, result);
                    return result;
                }
            }
        }

        // 3. Domain swap only — path may still be wrong but at least the origin is correct
        return sourceUrl.Replace(sourceOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase);
    }

    // XHTML URL/id rewriting (RewriteXhtmlUrls), ToRelativePath and NormalizeInlineImagePath
    // live in XhtmlProcessor.cs.

    internal static List<Guid> ExtractContentReferenceGuids(string json)
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

    private static void CollectGuids(JsonElement element, List<Guid> guids, HashSet<Guid> seen) =>
        WalkJsonElements(element, obj =>
        {
            if (obj.TryGetProperty("guidValue", out var gv) &&
                gv.ValueKind == JsonValueKind.String &&
                Guid.TryParse(gv.GetString(), out var guid) &&
                seen.Add(guid))
                guids.Add(guid);
        });

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

        var client = _cma.CreateClient();
        _logger.LogDebug(">>> GET (binary download) {Url}", binaryUrl);
        var dlRequest = new HttpRequestMessage(HttpMethod.Get, binaryUrl);
        dlRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
        var dlResponse = await client.SendAsync(dlRequest, HttpCompletionOption.ResponseHeadersRead);
        _logger.LogDebug("<<< {Status} GET (binary download) {Url} — {Bytes} bytes", (int)dlResponse.StatusCode, binaryUrl, dlResponse.Content.Headers.ContentLength ?? -1);

        if (!dlResponse.IsSuccessStatusCode)
        {
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

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            _logger.LogDebug(">>> PUT (multipart) {Url}\n    Content-Type: multipart/form-data\n[content part]\n{Json}\n[file part] name={FileName} mimeType={Mime} size={Bytes}",
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
            _logger.LogDebug("<<< {Status} PUT (multipart) {Url}\n{Body}", (int)response.StatusCode, url, responseBody);

            if (response.IsSuccessStatusCode)
                return (ExtractContentLinkId(responseBody), ExtractUrlPathAndQuery(responseBody));

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var unknownProp = ExtractPropertyNotFoundName(responseBody);
                if (unknownProp != null)
                {
                    _logger.LogDebug("Stripping unknown asset property '{Prop}' from {Guid} and retrying", unknownProp, guid);
                    activeJson = StripNamedProperty(activeJson, unknownProp);
                    continue;
                }
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // Asset already exists on target under a different parent — use the existing version.
                _logger.LogDebug("Asset {Guid} already exists on target with a different parent (InvalidParent) — reusing existing", guid);
                var existingId = await GetTargetContentIdAsync(guid, target, targetToken);
                var existingJson = await ReadFromTargetAsync(guid, target, targetToken);
                return (existingId, existingJson != null ? ExtractUrlPathAndQuery(existingJson) : null);
            }

            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        throw new HttpRequestException($"Asset write failed after {MaxWriteAttempts} attempts resolving property errors");
    }

    // Extracts the top-level CMA "url" field and returns its path + query component.
    private static string ExtractUrlPathAndQuery(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("url", out var urlProp) &&
                urlProp.ValueKind == JsonValueKind.String)
            {
                var fullUrl = urlProp.GetString() ?? "";
                return Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri)
                    ? uri.PathAndQuery
                    : (fullUrl.StartsWith('/') ? fullUrl : null);
            }
        }
        catch { }
        return null;
    }

    internal static string GetAssetBinaryUrl(string json)
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

    // Filters out CMS system pages (Root=1, Waste Basket=2) that are never meaningful
    // user content and should not appear in the dependency plan.
    private static bool IsSystemContentReference(ContentReference contentRef) =>
        !ContentReference.IsNullOrEmpty(contentRef) &&
        (contentRef.ID == ContentReference.RootPage.ID ||
         contentRef.ID == ContentReference.WasteBasket.ID);

    // Filters out Optimizely built-in PropertyContentReference properties that hold
    // structural/navigational links (parent, shortcut target, archive location) rather
    // than user-created content dependencies.
    private static readonly HashSet<string> SystemPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PageParentLink", "PageShortcutLink", "PageArchiveLink", "PageDeletedLink"
    };

    private static bool IsSystemPropertyName(string propName) =>
        !string.IsNullOrEmpty(propName) && SystemPropertyNames.Contains(propName);

    private static string GetMediaNodeType(string name)
    {
        var ext = Path.GetExtension(name ?? "").TrimStart('.').ToLowerInvariant();
        if (ext is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "svg" or "ico" or "tiff" or "tif" or "heic" or "heif" or "avif")
            return "Image";
        if (ext is "mp4" or "mov" or "avi" or "mkv" or "wmv" or "flv" or "webm" or "m4v" or "mpg" or "mpeg" or "m2v" or "3gp" or "3g2" or "ogv" or "mts" or "m2ts")
            return "Video";
        if (ext is "mp3" or "wav" or "ogg" or "flac" or "aac" or "m4a" or "wma" or "opus" or "aiff" or "mid" or "midi")
            return "Audio";
        if (ext is "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "odt" or "ods" or "odp" or "rtf" or "txt" or "csv" or "pages" or "numbers" or "keynote" or "epub")
            return "Document";
        return "UnknownMedia";
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

    // Detects {"code":"InvalidContent","detail":"Property 'X' is required."} errors and
    // returns the property name so the caller can strip it and defer it for a second pass.
    private static string ExtractInvalidContentPropertyName(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("code", out var code) ||
                !string.Equals(code.GetString(), "InvalidContent", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!root.TryGetProperty("detail", out var detail)) return null;
            var msg = detail.GetString() ?? "";
            var start = msg.IndexOf('\'') + 1;
            var end = msg.IndexOf('\'', start);
            if (start > 0 && end > start) return msg[start..end];
        }
        catch { }
        return null;
    }

    // Detects {"code":"ContentNotVersionable",...} — the CMA rejects status/startPublish/
    // stopPublish on content types that aren't versionable (some blocks and folders).
    private static bool IsContentNotVersionable(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            return doc.RootElement.TryGetProperty("code", out var code) &&
                   string.Equals(code.GetString(), "ContentNotVersionable", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Returns the first content GUID referenced inside a CMA property value node,
    // so the deferred-patch pass can check whether that content now exists on the target.
    private static Guid? ExtractReferencedContentGuid(string propertyJson)
    {
        try
        {
            var node = JsonNode.Parse(propertyJson)?.AsObject();
            if (node == null) return null;
            // PropertyContentReference: { value: { guidValue: "..." } }
            if (node["value"] is JsonObject val)
            {
                if (Guid.TryParse(val["guidValue"]?.GetValue<string>(), out var g)) return g;
            }
            // PropertyContentArea: { value: [ { contentLink: { guidValue: "..." } } ] }
            if (node["value"] is JsonArray arr && arr.Count > 0)
            {
                var cl = arr[0]?.AsObject()?["contentLink"]?.AsObject();
                if (cl != null && Guid.TryParse(cl["guidValue"]?.GetValue<string>(), out var g2)) return g2;
            }
        }
        catch { }
        return null;
    }

    // Returns a fallback GUID for a property whose referenced content doesn't exist on the target.
    // Page references → start page GUID (same across all environments in DXP).
    // Block/media references → a PLACEHOLDER copy created in the target page's asset folder.
    private async Task<string> GetFallbackReferenceGuidAsync(
        string propertyJson,
        Guid? sourceRefGuid,
        Guid pageTargetGuid,
        string sourceBaseUrl,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        // Determine whether the referenced content is a page
        bool isPage = false;
        if (sourceRefGuid.HasValue)
        {
            try
            {
                var srcJson = await ReadFromSourceAsync(sourceRefGuid.Value, sourceBaseUrl, sourceToken);
                using var doc = JsonDocument.Parse(srcJson);
                isPage = IsPageContent(doc.RootElement);
            }
            catch { }
        }
        else
        {
            // Fall back to property data type
            try
            {
                var pdt = JsonNode.Parse(propertyJson)?.AsObject()?["propertyDataType"]?.GetValue<string>();
                isPage = pdt == "PropertyContentReference";
            }
            catch { }
        }

        if (isPage)
        {
            // Use the site start page — its GUID is identical on all DXP environments
            var (startGuid, _) = GetSiteRootFallback();
            if (startGuid.HasValue && await ExistsOnTargetAsync(startGuid.Value, target, targetToken))
                return startGuid.Value.ToString();
            return null;
        }

        // Block or media — create a PLACEHOLDER in the target page's asset folder
        return sourceRefGuid.HasValue
            ? await CreatePlaceholderAsync(sourceRefGuid.Value, pageTargetGuid, sourceBaseUrl, sourceToken, target, targetToken)
            : null;
    }

    // Creates a minimal PLACEHOLDER content item on the target using the source item's content type.
    private async Task<string> CreatePlaceholderAsync(
        Guid sourceRefGuid,
        Guid pageTargetGuid,
        string sourceBaseUrl,
        string sourceToken,
        DxpEnvironmentConfig target,
        string targetToken)
    {
        try
        {
            var srcJson = await ReadFromSourceAsync(sourceRefGuid, sourceBaseUrl, sourceToken);
            string stubJson;
            using (var doc = JsonDocument.Parse(srcJson))
            {
                stubJson = IsMediaContent(doc.RootElement)
                    ? BuildMinimalAssetJson(srcJson, pageTargetGuid)
                    : BuildMinimalContentJson(srcJson, pageTargetGuid);
            }

            var stubNode = JsonNode.Parse(stubJson)?.AsObject();
            if (stubNode == null) return null;
            stubNode["name"] = "PLACEHOLDER";
            stubNode["status"] = "Published";

            var placeholderGuid = Guid.NewGuid();
            await WriteToTargetAsync(placeholderGuid, stubNode.ToJsonString(), target, targetToken);
            _logger.LogDebug("Created PLACEHOLDER for missing {SourceGuid} under page {Page}", sourceRefGuid, pageTargetGuid);
            return placeholderGuid.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not create PLACEHOLDER for {SourceGuid}: {Error}", sourceRefGuid, ex.Message);
            return null;
        }
    }

    private static JsonNode BuildFallbackPropertyValue(string propertyJson, string fallbackGuid)
    {
        try
        {
            var node = JsonNode.Parse(propertyJson)?.AsObject();
            if (node == null) return null;
            var pdt = node["propertyDataType"]?.GetValue<string>();
            if (pdt == "PropertyContentReference")
                node["value"] = new JsonObject { ["guidValue"] = fallbackGuid };
            else if (pdt == "PropertyContentArea")
                node["value"] = new JsonArray { new JsonObject { ["contentLink"] = new JsonObject { ["guidValue"] = fallbackGuid }, ["displayOption"] = "" } };
            return node;
        }
        catch { }
        return null;
    }

    private static string ExtractNamedPropertyJson(string json, string propertyName)
    {
        try
        {
            var node = JsonNode.Parse(json)?.AsObject();
            if (node != null && node[propertyName] is JsonNode val)
                return val.ToJsonString();
        }
        catch { }
        return null;
    }

    private async Task<string> ReadFromTargetAsync(Guid guid, DxpEnvironmentConfig target, string targetToken)
    {
        var r = await _cma.GetManagementAsync(target.BaseUrl, targetToken, guid, "read target");
        return r.IsSuccess ? r.Body : null;
    }

    private static string StripNamedProperty(string json, string propertyName)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node == null) return json;
        node.Remove(propertyName);
        return node.ToJsonString();
    }

    // Returns true if the content lives in a page's "For This Page" content-asset folder (its path
    // contains /contentassets/). Such content must be parented by the *target* page (targetGuid) so
    // Optimizely files it in that page's own asset folder — NOT by the source folder's guid, which
    // doesn't exist on the target and would orphan the block in a recreated copy.
    //
    // The content's own "url" is the obvious signal, but the CMA GET returns "url": null for anything
    // inside a content-asset folder — so we also check parentLink.url, which still carries the
    // /contentassets/{guid}/ path. Missing that fallback misdetects local blocks as global and is what
    // sent them to an orphan folder. Global content (/globalassets/ or no url at all) preserves its
    // source parentLink.
    internal static bool IsLocalContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (UrlContains(root, "/contentassets/")) return true;
            if (root.TryGetProperty("parentLink", out var parentLink) && parentLink.ValueKind == JsonValueKind.Object &&
                UrlContains(parentLink, "/contentassets/")) return true;
        }
        catch { }
        return false;
    }

    private static bool UrlContains(JsonElement obj, string fragment) =>
        obj.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String &&
        (u.GetString() ?? "").Contains(fragment, StringComparison.OrdinalIgnoreCase);

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
        string routeSegment = null;
        try
        {
            using var doc = JsonDocument.Parse(sourceJson);
            var root = doc.RootElement;
            // Media names must carry a file extension or the CMA rejects the write with
            // "File extension must be given" — editors sometimes name media without one.
            name = ResolveAssetFileName(root);
            // Preserve the source "Name in URL" so the media keeps the same URL on the target —
            // inline <img> srcs reference media by this segment, not by the Name (which can differ).
            if (root.TryGetProperty("routeSegment", out var rs) && rs.ValueKind == JsonValueKind.String)
                routeSegment = rs.GetString();
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
        if (!string.IsNullOrEmpty(routeSegment))
            obj["routeSegment"] = routeSegment;
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
            return ResolveAssetFileName(doc.RootElement);
        }
        catch { }
        return null;
    }

    // Returns the media item's name guaranteed to carry a file extension. The content Name is
    // used as-is when it already has one; otherwise the extension is recovered from the
    // routeSegment, then the url, then the mimeType.
    private static string ResolveAssetFileName(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        if (!string.IsNullOrEmpty(name) && Path.HasExtension(name)) return name;

        string ext = null;
        if (root.TryGetProperty("routeSegment", out var rs) && rs.ValueKind == JsonValueKind.String)
            ext = Path.GetExtension(rs.GetString() ?? "");
        if (string.IsNullOrEmpty(ext) && root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            ext = Path.GetExtension((u.GetString() ?? "").Split('?')[0]);
        if (string.IsNullOrEmpty(ext))
        {
            var mime = root.TryGetProperty("mimeType", out var mt) &&
                       mt.TryGetProperty("value", out var mv) && mv.ValueKind == JsonValueKind.String
                ? mv.GetString() : null;
            ext = ExtensionForMimeType(mime);
        }
        if (string.IsNullOrEmpty(name)) name = "asset";
        return string.IsNullOrEmpty(ext) ? name : name + ext;
    }

    private static string ExtensionForMimeType(string mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
        "image/avif" => ".avif",
        "image/heic" => ".heic",
        "application/pdf" => ".pdf",
        _ => null
    };

    private async Task<int?> WriteToTargetAsync(Guid guid, string contentJson, DxpEnvironmentConfig target, string token,
        List<(Guid guid, string property, string json)> deferredPatches = null,
        string sourceBaseUrl = null, string sourceToken = null)
    {
        var activeJson = contentJson;
        var strippedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versioningStripped = false;

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var response = await _cma.PutManagementAsync(target.BaseUrl, token, guid, activeJson, "write content");
            var body = response.Body;

            if (response.IsSuccess)
                return ExtractContentLinkId(body);

            if (response.Status == HttpStatusCode.BadRequest)
            {
                var unknownProp = ExtractPropertyNotFoundName(body);
                if (unknownProp != null)
                {
                    // If we've already tried stripping this property name and the error
                    // repeats, the property is nested (not at root level) so StripNamedProperty
                    // was a no-op. Throw the real error rather than looping indefinitely.
                    if (!strippedProps.Add(unknownProp))
                        throw new HttpRequestException($"HTTP {(int)response.Status}: {body}");

                    _logger.LogDebug("Stripping unknown property '{Prop}' from {Guid} and retrying", unknownProp, guid);
                    activeJson = StripNamedProperty(activeJson, unknownProp);
                    continue;
                }

                var requiredProp = ExtractInvalidContentPropertyName(body);
                if (requiredProp != null)
                {
                    var propJson = ExtractNamedPropertyJson(activeJson, requiredProp);

                    // If the property doesn't exist at root level it is nested inside a
                    // PropertyBlock. StripNamedProperty would be a no-op and we'd loop
                    // forever — throw the real error instead so the caller gets a clear message.
                    if (propJson == null)
                        throw new HttpRequestException($"HTTP {(int)response.Status}: {body}");

                    // If we've already tried this property and still get the same error the
                    // strip didn't take effect — bail rather than looping.
                    if (!strippedProps.Add(requiredProp))
                        throw new HttpRequestException($"HTTP {(int)response.Status}: {body}");

                    // Try to substitute a fallback value so the content can be written.
                    // Page references → site root; block/media references → placeholder copy.
                    if (!string.IsNullOrEmpty(sourceBaseUrl) && !string.IsNullOrEmpty(sourceToken))
                    {
                        try
                        {
                            var sourceRefGuid = ExtractReferencedContentGuid(propJson);
                            var fallbackGuid = await GetFallbackReferenceGuidAsync(
                                propJson, sourceRefGuid, guid,
                                sourceBaseUrl, sourceToken, target, token);
                            if (!string.IsNullOrEmpty(fallbackGuid))
                            {
                                var fallbackNode = BuildFallbackPropertyValue(propJson, fallbackGuid);
                                if (fallbackNode != null)
                                {
                                    var node = JsonNode.Parse(activeJson)?.AsObject();
                                    if (node != null)
                                    {
                                        node[requiredProp] = fallbackNode;
                                        activeJson = node.ToJsonString();
                                        _logger.LogDebug("Required property '{Prop}' on {Guid} — substituted fallback {FallbackGuid}", requiredProp, guid, fallbackGuid);
                                        continue;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Could not build fallback for required property '{Prop}' on {Guid}: {Error}", requiredProp, guid, ex.Message);
                        }
                    }

                    // No fallback available — strip the property and defer for a second pass.
                    if (deferredPatches != null)
                    {
                        deferredPatches.Add((guid, requiredProp, propJson));
                        _logger.LogDebug("Deferring required property '{Prop}' on {Guid}", requiredProp, guid);
                    }
                    activeJson = StripNamedProperty(activeJson, requiredProp);
                    continue;
                }

                // Non-versionable content (some blocks/folders) rejects status/startPublish/
                // stopPublish. Those only apply to versionable content, so strip them and retry.
                if (!versioningStripped && IsContentNotVersionable(body))
                {
                    versioningStripped = true;
                    var node = JsonNode.Parse(activeJson)?.AsObject();
                    if (node != null)
                    {
                        node.Remove("status");
                        node.Remove("startPublish");
                        node.Remove("stopPublish");
                        activeJson = node.ToJsonString();
                        _logger.LogDebug("{Guid} is not versionable — retrying without status/startPublish/stopPublish", guid);
                        continue;
                    }
                }
            }

            if (response.Status == HttpStatusCode.Conflict)
            {
                // A 409 usually means the content already exists on target under a different parent
                // (InvalidParent) — reuse the existing version. But it is also what a malformed write
                // returns (e.g. a language branch that still carries culture-invariant properties), and
                // silently reusing there hides a real failure. Log the body at Warning so it's never
                // silent, then reuse the existing version.
                _logger.LogWarning("409 Conflict writing {Guid} — reusing existing target version. Response: {Body}", guid, body);
                return await GetTargetContentIdAsync(guid, target, token);
            }

            throw new HttpRequestException($"HTTP {(int)response.Status}: {body}");
        }

        throw new HttpRequestException($"Write failed after {MaxWriteAttempts} attempts resolving property errors");
    }

    private (Guid? guid, string path) GetSiteRootFallback()
    {
        try
        {
            var startRef = SiteDefinition.Current?.StartPage ?? ContentReference.StartPage;
            if (ContentReference.IsNullOrEmpty(startRef)) return (null, null);
            var startPage = _contentLoader.Get<IContent>(startRef, LanguageSelector.AutoDetect(true));
            return (startPage.ContentGuid, $"{startPage.Name} (site root)");
        }
        catch { return (null, null); }
    }

    private static DxpEnvironmentConfig ResolveEnvironment(DxpTransferSettings settings, string name) =>
        name?.ToLowerInvariant() switch
        {
            "integration" => settings.Integration,
            "preproduction" => settings.Preproduction,
            "production" => settings.Production,
            _ => null
        };

    private static ContentReference ParseContentReference(string id) => ContentReferenceParser.Parse(id);
}
