using Microsoft.AspNetCore.Http;

namespace DxpContentTransfer.Middleware;

// Injects the environment-indicator script into the Optimizely shell pages (edit UI, admin,
// dashboard — anything served under /EPiServer as an HTML document) so the matched DXP environment
// is badged into the top navigation bar. Gated on Accept: text/html so the shell's many XHR/JSON
// and static-resource requests under /EPiServer are not buffered needlessly. EnvIndicator.js itself
// (served by DxpClientResourceController) decides whether to render anything for this environment.
public class DxpEnvIndicatorMiddleware(RequestDelegate next)
{
    private const string ShellPath = "/EPiServer";
    private const string ScriptTag = "<script src=\"/EPiServer/DxpContentTransfer/ClientResources/Scripts/EnvIndicator.js\"></script>";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsShellPageRequest(context))
        {
            await next(context);
            return;
        }

        await HtmlBodyInjector.InjectBeforeBodyCloseAsync(context, next, ScriptTag);
    }

    private static bool IsShellPageRequest(HttpContext context)
    {
        return HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments(ShellPath, StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }
}
