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
    // watches the hash and, when it matches, positions an iframe of the standalone settings page
    // (DxpSettingsController) directly over the admin content pane — leaving the tools menu on the
    // left visible — and hides it again when the user navigates anywhere else.
    private const string AdminInitScript = """
    (function () {
        var ROUTE = '#/DxpTransfer/Settings';
        var FRAME_ID = 'dxp-settings-frame';
        var SETTINGS_URL = '/EPiServer/DxpContentTransfer/Admin/Settings';
        // The admin side-bar navigation. It is present on every admin route (including ours),
        // whereas the content pane (.content-area-container) is only rendered for routes the SPA
        // recognises — not ours. So we anchor the iframe to the right edge of the side-bar, which
        // lands it exactly where the content pane sits while keeping the left menu visible.
        var NAV_SELECTOR = '.epi-side-bar-navigation';
        var CONTENT_SELECTOR = '.content-area-container';
        var trackTimer = null;

        function topOffset() {
            var nav = document.querySelector(
                '.epi-navigation, #epi-shellHeader, [class*="shellHeader"], [class*="GlobalNavigation"], header[role="banner"]');
            var bottom = nav ? Math.round(nav.getBoundingClientRect().bottom) : 0;
            return bottom > 0 ? bottom : 48;
        }

        function rectOf(selector, minW, minH) {
            var el = document.querySelector(selector);
            if (el) {
                var r = el.getBoundingClientRect();
                if (r.width > minW && r.height > minH) return r;
            }
            return null;
        }

        // Place the iframe in the content region using viewport coordinates (position:fixed).
        // Preference order: beside the side-bar nav (works on our unrecognised route) → over the
        // content pane if the SPA rendered it → full area below the platform nav bar.
        function applyGeometry(frame) {
            var nav = rectOf(NAV_SELECTOR, 100, 100);
            if (nav) {
                frame.style.top = nav.top + 'px';
                frame.style.left = nav.right + 'px';
                frame.style.width = Math.max(0, window.innerWidth - nav.right) + 'px';
                frame.style.height = nav.height + 'px';
                return;
            }
            var content = rectOf(CONTENT_SELECTOR, 100, 100);
            if (content) {
                frame.style.top = content.top + 'px';
                frame.style.left = content.left + 'px';
                frame.style.width = content.width + 'px';
                frame.style.height = content.height + 'px';
                return;
            }
            var t = topOffset();
            frame.style.top = t + 'px';
            frame.style.left = '0';
            frame.style.width = '100vw';
            frame.style.height = 'calc(100vh - ' + t + 'px)';
        }

        function showFrame() {
            var frame = document.getElementById(FRAME_ID);
            if (!frame) {
                frame = document.createElement('iframe');
                frame.id = FRAME_ID;
                frame.src = SETTINGS_URL;
                frame.title = 'DXP Content Transfer Settings';
                frame.style.cssText = 'position:fixed;border:0;z-index:2147483000;background:#fff;';
                document.body.appendChild(frame);
            }
            applyGeometry(frame);
            frame.style.display = 'block';
            // Keep the iframe aligned as the SPA finishes laying out, the menu collapses, etc.
            if (!trackTimer) trackTimer = setInterval(function () {
                var f = document.getElementById(FRAME_ID);
                if (f && f.style.display !== 'none') applyGeometry(f);
            }, 300);
        }

        function hideFrame() {
            var frame = document.getElementById(FRAME_ID);
            if (frame) frame.style.display = 'none';
            if (trackTimer) { clearInterval(trackTimer); trackTimer = null; }
        }

        function sync() {
            if ((location.hash || '').indexOf(ROUTE) === 0) showFrame();
            else hideFrame();
        }

        window.addEventListener('hashchange', sync);
        window.addEventListener('resize', function () {
            var f = document.getElementById(FRAME_ID);
            if (f && f.style.display !== 'none') applyGeometry(f);
        });

        if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', sync);
        else
            sync();

        // The admin SPA paints asynchronously; re-sync a few times so a deep-link straight to
        // the route still renders once the shell has finished loading.
        [250, 750, 1500].forEach(function (ms) { setTimeout(sync, ms); });
    })();
    """;
}
