# CLAUDE.md

Guidance for working in this repo. Keep it current when architecture or conventions change.

## What this is

An Optimizely CMS 12 add-in (NuGet package `MP.DxpContentTransfer`) that gives editors a
sidebar gadget to push content — pages, blocks, media, inline images, references — between DXP
environments (Integration / Preproduction / Production) over the Optimizely REST APIs. There is
no host application in this repo; it builds to a class library that a CMS site consumes.

## Build & restore

The EPiServer packages are **not on nuget.org** — they live on the Optimizely feed. A plain
`dotnet restore` fails with `NU1101: Unable to find package EPiServer.CMS.Core`. Use:

```bash
dotnet restore --source https://api.nuget.org/v3/index.json --source https://nuget.optimizely.com/feed/packages.svc/
dotnet build --no-restore
```

`TargetFramework` is `net10.0`, `Nullable` is **disabled**, `ImplicitUsings` enabled. A clean
build has 0 errors / 0 `CS` warnings; the ~20 `NU19xx` warnings are pre-existing CVE advisories
on transitive EPiServer dependencies (Newtonsoft.Json, ImageSharp, etc.) — not our code.

There is a unit-test project at `tests/DxpContentTransfer.Tests` (xUnit) covering the **pure**
helpers — the JSON/XHTML surgery where regressions hide (id/guid remapping, property stripping,
inline-image/fragment parsing). Those helpers are `internal static` and reached via
`[InternalsVisibleTo]`. Run it (same dual feed as the build):

```bash
dotnet test tests/DxpContentTransfer.Tests/DxpContentTransfer.Tests.csproj
```

The `tests/` folder is excluded from the library's own compilation (`<Compile Remove="tests/**" />`)
because the Razor SDK otherwise globs every `.cs` under the project root. There is no running host
here, so full "verify" for behavioural changes is still: it compiles, tests pass, plus manual
testing against a live DXP environment.

## Host site requirements (for the CMS that consumes this package)

The transfer reads source content over the **Content Management API (CMA)** and resolves URLs
over the **Content Delivery API (CDV)** — even for the environment it runs on — so the consuming
site must expose both, with OAuth. Missing pieces seen in the wild:

- `EPiServer.ContentManagementApi` package + `services.AddContentManagementApi(OpenIDConnectOptionsDefaults.AuthenticationScheme)`.
  Without it, `/api/episerver/v3.0/contentmanagement/{guid}` 404s and every source read fails.
- `EPiServer.ContentDeliveryApi.Cms` + `services.AddContentDeliveryApi()`.
- `services.AddOpenIDConnect<TUser>(...)` with an application that has the `epi_content_management`
  scope (the gadget authenticates with `client_credentials`).
- **`app.UseCors()` between `app.UseRouting()` and `app.UseEndpoints()`.** The OpenIDConnect token
  endpoint carries CORS metadata; without the middleware every `/connect/token` request 500s with
  "contains CORS metadata, but a middleware was not found that supports CORS".

The gadget logs a `build {version}` line at the start of every pre-check/transfer (see `BuildMarker`)
so the running build can be confirmed from the logs; keep it in sync with the package `Version`.

## Architecture

- `Controllers/DxpGadgetController` — renders the editor gadget (`Views/DxpGadget/Index.cshtml`,
  a self-contained page with inline CSS + a ~550-line JS IIFE that drives the pre-check/transfer/
  polling UI).
- `Controllers/DxpTransferApiController` — JSON API: `pre-check`, `transfer` (fire-and-forget
  `Task.Run`, returns a jobId), `progress/{jobId}`, `result/{jobId}`. Job state is an in-memory
  `ConcurrentDictionary` — **it does not survive an app-pool recycle**; that's a known tradeoff.
