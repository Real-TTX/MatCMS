# Shop-Plugin gegen Shopify — was fehlt, was nicht fertig ist

Stand 16.10.2026, nach CMS 1.13 (Block-Seiten). Maßstab ist Shopify Basic bis Grow für einen kleinen bis mittleren EU-Shop, nicht Shopify Plus.
Legende: **✓** vorhanden · **◐** teilweise / nicht richtig fertig · **✗** fehlt. Aufwand: S = Stunden, M = ein bis zwei Tage, L = mehrere Tage.

Die Einträge stammen aus dem Code und den Tests, nicht aus Vermutung; wo ich etwas nur aus der Doku weiß, steht es dabei.

## 1. Produkte und Katalog

| Funktion | Shopify | Hier | Anmerkung | Aufwand |
|---|---|---|---|---|
| Titel, Kurztext, Beschreibung, Bild, Galerie, Statt-Preis | ✓ | ✓ | Galerie mit Vorschaubildern und Großansicht | – |
| Kategorien als Baum | Collections (flach) | ✓ | Mehr als bei Shopify | – |
| Mehrere Kategorien je Produkt | ✓ | ✓ | Eine Hauptkategorie plus weitere (Haken im Produktformular); gezählt wird je Kategorie einmal, die Kategorie umfasst alles darunter | – |
| Automatische Kollektionen (Regeln: Preis, Schlagwort, Bestand, Neu, Sale) | ✓ | ✓ | Regel je Kategorie, z. B. `tag:Neuware; price<20; new:30; sale; instock; featured` (bis fünf Bedingungen, alle müssen passen) | – |
| Reihenfolge innerhalb einer Kategorie von Hand | ✓ | ◐ | Es gibt nur eine globale Sortierzahl je Produkt | S |
| Varianten (Name, Aufpreis, Bestand, Art.-Nr.) | ✓ | ✓ | Neu: Zeilen-Editor, Optionen, „Kombinationen erzeugen“ | – |
| Varianten im Shop als getrennte Optionen (Größe, Farbe) | ✓ | ✓ | Ein Wähler je Option (Tastatur, Preis und Bild wechseln mit, ausverkaufte Kombinationen durchgestrichen); Varianten, die nicht zu den Optionen passen, behalten die einfache Liste ; im Browser (Chrome) durchgespielt: Wahl, Preis, ausverkauft, Tastatur, Kauf | – |
| Bild je Variante | ✓ | ✓ | Im Varianten-Editor wählbar; wechselt das Hauptbild auf der Produktseite und steht in den Warenkorb-Zeilen | – |
| Preis je Variante absolut | ✓ | ◐ | Nur Aufpreis zum Produktpreis | S |
| Eigenschaften und Filter (Farbe, Gewicht …) | über Apps | ✓ | Stärker als Shopify-Standard; Preisfilter ✓ | – |
| Digitale Produkte mit Download-Limit | ✓ | ✓ | – | – |
| Produkt-Status Entwurf / geplant | ✓ | ◐ | Nur aktiv/inaktiv, keine Veröffentlichung zu einem Datum | S |
| Vorbestellung, Weiterverkauf bei Bestand 0 | ✓ | ✗ | – | M |
| Bundles, Mengenstaffeln, Geschenkkarten, Abos | Apps / ✓ | ✗ | – | L |
| Meta-Titel und -Beschreibung je Produkt | ✓ | ✓ | Eigene Felder; sonst Titel und Kurztext; Open-Graph-Tags (Titel, Beschreibung, Bild) | – |
| Strukturierte Daten (schema.org), Sitemap | ✓ | ✓ | – | – |
| Weiterleitung bei geänderter Adresse (301) | ✓ | ✓ | Die alte Adresse bleibt als Weiterleitung (nur für aktive Produkte, sonst 404) | – |
| Produktdaten mehrsprachig | ✓ | ✓ | Karte je Sprache am Produkt und an der Kategorie (Titel, Kurztext, Beschreibung, Meta); die Hauptsprache bleibt die Grundlage, leere Felder fallen darauf zurück; Bestellungen speichern, was der Kunde sah. Eigenschaften-Namen noch nicht | – |
| Mehrere Währungen / Märkte | ✓ | ✗ | Eine Währung je Shop | L |
| Bewertungen, Wunschliste, zuletzt angesehen, Vergleich | Apps | ✗ | Ein Plugin „Bewertungen“ existiert außerhalb des Shops | M–L |
| „Wieder verfügbar“-Benachrichtigung | Apps | ✗ | – | M |

## 2. Suche, Warenkorb, Kasse

