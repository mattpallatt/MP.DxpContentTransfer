using DxpContentTransfer.Services;
using EPiServer.Shell.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace DxpContentTransfer.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDxpContentTransfer(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.AddSingleton<IDxpSettingsService, DxpSettingsService>();
        services.AddSingleton<IEnvironmentTokenService, EnvironmentTokenService>();
        services.AddScoped<IContentTransferService, ContentTransferService>();

        services.Configure<ProtectedModuleOptions>(opts =>
        {
            if (!opts.Items.Any(x => string.Equals(x.Name, "DxpContentTransfer", StringComparison.OrdinalIgnoreCase)))
            {
                opts.Items.Add(new ModuleDetails { Name = "DxpContentTransfer" });
            }
        });

        return services;
    }
}
