using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Zeichnet jeden abgeschlossenen Trade auf und sendet eine gebrandete
/// Recap-Karte inklusive Chart-Screenshot an Telegram.
/// Zusätzlich: lokales CSV-Journal + Tages-Stats + MAE/MFE.
///
/// Build-Varianten:
///   Classic ATAS (Windows): TradeRecap.csproj  — kein ATASX-Symbol
///   ATAS X (cross-platform): TradeRecapX.csproj — ATASX-Symbol definiert
/// </summary>
[DisplayName("Trade Recap (Telegram)")]
[Category("Munich Traders")]
public class TradeRecapIndicator : Indicator
{
    // ── Telegram ──────────────────────────────────────────────────────────

    private string _botToken = "";
    private string _chatId = "";

    [Display(Name = "Bot Token", GroupName = "Telegram", Order = 1)]
    public string BotToken
    {
        get => _botToken;
        set => _botToken = value;
    }

    [Display(Name = "Chat ID", GroupName = "Telegram", Order = 2)]
    public string ChatId
    {
        get => _chatId;
        set => _chatId = value;
    }

    // ── Journal ───────────────────────────────────────────────────────────

    private string _csvPath = "";

    [Display(Name = "CSV-Pfad (z.B. C:\\Trading\\journal.csv)", GroupName = "Journal", Order = 1)]
    public string CsvPath
    {
        get => _csvPath;
        set
        {
            _csvPath = value;
            _csvWriter.Initialize(value);
        }
    }

    // ── Prop Firm ─────────────────────────────────────────────────────────

    private decimal _dailyDrawdownLimit;
    private decimal _accountBalance;

    [Display(Name = "Tages-Drawdown-Limit ($)", GroupName = "Prop Firm", Order = 1)]
    [Range(0, 1_000_000)]
    public decimal DailyDrawdownLimit
    {
        get => _dailyDrawdownLimit;
        set => _dailyDrawdownLimit = value;
    }

    [Display(Name = "Konto-Größe ($, Fallback)", GroupName = "Prop Firm", Order = 2,
        Description = "Wird automatisch aus dem ATAS-Konto gelesen. Nur ausfüllen wenn ATAS keinen Wert liefert.")]
    [Range(0, 10_000_000)]
    public decimal AccountBalance
    {
        get => _accountBalance;
        set => _accountBalance = value;
    }

    // ── Aktiver Trade ─────────────────────────────────────────────────────

    private string _tradeTag = "";

    /// <summary>
    /// Vor Trade-Schluss in der ATAS-UI eingeben (z.B. "FOMC Scalp").
    /// Wird beim nächsten Trade-Close übernommen und danach zurückgesetzt.
    /// KEIN RecalculateValues() — würde alle Bars neu berechnen.
    /// </summary>
    [Display(Name = "Trade-Tag", GroupName = "Aktiver Trade", Order = 1)]
    public string TradeTag
    {
        get => _tradeTag;
        set
        {
            _tradeTag = value;
            _positionTracker?.SetPendingTag(value);
        }
    }

    // ── Design ────────────────────────────────────────────────────────────

    private string _logoPath = "";

    [Display(Name = "Logo-Pfad (PNG)", GroupName = "Design", Order = 1)]
    public string LogoPath
    {
        get => _logoPath;
        set
        {
            _logoPath = value;
            _logoBytes = TryLoadLogo(value);  // sofort nachladen wenn Pfad gesetzt wird
        }
    }

    // ── Update ────────────────────────────────────────────────────────────

    [Display(Name = "Update installieren", GroupName = "Update", Order = 1,
        Description = "Lädt die neue Version herunter und installiert sie automatisch.")]
    public bool InstallUpdate
    {
        get => false;
        set
        {
            if (value && _updateVersion != null && _installStatus == 0)
                _ = DownloadAndInstallAsync();
        }
    }

    // ── Interne Felder ────────────────────────────────────────────────────

