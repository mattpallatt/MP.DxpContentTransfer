# DXP Content Transfer

An Optimizely CMS 12 gadget that lets editors push content between DXP environments (Integration, Preproduction, Production) directly from the editor sidebar — no developer involvement required.

---

## Features

- **Editor-facing gadget** — appears in the CMS Assets panel alongside any page
- **Two-phase transfer** — a pre-check shows exactly what will happen before anything is written
- **Full dependency resolution** — blocks, images, and inline XHTML images are transferred alongside their parent page, in the correct order
- **Sort order preserved** — page sort index is carried across to the target environment
- **Overwrite or create-new** — choose whether matched pages are updated in place or created with a new ID
- **Per-item progress** — each row in the plan table ticks off as it completes
- **Settings stored in DDS** — no config file edits; credentials are managed through an admin UI page

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
dotnet add package DxpContentTransfer
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

1. Open any page in the Optimizely CMS editor
2. Find the **DXP Content Transfer** gadget
3. Select a target environment from the dropdown
4. Choose whether to transfer as **published** or as a **draft**
5. Optionally check **Include Child Pages** to transfer the full subtree
6. Optionally check **Overwrite Matched Pages** to update pages that already exist on the target (by GUID); if unchecked, matched pages are created with a new ID
7. Click **Check Transfer Plan** — a pre-check runs against the target and shows what will be created, overwritten, or skipped, including all dependencies (blocks, images, inline images)
8. Review the plan, then click **Confirm & Transfer**
9. Each row ticks off as it completes; a summary with links to the transferred content is shown on completion

---

## How the transfer works

### Dependency resolution

Before writing anything, the gadget scans every property on the source content item and resolves:

- **Blocks** — transferred depth-first before their parent page; if a block already exists on the target it is referenced in place without re-uploading
- **Global media** (images in `/globalassets/`) — uploaded immediately, path resolved via the Content Delivery API
- **Local media** (images in `/contentassets/`) — deferred until after the page is created (which also creates the asset bucket), then uploaded
- **Inline XHTML images** — images embedded via the rich-text editor, including EPiServer permanent links (`/link/{guid}.aspx`), are resolved locally, uploaded to the target, and the XHTML is rewritten with the new URL

### API usage

| Operation | API |
|---|---|
| Read source content | Content Management API v3.0 (`GET /api/episerver/v3.0/contentmanagement/{guid}`) using an OAuth2 bearer token for the source environment |
| Write to target | Content Management API v3.0 (`PUT /api/episerver/v3.0/contentmanagement/{guid}`) using an OAuth2 bearer token for the target environment |
| Resolve asset URLs | Content Delivery API v3.0 (`GET /api/episerver/v3.0/content/{guid}`) |
| Authenticate | OAuth2 `client_credentials` grant (`POST {baseUrl}/api/episerver/connect/token`) — tokens are cached for their full lifetime minus a 30-second buffer |

### Parent resolution

If the transferred page's parent does not exist on the target, the gadget walks up the ancestor chain to find the nearest matching parent (by GUID, then by URL). If no ancestor can be found, the page is placed under the site root as an unpublished draft.

---

## Logging

The gadget logs at `DEBUG` level under the logger name `DxpContentTransfer.Services.ContentTransferService`.

To enable API call logging (full request/response bodies for every Content Management API call), add the following to `appsettings.json`:

```json
{
  "DxpContentTransfer": {
    "LogApiCalls": true
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