| Funktion | Shopify | Hier | Anmerkung | Aufwand |
|---|---|---|---|---|
| Suche, Sortierung, Filter, aktive Filter | ✓ | ✓ | Neu gestaltet (Filterleiste, Chips, Preisfilter) | – |
| Suchvorschläge, Tippfehler-Toleranz | ✓ | ✓ | Vorschläge beim Tippen (Produkte mit Bild und Preis, Kategorien) und „Meinten Sie …?“ bei null Treffern; im Browser (Chrome) durchgespielt: Tippen, Pfeiltasten, Escape | – |
| Warenkorb mit Bildern, Gutschein, Leer-Zustand | ✓ | ✓ | – | – |
| Mini-Warenkorb (Schublade) | ✓ | ✗ | – | M |
| Versandkosten-Schätzung im Warenkorb | ✓ | ✓ | Land wählen: Versandarten mit Preis, Gesamtsumme, „noch X bis gratis“ — gleiche Regeln wie die Kasse | – |
| Cross-Selling im Warenkorb, Geschenkhinweis | Apps | ✗ | – | M |
| Kasse auf einer Seite, Gast oder Konto | ✓ | ✓ | Nummerierte Karten, Zusammenfassung bleibt sichtbar | – |
| Express-Zahlung (Apple Pay, Google Pay, PayPal-Knopf im Warenkorb) | ✓ | ◐ | Knöpfe „direkt bezahlen mit PayPal / Karte“ im Warenkorb führen in die Kasse mit gewählter Zahlart; **kein Ein-Klick-Wallet-Kauf** (die Adresse wird weiter abgefragt), Apple Pay/Google Pay zeigt Stripes Seite, wo Gerät und Konto es erlauben | M (echte Express-Kasse braucht Bestellanlage aus dem Webhook und Live-Tests) |
| Adress-Vervollständigung | ✓ | ✗ | – | S |
| Zahlarten: Karte (Stripe), PayPal, Vorkasse | ✓ | ✓ | **Live-Durchlauf mit echten Zugangsdaten steht aus** (nur gegen Ersatzserver getestet) | M (Test) |
| Kauf auf Rechnung, Klarna, SEPA-Lastschrift | ✓ | ◐ | Nur soweit Stripe sie freischaltet | S |
| Abgebrochene Warenkörbe → Erinnerungs-Mail | ✓ | ✓ | Aus (Einstellung), nur auf Wunsch des Kunden mit E-Mail im Warenkorb, **eine** Mail je Warenkorb, nach einstellbarer Zeit, nicht nach 14 Tagen, nicht wenn inzwischen bestellt; Link stellt den Warenkorb wieder her; Text unter Mailvorlagen | – |

## 3. Rabatte und Steuern

| Funktion | Shopify | Hier | Anmerkung | Aufwand |
|---|---|---|---|---|
| Gutscheincodes (Prozent, Betrag, Gratisversand, Mindestwert, Limit, einmal je Kunde, Zeitraum, Kategorie) | ✓ | ✓ | Vollständig, mit Race-Tests | – |
| **Automatische Rabatte** (ohne Code), „2 kaufen, 1 gratis“, Mengenrabatt | ✓ | ✗ | – | L |
| Rabatt auf ein einzelnes Produkt (nicht nur Kategorie) | ✓ | ◐ | Nur Kategorie oder alles | S |
| Mehrere Codes kombinieren, Regeln | ✓ | ◐ | Ein Code je Warenkorb | M |
| Steuer: Satz je Produkt, EU-OSS, Kleinunternehmer | ✓ | ✓ | – | – |
| Reverse Charge (USt-IdNr.), Lieferschwelle, Sondergebiete | ✓ | ✗ | In der Doku als offen geführt | M |
| Preisangabe nach Omnibus-Richtlinie (30-Tage-Tiefstpreis beim Statt-Preis) | Apps | ✗ | Rechtlich relevant bei Statt-Preisen | M |

## 4. Versand und Bestellungen