    private DailyStats _dailyStats = new();
    private PositionTracker _positionTracker = null!;
    private readonly CsvJournalWriter _csvWriter = new();
    private HttpClient _httpClient = null!;
    private byte[]? _logoBytes;

    // Geschlossene PnL aus ATAS-Account (wird via OnPortfolioChanged aktualisiert)
    private decimal _accountClosedPnl = 0m;

    private const string CurrentVersion = "260621";

    // 0 = unbekannt, 1 = verbunden, 2 = Fehler
    private volatile int _tgStatus;

    // null = aktuell, sonst neue Versionsnummer
    private string? _updateVersion;

    // 0 = bereit, 1 = lädt, 2 = installiert, 3 = Fehler
    private volatile int _installStatus;

    // Lazy initialisiert in DrawStatusPanel (static init von RenderFont kann fehlschlagen)
    private RenderFont? _statusFont;
    private static readonly Color  _colorGold   = Color.FromArgb(255, 184, 150, 72);
    private static readonly Color  _colorGreen  = Color.FromArgb(255, 34,  197, 94);
    private static readonly Color  _colorRed    = Color.FromArgb(255, 239, 68,  68);
    private static readonly Color  _colorYellow = Color.FromArgb(255, 245, 158, 11);
    private static readonly Color  _colorMuted  = Color.FromArgb(255, 120, 120, 120);
    private static readonly Color  _colorBg     = Color.FromArgb(210, 15,  15,  15);

    // ── Konstruktor ───────────────────────────────────────────────────────

