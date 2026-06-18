# Trade Recap — ATAS Indikator

**Von Munich Traders** · Automatische Trade-Dokumentation direkt aus ATAS

---

## Was macht der Indikator?

Nach jedem geschlossenen Trade rendert der Indikator automatisch eine gebrandete Recap-Karte und sendet sie per Telegram an deinen Kanal. Kein manuelles Screenshotten, kein Copy-Paste — der Trade ist dokumentiert, bevor du den nächsten eröffnest.

**Was auf der Karte steht:**
- Symbol, Richtung (Long/Short), Contracts, Einstiegs- und Ausstiegspreis
- PnL in Punkten und USD
- MAE / MFE (maximaler unrealisierter Verlust / Gewinn während des Trades)
- Trade-Dauer
- Optionaler Trade-Tag (z. B. "FOMC Scalp", "VWAP Reclaim")
- Tages-Stats: Anzahl Trades, Tages-PnL, Drawdown-Status
- Mini-Candlestick-Chart mit Entry- und Exit-Markierung
- Dein Logo (optional)

---

## Installation

1. Die Datei `MunichTraders.TradeRecap.dll` herunterladen
2. In den ATAS-Indikatorordner kopieren:
   ```
   %AppData%\ATAS\Indicators\
   ```
3. ATAS neu starten
4. Indikator in der Indikatoren-Liste unter **Munich Traders → Trade Recap (Telegram)** suchen und auf den Chart ziehen

---

## Einrichtung

### Telegram-Bot erstellen
1. In Telegram [@BotFather](https://t.me/BotFather) öffnen → `/newbot`
2. Bot-Token kopieren
3. Den Bot in deinen Kanal/deine Gruppe einladen und dort eine Nachricht schreiben
4. Chat-ID ermitteln: `https://api.telegram.org/bot<TOKEN>/getUpdates`

### Indikator-Einstellungen in ATAS

| Gruppe | Feld | Beschreibung |
|---|---|---|
| **Telegram** | Bot Token | Token vom BotFather |
| **Telegram** | Chat ID | ID deines Kanals oder privaten Chats |
| **Journal** | CSV-Pfad | Optionaler Pfad für lokales Trade-Journal (z. B. `C:\Trading\journal.csv`) |
| **Prop Firm** | Tages-Drawdown-Limit ($) | Wird als Warnschwelle auf der Karte angezeigt |
| **Prop Firm** | Konto-Größe ($) | Fallback, falls ATAS keinen Kontostand liefert |
| **Design** | Logo-Pfad (PNG) | Dein Logo, erscheint oben links auf der Karte |
| **Aktiver Trade** | Trade-Tag | Vor Trade-Schluss eintragen — wird auf der Karte angezeigt |

---

## Auto-Update

Der Indikator prüft beim Start und alle 60 Sekunden, ob eine neue Version verfügbar ist. Ist eine vorhanden, erscheint ein Hinweis im Status-Panel oben rechts im Chart.

**Update installieren:** In den Indikator-Einstellungen unter **Update → Update installieren** den Haken setzen. Die neue DLL wird automatisch heruntergeladen und installiert. Danach ATAS neu starten.

---

## Unterstützte Märkte

Tick-Größe und Tick-Wert werden automatisch aus dem ATAS-Trade-Fill gelesen. Als Fallback sind folgende Futures eingebaut:

| Symbol | Tick-Größe | Tick-Wert |
|---|---|---|
| ES | 0,25 | $12,50 |
| NQ | 0,25 | $5,00 |
| MES | 0,25 | $1,25 |
| MNQ | 0,25 | $0,50 |
| CL | 0,01 | $10,00 |
| GC | 0,10 | $10,00 |
| RTY | 0,10 | $5,00 |
| YM | 1,00 | $5,00 |

Alle anderen Symbole (CFDs, Krypto etc.) werden mit Tick-Wert 1:1 berechnet, sofern ATAS keinen Wert liefert.

---

## Build-Varianten

| Projekt | Ziel-Plattform |
|---|---|
| `TradeRecap.csproj` | Classic ATAS (Windows) |
| `TradeRecapX.csproj` | ATAS X (Cross-Platform) |

---

## Versionsschema

Versionen folgen dem Format `YYMMDD` (z. B. `260618` = 18. Juni 2026).

---

## Lizenz & Hinweis

Dieser Indikator ist ein Community-Tool von Munich Traders und wird kostenlos bereitgestellt. Er dient ausschließlich der Dokumentation und sendet keine Handelssignale. Trading mit Futures und CFDs ist mit erheblichen Risiken verbunden.
