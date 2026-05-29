using EPiServer.Shell.Navigation;

namespace DxpContentTransfer.Menu;

[MenuProvider]
public class DxpTransferMenuProvider : IMenuProvider
{
    public IEnumerable<MenuItem> GetMenuItems()
    {
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
