# DXP Content Transfer

**Empower your editors to move content across environments with confidence.**

DXP Content Transfer is a specialized tool for Optimizely CMS 12 that removes the "developer bottleneck" when syncing content between Integration, Preproduction, and Production environments. Editors can now "push" pages, blocks, and media directly from the CMS sidebar without requiring database backups or manual recreation.

---

## Key Benefits

*   **Editor-Driven Control:** Transfer content trees directly from the Assets panel. No deployment pipelines or technical requests required.
*   **Safe & Predictable:** A mandatory "Pre-Check" phase generates a detailed plan, showing exactly what will be created, updated, or skipped before a single byte is written to the target.
*   **Zero-Orphan Logic:** The tool intelligently resolves dependencies. When you move a page, it automatically finds and moves the specific blocks and images used on that page.
*   **Smart Link Correction:** Links within rich text and images are automatically updated to work correctly on the target environment, handling the different internal IDs seamlessly.
*   **Hierarchy Awareness:** Maintain your site structure. If a parent page doesn't exist on the target, the tool helps you find the right place for it or places it safely under the site root.
*   **Automated Placeholders:** If a page references something that hasn't been transferred yet, the tool handles it gracefully by applying the link later or using a placeholder so the transfer doesn't fail.

---

## Simple Workflow

1.  **Select Target:** Choose your destination environment (e.g., Preproduction) and decide if you want the content to arrive as a **Published** version or a **Draft**.
2.  **Plan:** Click **Check Transfer Plan**. The tool crawls your content tree and displays an expandable list of everything it needs to move.
3.  **Transfer:** Review the badges (`block`, `image`, `inline`, etc.). Once satisfied, click **Confirm & Transfer**.
4.  **Verify:** Watch the real-time progress. Once complete, use the hierarchical result view to click directly through to the new content on the target environment.

---

## Technical Requirements & Installation

### Requirements
*   **Optimizely CMS 12** (.NET 6+)
*   **Content Management API** and **Content Delivery API** must be enabled on both source and target environments.

### Installation
1.  Install the NuGet package: `dotnet add package MP.DxpContentTransfer`
2.  Register the service in `Startup.cs`: `services.AddDxpContentTransfer();`
3.  Ensure your `module.config` in the host project points to the `DxpContentTransfer` assembly.

---

## Setup & Permissions

Once installed, navigate to **Admin › Config › Tool Settings › DXP Content Transfer** to open the settings page.

Enter the base URL and OAuth2 credentials for each environment you want to use as a transfer target:

| Field | Description |
|---|---|
| Base URL | The root URL of the environment, e.g. `https://mysite-integration.dxcloud.episerver.net` |
| Client Key | The OAuth2 `client_id` for that environment |
| Client Secret | The OAuth2 `client_secret` for that environment |

### Permissions
*   **Admins:** Required to access the configuration page (`CmsAdmins` or `WebAdmins`).
*   **Editors:** Any authenticated CMS user can use the transfer gadget.

---

## How it Handles Content

While the tool is easy to use, it performs complex operations under the hood to ensure integrity:

*   **Recursive Discovery:** It doesn't just move the page; it finds every block in every Content Area, and every block inside those blocks, transferring them in the correct order.
*   **Asset Management:** Images stored "For This Page" are moved into the equivalent folder on the target environment. Global assets are placed in their matching folder paths, which are created automatically if they don't exist.
*   **Link Integrity:** The tool scans for "Permanent Links" (the ones that look like `/link/guid.aspx`) and ensures they point to the new IDs on the target site.
*   **Safety First:** Transfers are performed via standard Optimizely APIs, ensuring that all CMS validation rules and content type requirements are respected.

---

## Known Limitations

*   **Unpublished Content:** Only published pages can be the "root" of a transfer.
*   **Content Types:** The target environment must have the same page and block types deployed (via code) as the source, or the transfer will fail.
*   **Sort Order:** While the tool tries to maintain order by transferring items sequentially, the Optimizely API does not allow for explicit "Sort Index" setting.

---

## Logging

Detailed logs are available for troubleshooting, recording every decision made during a transfer. Set the `DxpContentTransfer.Services` namespace to `Debug` in your `appsettings.json` to see the full audit trail.

---

*Developed for the Optimizely DXP Community.*

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