| Funktion | Shopify | Hier | Anmerkung | Aufwand |
|---|---|---|---|---|
| Versandarten nach Land, Gewicht, „gratis ab“ | ✓ | ✓ | – | – |
| Abholung im Laden / Lokale Lieferung | ✓ | ◐ | Als Versandart mit Preis 0 möglich, aber nicht als Abholung ausgewiesen | S |
| Sendungsverfolgung als Link | ✓ | ✓ | Neu: DHL, DPD, Hermes, GLS, UPS, FedEx u. a. | – |
| **Versandlabel / Carrier-Anbindung** | ✓ | ✗ | – | L |
| **Teillieferung** | ✓ | ✗ | Eine Bestellung ist ganz versendet oder nicht | M |
| Lieferschein / Packzettel | ✓ | ✓ | Knopf an der Bestellung: druckfertige Seite ohne Preise (Adresse, Positionen mit Art.-Nr., Kundennotiz) | – |
| Bestellliste mit Status, Ampel, Suche | ✓ | ✓ | – | – |
| **Massenaktionen** (mehrere als versendet markieren, drucken) | ✓ | ✗ | – | M |
| **Entwurfs-/manuelle Bestellung** (Telefon, Messe) | ✓ | ✗ | – | M |
| Bestellung nachträglich ändern | ✓ | ✗ | Bewusst: Bestellung ist ein Snapshot | L |
| Erstattung (Teil, mit Gutschrift, Bestand zurück) | ✓ | ✓ | – | – |
| Rückgabe-Abwicklung (RMA) | Apps | ✗ | – | L |
| Rechnung als PDF | ✓ (App) | ✓ | Mit Logo und Akzentfarbe; **keine E-Rechnung (ZUGFeRD/XRechnung)** | L |
| Notizen und Schlagwörter an Bestellung und Kunde | ✓ | ◐ | Interne Notiz im Verlauf ✓; Kundennotiz und Schlagwörter ✗ | S |
| Export (CSV) von Bestellungen, Bestellpositionen, Kunden, Produkten, Varianten; Import von Produkten und Varianten | ✓ | ✓ | Import & Export im Shop-Menü: deutsch (`;`, Komma) oder international, Zeitraum und Status, formelsicher; Import mit Prüflauf, Vorlage und Zeilenbericht | – |
| Produkt duplizieren, Massenbearbeitung | ✓ | ◐ | Duplizieren ✓ (Liste und Produktkopf; Kopie ist inaktiv, Varianten und Eigenschaften werden mitkopiert); Massenbearbeitung ✗ | S–M |

## 5. Lager, Berichte, Kunden

| Funktion | Shopify | Hier | Anmerkung | Aufwand |
|---|---|---|---|---|
| Bestand je Produkt und Variante, atomar | ✓ | ✓ | Mit Wettlauf-Tests | – |
| Mehrere Lager, Bestandsverlauf, Wareneingang | ✓ | ✗ | Es gibt keinen Verlauf der Bestandsänderungen | M |
| Warnung bei niedrigem Bestand | ✓ | ✓ | Als „Braucht Aufmerksamkeit“ | – |
| Berichte (Umsatz nach Zeit, Produkt, Land, Zahlart, Steuersatz) | ✓ | ✓ | Umsatzbericht mit Zeitraum, Verlauf, Meistverkauft, Aufteilung; Konversion ✗, Gutschein-Auswertung ✗ | – |
| Kundenkonto, Adressen, Datenexport, Löschen | ✓ | ✓ | DSGVO sauber; Kontoseite neu gestaltet | – |
| Kundenseite im Admin (Kontakt, Adressen, Bestellungen) | ✓ | ✓ (nur lesen) | Neu; Kunde bearbeiten, Notizen, Schlagwörter fehlen | S |
| Kundengruppen, B2B-Preise, Segmente | Plus / Apps | ✗ | – | L |
| „Erneut bestellen“, Wunschliste im Konto | Apps | ✗ | – | S–M |
| Mitarbeiter mit eingeschränkten Rechten | ✓ | ✗ | Wer im Admin ist, darf alles | M |

## 6. Aussehen und Plattform

| Funktion | Shopify | Hier | Anmerkung | Aufwand |
|---|---|---|---|---|
| Seiten im visuellen Editor mit Variablen | Online Store 2.0 | ✓ | Neu; Konto, Kasse und Bestellung teils nur mit Beispielstücken in der Vorschau | – |
| Theme-Anpassung (Farben, Schrift) | ✓ | ✓ | Shop folgt `--accent`, `--line`, `--bg`, `--font-head` | – |
| **Konto-Unterseiten einzeln gestalten** | ✓ | ◐ | Alle vier Konto-Seiten teilen eine Block-Seite | M |
| Mehrsprachige Oberfläche | ✓ | ✓ | – | – |
| Apps / Erweiterungen | ✓ | ✓ | Eigene Plugin-API, Events (`shop.order.*`) | – |
| API für Produkte, Bestellungen, Kunden | ✓ | ◐ | Cloud/MCP: Produkt und Gutschein anlegen, **Bestellungen lesen und weiterbewegen (bezahlt, versendet, abgeschlossen, storniert, Notiz, Sendungsnummer), Varianten setzen, Eigenschaften anlegen und Produkten Werte geben**; Lager, Kunden, Kategorien ✗; keine REST-Schnittstelle, nur MCP (wie die übrigen Inhalts-Aktionen) | S–M |
| Produkt-Feeds (Google Merchant, Meta) | ✓ | ✗ | – | M |
| Tracking-Pixel / GA4 E-Commerce-Ereignisse | ✓ | ✗ | – | M |
| Kasse für den Laden (POS) | ✓ | ✗ | Nicht Ziel | – |

