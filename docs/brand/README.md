# MatCMS – Logo „Blöcke“

The mark is the block editor as a picture: a header bar over a wide column and two small blocks, on the accent tile.
Overview of every variant: `uebersicht.png`.

| File | Use |
| --- | --- |
| `svg/matcms-logo.svg` / `-dark.svg` | the logo on light / dark ground (admin, documents) |
| `svg/matcms-logo-site.svg` / `-site-dark.svg` | the same with air above and below — for website headers that give the logo a FIXED height (MatCMS templates: `.logo img { height: 56px }`), so the mark stands ~40 px like the old logo (matcms.de) |
| `svg/matcms-logo-white.svg` | monochrome white, on photos and the accent colour |
| `svg/matcms-mark.svg` | the mark alone: favicon, app icon, avatar (the word is unreadable below ~24 px) |
| `svg/matcms-cloud-logo.svg` / `-dark.svg` | MatCMS Cloud (two lines); its tab icon is `matcms-mark.svg` like the CMS |
| `svg/matcms-cloud-mark.svg` | the cloud glyph — only the cloud's installed-app icon, so it differs from a site's admin on a home screen |
| `png/` | the same rendered transparent (logos 192 px high, site logos 128 px, marks 512/192/32) — for places that take no SVG |
| `vorschlaege/` | the six proposals the choice was made from (`uebersicht.png` shows them side by side); „Blöcke“ won |

Colours: accent `#2563eb`, ink `#0f172a`; on dark ground accent `#7aa2ff`, ink `#ecebe6`. Lettering: weight 800 in the system's
display face (Segoe UI Variable Display, SF Pro Display, …) — the SVG names a font stack and pins the word to its width with
`textLength`, so whatever font renders it never runs past the box. The PNGs were rendered on Windows (Segoe UI Variable).

**Where the files live in the apps** (these are copies — change the app's file and copy it here): CMS `src/MatCMS/wwwroot/img/`
(`logo.svg`, `logo-dark.svg`, `favicon.svg`), cloud `src/MatCMS.Cloud/wwwroot/img/`, app icons `src/MatCMS.Shared.Web/wwwroot/pwa/`.
