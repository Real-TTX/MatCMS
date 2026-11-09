# Buchungs-Plugin — Entwurf

Stand: 09.11.2026 — **Phase B1 gebaut** (`plugins/booking` 0.1: Objekte, Einheiten, Saisons, Preise, Gebühren/Kurtaxe, Sperren, Preis-Vorschau; Tests `BookingTests`), B2–B6 offen. Ein allgemeines Plugin für Ferienhäuser, Apartments und Villen („eine Villa mit drei
Apartments“): Belegungskalender, Buchungsanfragen, Saisonpreise, Gäste, Abgleich mit Booking.com/Airbnb/Rentlio, Check-in und Übergabe.
Erste Seiten: **Villa Nika** und **Villa Sogi** (gemeinsame Gäste). Ergänzt `docs/plugin-system-v2.md`; wie beim Shop gilt: was das
Plugin braucht und die API nicht kann, kommt **in die API**, nicht am Plugin vorbei.

## 1. Vorgaben (Matthias, 09.11.2026)

| Frage | Entscheidung |
| --- | --- |
| Art | **Natives Store-Plugin** (`plugins/booking`, Key `booking`), wie Verein und Forum — nicht eingebaut |
| Abgleich mit Portalen | **iCal** in beide Richtungen (Booking.com, Airbnb, Vrbo, Rentlio können es alle); keine Partner-API im ersten Schritt |
| Buchungsweg | **Anfrage → Gastgeber bestätigt**; Zahlung per Überweisung, der Gastgeber trägt den Eingang ein |
| On-/Offboarding | **Online-Check-in** (Meldedaten), **Ankunfts-/Abreise-Mails**, **Wechsel-/Reinigungsliste** |
| Allgemein | Kein Plugin für zwei Villen: alles, was Villa Nika/Sogi brauchen, ist eine Einstellung oder Funktion, die jede Seite hat |
| Gemeinsame Gäste | **Kundenverbund** als Funktion des Plugins (§8): eine Seite führt die Kartei, andere hängen sich per API-Schlüssel an |

## 2. Begriffe

- **Objekt** — das Haus, die Villa (Name, Adresse, Lage, Check-in-/Check-out-Zeiten, Hausregeln, Anfahrt). Eine Seite hat meist eins, kann mehrere haben.
- **Einheit** — was gebucht wird: ein Apartment, ein Zimmer oder das ganze Haus. Gehört zu einem Objekt; Name, Bilder, Text, Betten, max. Gäste,
  Ausstattung (Schlagwörter: Pool, Meerblick, Haustiere …).
  „Ganzes Haus“ ist eine Einheit, die die anderen **einschließt** (`contains`): ist eine davon belegt, ist das Haus belegt und umgekehrt.
- **Saison** — ein benannter Zeitraum mit Rang: Nebensaison, Hauptsaison, Weihnachten, Silvester, Ostern. Jahr für Jahr neu (Kopieren ins nächste
  Jahr ist ein Knopf). Überlappen zwei, gewinnt der höhere Rang (Silvester liegt in der Nebensaison und gewinnt).
- **Buchung** — ein Aufenthalt einer Einheit von Anreise bis Abreise (Nächte = die Tage dazwischen), mit Gast, Personen, Preis und Status.
- **Sperre** — belegt, ohne Buchung hier: Eigennutzung, Renovierung, oder eine Belegung, die aus einem Portal-Kalender kam.
- **Gast** — die Person, die bucht (Stammdaten). **Mitreisende** — alle Personen des Aufenthalts mit Meldedaten.

## 3. Daten (Schicht 1, Tabellen `p_booking_…`)

