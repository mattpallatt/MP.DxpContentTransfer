using Microsoft.AspNetCore.Http;

namespace DxpContentTransfer.Middleware;

// Injects the admin settings bootstrap script into CMS 12 admin pages (so the tools-menu route
// can render the settings iframe). Not used for CMS 13 — the Settings menu item registered via
// IMenuProvider navigates directly to the settings page instead.
// The buffer-and-splice logic lives in HtmlBodyInjector.
public class DxpAdminScriptMiddleware(RequestDelegate next)
{
    private static readonly bool _isCms13 =
        typeof(EPiServer.Core.ContentReference).Assembly.GetName().Version?.Major >= 13;

    private const string AdminPath = "/EPiServer/EPiServer.Cms.UI.Admin";
    private const string ScriptTag = "<script src=\"/EPiServer/DxpContentTransfer/ClientResources/Scripts/AdminInit.js\"></script>";

    public async Task InvokeAsync(HttpContext context)
    {
        if (_isCms13 || context.WebSockets.IsWebSocketRequest || !IsAdminPageRequest(context))
        {
            await next(context);
            return;
        }

        await HtmlBodyInjector.InjectBeforeBodyCloseAsync(context, next, ScriptTag);
    }

    private static bool IsAdminPageRequest(HttpContext context)
    {
        return HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments(AdminPath, StringComparison.OrdinalIgnoreCase);
    }
}
