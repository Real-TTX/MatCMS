# Leserstimmen (MatCMS-Plugin)

Rezensionen mit Sterne-, Flammen- oder Herz-Bewertung für Autorenseiten und alles andere, was
Stimmen sammelt. Liegt im **Global Store** der Cloud (`leserstimmen`).
Entstanden aus dem Plugin „Bewertungen“ (Wolf Scheiber, Seite „Leserstimmen“), komplett überarbeitet.

- `plugin.csx`: der Code (Quelle der Wahrheit). `meta.json`: Name/Version/Beschreibung.
- Bundle: `../pack.ps1 -Key leserstimmen`, Upload siehe `../README.md`. **Version in `meta.json` erhöhen.**
- N.S. Libera bindet es über `nslibera/build.js` ein (liest `plugin.csx` + `meta.json`).

## Warum ein eigener Schlüssel und nicht „bewertungen“
Auf vielen Instanzen existiert „bewertungen“ schon (früher von MatCMS selbst angelegt, mit anderem Code
und anderem Block). Unter einem eigenen Key kommen sich beide nicht in die Quere.
Gespeichert wird getrennt: `SiteSettings` `plugin.leserstimmen.<sammlung>` (JSON-Array).

## Was es kann
- Block „Leserstimmen“ mit Feldern: Überschrift, Sammlung, Symbol (Sterne/Flammen/Herzen), Anrede
  (Sie/du), Spalten, Durchschnitt, Formular an/aus, Link-Feld, E-Mail-Feld (nie öffentlich),
  Formular-Überschrift, Hinweis, Button-Text.
- Spamschutz ohne Dritte: Honeypot + signierte Renderzeit (mind. 3 s, max. 7 Tage). Spam wird still
  verworfen. Dazu das Rate-Limit von `/plugin/*` (20/min pro IP) und max. 1000 Stimmen pro Sammlung.
- Admin „Leserstimmen“: freigeben, verbergen, löschen, **von Hand hinzufügen** (z. B. von Instagram),
  **„Aus Bewertungen übernehmen“** (erscheint nur, wenn noch alte Stimmen da sind; doppelte werden übersprungen,
  die alten Daten bleiben unangetastet).
- Konfiguration: `autoPublish=true` (ohne Freigabe), `notifyEmail=…` (sonst Kontakt-Empfänger der
  Seite; ohne beides keine Mail).
- Design: alle Farben aus den Template-Variablen (`--accent`, `--ink`, `--bg` …). Ein Template passt es
  über `--mls-*` an, **mit `body`-Präfix** (`body .mls{…}`), weil das Plugin-CSS im `<head>` später kommt.

## Fallen
- **Ein Restore ersetzt ALLE Einstellungen** und damit auch die Stimmen. Vor einem Live-Restore das
  frische Live-Backup als `live-content.json` in den Site-Ordner legen, `build.js` übernimmt die Stimmen.
- Neu per Restore/Store installiert = **deaktiviert**, einmal im Admin einschalten.
- Ein Restore mit neuerem Code startet das Plugin **nicht** neu: bis zum Aus-/Einschalten (oder
  Neustart) läuft der alte Code weiter.
- MatCMS läuft mit `InvariantGlobalization`: Zahlen/Monate werden im Code von Hand deutsch formatiert.
- `/plugin/{key}` lehnt einen `__return` mit `#anker` ab (Rücksprung auf „/“), deshalb scrollt ein
  kleines Skript zur Danke-Meldung.