| Tabelle | Inhalt |
| --- | --- |
| `properties` | Objekt: Name, Slug, Adresse, Geo, Check-in/-out-Zeit, Hausregeln, Anfahrt, Kontakt, Kurtaxe-Regel, Sprache der Mails |
| `units` | Einheit: Objekt, Name, Slug, Text, Bilder, Betten, Gäste (Standard/max.), `contains` (umfasst Einheiten), Puffertage, Reihenfolge, aktiv |
| `seasons` | Name, von, bis, Rang, Farbe; gilt für alle Einheiten eines Objekts |
| `rates` | Einheit × Saison: Preis je Nacht, Mindestnächte, erlaubte Anreisetage (z. B. nur Sa), Wochenpreis (optional), Preis je zusätzlicher Person ab N |
| `fees` | Endreinigung, Bettwäsche, Haustier, Kinderbett …: je Aufenthalt / je Nacht / je Person / je Person und Nacht; Pflicht oder wählbar |
| `bookings` | Einheit, Gast, Anreise, Abreise, Erwachsene/Kinder/Haustiere, Status, Quelle, Preis-Schnappschuss (JSON der Rechnung), Anzahlung/Rest + Fälligkeiten, bezahlt, geheimer Schlüssel, Sprache, Notizen, Verlauf |
| `nights` | eine Zeile **je belegter Nacht und Einheit** (Buchung oder Sperre) — die Verfügbarkeit; eindeutig (`unit`, `night`) |
| `blocks` | Sperren: Einheit, von, bis, Grund, Quelle (manuell / Feed-Id), UID aus dem Feed |
| `feeds` | iCal-Eingang je Einheit: Name (Airbnb …), https-Adresse, letzter Abruf, Ergebnis, Fehler |
| `guests` | Name, E-Mail (eindeutig, klein), Telefon, Adresse, Land, Sprache, Notizen, Stammgast-Merker, Verbund-Id |
| `travellers` | Mitreisende einer Buchung: Name, Geburtsdatum, Staatsangehörigkeit, Ausweisart/-nummer, Wohnort … (Felder je Land, §9) |
| `tasks` | Wechsel/Reinigung: Einheit, Tag, Art (Abreise-Reinigung, Zwischenreinigung, Übergabe), Zuständig, erledigt, Checkliste |
| `meta` | Merker (einmalige Übernahmen, Seeds) |

Regeln, die vom Shop bekannt sind und hier genauso gelten:

- **Geld in ganzen Cent, Preise brutto, der Server rechnet alles** — nichts, was ein Formular als Preis schickt, wird gelesen. Eine Buchung speichert ihre
  Rechnung als **Schnappschuss**; spätere Preisänderungen ändern sie nie.
- **Doppelbuchung ist ausgeschlossen durch die Datenbank**, nicht durch eine Prüfung davor: eine Buchung schreibt ihre Nächte in `nights` in **einer**
  `Serializable`-Transaktion (mit Schreiben beginnen, `LockForWrite` wie im Shop); der eindeutige Index lässt die zweite scheitern. Eine Einheit mit `contains`
  schreibt ihre Nächte auch für die umfassten Einheiten (und die umfassten für das Haus).
- **Jeder Statuswechsel geht durch eine Stelle** (`BookingFlow`, die einzige Liste erlaubter Wechsel), Mails und Ereignisse nach dem Commit.

## 4. Preise

Die Rechnung eines Aufenthalts wird **Nacht für Nacht** gebildet: für jede Nacht die Saison mit dem höchsten Rang, deren Zeitraum sie enthält (keine = Grundpreis
der Einheit), daraus der Nachtpreis; dazu je Person über der Standardbelegung der Aufschlag dieser Saison.

- **Mindestnächte und Anreisetag** gelten nach der Saison der **Anreisenacht** („Hauptsaison: mind. 7 Nächte, Anreise Samstag“). Der Kalender zeigt
  nicht erlaubte Anreisetage ausgegraut, die Anfrage nennt den Grund („In der Hauptsaison mindestens 7 Nächte“).
- **Wochenpreis**: hat eine Saison einen, kosten 7 Nächte am Stück darin den Wochenpreis statt 7 × Nachtpreis.
- **Gebühren** nach Art (je Aufenthalt/Nacht/Person); **Kurtaxe** je Person und Nacht mit Altersgrenzen (Kroatien: Kinder unter 12 frei, 12–18 halb) —
  sie ist eine eigene Zeile, weil sie in vielen Ländern getrennt ausgewiesen und abgeführt wird.
