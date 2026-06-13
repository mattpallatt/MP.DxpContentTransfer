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

There is no test project and no running host here, so "verify" means: it compiles, plus manual
testing against a live DXP environment for behavioral changes.

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
  credentials (admin roles only).
- `Services/ContentTransferService` — the engine (~2300 lines). Pre-check planning, the recursive
  stub→blocks→media→full transfer cycle, JSON property surgery, deferred forward-reference patches,
  placeholder fabrication, URL/domain relinking.
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
  visitors `WalkJsonObjects` / `WalkJsonElements` / `MutateJsonStrings` — don't hand-roll another
  "if object recurse / if array recurse" method.
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
- **`ContentNotVersionable` on write** (some blocks/folders): the CMA rejects `status`/`startPublish`/
  `stopPublish` on non-versionable content. `WriteToTargetAsync` detects this code and retries once
  with those fields stripped — don't remove that branch.
