# Changelog

Alle Änderungen werden hier dokumentiert. Format: `YYMMDD`.

---

## [260618] — 2026-06-18

### Behoben
- **Versions-Bug:** `CurrentVersion` war nach dem letzten Build nicht aktualisiert worden — der Indikator zeigte sich selbst fälschlicherweise als veraltet an und forderte ein Update auf sich selbst an. Behoben durch korrektes Setzen der internen Versionskonstante auf `260618`.

---

## [260611] — 2026-06-11

### Neu
- **Mini-Candlestick-Chart** auf der Recap-Karte: zeigt die Kerzen rund um Entry und Exit mit farbigen Markierungen (grüner Pfeil = Long-Entry, roter Pfeil = Short-Entry, X = Exit)
- **MAE / MFE Tracking** via `OnNewTrade` — Tick-genaue Erfassung des maximalen unrealisierten Verlusts und Gewinns während eines offenen Trades
- **MAE / MFE in Ticks** auf der Karte (`MAETicks`, `MFETicks`) zusätzlich zu USD-Werten
- **Prop Firm Drawdown-Anzeige** mit Farbskala (Grün / Gelb / Rot) basierend auf konfiguriertem Tages-Limit
- **Auto-Update-Mechanismus:** Indikator prüft beim Start gegen `version.json` auf GitHub und bietet bei neuer Version einen 1-Klick-Download in den ATAS-Indikatorordner an
- **Trade-Tag** — freies Textfeld in den Einstellungen, wird beim nächsten Trade-Close übernommen und danach zurückgesetzt
- **Logo-Unterstützung** — beliebiges PNG oben links auf der Karte, Seitenverhältnis wird beibehalten

### Geändert
- Karten-Layout auf 1080×1920px (9:16) — optimiert für Instagram Stories und Telegram
- `OnPortfolioChanged` liest `RealizedProfit` / `ClosedPnL` automatisch aus (mehrere Property-Namen werden probiert für ATAS-Versionskompatibilität)
- Tick-Größe und Tick-Wert werden primär aus dem ATAS-Trade-Fill gelesen; statische Fallback-Tabelle nur als letzter Ausweg

### Behoben
- PnL-Berechnung bei Trades mit mehreren Teil-Fills (weighted average entry/exit)
- `DailyStats.AddTrade` wurde vor der PnL-Berechnung aufgerufen — Tages-PnL war dadurch immer 0

---

## [260501] — 2026-05-01

### Neu
- Erste öffentliche Version
- Automatisches Erkennen von Trade-Closes via `OnNewMyTrade`
- Gebrandete Recap-Karte (System.Drawing) mit PnL, Symbol, Richtung, Contracts
- Tages-Statistiken (Anzahl Trades, Tages-PnL, Win-Rate)
- Telegram-Versand via Bot-API (`sendPhoto`)
- Lokales CSV-Journal
- Status-Panel im Chart (oben rechts): Telegram-Verbindung, aktiver Trade
- Unterstützung für Classic ATAS und ATAS X (zwei `.csproj`-Varianten)