- **Rabatt/Aufschlag des Gastgebers** im Angebot (freier Betrag mit Text) — die einzige Stelle, an der ein Mensch den Preis ändert.
- **Anzahlung**: Prozent oder Betrag, fällig X Tage nach Bestätigung; Rest fällig Y Tage vor Anreise (bei kurzfristiger Buchung alles sofort).
- **Währung**: eine je Seite (Standard EUR); Anzeige im Format der Besuchersprache.
- Eine **Preis-Vorschau** (`/buchung/preis?unit=&von=&bis=&personen=`) gibt dieselbe Rechnung als JSON — der Anfrage-Block zeigt sie live; sie ist auf
  Anfragen je Adresse begrenzt.

## 5. Ablauf einer Buchung

```
Anfrage ──► Angebot ──► bestätigt ──► angezahlt ──► bezahlt ──► angereist ──► abgereist
   │           │            │                                           (Bewertung erbeten)
   ├► abgelehnt└► abgelaufen└► storniert (mit Stornobedingungen)
```

1. **Anfrage** (Website): Einheit, Zeitraum, Personen, Name, E-Mail, Telefon, Nachricht, Datenschutz-Haken. Die Nächte werden **vorgemerkt**
   (Status „Anfrage“ hält den Kalender für N Stunden, Einstellung, Standard 48 h — sonst bucht ein Portal dazwischen). Gast bekommt die Eingangsbestätigung,
   Gastgeber eine Mail und einen Eintrag unter „Aufmerksamkeit“.
2. **Gastgeber entscheidet**: annehmen wie angefragt (→ Angebot mit dem berechneten Preis), Preis anpassen (→ Angebot), oder ablehnen (mit Text; die Nächte werden frei).
3. **Angebot**: Mail mit Link auf die Gastseite `/buchung/{schlüssel}` — Zusammenfassung, Rechnung, Zahlungsplan, Stornobedingungen, Knopf „Verbindlich buchen“.
   Frist (Standard 3 Tage), danach „abgelaufen“, Nächte frei. Ein Job prüft das stündlich.
4. **Bestätigt**: Bestätigung als Mail und PDF (Buchungsbestätigung auf dem Briefkopf, wie die Verein-PDFs), Zahlungsplan mit Bankverbindung und
   Verwendungszweck (Buchungsnummer). Optional ist auch ein **Direktbuchen** durch den Gastgeber im Admin (Telefonbuchung) — gleiche Kette ab hier.
5. **Zahlung**: der Gastgeber trägt Eingänge ein (Betrag, Tag); Erinnerung an den Gast bei fälliger Anzahlung/Rest (einmal, Einstellung).
   Online-Zahlung (Stripe/PayPal wie im Shop) ist **Phase später** und hängt sich als weiterer Zahlungsweg an dieselbe Stelle.
6. **Storno**: Stornobedingungen als Staffel (bis 60 Tage frei, bis 30 Tage 50 % …); das Plugin rechnet den Betrag aus, der Gastgeber bestätigt.

Buchungsnummern sind lückenlos (`NextNumberAsync`), z. B. `2027-0042`.

## 6. Website (öffentlich)

- **Blöcke**: „Belegungskalender“ (eine Einheit oder alle, 1–12 Monate, Saisonfarben, freie/belegte/An-/Abreisetage halb), „Buchungsanfrage“ (Formular mit
  Live-Preis, vorbelegbar mit einer Einheit), „Unterkünfte“ (Karten aller Einheiten mit „ab … € / Nacht“), „Preise“ (Tabelle Saisons × Einheiten).
- **Seiten** als Block-Seiten (`PageRole`, wie der Shop): Einheit (`/unterkunft/{slug}`), Anfrage, Gastseite. Ohne angelegte Seite zeichnet das Plugin
  ein Template-Teil (`booking-unit.html` …) — theme-bar über die Variablen der Seite (`--accent` …), keine festen Farben.
- **Sprachen**: Gäste kommen von überall — Texte des Plugins in de/en/hr/it (`Strings`), Inhalte (Einheitentexte) über die Sprachen der Seite;
  Mails in der Sprache, in der angefragt wurde.
- **Belegung ist öffentlich, Gäste nie**: der Kalender zeigt nur frei/belegt, nie Namen oder Quelle.
- **Schema.org** (`LodgingBusiness`, `Accommodation`, `Offer`) und Sitemap-Einträge je Einheit.