    public TradeRecapIndicator() : base(true)
    {
        ((ValueDataSeries)DataSeries[0]).IsHidden = true;

        // Ohne diese zwei Zeilen wird OnRender niemals aufgerufen!
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    // ── Initialisierung ───────────────────────────────────────────────────

    protected override void OnInitialize()
    {
        _dailyStats = new DailyStats();
        _positionTracker = new PositionTracker(_dailyStats);
        _positionTracker.PositionClosed += OnPositionClosed;

        // HttpClient einmalig erstellen (Socket-Exhaustion vermeiden)
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _logoBytes = TryLoadLogo(_logoPath);

        if (!string.IsNullOrWhiteSpace(_csvPath))
            _csvWriter.Initialize(_csvPath);

        // Sofort und dann alle 60s Verbindung prüfen
        _ = CheckTelegramAsync();
        SubscribeToTimer(TimeSpan.FromSeconds(60), () => _ = CheckTelegramAsync());

        _ = CheckVersionAsync();
    }

    // ── Bar-Berechnung (MAE/MFE-Tracking) ────────────────────────────────

    protected override void OnCalculate(int bar, decimal value)
    {
        _dailyStats.ResetIfNewDay();
    }

    // Jeder Markt-Tick → live MaxTicks/MinTicks updaten (kein Kerzen-Bezug)
    protected override void OnNewTrade(MarketDataArg trade)
    {
        if (_positionTracker?.IsPositionOpen == true)
            _positionTracker.UpdateMAEMFEFromTick(trade.Price);
    }

    // ── Portfolio-Updates (geschlossene PnL aus ATAS-Account) ────────────

    protected override void OnPortfolioChanged(Portfolio portfolio)
    {
        base.OnPortfolioChanged(portfolio);
        try
        {
            // Kontogröße: Portfolio.Balance überschreibt den manuell eingetragenen Fallback-Wert
            if (portfolio?.Balance is decimal bal && bal > 0)
                _accountBalance = bal;

            // Geschlossene PnL: Property-Name variiert je nach ATAS-Version
            var type = portfolio?.GetType();
            foreach (string name in new[] { "RealizedProfit", "RealizedPnL", "ClosedPnL", "DayRealizedPnL", "CloseProfit" })
            {
                if (type?.GetProperty(name)?.GetValue(portfolio) is decimal val)
                {
                    _accountClosedPnl = val;
                    return;
                }
            }
        }
        catch { }
    }

    // ── Trade-Erkennung ───────────────────────────────────────────────────

    protected override void OnNewMyTrade(MyTrade trade)
    {
        base.OnNewMyTrade(trade);
        _positionTracker.ProcessFill(trade);
    }

    // ── Trade abgeschlossen → Screenshot + Karte + Telegram ──────────────

    private void OnPositionClosed(PositionRecord record)
    {
        // Tick-Daten: primär aus dem Trade-Fill (Security), Fallback statische Tabelle
        decimal tickSize = record.TickSize > 0 ? record.TickSize : GetTickSizeFallback(record.Symbol);
        decimal tickCost = record.TickCost > 0 ? record.TickCost : GetTickCostFallback(record.Symbol);
        // TickSize in Record aktualisieren damit PnlTicks-Properties korrekt berechnen
        if (record.TickSize == 0) record.TickSize = tickSize;
        record.PnlUsd = tickSize > 0 && tickCost > 0
            ? record.PnlPoints / tickSize * tickCost * record.Contracts
            : record.PnlPoints * record.Contracts;

        // DailyStats NACH PnlUsd-Berechnung updaten (vorher war es 0 → Bug)
        _dailyStats.AddTrade(record);

        _tradeTag = "";

        // Mini-Chart aus OHLC-Daten rendern (zuverlässiger als WPF-Screenshot)
        byte[]? chartBytes = BuildMiniChart(record);

        // Snapshots für Background-Thread (immutable)
        var recordSnapshot = record;
        var statsSnapshot  = _dailyStats.Snapshot(_accountClosedPnl);
        decimal ddLimit    = _dailyDrawdownLimit;
        decimal balance    = _accountBalance;
        byte[]? logoSnap   = _logoBytes;
        string botToken    = _botToken;
        string chatId      = _chatId;

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] cardBytes = CardRenderer.RenderCard(
                    recordSnapshot, statsSnapshot, logoSnap, chartBytes, ddLimit, balance);

                string caption = TelegramSender.BuildCaption(recordSnapshot, statsSnapshot);

                await TelegramSender.SendPhotoAsync(botToken, chatId, cardBytes, caption, _httpClient)
                    .ConfigureAwait(false);

                _csvWriter.AppendTrade(recordSnapshot, statsSnapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TradeRecap] Fehler: {ex.Message}");
            }
        });
    }

    // ── Status-Overlay ────────────────────────────────────────────────────

    private async Task CheckVersionAsync()
    {
        _updateVersion = await VersionChecker.CheckAsync(_httpClient, CurrentVersion).ConfigureAwait(false);
        if (_updateVersion != null) RedrawChart();
    }

    private async Task DownloadAndInstallAsync()
    {
        _installStatus = 1;
        RedrawChart();
        try
        {
            const string DllUrl =
                "https://github.com/munichtraders/TradeRecapIndikatorATAS/releases/latest/download/MunichTraders.TradeRecap.dll";

            string installPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ATAS", "Indicators", "MunichTraders.TradeRecap.dll");

            byte[] dllBytes = await _httpClient.GetByteArrayAsync(DllUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(installPath, dllBytes).ConfigureAwait(false);

            _updateVersion  = null;
            _installStatus  = 2;
        }
        catch
        {
            _installStatus = 3;
        }
        RedrawChart();
    }

    private async Task CheckTelegramAsync()
    {
        if (string.IsNullOrWhiteSpace(_botToken)) { _tgStatus = 0; RedrawChart(); return; }
        try
        {
            var r = await _httpClient
                .GetAsync($"https://api.telegram.org/bot{_botToken}/getMe")
                .ConfigureAwait(false);
            _tgStatus = r.IsSuccessStatusCode ? 1 : 2;
        }
        catch { _tgStatus = 2; }
        RedrawChart();
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (layout != DrawingLayouts.Final) return;
        try { DrawStatusPanel(context); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TradeRecap] OnRender Fehler: {ex.Message}");
        }
    }

    private void DrawStatusPanel(RenderContext context)
    {
        _statusFont ??= new RenderFont("Calibri", 10);

        const int LineH   = 20;
        const int PadX    = 8;
        const int PadY    = 6;
        bool hasUpdate    = _updateVersion != null || _installStatus > 0;
        // Breite nur beim langen Anleitungstext erweitern (installStatus==0 = noch nicht gestartet)
        int  PanelW       = (hasUpdate && _installStatus == 0) ? 480 : 240;
        // 4 Zeilen nur wenn Anleitungstext sichtbar, sonst 3 (kurze Status-Texte)
        int  lines        = (hasUpdate && _installStatus == 0) ? 4 : hasUpdate ? 3 : 2;
        int  PanelH       = LineH * lines + PadY * 2;

        var clip = context.ClipBounds;
        if (clip.Width < 100) return;   // kein sinnvoller Render-Bereich

        int panX = clip.Right - PanelW - 12;
        int panY = clip.Top   + 12;

        // Hauptfeld dunkel + schmale Gold-Akzentleiste oben (2px)
        var accentRect = new Rectangle(panX, panY, PanelW, 2);
        var bgRect     = new Rectangle(panX, panY + 2, PanelW, PanelH - 2);
        context.FillRectangle(_colorGold, accentRect);
        context.FillRectangle(_colorBg,   bgRect);

        // Zeile 1 — Telegram-Status
        string tgText;
        Color  tgColor;
        switch (_tgStatus)
        {
            case 1:  tgText = "TG  OK  Verbunden";             tgColor = _colorGreen;  break;
            case 2:  tgText = "TG  ERR  Token/ID prüfen";      tgColor = _colorRed;    break;
            default: tgText = "TG  ...  Prüfe Verbindung";     tgColor = _colorYellow; break;
        }
        context.DrawString(tgText, _statusFont, tgColor, panX + PadX, panY + PadY);

        // Zeile 2 — Trade-Status
        var active = _positionTracker?.ActiveRecord;
        string tradeText;
        Color  tradeColor;
        if (active != null)
        {
            string dir = active.Direction == PositionDirection.Long ? "LONG" : "SHORT";
            tradeText  = $"{dir}  {active.Contracts}K  @  {active.AvgEntryPrice:F2}";
            tradeColor = _colorGold;
        }
        else
        {
            tradeText  = "Kein Trade offen";
            tradeColor = _colorMuted;
        }
        context.DrawString(tradeText, _statusFont, tradeColor, panX + PadX, panY + PadY + LineH);

        if (hasUpdate)
        {
            string updateLine1 = _installStatus switch
            {
                1 => "⏳ Update wird heruntergeladen...",
                2 => "✓ Update installiert",
                3 => "⚠ Download fehlgeschlagen",
                _ => $"⬇ Update v{_updateVersion} verfügbar"
            };
            Color updateColor = _installStatus switch
            {
                2 => _colorGreen,
                3 => _colorRed,
                _ => _colorYellow
            };
            int textY = panY + PadY + LineH * 2;
            context.DrawString(updateLine1, _statusFont, updateColor, panX + PadX, textY);

            if (_installStatus == 0)
                context.DrawString(
                    "Indikatoren - Einst. TradeRecap (Telegram) - Haken bei installieren",
                    _statusFont, _colorMuted, panX + PadX, textY + LineH);
        }
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────────────

    /// <summary>
    /// Liest die letzten N Bars via GetCandle() und rendert daraus einen
    /// gebrandeten Mini-Candlestick-Chart mit Entry/Exit-Markierungen.
    /// </summary>
    private byte[]? BuildMiniChart(PositionRecord record)
    {
        // ATAS-Indexierung: GetCandle(0) = neuester Bar, GetCandle(CurrentBar) = ältester Bar
        // Höherer Index = weiter in der Vergangenheit
        const int PreEntryBars = 10;
        const int PostExitBars = 5;

        int totalBars = CurrentBar;
        if (totalBars < 2) return null;

        // Entry-Bar suchen: von Index 0 (neu) aufwärts bis wir den Bar finden,
        // dessen Zeit <= OpenTime ist (letzter Treffer = der Bar kurz vor/bei Entry)
        int entryBar = 0;
        for (int i = 0; i <= totalBars; i++)
        {
            try
            {
                if (GetCandle(i).Time <= record.OpenTime) { entryBar = i; break; }
            }
            catch { break; }
        }

        // Exit-Bar suchen: ab entryBar weiter aufwärts (in die Vergangenheit — falsch,
        // Exit ist nach Entry, also niedrigerer Index)
        int exitBar = entryBar;
        for (int i = entryBar; i >= 0; i--)
        {
            try
            {
                if (GetCandle(i).Time >= record.CloseTime) { exitBar = i; break; }
            }
            catch { break; }
        }

        // firstBar = älteste Kerze (höchster Index = PreEntryBars vor Entry)
        int firstBar = Math.Min(totalBars, entryBar + PreEntryBars);
        // lastBar = neueste Kerze (niedrigster Index = PostExitBars nach Exit)
        int lastBar  = Math.Max(0, exitBar - PostExitBars);

        // Kerzen von alt→neu einlesen: Index firstBar (älteste) bis lastBar (neueste)
        var candles = new List<CandleData>(firstBar - lastBar + 1);
        for (int i = firstBar; i >= lastBar; i--)
        {
            try
            {
                var c = GetCandle(i);
                candles.Add(new CandleData(c.Open, c.High, c.Low, c.Close, c.Volume, c.Time));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TradeRecap] GetCandle({i}) Fehler: {ex.Message}");
                break;
            }
        }

        if (candles.Count < 3) return null;

        try   { return MiniChartRenderer.Render(candles, record); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TradeRecap] MiniChart Fehler: {ex.Message}");
            return null;
        }
    }

    private static byte[]? TryLoadLogo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    private static decimal GetTickCostFallback(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "ES"  or "ESZ" or "ESM" or "ESH" or "ESU" => 12.50m,
            "NQ"  or "NQZ" or "NQM" or "NQH" or "NQU" => 5.00m,
            "CL"  or "CLZ" or "CLM" or "CLH" or "CLU" => 10.00m,
            "GC"  or "GCZ" or "GCM" or "GCH" or "GCU" => 10.00m,
            "MES"                                       => 1.25m,
            "MNQ"                                       => 0.50m,
            "MCL"                                       => 1.00m,
            "MGC"                                       => 1.00m,
            "RTY"                                       => 5.00m,
            "YM"                                        => 5.00m,
            _                                           => 1.00m,
        };

    private static decimal GetTickSizeFallback(string symbol) =>
        symbol.ToUpperInvariant() switch
        {
            "ES"  or "ESZ" or "ESM" or "ESH" or "ESU" => 0.25m,
            "NQ"  or "NQZ" or "NQM" or "NQH" or "NQU" => 0.25m,
            "CL"  or "CLZ" or "CLM" or "CLH" or "CLU" => 0.01m,
            "GC"  or "GCZ" or "GCM" or "GCH" or "GCU" => 0.10m,
            "MES"                                       => 0.25m,
            "MNQ"                                       => 0.25m,
            "MCL"                                       => 0.01m,
            "MGC"                                       => 0.10m,
            "RTY"                                       => 0.10m,
            "YM"                                        => 1.00m,
            _                                           => 1.00m,
        };

    // ── Cleanup ───────────────────────────────────────────────────────────

    // ATAS 8.0.13+ / ATAS X 8.100+: OnDispose(bool) aus Basisklasse entfernt — kein override möglich.
    protected void OnDispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient?.Dispose();
            if (_positionTracker != null)
                _positionTracker.PositionClosed -= OnPositionClosed;
        }
    }
}
