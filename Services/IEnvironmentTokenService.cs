using DxpContentTransfer.Models;

namespace DxpContentTransfer.Services;

public interface IEnvironmentTokenService
{
    Task<string> GetTokenAsync(DxpEnvironmentConfig config);
}