## 7. Abgleich mit Portalen (iCal)

- **Ausgang**: je Einheit eine **geheime** Adresse `/buchung/ical/{unit}-{token}.ics` mit allen belegten Nächten (Buchungen, vorgemerkte Anfragen,
  Sperren) — ohne Namen, Summary „Belegt“. Diese Adresse trägt der Gastgeber bei Airbnb/Booking.com/Rentlio ein. Token neu erzeugen = alte Adresse tot.
  Nächte, die aus einem Feed **kamen**, gehen nicht in denselben Feed zurück (sonst blockiert sich ein Portal selbst).
- **Eingang**: je Einheit beliebig viele Feeds (Airbnb, Booking.com, Vrbo, Rentlio, eine andere MatCMS-Seite). Ein Job ruft sie alle **15 Minuten** ab
  und ersetzt die Sperren dieses Feeds vollständig (eine Belegung, die im Portal storniert wurde, verschwindet hier). Nur `https`, keine privaten/lokalen
  Adressen (aufgelöst geprüft, auch nach Weiterleitung), max. 2 MB, 20 s; ein Fehler steht am Feed und unter „Aufmerksamkeit“, die alten Sperren bleiben stehen.
- **Konflikte**: kommt aus einem Feed eine Belegung über eine bestätigte Buchung, wird sie trotzdem eingetragen (Feed gewinnt nie still) und als
  **Doppelbuchung** rot gemeldet — per Mail an den Gastgeber.
- **Grenzen, offen gesagt**: iCal trägt weder Preis noch Gast, und die Portale rufen in ihrem eigenen Takt ab (Airbnb ~ stündlich, Booking.com
  bis zu mehreren Stunden). Zwischen zwei Abrufen ist eine Doppelbuchung möglich — die Vormerkung von Anfragen (§5.1) verkürzt das Fenster, schließt es
  nicht. Wer mehr will, nimmt einen Channel-Manager (Rentlio) als einzige Quelle; dessen API ist eine spätere Phase.

## 8. Gäste und Kundenverbund

**Gäste** sind eine eigene Kartei (nicht die Mitgliederkonten der Seite — ein Gast braucht kein Passwort; seine Gastseite öffnet der geheime Link).
Wiedererkannt wird ein Gast an der E-Mail-Adresse; zwei Einträge lassen sich zusammenführen. Profil: Stammdaten, Notizen („bringt Hund mit“), alle
Aufenthalte, Umsatz, Stammgast-Merker.

**Kundenverbund** — für mehrere Seiten mit denselben Gästen (Villa Nika + Villa Sogi), eine Einstellung des Plugins, die jede Seite hat:

| Rolle | Was sie tut |
| --- | --- |
| **für sich** (Standard) | eigene Kartei, kein Verbund |
| **führt die Kartei** | stellt ihre Gäste über die Plugin-API bereit (`/api/v1/booking/guests…`); der Zugang ist ein **Site-API-Schlüssel** mit dem Recht `booking:personal`, den der Gastgeber unter Admin → API-Schlüssel anlegt |
| **hängt an** | trägt Adresse + Schlüssel der führenden Seite ein; liest und schreibt Gäste DORT, meldet ihre Aufenthalte als Zusammenfassung dorthin (Haus, Einheit, Zeitraum, Status, Betrag — keine Meldedaten) |

- Läuft **ohne Cloud** (zwei MatCMS-Seiten reichen). Die Cloud kann die Verbindung nur einrichten (beide Seiten gehören zum selben Betreiber → Schlüssel
  anlegen und eintragen auf Knopfdruck) — später.
- Die anhängende Seite hält eine **Kopie** der Gäste, die sie braucht (eine Buchung muss auch gehen, wenn die führende Seite gerade nicht erreichbar ist);
  Änderungen gehen in eine Warteschlange und werden nachgereicht; bei gleichzeitiger Änderung gewinnt die neuere (Zeitstempel je Feld).
- **Meldedaten reisen nicht mit** (Ausweisnummern gehören zu einem Aufenthalt in einem Haus) — außer der Gast setzt beim Check-in den Haken
  „für meinen nächsten Aufenthalt merken“; dann liegen sie in seinem Gästeeintrag und sind beim nächsten Check-in vorausgefüllt.
