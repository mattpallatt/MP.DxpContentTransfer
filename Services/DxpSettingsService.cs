using DxpContentTransfer.Models;
using EPiServer.Data.Dynamic;

namespace DxpContentTransfer.Services;

public class DxpSettingsService : IDxpSettingsService
{
    public DxpTransferSettings Get()
    {
        var store = DynamicDataStoreFactory.Instance.GetStore(typeof(DxpTransferSettings));
        if (store == null)
            return new DxpTransferSettings();

        return store.LoadAll<DxpTransferSettings>().FirstOrDefault() ?? new DxpTransferSettings();
    }

    public void Save(DxpTransferSettings settings)
    {
        var store = DynamicDataStoreFactory.Instance.GetStore(typeof(DxpTransferSettings))
                    ?? DynamicDataStoreFactory.Instance.CreateStore(typeof(DxpTransferSettings));

        var existing = store.LoadAll<DxpTransferSettings>().FirstOrDefault();
        if (existing != null)
        {
            settings.Id = existing.Id;
        }

        store.Save(settings);
    }
}
