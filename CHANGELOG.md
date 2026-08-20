# Changelog

Alle Änderungen werden hier dokumentiert. Format: `YYMMDD`.

---

## [260822] — 2026-08-20

### Neu — Chart im Exit-Check
- Der Exit-Check (5 Minuten nach Trade-Close) verschickt jetzt statt einer reinen Textnachricht den gewohnten Mini-Chart (`MiniChartRenderer`/`BuildMiniChart`) als Foto mit dem Bewertungstext als Caption.
- Kein neuer Rendering-Code nötig: `BuildMiniChart` fenstert grundsätzlich bis zum aktuellen Bar, ruft man es beim Fälligwerden der Auswertung (statt beim Close) auf, zeigt derselbe Mini-Chart automatisch auch die Kursbewegung der 5 Minuten nach dem Exit mit.
- Dafür musste der Fälligkeits-Check von einem separaten 15-Sekunden-Timer nach `OnCalculate` verschoben werden — `BuildMiniChart`/`GetCandle` sind nur im Indikator-Callback-Kontext sicher aufrufbar, nicht im Timer-Thread. Neuer Timer entfällt damit.
- Fallback: kann kein Chart gebaut werden (z. B. zu wenige Kerzen), geht wie bisher eine reine Textnachricht raus.
- Betrifft `TradeRecapIndicator.cs`.

## [260821] — 2026-08-20

### Neu — Exit-Check 5 Minuten nach Trade-Close
- Neue Klasse `PostTradeEvaluator.cs` (plattformunabhängig, wie `CardRenderer.cs`): trackt nach jedem Trade-Close 5 Minuten lang die Kursbewegung ab dem Exit-Preis — sowohl weiter in die ursprüngliche Trade-Richtung (`RunFavorable`, verpasster Gewinn) als auch dagegen (`RunAdverse`, bestätigt den Exit).
- Speist sich aus denselben Hooks wie das bestehende MAE/MFE-Tracking (`OnNewTrade` für Live-Ticks, `OnCalculate`/`GetCandle` als Kerzen-Sicherheitsnetz), läuft aber unabhängig von der aktuell offenen Position weiter.
- Neuer 15-Sekunden-Timer (`CheckPostTradeEvaluationsAsync`) prüft auf fällige Auswertungen und verschickt bei Fälligkeit eine eigenständige Telegram-Textnachricht (`TelegramSender.SendMessageAsync` + `BuildExitVerdict`), z. B. "🟡 Zu früh raus — Kurs lief danach 12 Ticks weiter in Trade-Richtung" oder "🟢 Guter Exit — Kurs drehte danach 9 Ticks gegen die Trade-Richtung".
- Schwelle für eine "deutliche" Bewegung ist relativ zur eigenen MAE/MFE-Range des Trades (50%), mit einem Boden von 3 Ticks, damit sehr enge Trades nicht bei jedem Rauschen ausschlagen.
- Bekannte Lücke: keine Persistenz über einen Neustart von ATAS — schließt der Indikator zwischen Exit und Fälligkeit neu, geht die ausstehende Auswertung verloren.
- Betrifft `PostTradeEvaluator.cs` (neu), `TelegramSender.cs`, `TradeRecapIndicator.cs`.

## [260820] — 2026-08-20

### Fix — Min/Max Ticks verpassten schnelle Kursbewegungen
- **Problem:** `MAE`/`MFE` ("Min/Max Ticks" auf der Karte) wurden ausschließlich aus dem Live-Tick-Stream berechnet (`OnNewTrade`). Bei schnellen Bewegungen kann dieser Stream einzelne Ticks auslassen — der MiniChart (aus den regulären Kerzendaten) zeigte dann einen deutlich größeren Ausschlag, als die Karte als Min/Max Ticks auswies (in einem beobachteten Fall: ~14 Ticks angezeigt bei tatsächlich ~220 Ticks laut Kerzen-Docht).
- **Fix:** Neue Methode `PositionTracker.UpdateMAEMFEFromBar(high, low, barTime)` prüft zusätzlich bei jeder Kerzen-Aktualisierung `High`/`Low` der aktuell laufenden Kerze gegen den Einstiegspreis — die Kerzen-Engine der Plattform verpasst nie einen Preis, im Gegensatz zum Tick-Stream. Aufgerufen aus `OnCalculate` über `GetCandle(bar)`.
- Betrifft `PositionTracker.cs`, `TradeRecapIndicator.cs`.

## [260803] — 2026-07-31

