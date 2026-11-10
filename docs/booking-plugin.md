# Buchungs-Plugin — Entwurf

Stand: 10.11.2026 — **B1–B6 gebaut** (`plugins/booking` 0.6: … B3 Belegungsplan, Zahlungen, PDF, Gästekartei; B4 iCal-Abgleich in beide Richtungen mit Konflikt-Meldung; B5 Online-Check-in (Felder je Land), Meldungen/CSV/PDF, Löschfrist, Gästemails vor/nach dem Aufenthalt, Reinigungsliste mit Teamkalender; B6 Kundenverbund (§8: Kartei-API, Abgleich mit Warteschlange, Zeitstempel je Feld); Tests `BookingTests`, `BookingFlowTests`, `BookingB3Tests`, `BookingIcalTests`, `BookingB5Tests`, `BookingFederationTests`). Demo: `matcms-demo-villa` (Villa Mirta). Abweichung vom Entwurf: das iCal-Fenster reicht drei Jahre voraus (Airbnb exportiert so weit).
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

- **Blöcke**: „Belegungskalender“ (eine Einheit, zwei Monate zum Blättern), „Buchungsanfrage“ (Formular mit
  Live-Preis, vorbelegbar mit einer Einheit; der Zeitraum ist EIN Feld — der Zeitraum-Picker der Website mit `data-dp-busy-url` auf
  `/buchung/belegung`: belegte Nächte gesperrt, Wechseltag als Abreise, Mindestaufenthalt/Anreisetage der Anreise-Saison, 0.7), „Unterkünfte“ (Karten aller Einheiten mit „ab … € / Nacht“), „Preise“ (Tabelle Saisons × Einheiten).
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

## 8. Gäste und Verbund

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
- **Gebaut (B6)**: Abgleich der anhängenden Seite per Job alle 10 Minuten; die „Warteschlange“ ist keine eigene Tabelle, sondern alles, was seit dem letzten
  GELUNGENEN Lauf geändert wurde (`updated` an Gast und Buchung, Marke `fed.pushed` in `meta`) — so geht nichts verloren, und doppelt Gesendetes ändert nichts.
  Die Seite holt nur Karten der Gäste, die sie selbst kennt. Ein Gast wird auf der Seite gelöscht, auf der man es tut (die Kartei behält ihn, bis man ihn dort löscht).
- Das Muster (eine Seite führt, andere hängen per Schlüssel an) ist absichtlich allgemein — der Shop kann es später für gemeinsame Kunden übernehmen.

### 8a. Verbund mit Zentrale (B7 — Entwurf 10.11.2026; B7a gebaut, 0.11)

Matthias, 10.11.: *„Kundenverbund wird zu ‚Verbund'. Es sollen nicht nur Kunden, sondern auch Anfragen und Antworten über eine Oberfläche möglich
sein, dass man quasi EIN DASHBOARD hat."* Entschieden: **in MatCMS, im Buchungs-Plugin** — kein eigenes Programm (Preise, Belegung, Ablauf, iCal,
Check-in und Mails gibt es schon; ein zweites Programm müsste sie nachbauen oder die Seiten zu Anzeigen machen) und ohne Cloud.

**Ziel:** Der Gastgeber arbeitet an EINER Stelle — Anfragen aller Häuser sehen und beantworten, Buchungen, Belegung, Gäste, Reinigung. Der Gast bucht
weiter auf der Seite seines Hauses und bekommt Mails, Gastseite und Rechnung von diesem Haus.

**Rollen** (die Werte der Einstellung bleiben `solo | lead | member`, nur die Namen ändern sich):

| Rolle | Was sie tut |
| --- | --- |
| **für sich** | wie bisher |
| **Zentrale** (`lead`) | führt die Gästekartei (B6) UND hat das Dashboard über alle Häuser. Kann selbst ein Haus sein (Villa Nika) oder eine Büro-Instanz ohne eigene Unterkünfte und ohne öffentliche Buchungsblöcke. |
| **Haus im Verbund** (`member`) | nimmt Anfragen an wie bisher, meldet sie sofort der Zentrale und lässt sich von ihr steuern |

**Grundsatz: eine Buchung lebt dort, wo sie gebucht wurde.** Die Zentrale hält nur einen **Spiegel** (Liste + Stand) und führt jede Handlung AUF dem
Haus aus. So gibt es keine zweite Wahrheit, Nächte werden nur an einer Stelle vergeben (keine Doppelbuchung über den Verbund), und Absender, Gastlink,
Nummernkreis und Rechnung bleiben die des Hauses.

**Zwei Wege, zwei Schlüssel**

| Richtung | Wozu | Schlüssel |
| --- | --- | --- |
| Haus → Zentrale | Gäste (B6), Buchungen und Anfragen melden | auf der Zentrale angelegt, Recht `booking:personal` (gibt es) |
| Zentrale → Haus | Stand lesen, Handlungen ausführen | auf dem HAUS angelegt, neues Recht `booking:manage` (allein stehend wie `personal`: wer es hat, ändert Buchungen dieses Hauses) |

