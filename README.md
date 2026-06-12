# DXP Content Transfer

An Optimizely CMS 12 gadget that lets editors push content between DXP environments (Integration, Preproduction, Production) directly from the editor sidebar — no developer involvement required.

---

## Features

- **Editor-facing gadget** — appears in the CMS Assets panel alongside any published page
- **Two-phase transfer** — a pre-check shows exactly what will happen before anything is written
- **Full dependency resolution** — blocks, images, inline XHTML images, and page references are resolved and shown in the plan
- **Expandable plan** — each page row in the plan can be expanded to reveal its dependencies; rows tick off as they complete, with the page only marking done after all its dependencies finish
- **Correct sort order** — pages are transferred in their source sort order, preserving display order on manual-ordering targets
- **Overwrite or create-new** — choose whether matched pages are updated in place or created with a new ID
- **Context-aware cascade** — if a parent page doesn't exist on the target (or is being created fresh), all descendants are automatically treated the same way, preventing orphaned or misplaced content
- **Forward-reference recovery** — properties that reference content not yet on target are deferred and re-applied once all pages exist; if they still can't be resolved, automatic placeholders are inserted (start page for page refs, typed PLACEHOLDER copy for block/media refs)
- **Hierarchical result view** — the completion summary shows transferred pages in their tree structure with clickable links
- **Download log** — one-click download of a timestamped transfer log (OK/FAIL, errors, placeholder warnings)
- **Unpublished guard** — the gadget blocks transfer if the selected page is not published; unpublished children are silently skipped
- **Settings stored in DDS** — no config file edits; credentials managed through an admin UI page

---

## Requirements

| Dependency | Version |
|---|---|
| Optimizely CMS (EPiServer.CMS.Core) | 12.x |
| EPiServer.CMS.UI.Core | 12.x |
| .NET | 6+ |

---

## Installation

### 1. Add the NuGet package

```
dotnet add package MP.DxpContentTransfer
```

### 2. Register services

In `Startup.cs` (or `Program.cs`), call the extension method inside `ConfigureServices`:

```csharp
services.AddDxpContentTransfer();
```

This registers all required services, HTTP clients, memory cache, and the Optimizely shell module.

### 3. Add the module descriptor

Create the file `modules/_protected/DxpContentTransfer/module.config` in your **host** web project:

```xml
<?xml version="1.0" encoding="utf-8"?>
<module loadFromBin="true" clientResourceRelativePath="" moduleJsonSerializerType="None">
  <assemblies>
    <add assembly="DxpContentTransfer" />
  </assemblies>
  <clientModule>
    <moduleDependencies>
      <add dependency="CMS" />
    </moduleDependencies>
  </clientModule>
</module>
```

---

## Configuration

Once installed, navigate to **Admin › Config › Tool Settings › DXP Content Transfer** to open the settings page.

Enter the base URL and OAuth2 credentials for each environment you want to use as a transfer target:

| Field | Description |
|---|---|
| Base URL | The root URL of the environment, e.g. `https://mysite-integration.dxcloud.episerver.net` |
| Client Key | The OAuth2 `client_id` for that environment |
| Client Secret | The OAuth2 `client_secret` for that environment |

Settings are saved to the Optimizely Dynamic Data Store (DDS) and persist across deployments without requiring any configuration file changes.

> **Which environments to configure?** Configure all environments you want to transfer *to*. The gadget automatically detects which environment it is running on and excludes it from the target list.

---

## Usage

1. Open any **published** page in the Optimizely CMS editor
2. Find the **DXP Content Transfer** gadget in the Assets panel
3. Select a target environment from the dropdown
4. Choose whether to transfer as **published** or as a **draft**
5. Optionally check **Include Child Pages** to transfer the full subtree (unpublished children are skipped automatically)
6. Optionally check **Overwrite Matched Pages** to update pages that already exist on the target by GUID; if unchecked, matched pages are created as new copies with a fresh ID
7. Click **Check Transfer Plan** — a pre-check runs against the target and shows what will be created, overwritten, or skipped, including all dependencies (blocks, images, inline images, page references)
8. Review the plan — expand any row with ▶ to see its dependency detail. Click ▶ again to collapse
9. Click **Confirm & Transfer**
10. Each row ticks ✓ as it completes. The page row only ticks after all its dependency rows finish
11. A hierarchical summary with links to all transferred content is shown on completion, with a **Download transfer log** link

---

## How the transfer works

### Pre-check and plan

Before writing anything, the gadget runs a pre-check against the target environment to determine the action for each item:

| Action | Meaning |
|---|---|
| **Create** | Does not exist on target — will be created at source GUID |
| **Create (new ID)** | GUID exists on target at a different location — will be created as a copy with a new GUID |
| **Overwrite** | Exists on target — will be updated in place |
| **Cannot transfer** | Parent cannot be resolved and site root fallback also failed |