## 7. Was „nicht richtig fertig“ ist (Stand nach Paket A und B, 16.10.2026)

1. **Zahlungen live ungetestet.** Stripe und PayPal sind nur gegen Ersatzserver geprüft. Neu: Shop → Konditionen → Zahlungsarten → **„Verbindung prüfen“** fragt den Anbieter mit den gespeicherten Zugangsdaten (nur lesend: Schlüssel, Modus, Webhook-Adresse und -Ereignisse) und die Checkliste dort führt durch den Testkauf. Das ersetzt den Testkauf nicht; er ist vor dem ersten echten Verkauf zwingend und braucht Zugangsdaten.
2. **Echte Express-Kasse** (Wallet-Knopf im Warenkorb, Adresse kommt vom Wallet) fehlt: nur Direkt-Knöpfe in die Kasse.
3. **Skripte nur in Chrome geprüft**: Variantenwähler und Suchvorschläge sind per Test für Markup und Daten abgesichert und im Test-Container mit Chrome durchgespielt; Firefox und Safari (auch auf dem Handy) sind nicht geprüft.
4. **Massenbearbeitung, Teillieferung, Massenaktionen, Entwurfsbestellung** (Paket C) fehlen.
5. **Keine REST-Schnittstelle** für den Shop, nur MCP; Lager und Kunden sind nicht per Agent steuerbar.
6. **Eigenschaften-Namen und Optionen** sind nicht übersetzbar (Produkt- und Kategorietexte sind es). Auch die Zeilenmeldungen des Import-Berichts („angelegt“, „übersprungen: …“) und die Texte der Cloud-Aktionen sind nur deutsch.
7. **Rechtlich offen:** Reverse Charge, E-Rechnung, Omnibus-Tiefstpreis, Lieferschwelle (siehe `docs/shop-plugin.md` § 9).
8. **Block-Seiten:** Kasse und Konto zeigen im Editor Beispielstücke statt echter Daten; die Konto-Unterseiten sind nicht einzeln gestaltbar.
9. **Barrierefreiheit** (Tastatur, Screenreader-Texte, Kontraste im Konto/Filter) ist nicht systematisch geprüft; der Variantenwähler und die Suchvorschläge haben Tastatur und ARIA.

## 8. Vorschlag für die Reihenfolge

**Paket A — gebaut (16.10.2026), bis auf den echten Zahlungs-Testlauf:** Verbindungsprüfung und Checkliste für Stripe/PayPal ✓ · CSV-Export und Produkt-Import ✓ · Umsatzbericht ✓ · 301 bei geänderter Adresse ✓ · Produkt duplizieren ✓ · Lieferschein ✓ · Meta-Titel/-Beschreibung ✓ · Cloud/MCP-Aktionen für Bestellungen, Varianten, Eigenschaften ✓. **Offen: der Testkauf mit echten Test-Zugangsdaten.**

**Paket B — gebaut (16.10.2026):** getrennte Variantenwahl mit Bild je Variante ✓ · mehrere Kategorien und automatische Kollektionen ✓ · Versandkosten-Schätzung im Warenkorb ✓ · Direkt-Zahlungsknöpfe im Warenkorb ◐ (keine Ein-Klick-Express-Kasse) · Erinnerung bei abgebrochenem Warenkorb ✓ · Suchvorschläge und „Meinten Sie“ ✓ · Produktdaten mehrsprachig ✓.

**Paket C — Händler-Werkzeuge:** Entwurfsbestellung · Teillieferung · Massenaktionen · Kundennotizen und Schlagwörter · Bestandsverlauf · Rollen und Rechte.

**Paket D — später / bei Bedarf:** automatische Rabatte und Mengenstaffeln · Geschenkkarten · Bundles · Versandlabel-Anbindung · E-Rechnung · Reverse Charge · Omnibus-Hinweis · Bewertungen · Wunschliste · Produkt-Feeds · Tracking-Pixel · mehrere Währungen.
