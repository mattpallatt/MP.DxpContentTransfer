using DxpContentTransfer.Menu;
using DxpContentTransfer.Middleware;
using DxpContentTransfer.Services;
using EPiServer.Shell.Modules;
using EPiServer.Shell.Navigation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DxpContentTransfer.Extensions;

public static class ServiceCollectionExtensions
{
    private static readonly bool _isCms13 =
        typeof(EPiServer.Core.ContentReference).Assembly.GetName().Version?.Major >= 13;

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

        // CMS 12 only: module registration is needed for [MenuProvider] attribute scanning.
        // In CMS 13 it creates an unwanted Add-ons sidebar entry; IMenuProvider DI registration
        // (below) is sufficient for menu discovery in CMS 13.
        if (!_isCms13)
        {
            services.Configure<ProtectedModuleOptions>(opts =>
            {
                if (!opts.Items.Any(x => string.Equals(x.Name, "DxpContentTransfer", StringComparison.OrdinalIgnoreCase)))
                {
                    opts.Items.Add(new ModuleDetails { Name = "DxpContentTransfer" });
                }
            });
        }

        // CMS 12: [MenuProvider] attribute is the discovery signal; DI is the instantiation path.
        // CMS 13: DI registration alone is sufficient; attribute scanning is not used.
        services.AddTransient<IMenuProvider, DxpTransferMenuProvider>();

        return services;
    }
}
