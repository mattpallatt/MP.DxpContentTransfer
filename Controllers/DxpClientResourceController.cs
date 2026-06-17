using System.Text.Json;
using DxpContentTransfer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace DxpContentTransfer.Controllers;

// Serves the small client-side scripts the shell needs (the admin settings bootstrap, and the
// top-bar environment badge). Each is referenced by an injecting middleware. Served from a
// controller (rather than a packaged static asset) so the class library is self-contained — there
// is no host-side ClientResources folder to deploy.
[AllowAnonymous]
public class DxpClientResourceController(IDxpSettingsService settingsService, IWebHostEnvironment hostEnvironment) : Controller
{
    [HttpGet]
    [Route("~/EPiServer/DxpContentTransfer/ClientResources/Scripts/AdminInit.js")]
    [ResponseCache(Duration = 300)]
    public IActionResult AdminInit() =>
        Content(AdminInitScript, "application/javascript; charset=utf-8");

    // Badges the running environment into the shell's top navigation bar, resolved server-side and
    // baked into the script (no client-side fetch, no response cache so colour/label/selector changes
    // take effect on the next load). The matched environment (DetectByHost — the same match the gadget
    // uses) supplies its configured colour and label; an unmatched host falls back to
    // ASPNETCORE_ENVIRONMENT so local dev still shows a badge. Production is badged unless opted out.
    [HttpGet]
    [Route("~/EPiServer/DxpContentTransfer/ClientResources/Scripts/EnvIndicator.js")]
    public IActionResult EnvIndicator()
    {
        var settings = settingsService.Get();
        var matched = settings.DetectByHost(Request.Host.Host ?? string.Empty);

        string name, label, color;
        if (matched != null)
        {
            if (string.Equals(matched.Name, "Production", StringComparison.OrdinalIgnoreCase) && !settings.ShowOnProduction)
                return Inert();
            name = matched.Name;
            color = string.IsNullOrWhiteSpace(matched.Color) ? EnvironmentBadge.DefaultColor(name) : matched.Color;
            label = EnvironmentBadge.EffectiveLabel(name, matched.Label);
        }
        else if (hostEnvironment.IsDevelopment())
        {
            name = "Development";
            color = EnvironmentBadge.DefaultColor(name);
            label = EnvironmentBadge.EffectiveLabel(name, null);
        }
        else
        {
            return Inert();
        }

        if (string.IsNullOrEmpty(color)) return Inert();

        var selector = string.IsNullOrWhiteSpace(settings.Selector) ? EnvironmentBadge.DefaultSelector : settings.Selector;
        var prelude = $"var __DXP_ENV={JsonSerializer.Serialize(name)};"
                    + $"var __DXP_LABEL={JsonSerializer.Serialize(label)};"
                    + $"var __DXP_COLOR={JsonSerializer.Serialize(color)};"
                    + $"var __DXP_TEXT={JsonSerializer.Serialize(EnvironmentBadge.TextColor(color))};"
                    + $"var __DXP_SELECTOR={JsonSerializer.Serialize(selector)};\n";
        return Content(prelude + EnvIndicatorScript, "application/javascript; charset=utf-8");
    }

    private ContentResult Inert() =>
        Content("/* DXP environment indicator: no badge for this environment */", "application/javascript; charset=utf-8");

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

    // Reads __DXP_ENV / __DXP_COLOR from the prelude prepended by EnvIndicator(). Finds the product
    // label cell ("CMS") in the top navigation's centre section and inserts a coloured environment
    // pill immediately after it, so the bar reads "CMS [ENV]". The badge is appended (not an
    // innerHTML rewrite) so it survives the SPA re-rendering the label, and is idempotent via the
    // .dxp-env-badge marker. The shell renders asynchronously (React), so it retries via a
    // MutationObserver until the cell exists, then disconnects — with a hard 15s deadline so pages
    // that never show the bar (e.g. login) don't observe forever.
    private const string EnvIndicatorScript = """
    (function () {
        var deadline = Date.now() + 15000;

        function apply() {
            var label = document.querySelector(__DXP_SELECTOR);
            if (!label) return false;
            var host = label.closest('.oui-dropdown-group') || label.closest('.oui-button') || label.parentElement;
            if (!host || !host.parentElement) return false;
            if (host.parentElement.querySelector('.dxp-env-badge')) return true;
            var badge = document.createElement('span');
            badge.className = 'dxp-env-badge';
            badge.setAttribute('data-dxp-env', __DXP_ENV);
            badge.textContent = __DXP_LABEL;
            badge.style.cssText = 'display:inline-flex;align-items:center;padding:1px 7px;background:' + __DXP_COLOR +
                ';color:' + __DXP_TEXT + ';font-size:11px;font-weight:700;border-radius:3px;letter-spacing:0.5px;' +
                'margin-left:8px;flex-shrink:0;white-space:nowrap;';
            host.insertAdjacentElement('afterend', badge);
            return true;
        }

        function start() {
            if (apply()) return;
            var obs = new MutationObserver(function () {
                if (apply() || Date.now() > deadline) obs.disconnect();
            });
            obs.observe(document.body, { childList: true, subtree: true });
            setTimeout(function () { obs.disconnect(); }, 15000);
        }

        if (document.readyState === 'loading')
            document.addEventListener('DOMContentLoaded', start);
        else
            start();
    })();
    """;
}