- Datenschutz: der Verbund ist eine gemeinsame Verarbeitung zweier Häuser — die Datenschutzerklärung beider Seiten muss es sagen (Hinweis im Admin, kein Rechtsrat).
- Das Muster (eine Seite führt, andere hängen per Schlüssel an) ist absichtlich allgemein — der Shop kann es später für gemeinsame Kunden übernehmen.

## 9. On- und Offboarding

**Online-Check-in** — X Tage vor Anreise (Standard 7) bekommt der Gast einen Link: Mitreisende mit Meldedaten, Ankunftszeit, Anreiseart/Kennzeichen,
Wünsche. Die Felder hängen vom **Land des Objekts** ab (Vorlagen: Kroatien/eVisitor, Deutschland/Meldeschein, Italien/Alloggiati, „einfach“), einzelne
Felder abwählbar. Export: CSV je Buchung und je Tag (für die Meldung bei der Behörde) und PDF (Meldeschein zum Unterschreiben). Meldedaten werden nach
der gesetzlichen Frist gelöscht (Einstellung, Standard 12 Monate nach Abreise; ein Job).

**Mails** (Mailvorlagen des Plugins, je Objekt anpassbar, in der Sprache des Gastes):

| Wann | Mail |
| --- | --- |
| Anfrage | Eingang (Gast), Neue Anfrage (Gastgeber) |
| Angebot / Bestätigung / Ablehnung / Ablauf | je eine |
| Zahlung fällig | Erinnerung Anzahlung, Erinnerung Rest |
| X Tage vor Anreise | Check-in-Link + Anfahrt, Hausregeln, Kontakt vor Ort |
| 1 Tag vor Anreise | Schlüssel/Türcode, WLAN — **erst jetzt** (ein Code steht nie in einer frühen Mail) |
| Abreisetag (morgens) | Check-out-Hinweise (Uhrzeit, Schlüssel, Müll) |
| 2 Tage nach Abreise | Danke + Bitte um Bewertung (Link einstellbar, z. B. Google) |

Jede Mail ist einzeln abschaltbar; der Verlauf der Buchung zeigt, was wann ging. Platzhalter: `{{gast}}`, `{{einheit}}`, `{{anreise}}`, `{{code}}` …

**Wechsel und Reinigung** — aus den Buchungen entsteht je Abreise eine Aufgabe (bei Anreise am selben Tag: „Wechsel bis 15:00“), optional Zwischenreinigung
nach N Nächten. Wechselliste je Tag/Woche (Einheit, Abreise → Anreise, Personen der nächsten Gäste, Kinderbett/Haustier), Zuständige (Name + Telefon/E-Mail,
kein Konto nötig), Checkliste je Einheit, „erledigt“ mit Zeit. Druck als PDF und ein **geheimer Kalender** (iCal) für das Reinigungsteam — ohne Gastnamen.

## 10. Admin (Bereich „Buchungen“, eigenes Menü)

- **Übersicht**: heute An-/Abreisen, offene Anfragen und Angebote, fällige Zahlungen, Feed-Fehler, Doppelbuchungen, Auslastung des Monats.
- **Belegungsplan**: Zeitleiste Einheiten × Tage (Monat, scrollbar), Buchungen als Balken nach Quelle gefärbt, Sperren schraffiert; Klick auf freie Tage = neue
  Buchung/Sperre. Auf dem Handy: Liste je Einheit.
- **Anfragen**, **Buchungen** (Liste mit Filter nach Status/Einheit/Zeitraum, CSV), **Buchung** (Kopf mit den nächsten Schritten als Knöpfe wie beim Shop,
  Rechnung, Zahlungen, Mitreisende, Mails, Verlauf).
- **Gäste** (Kartei, Zusammenführen, Verbund-Status).
- **Objekte & Einheiten**, **Saisons & Preise** (Matrix Saison × Einheit, „ins nächste Jahr kopieren“), **Gebühren & Bedingungen** (Storno, Anzahlung).
- **Kanäle** (iCal-Ausgang je Einheit zum Kopieren, Eingänge mit Status), **Reinigung**, **Einstellungen** (Kundenverbund, Check-in-Felder, Mails, Fristen).
- Dashboard-Karte „Buchungen · heute“; Aufmerksamkeit für neue Anfragen, Feed-Fehler, Doppelbuchungen.

