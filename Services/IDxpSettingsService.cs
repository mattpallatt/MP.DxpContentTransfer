using DxpContentTransfer.Models;

namespace DxpContentTransfer.Services;

public interface IDxpSettingsService
{
    DxpTransferSettings Get();
    void Save(DxpTransferSettings settings);
}
