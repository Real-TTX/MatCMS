# Cookie-Banner (`cookie-consent`)

Fragt Besucher, bevor Statistik, Marketing oder externe Medien geladen werden – und lädt sie erst danach.

**Braucht eine Seite das?** Nur wenn sie etwas einbindet, das eine Einwilligung braucht (Google Analytics,
Meta Pixel, YouTube-/Maps-Einbettungen …). MatCMS selbst setzt öffentlich nur technisch notwendige Cookies
(Anmeldung, Sprache, Formularschutz), die Besucherstatistik kommt ohne Cookies aus. Deshalb bleibt der Banner
**ausgeblendet, solange keine optionale Kategorie eingeschaltet ist** – das Plugin kann also aktiv sein, ohne
dass Besucher etwas sehen.

## Einrichten
Admin → **Cookie-Banner**: Kategorien einschalten (Statistik, Marketing, Externe Medien), optional pro
Kategorie den Code einfügen, der erst nach Zustimmung geladen wird (z. B. das Analytics-Snippet), Texte,
Position (Kasten / Leiste / Fenster), Farbschema, Akzentfarbe, Gültigkeit. „Vorschau auf der Website“ öffnet
die Seite mit `?cookie-vorschau`.

## Was blockiert wird
- Code aus dem Admin, pro Kategorie (Scripts werden nach der Zustimmung ausgeführt).
- `<script type="text/plain" data-consent="statistik">…</script>` irgendwo auf der Seite.
- `<iframe data-consent="medien" data-src="https://…"></iframe>` – bis dahin ein Platzhalter mit
  „Inhalt laden“ (nur dieses) und „Externe Medien immer erlauben“.
- Optional **Google Consent Mode v2**: Standard „abgelehnt“ vor allen Google-Tags, Entscheidung wird an gtag gemeldet.

## Für Templates und Plugins
`MatConsent.has('statistik')`, `MatConsent.open()`, `MatConsent.reset()`, Ereignis `matconsent`
(`detail.categories`). Ein Link mit `href="#cookie-einstellungen"` oder ein Element mit `data-cookie-settings`
öffnet die Einstellungen.

## Sprachen (seit 1.1)
Deutsch und Englisch. Die Admin-Seite folgt der Sprache des Admins. Die Banner-Texte sind Einstellungen: ein Text, der
noch dem mitgelieferten Standard entspricht (deutsch oder englisch, oder nie gespeichert), folgt der Sprache der Seite
(`<html lang>`, also `/en/…`); ein selbst geschriebener Text gilt in jeder Sprache so, wie er dasteht. Gespeicherte Werte
werden nie umgeschrieben. Der Banner wird beim Laden des Plugins in der Standardsprache der Seite gezeichnet und trägt
beide Texttabellen; sein Skript nimmt die der Seite (ohne Skript bleibt die Standardsprache).

## Speicherung
- Einstellungen: SiteSetting `plugin.cookie-consent.config` (wird wie alle Plugin-Daten von einem Restore ersetzt).
- Entscheidung des Besuchers: Cookie `mcc` = `<version>|<kategorien>|<zeit>`, Laufzeit einstellbar (Standard 12 Monate).
  „Alle Besucher erneut fragen“ erhöht die Version – nötig, wenn neue Dienste dazukommen.

## Rechtliches (Design-Entscheidungen)
„Alle akzeptieren“ und „Nur notwendige“ sind gleich groß und gleich gestaltet, nichts ist vorausgewählt,
die Auswahl lässt sich jederzeit über den Cookie-Button unten links (abschaltbar) ändern.
Die Datenschutzerklärung der Seite muss die eingesetzten Dienste trotzdem selbst beschreiben.