## 11. Datenschutz und Sicherheit

- Gäste- und Meldedaten sind personenbezogen: `host.member.export/erase` greift hier nicht (Gäste sind keine Mitglieder) — das Plugin hat eine eigene
  Auskunft/Löschung je Gast im Admin; Buchungen bleiben für die Aufbewahrungspflicht, ihr Gast wird anonymisiert.
- Operationen für KI/Cloud sind entweder ohne Personendaten (Belegung, Preise, Saisons) oder `PersonalData` (nie an ein fremdes Modell, wie Bestellungen).
- Die Gastseite gilt nur mit ihrem Schlüssel (lang, zufällig, nur als Hash gespeichert), Antworten `no-store`, kein Index.
- Öffentliche Formulare: Antiforgery, `PostsPerMinute`, Spam-Schutz der Seite; der Preis kommt nie aus dem Formular.
- Feeds: nur https, keine privaten Adressen, Größen- und Zeitgrenze (§7).

## 12. Was die Plugin-API dafür noch braucht (zu prüfen beim Bau)

- **Ausgehende Abrufe** mit SSRF-Schutz als Host-Dienst (`ctx.Http.GetAsync(url, limits)`), statt dass jedes Plugin eigene Prüfungen schreibt —
  der Verein, Matmon-Live und Google-Reviews rufen heute selbst `HttpClient`.
- **Eine Personen-Löschung außerhalb der Mitgliederkonten** (Auskunft/Löschung für Gäste) — vielleicht ein allgemeiner Weg „Personendaten eines Plugins“.
- Alles andere (Tabellen, Jobs, Mailvorlagen, Blöcke, Block-Seiten, API-Endpunkte mit Rechten, Dateien, PDF wie im Verein) gibt es.

## 13. Phasen

| Phase | Inhalt | Ergebnis |
| --- | --- | --- |
| **B1** | Objekte, Einheiten, Saisons, Preise, Gebühren; Verfügbarkeit (`nights`); Preisrechnung mit Tests | Preis-Vorschau stimmt |
| **B2** | Öffentliche Blöcke (Kalender, Anfrage, Unterkünfte), Anfrage → Angebot → Bestätigung, Gastseite, Mails, Bestätigungs-PDF | erste echte Anfrage auf Villa Nika |
| **B3** | Admin: Übersicht, Belegungsplan, Buchungen, Gäste, Sperren, Zahlungen eintragen | Gastgeber arbeitet nur noch hier |
| **B4** | iCal Ein- und Ausgang, Konflikte | Airbnb/Booking.com abgeglichen |
| **B5** | Online-Check-in, Mails vor/nach dem Aufenthalt, Reinigung | On-/Offboarding |
| **B6** | Kundenverbund | Nika + Sogi teilen Gäste |
| später | Online-Zahlung (Stripe/PayPal), Rentlio-API, Gutscheine, Bewertungen auf der Seite | |

Jede Phase mit Tests (`tests/MatCMS.Plugins.Tests/Booking*Tests.cs`, ganze App in memory wie Verein/Forum; Doppelbuchung unter Last wie die Shop-Race-Tests)
und einer Prüfung an einer lokalen Kopie von Villa Nika (eigener Container, eigenes Volume, danach gelöscht).

## 14. Offene Fragen

1. **Kurtaxe**: soll das Plugin sie berechnen und ausweisen, oder zahlt der Gast sie vor Ort (dann nur ein Hinweis)?
2. **Stornobedingungen**: eine Staffel je Seite, oder je Saison verschieden (Hauptsaison strenger)?
3. **Sprachen der Gästetexte**: de/en/hr/it genug, oder weitere (nl, pl, cs …)?
4. **Was Villa Nika/Sogi heute nutzen**: welche Portale und ob ein Channel-Manager (Rentlio) schon im Spiel ist — das entscheidet, ob iCal reicht.