**Context cascade:** if a parent in the batch is being created (not overwritten), all its descendants are automatically treated as non-existent on the target, even if their GUIDs happen to match something elsewhere. This prevents orphaned copies and misplaced content.

The plan shows each item's dependencies (blocks, images, page references) under an expandable row. Dependencies are labelled by type:

| Badge | Meaning |
|---|---|
| `block` | A shared block that will be transferred or referenced |
| `page ref` | A page referenced by a property — not transferred, just shown for awareness |
| `image` | An image asset |
| `inline` | An inline XHTML image |
| `document` | A document asset |
| `video` / `audio` | Media assets |

### Dependency resolution

For each page being transferred, the gadget resolves and transfers:

- **Blocks** — transferred depth-first before their parent page; if a block already exists on the target it is referenced in place without re-uploading
- **Global media** (images in `/globalassets/`) — uploaded immediately; folder paths are created on the target if missing
- **Local media** (images in `/contentassets/`) — deferred until after the page is created (which also creates the asset bucket), then uploaded
- **Inline XHTML images** — images embedded via the rich-text editor, including EPiServer permanent links (`/link/{guid}.aspx`), are resolved, uploaded to the target, and the XHTML is rewritten with the new target URL

### Sort order

Sort order is preserved by transferring pages in their `PageSortIndex` order at each level of the tree. On targets using manual page ordering, the insertion order becomes the display order. The Optimizely Content Management API does not support setting sort index via the REST API directly.

### Forward-reference recovery

Some page types have required properties (e.g. "Set go Back to Page link") that reference other pages. If that page doesn't exist on the target when the content is first written, the gadget:

1. Strips the property and writes the content without it (so the transfer doesn't fail)
2. After all pages have been transferred, re-applies the deferred properties in a second pass
3. If the referenced content still doesn't exist (it was never transferred), substitutes an automatic placeholder:
   - **Page reference** → the site start page (identical GUID across all DXP environments)
   - **Block/media reference** → a typed `PLACEHOLDER` copy created in the page's "For This Page" folder, using the same content type as the original source item

Items that received placeholders are listed in the result panel and the download log.

### API usage

| Operation | API |
|---|---|
| Read source content | Content Management API v3.0 (`GET /api/episerver/v3.0/contentmanagement/{guid}`) |
| Write to target | Content Management API v3.0 (`PUT /api/episerver/v3.0/contentmanagement/{guid}`) |
| Resolve asset URLs | Content Delivery API v3.0 (`GET /api/episerver/v3.0/content/{guid}`) |
| Look up content by URL | Content Delivery API v3.0 (`GET /api/episerver/v3.0/content/?contentURL={path}`) |
| Authenticate | OAuth2 `client_credentials` grant (`POST {baseUrl}/api/episerver/connect/token`) — tokens are cached for their full lifetime minus a 30-second buffer |

### Parent resolution

If the transferred page's parent does not exist on the target, the gadget walks up the ancestor chain to find the nearest matching parent (by GUID, then by URL). If no ancestor can be found, the page is placed under the site root as an unpublished draft.

---

## Logging

The gadget logs at `DEBUG` level under the `DxpContentTransfer.Services` namespace:

| Logger | What it logs |
|---|---|
| `DxpContentTransfer.Services.CmaClient` | Every CMA/CDV request and response (method, URL, body) |
| `DxpContentTransfer.Services.ContentTransferService` | Transfer decisions, dependency resolution, deferred patches, placeholders |
| `DxpContentTransfer.Services.EnvironmentTokenService` | Token acquisition (the client secret is redacted) |

Full request/response bodies are emitted whenever `DEBUG` is enabled for these loggers — there is no separate toggle. Enable it via your logging configuration, e.g. in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "DxpContentTransfer.Services": "Debug"
    }
  }
}
```

Log file location (log4net rolling file): `App_Data/Logs/gadgetworkbench.log`

---

## Permissions

| Action | Required role |
|---|---|
| Use the transfer gadget | Any authenticated CMS user |
| Access the settings admin page | `CmsAdmins`, `Administrators`, or `WebAdmins` |

---

## Known limitations

- **Sort index** — the Optimizely Content Management API does not accept `sortIndex` in PUT requests. Sort order is preserved by transfer sequence on manual-ordering parents, but cannot be set explicitly via the API.
- **Required properties with missing references** — the gadget handles these automatically with placeholders, but placeholder content will need reviewing by editors after transfer.
- **Unpublished content** — unpublished pages cannot be selected as the transfer root, and unpublished children are skipped. Use "Transfer as draft" on the target if you want transferred content to be a draft there.
- **Content type registration** — all content types used by transferred pages must be registered (deployed) on the target environment; otherwise the CMA PUT will return a 400 error.
