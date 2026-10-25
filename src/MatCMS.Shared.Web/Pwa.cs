using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MatCMS.Shared.Web;

/// <summary>What one app's back office tells the browser about itself as an installable app.</summary>
/// <param name="Name">Full name (the site's or the cloud's).</param>
/// <param name="ShortName">Under the icon on a home screen (≤ 12 characters reads well).</param>
/// <param name="Icon">The icon set in wwwroot/pwa: "matcms" or "cloud".</param>
/// <param name="NavigationScope">Which navigations the service worker answers when offline: "/admin" + "/login" for the CMS
/// (its public site stays the visitor's, untouched), "/" for the cloud (all of it is back office).</param>
public sealed record PwaInfo(string Name, string ShortName, string Icon, string NavigationScope, string Lang = "de");

/// <summary>
/// The back offices of MatCMS and MatCMS.Cloud as installable apps (PWA): manifest, service worker, offline page — one implementation
/// for both, under <c>/pwa/</c> (the CMS's maintenance mode lets that prefix through).
/// <para><b>What works offline, and what deliberately does not.</b> Static files (CSS, scripts, fonts, icons) are cached: the versioned
/// ones (<c>?v=</c>) cache-first, the rest stale-while-revalidate. Pages of the back office are NEVER cached: they are signed in, hold
/// antiforgery tokens and personal data, and a page from yesterday would let somebody edit a state that no longer exists. A navigation
/// that fails shows the offline page instead (it reconnects by itself), and <c>pwa.js</c> keeps forms from being sent into the void.</para>
/// </summary>
public static class Pwa
{
    /// <summary>Changes with every build of this assembly, so a new image replaces the service worker and its static cache.</summary>
    private static readonly string Version =
        System.IO.File.GetLastWriteTimeUtc(typeof(Pwa).Assembly.Location).Ticks.ToString("x");

    /// <summary>
    /// The &lt;head&gt; lines of an installable back-office page: the manifest, iOS's own (home-screen title and icon, the status bar)
    /// and the colour of the browser's/OS's bar — the top bar's, so status bar and app are one surface. <paramref name="theme"/> is the
    /// user's scheme (light | dark | system): system gets both colours, chosen by the OS. iOS keeps its status bar "default" (dark text
    /// on the light bar); with "black-translucent" the page would run under it and the text turns white on a light bar.
    /// </summary>
    public static Microsoft.AspNetCore.Html.IHtmlContent Head(string? theme, string shortName, string icon)
    {
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        const string light = "#f5f4f1", dark = "#151514";   // --mat-glass / --mat-bg of MatUi, light and dark
        var color = theme switch
        {
            "dark" => $"<meta name=\"theme-color\" content=\"{dark}\">",
            "system" => $"<meta name=\"theme-color\" content=\"{light}\" media=\"(prefers-color-scheme: light)\">\n" +
                        $"    <meta name=\"theme-color\" content=\"{dark}\" media=\"(prefers-color-scheme: dark)\">",
            _ => $"<meta name=\"theme-color\" content=\"{light}\">",
        };
        var html =
            $"<link rel=\"manifest\" href=\"/pwa/manifest.webmanifest\">\n" +
            $"    {color}\n" +
            $"    <meta name=\"mobile-web-app-capable\" content=\"yes\">\n" +
            $"    <meta name=\"apple-mobile-web-app-capable\" content=\"yes\">\n" +
            $"    <meta name=\"apple-mobile-web-app-status-bar-style\" content=\"default\">\n" +
            $"    <meta name=\"apple-mobile-web-app-title\" content=\"{E(shortName)}\">\n" +
            $"    <meta name=\"format-detection\" content=\"telephone=no\">\n" +
            $"    <link rel=\"apple-touch-icon\" href=\"/_content/MatCMS.Shared.Web/pwa/{E(icon)}-apple-180.png\">\n" +
            $"    <script src=\"/_content/MatCMS.Shared.Web/js/pwa.js?v={Version}\" defer></script>";
        return new Microsoft.AspNetCore.Html.HtmlString(html);
    }

