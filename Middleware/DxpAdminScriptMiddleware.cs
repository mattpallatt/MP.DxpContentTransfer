using Microsoft.AspNetCore.Http;

namespace DxpContentTransfer.Middleware;

// Injects the admin settings bootstrap script into every admin page (so the tools-menu route can
// render the settings iframe). The buffer-and-splice logic lives in HtmlBodyInjector.
public class DxpAdminScriptMiddleware(RequestDelegate next)
{
    private const string AdminPath = "/EPiServer/EPiServer.Cms.UI.Admin";
    private const string ScriptTag = "<script src=\"/EPiServer/DxpContentTransfer/ClientResources/Scripts/AdminInit.js\"></script>";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAdminPageRequest(context))
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
