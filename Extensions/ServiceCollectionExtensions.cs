using DxpContentTransfer.Middleware;
using DxpContentTransfer.Services;
using EPiServer.Shell.Modules;
using Microsoft.AspNetCore.Hosting;
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
        services.AddSingleton<CmaClient>();
        services.AddSingleton<IEnvironmentHealthService, EnvironmentHealthService>();
        services.AddScoped<IContentTransferService, ContentTransferService>();

        // Inject the admin settings-page bootstrap script into admin pages automatically.
        services.AddTransient<IStartupFilter, DxpAdminScriptStartupFilter>();

        // Badge the matched DXP environment into the shell's top navigation bar.
        services.AddTransient<IStartupFilter, DxpEnvIndicatorStartupFilter>();

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
