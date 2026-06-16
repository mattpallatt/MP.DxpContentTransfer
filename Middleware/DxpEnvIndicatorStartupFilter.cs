using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace DxpContentTransfer.Middleware;

// Registers DxpEnvIndicatorMiddleware into the request pipeline automatically, so a consuming host
// only needs to call AddDxpContentTransfer() — no manual app.UseMiddleware<>() wiring required.
// The middleware is idempotent, so this is harmless even if a host also registers it explicitly.
public class DxpEnvIndicatorStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseMiddleware<DxpEnvIndicatorMiddleware>();
            next(app);
        };
}