Einrichten: Auf dem Haus „Verbund" → Rolle, Adresse der Zentrale, deren Schlüssel; dazu legt das Haus seinen `manage`-Schlüssel an und schickt ihn mit dem
ersten Abgleich an die Zentrale (`PUT /api/v1/booking/houses/self`: Name, Adresse, Schlüssel — dort verschlüsselt gespeichert, nie angezeigt). Ein Schritt
statt zwei Seiten hin und her. Die Zentrale listet die Häuser unter **Verbund → Häuser** (Name, Adresse, zuletzt gemeldet, erreichbar ja/nein,
„Verbindung prüfen", „Haus entfernen" = Schlüssel vergessen). Später kann die Cloud beides auf Knopfdruck einrichten.

**Spiegel (Haus → Zentrale)**

- Je Buchung: Haus, Nummer, Einheit, Zeitraum, Personen, Status, Gast (Name, E-Mail, Telefon, Sprache), Nachricht der Anfrage, Rechnung (Zeilen + Summe),
  Zahlungen (Soll/Ist), Fristen (`hold_until`, `offer_until`), letzte Änderung. NICHT: Meldedaten (`travellers`), Gastschlüssel.
- **Neue Anfrage = sofort**: nach dem Commit einer Anfrage schickt das Haus sie direkt (`POST /api/v1/booking/mirror`), ohne auf den 10-Minuten-Job zu
  warten; geht das nicht, holt der Job sie nach (dieselbe Warteschlange „seit dem letzten gelungenen Lauf" über `bookings.updated`). Doppelt Gesendetes
  überschreibt nur denselben Stand (Schlüssel: Haus + Nummer).
- Tabelle `remote_stays` wird zu `mirror` erweitert (neue Spalten; eine B6-Zeile ist ein Spiegel ohne Rechnung).
- Je Haus außerdem: Unterkünfte (Name, Slug, Bild, Personen) und belegte Nächte der nächsten 18 Monate inkl. Sperren und Portal-Nächte
  (`GET /api/v1/booking/occupancy`, beim Öffnen des Belegungsplans abgefragt, eine Minute zwischengespeichert; fällt das Haus aus, zeigt der Plan den
  Spiegel und sagt „Stand von …").

**Handlungen (Zentrale → Haus)**

- `POST /api/v1/booking/bookings/{nummer}/actions` mit `{ action, args, expect, op }`: `offer` (Nachlass, Text, Frist), `confirm`, `decline` (Grund,
  benachrichtigen), `cancel`, `pay`/`unpay`, `note`, `message` (s. u.). Neu anlegen (Telefonbuchung für ein Haus): `POST /api/v1/booking/bookings`.
- **Ein Weg im Code:** die Handlungen der Buchungsseite im Admin werden zu `BookingActions` (eine Methode je Handlung), die Admin-Seite UND die API rufen
  sie — die Zentrale kann nichts, was der Gastgeber im Haus nicht auch kann, und umgekehrt. Alles geht weiter durch `BookingFlow.MoveAsync`.
- **`expect`** = der Status, den die Zentrale gesehen hat; hat sich die Buchung inzwischen geändert (jemand hat im Haus selbst geantwortet), antwortet das
  Haus 409 mit dem neuen Stand — die Zentrale zeigt ihn, statt blind zu überschreiben.
- **`op`** = Kennung der Handlung (UUID); das Haus merkt sie sich in der Historie, eine Wiederholung (Zeitüberschreitung, zweiter Klick) ändert nichts.
- **Nur online:** ist das Haus nicht erreichbar, sind die Knöpfe für dieses Haus aus (mit Grund). Handlungen werden NICHT gesammelt und später gesendet —
  ein Angebot, das Stunden später rausgeht, ist ein anderes Angebot.
- Historie im Haus: „Verbund (Zentrale Büro): Angebot gesendet" — wer gehandelt hat, steht beim Haus, nicht nur in der Zentrale.

**Antworten = Gespräch je Buchung (neu, auch ohne Verbund nützlich)**

- Tabelle `messages` (Buchung, von host|guest, Text, Zeit, wer). Der Gastgeber schreibt im Admin der Buchung (oder in der Zentrale → `message`); der Gast
  bekommt eine Mail vom Haus mit dem Text und dem Link zu seiner Gastseite.
- Der Gast antwortet **auf seiner Gastseite** (Formular „Nachricht an den Gastgeber", Antiforgery, Grenze je Stunde) — keine Mail-Empfangstechnik nötig.
  Seine Nachricht landet beim Haus, geht sofort in den Spiegel und erscheint im Posteingang der Zentrale als „neue Antwort".
- Mails an Gäste bleiben bei den Schaltern des Hauses; auf den Demo-Seiten aus.

**Oberfläche der Zentrale (Admin → Buchungen, eigenes Menü wie heute)**

- **Übersicht** (Dashboard): neue Anfragen und Antworten (Zähler am Menüpunkt), heute An-/Abreise je Haus, fällige Zahlungen, Fristen, die bald
  ablaufen, Doppelbuchungen aus iCal, Häuser, die nicht antworten.
- **Posteingang**: Anfragen und Gast-Antworten ALLER Häuser in einer Liste (Haus als Spalte und Filter, Tabs offen / beantwortet / alle); ein Klick öffnet
  die Buchung mit Rechnung, Gespräch und den Knöpfen des Hauses.
- **Buchungen** und **Belegungsplan** über alle Häuser (Plan: Häuser untereinander, Einheiten als Zeilen — wie der heutige Plan, nur länger).
- **Gäste** (die Kartei aus B6, jetzt mit den Buchungen aller Häuser) und **Reinigung** (die Listen aller Häuser zusammen, Haus als Spalte).
- Eigene Unterkünfte der Zentrale (wenn sie ein Haus ist) erscheinen überall wie ein weiteres Haus („dieses Haus").
- Mitteilungen: die Zentrale hat EINE Adresse für neue Anfragen aller Häuser; jedes Haus kann seine eigene Mitteilung abschalten.

**Datenschutz und Sicherheit**

- Die Zentrale hält personenbezogene Daten aller Anfragen — gemeinsame Verarbeitung wie B6; der Hinweis im Admin nennt jetzt auch Anfragen und Nachrichten.
- `manage`-Schlüssel nur verschlüsselt auf der Zentrale, nie im Backup (wie alle Schlüssel); ein Haus kann ihn jederzeit widerrufen → die Zentrale zeigt es
  sofort als „nicht verbunden".
- Ausgehende Aufrufe der Zentrale wie bisher nur https an öffentliche Adressen, geprüft im Verbindungsaufbau.
- Löschen bleibt pro Seite; löscht das Haus eine Buchung oder einen Gast, verschwindet der Spiegel beim nächsten Abgleich.

**Teile**

| Teil | Inhalt | Ergebnis |
| --- | --- | --- |
| **B7a** | Umbenennung „Verbund", Rollen Zentrale/Haus, Häuser-Liste, Spiegel (sofort + Job), Übersicht + Posteingang + Buchungen über alle Häuser, nur lesen | Matthias sieht Nika und Sogi an einer Stelle |
| **B7b** | `booking:manage`, `BookingActions` (ein Weg für Admin und API), Handlungen aus der Zentrale mit `expect`/`op`, Telefonbuchung für ein Haus, Belegungsplan über alle | Antworten ohne zweiten Login |
| **B7c** | Gespräch je Buchung: Nachrichten im Admin und auf der Gastseite, Mails, „neue Antwort" im Posteingang; Reinigung über alle Häuser | Anfragen UND Antworten an einer Stelle |

Tests: drei ganze Seiten in einem Test (Büro + zwei Häuser, wie `FederationSites`): Anfrage auf Haus B erscheint sofort in der Zentrale; Angebot aus der
Zentrale geht als Mail des Hauses B; gleichzeitige Antwort im Haus → 409 mit neuem Stand; Wiederholung mit gleicher `op` ändert nichts; Haus aus →
Knöpfe aus, Anfrage auf dem Haus geht trotzdem; widerrufener Schlüssel; kein Meldedatum in der Zentrale.

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
| **B7** | Verbund mit Zentrale (§ 8a): ein Dashboard für Anfragen, Antworten, Buchungen, Belegung und Gäste aller Häuser — Teile B7a–c | ein Dashboard für Nika + Sogi |
| später | Online-Zahlung (Stripe/PayPal), Rentlio-API, Gutscheine, Bewertungen auf der Seite | |

Jede Phase mit Tests (`tests/MatCMS.Plugins.Tests/Booking*Tests.cs`, ganze App in memory wie Verein/Forum; Doppelbuchung unter Last wie die Shop-Race-Tests)
und einer Prüfung an einer lokalen Kopie von Villa Nika (eigener Container, eigenes Volume, danach gelöscht).

## 14. Offene Fragen

1. **Kurtaxe**: soll das Plugin sie berechnen und ausweisen, oder zahlt der Gast sie vor Ort (dann nur ein Hinweis)?
2. **Stornobedingungen**: eine Staffel je Seite, oder je Saison verschieden (Hauptsaison strenger)?
3. **Sprachen der Gästetexte**: de/en/hr/it genug, oder weitere (nl, pl, cs …)?
4. **Was Villa Nika/Sogi heute nutzen**: welche Portale und ob ein Channel-Manager (Rentlio) schon im Spiel ist — das entscheidet, ob iCal reicht.
5. **Zentrale für Nika + Sogi**: Villa Nika selbst, oder eine eigene Büro-Instanz ohne Website (kein Haus bevorzugt; die Zentrale bleibt erreichbar, wenn eine Seite umgebaut wird)?
6. **Wer antwortet**: nur ein Konto, oder mehrere Personen mit Rechten je Haus (dann braucht B7 Rollen im Plugin)?