- `Controllers/DxpSettingsController` + `Views/DxpSettings` — admin page for per-environment
  credentials (admin roles only). Also exposes `Admin/TestConnection` (AJAX), backed by
  `EnvironmentHealthService`, for the per-environment "Test connection" button. Reached via the admin
  tools menu: `Controllers/DxpClientResourceController` serves `AdminInit.js`, which
  `Middleware/DxpAdminScriptMiddleware` (using `HtmlBodyInjector`, auto-registered via `IStartupFilter`)
  injects into admin pages to overlay the settings iframe on the SPA's hash route.
  NB: the top-bar environment badge lives in a *separate* package, `MP.DxpEnvironmentIndicator` — it was
  removed from this add-in so each tool does one thing.
- `Services/EnvironmentHealthService` — the "Test connection" probe: fetches a client-credentials
  token (catches the CORS-500 / bad-secret case), checks the granted scope, then GETs the CMA for a
  throwaway GUID. **A 404 here is SUCCESS, not failure** — a healthy CMA 404s a non-existent GUID
  exactly as `ExistsOnTargetAsync` relies on during a transfer. (An earlier version re-probed
  anonymously expecting a 401/403 challenge to prove the route exists — wrong: Optimizely returns 404
  for unauthorized/unknown content, so that false-failed working environments. Don't reinstate it.)
  Registered-but-not-found vs route-not-registered is separated only by response *shape* (a real API
  emits a JSON/problem body; a bare framework 404 doesn't), and even a bare 404 is reported as a pass
  with a soft advisory — never a hard fail.
- `Services/ContentTransferService` — the engine (~2500 lines). Pre-check planning, the recursive
  stub→blocks→media→full transfer cycle, **per-language branch writes**, JSON property surgery, deferred
  forward-reference patches, **page-local block placement** (deferred-until-folder-mapped), placeholder
  fabrication, URL/domain relinking. The **pure** XHTML/JSON helpers have been lifted out (see next two),
  leaving the stateful engine; it pulls the visitors back in via `using static`.
- `Services/XhtmlProcessor` — pure PropertyXhtmlString processing: `ExtractXhtmlImageUrls`,
  `ExtractXhtmlContentFragments`/`ParseContentFragments`, `RewriteXhtmlUrls`, `NormalizeInlineImagePath`,
  `ToRelativePath`. No I/O or engine state, so it's directly unit-tested. The engine still drives it and
  owns the resolution/upload of whatever it surfaces.
  - **Reads parse, writes string-replace — keep it that way.** The *read* paths
    (`ExtractXhtmlImageUrls`, `ParseContentFragments`) use **HtmlAgilityPack** to parse the markup, so
    attribute order/quoting/whitespace can't slip an asset past. The *write* path (`RewriteXhtmlUrls`)
    stays surgical string replacement and **must not** be "consistency-refactored" to parse→mutate→
    re-serialise: HAP (like `XhtmlString.ToString()`) re-emits a normalised document, which mangles the
    `epi-contentfragment` divs and `,,{id}` edit-mode suffixes the transfer depends on byte-for-byte.
- `Services/JsonVisitors` — the three shared tree walkers (`WalkJsonObjects` / `WalkJsonElements` /
  `MutateJsonStrings`). Imported `using static` so callers read unqualified.
- `Services/CmaClient` — the only HTTP transport. All CMA/CDV GET/PUT go through it (URL building,
  bearer auth, logging). If you need a new API call, add a method here, don't inline `HttpClient`.
- `Services/EnvironmentTokenService` — OAuth2 client-credentials tokens, cached per BaseUrl+ClientKey
  until expiry minus 30s.
- `Services/DxpSettingsService` — loads/saves settings to the Optimizely Dynamic Data Store (DDS).
- `Services/ContentReferenceParser` — shared `"123_456"`/`"123:eng"` → `ContentReference` parsing.
- `Models/DxpTransferSettings` — DDS-backed flat strings + cached per-environment config objects +
  `DetectByHost`. Registered via `Extensions/ServiceCollectionExtensions.AddDxpContentTransfer()`.

## Conventions & gotchas

- **DDS / IDatabaseExecutor has thread affinity.** It must not be touched from the background
  `Task.Run` transfer thread. Two consequences already baked in, do not undo them:
  1. `DxpSettingsService` caches settings after the first HTTP-thread load.
  2. `PreCheckAsync`/`TransferAsync` pre-load *all* `IContentLoader`/`IUrlResolver` data into an
     `itemContexts` list **before the first `await`**, because an await can resume on a different
     thread. New code that reads content must follow this pattern.
- **JSON is manipulated as `System.Text.Json` trees.** For whole-document walks use the shared
  visitors `WalkJsonObjects` / `WalkJsonElements` / `MutateJsonStrings` (in `Services/JsonVisitors`,
  imported via `using static`) — don't hand-roll another "if object recurse / if array recurse" method.
- **CMA v3 quirks** the code deliberately works around: integer ids are environment-specific (strip
  them, the target binds by `guidValue`); media refs need the target integer id injected back;
  unknown/required properties are stripped-and-retried in a bounded loop (`MaxWriteAttempts`), with
  required references either substituted with a fallback or deferred to a second pass.
- **Do NOT re-add `sortIndex` injection.** The CMA write model has no `sortIndex` field (verified
  against the Optimizely docs); a previous version threaded it through three methods to an
  `InjectSortIndex` that was never called, and it was removed. Child-page order is preserved by
  `CollectItems` enqueuing children in `PageSortIndex` order so they're *created* in order.
- Empty `catch {}` around best-effort URL/JSON probes is intentional (not-found is normal control
  flow). Catches around content/parent loads log at Debug. Don't promote those to Warning — it
  floods the log on the folder-existence checks.
- **Inline images in XHTML are the fiddly part** (`TransferItemCoreAsync` step 3). The editor stores
  `<img src>` as an internal edit-mode URL like
  `/EPiServer/CMS/Content/globalassets/en/foo/bar.jpg,,105?epieditmode=false`, where `,,105` is the
  **source** integer content id (environment-specific). The pipeline:
  1. `NormalizeInlineImagePath` decodes, drops the query/`,,version`, and rewrites the
     `/EPiServer/CMS/Content` prefix to its friendly form for lookup.
  2. Resolution order: CDV by friendly URL → local `IUrlResolver.Route` → `TryResolveByEmbeddedContentId`
     (loads the `,,{id}` locally via `IContentLoader`).
  3. The media write preserves the source `routeSegment` (`BuildMinimalAssetJson`) so the target keeps
     the same URL, and `ResolveAssetFileName` guarantees the name has a file extension (the CMA rejects
     media without one: "File extension must be given").
  4. The markup is rewritten two ways (both run; whichever matches wins): the full src → friendly URL
     (`xhtmlUrlMap`), and `,,{sourceId}` → `,,{targetId}` (`RecordInlineImageIdRemapAsync` +
     `RewriteXhtmlNodes`), since the editor bakes the source integer id into the stored URL.
  - Both `<img src>` **and `<a href>`** are scanned (`CollectXhtmlUrls`), so linked media (mp4, pdf…)
    transfer too — but only content that is actually `Media` is uploaded (the `GetAssetBinaryUrl`
    guard in the loop), so an `<a href>` to a page/block isn't created out-of-band as a bogus asset.
- **Inline content blocks** (`<div class="epi-contentfragment" data-contentguid data-contentlink>`
  baked into XHTML) are a second hidden-dependency case. The block guid lives only in the HTML string,
  so `ExtractContentReferenceGuids` (a `guidValue` walker) never sees it. `ExtractXhtmlContentFragments`
  /`ParseContentFragments` pull `(guid, contentLink, name)` out of the divs; their guids are appended to
  the step-1 transfer enumeration so the block is created/re-PUT like any ContentArea block, and
  `data-contentlink="{sourceId}"` is remapped to the target integer id in step 4 (`xhtmlBlockIdMap` →
  `RewriteXhtmlNodes`). The guid is environment-stable so `data-contentguid` needs no rewrite.
  - **Pre-check vs transfer read the XHTML differently.** The transfer reads the raw CMA JSON `value`
    (which contains the `epi-contentfragment` divs, so the regex parser works). The pre-check has only
    the in-process `XhtmlString`, and `XhtmlString.ToString()`/the raw long-string **does not reliably
    carry the fragment divs** (inline `<a>`/`<img>` survive, content fragments don't) — so the pre-check
    must walk `XhtmlString.Fragments` for `ContentFragment` (use `cf.ContentGuid` + `cf.GetContent()`)
    to surface inline blocks as `InlineBlock` nodes. Don't switch it back to regex-on-string.
- **`ContentNotVersionable` on write** (some blocks/folders): the CMA rejects `status`/`startPublish`/
  `stopPublish` on non-versionable content. `WriteToTargetAsync` detects this code and retries once
  with those fields stripped — don't remove that branch.
- **Multilingual: every item transfers all its language branches** (`TransferItemCoreAsync` step 5 →
  `WriteLanguageBranchesAsync`). The master language is written by the normal flow; then each language
  in `existingLanguages` is read with an `Accept-Language` header and written as a **branch payload**
  (`BuildLanguageBranchJson`). The CMA treats languages as separate objects sharing the culture-invariant
  properties, so a branch write must send **only the culture-specific properties** — sending the
  invariant ones is what returns **409 Conflict** (the bug that started this). Which properties are
  culture-specific comes from `PropertyDefinition.LanguageSpecific`, pre-loaded into a `contentType →
  names` map **on the HTTP thread** (DB-backed, same affinity rule as the content pre-load) and threaded
  through as `LanguagePlan`. Each branch first runs `ProcessReferencedDependenciesAsync` on its own JSON,
  so a block/media **referenced only from a non-master branch** is transferred too (the master pass's
  `visited`/`idMap` dedup shared deps). **Known gap:** an inline rich-text *image* embedded only in a
  non-master branch (not shared with the master, so not covered by the master's XHTML maps) is not yet
  re-uploaded for the branch. Don't reinstate the old "409 ⇒ silently reuse existing" behaviour — it now
  logs the body at Warning.
  - **Language picker.** Pre-check returns `AvailableLanguages` (the union of `ILocalizable.ExistingLanguages`
    across items, read **before the first await**). The gadget shows a multiselect in the plan view; no
    ticks = "Transfer all languages" = `selectedLanguages` null/empty = every branch. Ticked locale codes
    flow `TransferRequestModel.SelectedLanguages → TransferAsync → LanguagePlan.Includes`. The master
    language **always** transfers (it's the prerequisite for any branch); selection narrows the *extras*.
- **Page-local "For This Page" placement is the deepest rabbit hole — read this before touching block
  parenting.** A page/block's local assets live in a `SysContentAssetFolder` whose GUID is **generated
  per environment** (stored as the owner's `ContentAssetsID`, not derivable, not exposed by the CMA) and
  parented by the global `/contentassets/` root, not the owner. Optimizely **routes page-local *media***
  parented-by-owner into that folder automatically (creating it on first upload), but does **NOT route
  page-local *blocks*** — a block parented by the owner is orphaned directly under the page. The pipeline
  (`ProcessReferencedDependenciesAsync` + `TransferDeferredLocalBlocksAsync`):
  1. `IsLocalContent` detects local content by the item's `url` **and** its `parentLink.url` containing
     `/contentassets/` (the CMA returns `url:null` for asset-folder content — checking only `url` was the
     misdetection that orphaned blocks).
  2. Local **blocks are deferred** (collected, not transferred inline) until *after* the page write and
     local-media upload, because the target folder GUID isn't known until then.
  3. As local media transfers, `CaptureFolderMappingAsync` records source-folder → target-folder in
     `LanguagePlan.FolderMap` (the routed media's `parentLink` reveals the target folder GUID). Deferred
     local blocks reuse that map to land in the **same folder as their sibling assets**.
  4. Folder with no sibling media → `ResolveAssetFolderGuidAsync` **probes**: PUTs a 1×1 image to the owner
     (Optimizely creates+links the folder + routes the image), reads the folder GUID off its `parentLink`,
     deletes the probe (the folder stays). Needs the owner to exist on target — which is *why* blocks are
     deferred to after the page write.
  Do **not** parent a local block by the owner (orphans it under the page) or by the source folder GUID via
  `EnsureContentParentAsync` (creates an orphan *copy* of the folder) — both were tried and both fail.
