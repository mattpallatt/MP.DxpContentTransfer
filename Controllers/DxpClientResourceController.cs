using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DxpContentTransfer.Controllers;

// Serves the small client-side script that the admin SPA needs to render the settings page.
// The script is referenced by DxpAdminScriptMiddleware, which injects a <script> tag for it
// into every admin page. Served from a controller (rather than a packaged static asset) so the
// class library is self-contained — there is no host-side ClientResources folder to deploy.
[AllowAnonymous]
public class DxpClientResourceController : Controller
{
    [HttpGet]
    [Route("~/EPiServer/DxpContentTransfer/ClientResources/Scripts/AdminInit.js")]
    [ResponseCache(Duration = 300)]
    public IActionResult AdminInit() =>
        Content(AdminInitScript, "application/javascript; charset=utf-8");

    // The admin tools menu item points at the SPA hash route "#/DxpTransfer/Settings". The admin
    // SPA has no handler for that route, so on its own it shows a blank content area. This script
    // watches the hash and, when it matches, drops a full-area iframe over the content region
    // pointing at the standalone settings page (DxpSettingsController). It hides the iframe again
    // when the user navigates anywhere else.
    private const string AdminInitScript = """
    (function () {
        var ROUTE = '#/DxpTransfer/Settings';
        var FRAME_ID = 'dxp-settings-frame';
        var SETTINGS_URL = '/EPiServer/DxpContentTransfer/Admin/Settings';

        // Height of the platform navigation bar, so the iframe sits below it rather than
        // covering the only chrome the user can use to navigate away. Falls back to 40px.
        function topOffset() {
            var nav = document.querySelector(
                '.epi-navigation, #epi-shellHeader, [class*="shellHeader"], header[role="banner"]');
            return nav ? Math.max(0, Math.round(nav.getBoundingClientRect().bottom)) : 40;
        }

        function showFrame() {
            var frame = document.getElementById(FRAME_ID);
            if (!frame) {
                frame = document.createElement('iframe');
                frame.id = FRAME_ID;
                frame.src = SETTINGS_URL;
                frame.title = 'DXP Content Transfer Settings';
                frame.style.cssText =
                    'position:fixed;left:0;right:0;bottom:0;width:100%;border:0;z-index:9999;background:#fff;';
                document.body.appendChild(frame);
            }
            frame.style.top = topOffset() + 'px';
            frame.style.display = 'block';
        }

        function hideFrame() {
            var frame = document.getElementById(FRAME_ID);
            if (frame) frame.style.display = 'none';
        }

        function sync() {
            if ((location.hash || '').indexOf(ROUTE) === 0) showFrame();
            else hideFrame();
        }

        window.addEventListener('hashchange', sync);
        window.addEventListener('resize', function () {
            var f = document.getElementById(FRAME_ID);
            if (f && f.style.display !== 'none') f.style.top = topOffset() + 'px';
        });

        if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', sync);
        else
            sync();

        // The admin SPA paints asynchronously; re-sync a few times so a deep-link straight to
        // the route still renders once the shell (and its header) have finished loading.
        [250, 750, 1500].forEach(function (ms) { setTimeout(sync, ms); });
    })();
    """;
}
