using EPiServer.Shell.Navigation;

namespace DxpContentTransfer.Menu;

[MenuProvider]
public class DxpTransferMenuProvider : IMenuProvider
{
    private static readonly bool _isCms13 =
        typeof(EPiServer.Core.ContentReference).Assembly.GetName().Version?.Major >= 13;

    public IEnumerable<MenuItem> GetMenuItems()
    {
        if (_isCms13)
        {
            // CMS 13: link directly to the settings page; the admin menu is labelled "Settings" in the UI.
            return new[]
            {
                new UrlMenuItem(
                    "DXP Content Transfer",
                    MenuPaths.Global + "/cms/admin/tools/dxp.transfer",
                    "/EPiServer/DxpContentTransfer/Admin/Settings")
                {
                    IsAvailable = _ => true,
                    SortIndex = SortIndex.Last + 1
                }
            };
        }

        // CMS 12: point at the admin SPA hash route so the shell chrome stays visible;
        // AdminInit.js (injected by DxpAdminScriptMiddleware) overlays the settings iframe.
        return new[]
        {
            new UrlMenuItem(
                "DXP Content Transfer",
                "/global/cms/admin/tools/dxp.transfer",
                "/EPiServer/EPiServer.Cms.UI.Admin/default#/DxpTransfer/Settings")
            {
                IsAvailable = _ => true,
                SortIndex = SortIndex.Last + 1
            }
        };
    }
}