### Behoben
- **Recap-Karte (CardRenderer) zeigte weiterhin Ø-Preis bei EINSTIEG/AUSSTIEG:** Der Fix aus 260802 deckte nur die Telegram-Caption und das MiniChart ab — die eigentliche gerenderte Karte (`CardRenderer.cs`, die als Foto verschickt wird) hatte ihre eigenen `EINSTIEG`/`AUSSTIEG`-Felder, die weiterhin `AvgEntryPrice`/`AvgExitPrice` zeigten. Jetzt zeigt auch die Karte den tatsächlichen ersten/letzten Fill-Preis.

## [260802] — 2026-07-31

### Behoben
- **Entry/Exit zeigten Ø-Preis statt tatsächlichem Fill-Preis:** Bei Trades mit Scale-In/Scale-Out zeigten `Entry:`/`Exit:` (Caption) sowie die `ENTRY`/`EXIT`-Linien und Haupt-Pfeile (MiniChart) bisher den mengengewichteten Durchschnittspreis statt des Preises, bei dem der Trade tatsächlich gestartet/beendet wurde. Jetzt zeigen beide den echten ersten Open-Fill- bzw. letzten Close-Fill-Preis; die Caption ergänzt bei mehreren Fills zusätzlich den Ø-Preis in Klammern (z. B. `Entry: ... @ 28504.50 (Ø 28500.56)`), damit der Bezug zur PnL-Berechnung (die weiterhin auf dem Ø-Preis basiert) nachvollziehbar bleibt.

## [260801] — 2026-07-31

### Entfernt
- **ATAS X komplett entfernt:** `TradeRecapX.csproj`, `CardRendererSkia.cs` und `ScreenshotHelper.cs` gelöscht (SkiaSharp-Abhängigkeit damit weg). Der Indikator existiert nur noch als Classic-ATAS-Build (`TradeRecap.csproj`, Windows). `CardRenderer.cs` läuft jetzt bedingungslos (kein `#if !ATASX`-Wrapper mehr).

## [260731] — 2026-07-31

### Neu
- **Scale-In/Scale-Out sichtbar im MiniChart:** Jeder Nachkauf (Scale-In) und jeder Teilverkauf (Scale-Out) innerhalb eines Trades bekommt jetzt einen eigenen Pfeil im generierten Mini-Chart — kleiner (26px statt 44px) und transparenter (~47% Deckkraft) als die Haupt-Entry-/Exit-Pfeile, positioniert am tatsächlichen Fill-Preis/-Zeitpunkt. Mehrere Fills im selben Balken werden per kleinem Rechts-Versatz auseinandergezogen, damit sie nicht komplett übereinanderliegen.
- **Fill-Aufschlüsselung in der Telegram-Caption:** Bei Trades mit mehr als einem Open- oder Close-Fill zeigt die Caption zusätzlich eine `Fills:`-Zeile (z. B. `Fills: +1@15000.00, +1@15200.00 → -1@15300.00, -1@15400.00`). Einfache Trades ohne Scale-In/-Out bleiben unverändert ohne diese Zeile.

### Hinweis
- Die zugrunde liegende Trade-Erkennung (`PositionTracker`) berechnete Scale-In/Scale-Out bereits vorher korrekt (mengengewichteter Ø-Entry/Ø-Exit, korrekter Gesamt-PnL) — diese Änderung ist rein visuell/informativ, keine Änderung an der PnL-Logik.

## [260629] — 2026-06-18

### Geändert
- **MiniChart Kerzen-Fenster:** Minimum ist 100 Kerzen. Zusätzlich wird sichergestellt, dass vor der Entry-Kerze mindestens 30 Kerzen sichtbar sind — bei langen Trades wird das Fenster entsprechend nach hinten erweitert.

---

## [260628] — 2026-06-18

### Neu
- **Entry/Exit-Pfeile im MiniChart:** Grüner Pfeil ↑ für Long-Entry / Short-Exit, roter Pfeil ↓ für Short-Entry / Long-Exit. Pfeile sind als GDI+-Vektoren direkt in der DLL eingebettet — keine externen PNG-Dateien nötig. Feste Größe (44 px) unabhängig von der Anzahl angezeigter Kerzen.

### Behoben
- **Timezone-Bug MiniChart:** Entry/Exit-Bar wurde nie gefunden (`entryIdx = -1`) weil Kerzenzeiten (UTC→Lokalzeit konvertiert) gegen Trade-Zeiten (UTC, `DateTimeKind.Unspecified`) verglichen wurden. Resultat: kein Zone-Highlight, keine Pfeile. Behoben durch konsistente UTC→Local-Konvertierung beider Seiten in `MiniChartRenderer`.

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
