# Leserstimmen (MatCMS-Plugin)

Rezensionen und Kundenstimmen für Autorenseiten, Dienstleister und alles andere, was Stimmen sammelt –
mit Sternen, Herzen, Punkten, Flammen oder ganz ohne Wertung. Liegt im **Global Store** der Cloud (`leserstimmen`).
Entstanden aus dem Plugin „Bewertungen“ (Wolf Scheiber, Seite „Leserstimmen“), komplett überarbeitet.

- `plugin.csx`: der Code (Quelle der Wahrheit). `meta.json`: Name/Version/Beschreibung.
- Bundle: `../pack.ps1 -Key leserstimmen`, Upload siehe `../README.md`. **Version in `meta.json` erhöhen.**
- N.S. Libera bindet es über `nslibera/build.js` ein (liest `plugin.csx` + `meta.json`).

## Warum ein eigener Schlüssel und nicht „bewertungen“
Auf vielen Instanzen existiert „bewertungen“ schon (früher von MatCMS selbst angelegt, mit anderem Code
und anderem Block). Unter einem eigenen Key kommen sich beide nicht in die Quere.
Gespeichert wird getrennt: `SiteSettings` `plugin.leserstimmen.<sammlung>` (JSON-Array).

## Was es kann
- Block „Leserstimmen“ mit Feldern: Überschrift, Sammlung, **Darstellung**, **Farben**, Wertung mit
  (Sterne/Herzen/Punkte/Flammen/ohne), Anrede (Sie/du), Spalten, Durchschnitt, Formular an/aus, Link-Feld,
  E-Mail-Feld (nie öffentlich), Formular-Überschrift, Hinweis, Button-Text.
- **Darstellungen** (seit 1.3): Karten (klassisch, unverändert – Seiten mit eigenen `--mls-*`-Anpassungen
  sehen nach dem Update gleich aus), Karten mit Zitatzeichen und Initialen, großes Zitat (Karussell mit
  Punkten/Pfeilen, wischbar, wechselt alle 7 s, pausiert unter der Maus), schlichte Liste, Laufband.
  **Farben**: wie das Template, hell, dunkel, Akzentfarbe – sie gelten nur für Karten, Formular und Knöpfe;
  Überschrift und Durchschnitt bleiben in den Template-Farben.
- **Ohne Wertung**: Das Formular fragt keine Sterne ab, die Stimme wird mit `rating: 0` gespeichert und
  zählt nie in einen Durchschnitt.
- Spamschutz ohne Dritte: Honeypot + signierte Renderzeit (mind. 3 s, max. 7 Tage). Spam wird still
  verworfen. Dazu das Rate-Limit von `/plugin/*` (20/min pro IP) und max. 1000 Stimmen pro Sammlung.
- Admin „Leserstimmen“: freigeben, verbergen, löschen, **von Hand hinzufügen** (z. B. von Instagram),
  **„Aus Bewertungen übernehmen“** (erscheint nur, wenn noch alte Stimmen da sind; doppelte werden übersprungen,
  die alten Daten bleiben unangetastet).
- **Benachrichtigung** (Admin → Leserstimmen → Benachrichtigung, seit 1.3): an/aus, bei jeder Stimme oder
  nur wenn eine Freigabe nötig ist, Empfänger aus den Benutzern (mit E-Mail) plus weitere Adressen.
  HTML-Mail mit Wertung, Text und Knopf „Jetzt prüfen und freigeben“. „Test-Mail senden“ zeigt das
  Ergebnis direkt in der Karte. Gespeichert unter `plugin.leserstimmen-notify` – bewusst NICHT unter dem
  Präfix der Sammlungen. Solange dort nichts gespeichert ist, gilt das alte Verhalten (`notifyEmail`,
  sonst Kontakt-Empfänger der Seite).
- **Zähler im Menü**: „Leserstimmen (2)“ = Stimmen, die auf Freigabe warten; aktualisiert sich nach jeder
  Abgabe und Moderation, ohne die Plugins neu zu starten.
- Konfiguration: `autoPublish=true` (ohne Freigabe).
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