    /// <summary>A home-screen label: the name if it is short, else its first word (cut to 12 characters).</summary>
    public static string ShortName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length <= 12) return name.Length > 0 ? name : "MatCMS";
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Length <= 12 ? first : first[..12];
    }

    public static IEndpointRouteBuilder MapPwa(this IEndpointRouteBuilder app, Func<HttpContext, PwaInfo> info)
    {
        app.MapGet("/pwa/manifest.webmanifest", (HttpContext ctx, CancellationToken _) =>
        {
            var i = info(ctx);
            var icons = new object[]
            {
                new { src = $"/_content/MatCMS.Shared.Web/pwa/{i.Icon}-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = $"/_content/MatCMS.Shared.Web/pwa/{i.Icon}-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = $"/_content/MatCMS.Shared.Web/pwa/{i.Icon}-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
            };
            var manifest = new Dictionary<string, object?>
            {
                ["id"] = "/admin",
                ["name"] = i.Name,
                ["short_name"] = i.ShortName,
                ["lang"] = i.Lang,
                ["start_url"] = "/admin",
                ["scope"] = "/",
                ["display"] = "standalone",
                // The colours of the top bar (MatUi compact): the status bar and the splash screen match the app.
                ["background_color"] = "#f5f4f1",
                ["theme_color"] = "#f5f4f1",
                ["icons"] = icons,
            };
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(JsonSerializer.Serialize(manifest), "application/manifest+json; charset=utf-8");
        });

        app.MapGet("/pwa/sw.js", (HttpContext ctx, CancellationToken _) =>
        {
            var i = info(ctx);
            // Registered with scope "/" (so /login is inside the app too): a worker at /pwa/ may only claim that if the server allows it.
            ctx.Response.Headers["Service-Worker-Allowed"] = "/";
            ctx.Response.Headers.CacheControl = "no-cache";
            var js = ServiceWorker.Replace("__VERSION__", Version).Replace("__NAV__", JsonSerializer.Serialize(i.NavigationScope));
            return Results.Text(js, "text/javascript; charset=utf-8");
        });

        app.MapGet("/pwa/offline", (HttpContext ctx, CancellationToken _) =>
        {
            var i = info(ctx);
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Text(OfflinePage.Replace("__NAME__", System.Net.WebUtility.HtmlEncode(i.Name)), "text/html; charset=utf-8");
        });
        return app;
    }

    // The service worker. Plain JS on purpose (no build step in this repo).
    private const string ServiceWorker = """
        // MatCMS back office — service worker __VERSION__ (generated by MatCMS.Shared.Web/Pwa.cs)
        const STATIC = 'matcms-static-__VERSION__';
        const OFFLINE = '/pwa/offline';
        const NAV = __NAV__;   // which navigations are the back office's: "/" (cloud) or "/admin" (+ /login) for a site

        self.addEventListener('install', (e) => {
          e.waitUntil(caches.open(STATIC).then((c) => c.add(new Request(OFFLINE, { cache: 'reload' }))).then(() => self.skipWaiting()));
        });
        self.addEventListener('activate', (e) => {
          e.waitUntil(caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k.startsWith('matcms-static-') && k !== STATIC).map((k) => caches.delete(k))))
            .then(() => self.clients.claim()));
        });

        const ownNavigation = (path) => NAV === '/' ||
          path === '/admin' || path.startsWith('/admin/') || path === '/login' || path.startsWith('/login/') || path.startsWith('/pwa/');
        // Static files only: never a page, never the media library (/uploads) or anything an API answers.
        const staticFile = (path) => path.startsWith('/_content/') || path.startsWith('/lib/') || path.startsWith('/css/') ||
          path.startsWith('/js/') || path.startsWith('/img/') || path.startsWith('/fonts/');

        self.addEventListener('fetch', (e) => {
          const req = e.request;
          if (req.method !== 'GET') return;
          const url = new URL(req.url);
          if (url.origin !== self.location.origin) return;

          if (req.mode === 'navigate') {
            // A page of the back office: always from the network (never a stale copy); without a network, the offline page.
            // Anything else (the CMS's public site) is not ours to touch.
            if (!ownNavigation(url.pathname)) return;
            e.respondWith(fetch(req).catch(() => caches.match(OFFLINE)));
            return;
          }
          if (!staticFile(url.pathname)) return;
          e.respondWith(caches.open(STATIC).then(async (cache) => {
            const hit = await cache.match(req);
            // Versioned files never change under their address: the cached copy is the file.
            if (hit && url.searchParams.has('v')) return hit;
            const fresh = fetch(req).then((res) => { if (res.ok && res.type === 'basic') cache.put(req, res.clone()); return res; });
            if (hit) { e.waitUntil(fresh.catch(() => {})); return hit; }   // stale-while-revalidate
            return fresh;
          }));
        });
        """;

    // The page shown for a back-office navigation without a network. Self-contained (it is served from the cache, with nothing
    // else reachable): MatUi's sign-in card in its light and dark colours, German or English by the browser's language, and it
    // reconnects by itself — on the "online" event and every 5 s — back to the address that failed (the page keeps it).
    private const string OfflinePage = """
        <!DOCTYPE html>
        <html lang="de">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="theme-color" content="#f5f4f1" media="(prefers-color-scheme: light)">
        <meta name="theme-color" content="#151514" media="(prefers-color-scheme: dark)">
        <title>Offline – __NAME__</title>
        <style>
          :root { color-scheme: light dark; --bg: #f5f4f1; --surface: #fff; --line: rgba(31,30,27,.13); --text: #1f1e1b; --muted: #5d5b55; --accent: #2563eb; }
          @media (prefers-color-scheme: dark) { :root { --bg: #151514; --surface: #1e1e1c; --line: rgba(255,255,255,.10); --text: #ecebe6; --muted: #a8a69e; --accent: #7aa2ff; } }
          * { box-sizing: border-box; }
          html, body { margin: 0; min-height: 100%; background: var(--bg); color: var(--text);
            font: 15px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; }
          body { display: grid; place-items: center; min-height: 100dvh; padding: calc(24px + env(safe-area-inset-top)) 20px calc(24px + env(safe-area-inset-bottom)); }
          .card { width: min(400px, 100%); background: var(--surface); border: 1px solid var(--line); border-radius: 12px; padding: 32px 28px 24px; }
          .mark { width: 44px; height: 44px; border-radius: 22%; background: var(--accent); display: grid; place-items: center; margin-bottom: 20px; }
          h1 { font-size: 20px; margin: 0 0 8px; font-weight: 650; }
          p { margin: 0 0 20px; color: var(--muted); }
          .row { display: flex; align-items: center; gap: 12px; }
          button { min-height: 44px; padding: 0 18px; border: 0; border-radius: 6px; background: var(--accent); color: #fff; font: inherit; font-weight: 600; cursor: pointer; touch-action: manipulation; }
          .state { font-size: 13px; color: var(--muted); }
        </style>
        </head>
        <body>
          <main class="card" role="main">
            <div class="mark" aria-hidden="true"><svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="2" y1="2" x2="22" y2="22"/><path d="M8.5 16.5a5 5 0 0 1 7 0"/><path d="M2 8.82a15 15 0 0 1 4.17-2.65"/><path d="M10.66 5c4.01-.36 8.14.9 11.34 3.76"/><path d="M16.85 11.25a10 10 0 0 1 2.22 1.68"/><path d="M5 13a10 10 0 0 1 5.24-2.76"/><line x1="12" y1="20" x2="12.01" y2="20"/></svg></div>
            <h1 data-de="Keine Verbindung" data-en="No connection">Keine Verbindung</h1>
            <p data-de="__NAME__ ist gerade nicht erreichbar. Sobald die Verbindung zurück ist, geht es hier von selbst weiter – nichts geht verloren." data-en="__NAME__ cannot be reached right now. As soon as the connection is back, this page continues by itself – nothing is lost.">__NAME__ ist gerade nicht erreichbar. Sobald die Verbindung zurück ist, geht es hier von selbst weiter – nichts geht verloren.</p>
            <div class="row">
              <button type="button" id="retry" data-de="Erneut versuchen" data-en="Try again">Erneut versuchen</button>
              <span class="state" id="state" aria-live="polite"></span>
            </div>
          </main>
          <script>
            (function () {
              var en = !/^de\b/i.test(navigator.language || 'de');
              if (en) { document.documentElement.lang = 'en'; document.querySelectorAll('[data-en]').forEach(function (el) { el.textContent = el.getAttribute('data-en'); }); }
              var state = document.getElementById('state'), busy = false;
              function check(manual) {
                if (busy) return; busy = true;
                if (manual) state.textContent = en ? 'Checking…' : 'Prüfe Verbindung …';
                fetch(location.href, { method: 'HEAD', cache: 'no-store', credentials: 'same-origin' })
                  .then(function (r) { if (r.ok || r.status < 500) location.reload(); else throw 0; })
                  .catch(function () { state.textContent = en ? 'Still offline – trying again shortly.' : 'Noch offline – neuer Versuch gleich.'; })
                  .then(function () { busy = false; });
              }
              document.getElementById('retry').addEventListener('click', function () { check(true); });
              window.addEventListener('online', function () { check(false); });
              setInterval(function () { if (navigator.onLine !== false) check(false); }, 5000);
            })();
          </script>
        </body>
        </html>
        """;
}
