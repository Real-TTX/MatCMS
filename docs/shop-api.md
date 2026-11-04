# Shop-API für ERP und Warenwirtschaft

MatCMS bietet für andere Programme eine JSON-API unter `/api/v1/{plugin}/…`. Der Shop nutzt sie für die Anbindung an ein ERP:
Produkte per **Artikelnummer (SKU)** anlegen und ändern, **Bestand** und **Preise** in großen Mengen setzen, **Kategorien** pflegen und
**Bestellungen** abholen und als versendet melden.

## Zugang

1. Im Admin unter **Verwaltung → API-Schlüssel** einen Schlüssel anlegen, z. B. „ERP Lager“.
2. Rechte wählen (pro Plugin):
   - **Lesen** – Produkte und Kategorien abrufen
   - **Schreiben** – Produkte, Bestand, Preise, Kategorien ändern (schließt Lesen ein)
   - **Personenbezogen** – Bestellungen mit Kundendaten lesen und ändern (eigenes Recht, nie in einem anderen enthalten)
3. Der Schlüssel (`msk_…`) wird **einmal** angezeigt. Er wird nur als Prüfsumme gespeichert; geht er verloren, einen neuen anlegen und den alten widerrufen.

Jeder Aufruf schickt den Schlüssel im Header:

```
Authorization: Bearer msk_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
Content-Type: application/json
```

`GET /api/v1` zeigt, welcher Schlüssel das ist und welche Endpunkte er aufrufen darf. Höchstens **600 Aufrufe pro Minute** und Schlüssel –
Bestand und Preise deshalb **gesammelt** schicken (bis 5000 Zeilen pro Aufruf). Die API läuft auch im Wartungsmodus der Website.

## Regeln

- **Artikelnummer** ist der Schlüssel des ERP: die SKU eines Produkts oder einer seiner Varianten.
- **Preise** sind **brutto** in **Cent** (`price_cents: 2490` = 24,90 €). `"price": "24,90"` wird ebenfalls gelesen.
- **Bestand**: eine ganze Zahl; `null` heißt „wird nicht gezählt“ (unbegrenzt).
- **Teiländerungen**: Bei `PUT` ändert sich nur, was im Body steht. Was der Shop selbst pflegt (Texte, Bilder), bleibt, wenn das ERP es nicht schickt.
- **Löschen** gibt es nicht. `DELETE` schaltet ein Produkt ab (`active: false`), denn Bestellungen verweisen darauf.
- **Fehler** kommen als `{"error": "…"}` mit passendem Status: 400 (Eingabe), 401 (Schlüssel), 403 (Recht), 404 (unbekannt), 409 (Konflikt), 413 (zu groß), 429 (zu viele Aufrufe).

## Endpunkte

| Methode | Adresse | Recht | Zweck |
|---|---|---|---|
| GET | `/api/v1/shop/products` | Lesen | Produkte seitenweise: `?updated_since=2026-11-01T00:00:00Z&active=true&page=1&limit=100` (max. 500) |
| GET | `/api/v1/shop/products/{sku}` | Lesen | Ein Produkt mit Varianten (auch über die SKU einer Variante) |
| PUT | `/api/v1/shop/products/{sku}` | Schreiben | Anlegen oder ändern (Upsert) – Antwort 201 bei neu, 200 bei geändert |
| DELETE | `/api/v1/shop/products/{sku}` | Schreiben | Abschalten |
| POST | `/api/v1/shop/stock` | Schreiben | Bestand vieler Artikel |
| POST | `/api/v1/shop/prices` | Schreiben | Preise vieler Artikel |
| GET | `/api/v1/shop/categories` | Lesen | Alle Kategorien |
| PUT | `/api/v1/shop/categories/{slug}` | Schreiben | Kategorie anlegen oder ändern |
| GET | `/api/v1/shop/orders` | Personenbezogen | Bestellungen: `?status=paid&since=2026-11-01&limit=25` (max. 100) |
| GET | `/api/v1/shop/orders/{nummer}` | Personenbezogen | Eine Bestellung |
| POST | `/api/v1/shop/orders/{nummer}/ship` | Personenbezogen | Als versendet melden |
| POST | `/api/v1/shop/orders/{nummer}/status` | Personenbezogen | `paid`, `complete`, `cancel` oder `note` |

### Produkt anlegen oder ändern

```http
PUT /api/v1/shop/products/ESP-1000
{
  "title": "Espresso Bohne 1 kg",
  "price_cents": 2490,
  "compare_cents": 2990,
  "tax_percent": 7,
  "stock": 120,
  "gtin": "4012345678901",
  "category": "kaffee",
  "active": true,
  "variants": [
    { "sku": "ESP-1000-M", "name": "Gemahlen", "price_cents": 2590, "stock": 40 }
  ]
}
```

Weitere Felder: `summary`, `description_html`, `weight_g`, `kind` (`physical`/`digital`), `image` (Adresse), `gallery` (Liste),
`tags` (Liste oder Text), `categories` (weitere Kategorien), `brand` (Marke – Adresse oder Name; eine neue Marke wird angelegt, `""` nimmt sie weg),
`featured`, `slug` (nur beim Anlegen).

Ein **neues** Produkt braucht `title` und einen Preis. Varianten werden über ihre eigene SKU gefunden. Eine Variante hat entweder
`price_cents` (ihr Endpreis) oder `price_delta_cents` (Aufpreis). Mit `"variants_replace": true` werden Varianten, die nicht mehr in der Liste stehen,
abgeschaltet (nicht gelöscht). Die Kategorie muss es geben: sie wird über die Adresse (`kaffee`) oder den Namen (`Kaffee`) gefunden.

### Bestand

```http
POST /api/v1/shop/stock
{ "items": [
  { "sku": "ESP-1000",   "stock": 120 },   // absolut setzen
  { "sku": "ESP-1000-M", "add": -3 },      // relativ ändern (nie unter 0)
  { "sku": "GUTSCHEIN",  "stock": null }   // nicht mehr zählen
] }
```

Antwort – jede Zeile für sich, eine falsche hält die anderen nicht auf:

```json
{ "ok": 2, "failed": 1, "items": [
  { "sku": "ESP-1000", "stock": 120, "ok": true },
  { "sku": "ESP-1000-M", "stock": 37, "ok": true },
  { "sku": "GUTSCHEIN", "ok": false, "error": "unbekannte Artikelnummer" }
] }
```

### Preise

```http
POST /api/v1/shop/prices
{ "items": [
  { "sku": "ESP-1000", "price_cents": 2490, "compare_cents": 2990 },
  { "sku": "ESP-1000-M", "price_cents": 2590 }
] }
```

Für eine Variante ist `price_cents` ihr Endpreis. Streichpreise gibt es nur für das Produkt.

### Bestellungen

```http
GET /api/v1/shop/orders?status=paid&since=2026-11-01
POST /api/v1/shop/orders/2026-0042/ship   { "tracking": "00340434161234567890", "carrier": "dhl", "notify": true }
POST /api/v1/shop/orders/2026-0042/status { "action": "paid" }
```

Erlaubt sind dieselben Schritte wie im Admin (z. B. erst bezahlt, dann versendet); ein Schritt, den die Bestellung nicht machen kann, gibt 409 mit dem
Grund. Im Verlauf der Bestellung steht der Name des Schlüssels (`api:ERP Lager`).

### Beispiel mit curl

```bash
curl -X PUT https://ihre-website.de/api/v1/shop/products/ESP-1000 \
  -H "Authorization: Bearer msk_…" -H "Content-Type: application/json" \
  -d '{"title":"Espresso Bohne 1 kg","price_cents":2490,"stock":120}'
```

## Für Plugin-Entwickler

Jedes Plugin kann eigene Endpunkte anbieten:

```csharp
b.Api("PUT", "products/{sku}", UpsertAsync, new ApiRouteOptions { Access = ApiAccess.Write, Summary = "…", Example = "{…}" });
```

Der Host prüft Schlüssel und Recht, begrenzt die Aufrufe, liest den JSON-Body (`ctx.Request.Json()`, höchstens 4 MB) und gibt die Antwort des
Handlers (`PluginResult.Json(…)`) weiter. Der Handler sieht nie den Schlüssel, nur seinen Namen (`ctx.Request.ApiCaller`). Die Endpunkte erscheinen
automatisch unter Admin → API-Schlüssel und in `GET /api/v1`.
