using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using OFT.Rendering;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Zeichnet jeden abgeschlossenen Trade auf und sendet eine gebrandete
/// Recap-Karte inklusive Chart-Screenshot an Telegram.
/// Zusätzlich: lokales CSV-Journal + Tages-Stats + MAE/MFE.
/// </summary>
[DisplayName("Trade Recap (Telegram)")]
[Category("Munich Traders")]
public class TradeRecapIndicator : Indicator
{
    // ── Telegram ──────────────────────────────────────────────────────────

    private string _botToken = "7800685401:AAEnsF6E4dtm-4pUO-yjgiRjDQyHvOkoT64";
    private string _chatId = "-4946993985";

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

    // ── Server-Journal (zentrale CSV auf dem Munich-Traders-Server) ────────

    private string _serverUrl = "http://187.124.10.151:9878/trade";
    private string _serverToken = "5029729378e17dfb4284a2e75855f2ca3bd1a8a95805c94c";

    [Display(Name = "Server-URL (z.B. http://SERVER:9878/trade)", GroupName = "Server-Journal", Order = 1,
        Description = "Jeder abgeschlossene Trade wird zusaetzlich an diesen Server gesendet und dort in einer zentralen CSV gesammelt.")]
    public string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = value;
    }

    [Display(Name = "Server-Token", GroupName = "Server-Journal", Order = 2)]
    public string ServerToken
    {
        get => _serverToken;
        set => _serverToken = value;
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

    private TraderIdentity _traderIdentity = TraderIdentity.Martin;

    [Display(Name = "Trader-Name", GroupName = "Design", Order = 2,
        Description = "Default-Trader, falls der Sessioncheck im Panel (noch) nicht bestätigt wurde.")]
    public TraderIdentity TraderIdentity
    {
        get => _traderIdentity;
        set => _traderIdentity = value;
    }

    // Vom Sessioncheck im Panel bestätigter Name für die laufende Session — hat Vorrang
    // vor der Settings-Auswahl oben, sobald der Fragebogen abgeschlossen ist.
    private string? _sessionTraderName;

    // Frei verschiebbares Status-Panel: Position + Einklapp-Zustand werden mit dem
    // Chart-Template gespeichert (Browsable(false) blendet sie nur aus dem Settings-Dialog
    // aus, ändert aber nichts an der normalen Property-Serialisierung).
    private int  _panelX = int.MinValue;  // int.MinValue = noch nie verschoben -> Standardposition oben rechts
    private int  _panelY = int.MinValue;
    private bool _panelCollapsed;

    [Display(Name = "Panel X", GroupName = "Design", Order = 3)]
    [Browsable(false)]
    public int PanelPositionX { get => _panelX; set => _panelX = value; }

    [Display(Name = "Panel Y", GroupName = "Design", Order = 4)]
    [Browsable(false)]
    public int PanelPositionY { get => _panelY; set => _panelY = value; }

    [Display(Name = "Panel eingeklappt", GroupName = "Design", Order = 5)]
    [Browsable(false)]
    public bool PanelCollapsed { get => _panelCollapsed; set => _panelCollapsed = value; }

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
    private readonly PostTradeEvaluator _postTradeEvaluator = new();
    private readonly CsvJournalWriter _csvWriter = new();
    private HttpClient _httpClient = null!;
    private byte[]? _logoBytes;

    // Start-Fragebogen (Trader bestätigen, Zustandscheck, Bias) — wird direkt im Panel per
    // Mausklick ausgefüllt (siehe PanelCheckinFlow, DrawStatusPanel, ProcessMouseDown). Läuft
    // nur in der Instanz, die den Flow über CheckinGate beansprucht hat (siehe OnInitialize) —
    // andere Instanzen übernehmen das Ergebnis passiv aus dem Gate.
    private readonly PanelCheckinFlow _panelCheckin = new();
    private bool _ownsCheckinFlow;
    // True sobald diese Instanz beim Start feststellt, dass ein neuer Sessioncheck fällig ist.
    // Zeigt den Fällig-Hinweis im Panel, bis der Trader ihn ausfüllt oder wegklickt.
    private bool _checkinDue;
    // Trader hat den Fällig-Hinweis (oder den laufenden Fragebogen) weggeklickt — Panel zeigt
    // für den Rest dieser Chart-Session keinen Checkin-Hinweis mehr. Gate bleibt offen, ein
    // Neuladen fragt also erneut.
    private bool _checkinPromptDismissed;
    // Aktuell gültige Ampel-Warnung fürs Panel — unabhängig davon, ob sie aus dem eigenen
    // Fragebogen, dem Cache (CheckinGate) oder einer anderen Instanz stammt.
    private AmpelColor? _activeAmpel;

    // Verhindert doppelte Trade-Verarbeitung, wenn derselbe Markt in mehreren Charts
    // gleichzeitig offen ist (siehe MarketOwnerGate). Nur die Owner-Instanz verarbeitet Fills.
    private readonly Guid _instanceId = Guid.NewGuid();
    private string _symbol = "";
    private bool _isMarketOwner = true;

    // Geschlossene PnL aus ATAS-Account (wird via OnPortfolioChanged aktualisiert)
    private decimal _accountClosedPnl = 0m;
    // Zählt jedes OnPortfolioChanged-Update — damit OnPositionClosed erkennen kann,
    // ob der PnL-Wert für DIESEN Trade schon angekommen ist (ATAS meldet ihn leicht verzögert)
    private int _portfolioUpdateSeq = 0;

    // Zeitstempel des Indikator-Starts — historische Trades davor werden nicht verschickt
    private DateTime _initTime;

    // Fingerprints bereits verschickter Trades — ATAS liefert beim Schließen/Neuladen
    // manchmal die Fills der Session nochmal durch OnNewMyTrade (Replay). Ohne diese
    // Sperre würden dann alle Trades des Tages ein zweites Mal an Telegram gehen.
    private readonly HashSet<string> _sentTradeKeys = new();

    private const string CurrentVersion = "260918";

    // 0 = unbekannt, 1 = verbunden, 2 = Fehler
    private volatile int _tgStatus;

    // null = aktuell, sonst neue Versionsnummer
    private string? _updateVersion;

    // 0 = bereit, 1 = lädt, 2 = installiert, 3 = Fehler
    private volatile int _installStatus;

    // Lazy initialisiert in DrawStatusPanel (static init von RenderFont kann fehlschlagen)
    private RenderFont? _statusFont;
    private RenderFont? _titleFont;
    private RenderFont? _smallFont;
    private RenderFont? _avatarFont;
    private System.Drawing.Image? _logoImage;     // aus _logoBytes, lazy für den Avatar-Badge
    private bool _logoImageLoadAttempted;
    private static readonly Color  _colorGold      = Color.FromArgb(255, 184, 150, 72);
    private static readonly Color  _colorGreen     = Color.FromArgb(255, 34,  197, 94);
    private static readonly Color  _colorRed       = Color.FromArgb(255, 239, 68,  68);
    private static readonly Color  _colorYellow    = Color.FromArgb(255, 245, 158, 11);
    private static readonly Color  _colorMuted     = Color.FromArgb(255, 120, 120, 120);
    private static readonly Color  _colorBg        = Color.FromArgb(210, 15,  15,  15);
    private static readonly Color  _colorCardBg    = Color.FromArgb(238, 17,  16,  15);
    private static readonly Color  _colorBorder    = Color.FromArgb(255, 48,  43,  34);
    private static readonly Color  _colorAvatarBg  = Color.FromArgb(255, 28,  26,  22);
    private static readonly Color  _colorTextPrime = Color.FromArgb(255, 236, 232, 222);
    private static readonly Color  _colorTextMuted = Color.FromArgb(255, 152, 147, 138);

    // Geometrie des zuletzt gezeichneten Panels — für Hit-Tests bei Maus-Events.
    private Rectangle _lastPanelRect;
    private Rectangle _lastHeaderRect;
    private Rectangle _lastChevronRect;

    // Drag-Zustand für das frei verschiebbare Panel.
    private bool  _isDraggingPanel;
    private Point _dragMouseStart;
    private Point _dragPanelStart;

    // Geometrie der Checkin-Bedienelemente im Panel — pro Frame in DrawStatusPanel neu
    // befüllt, in ProcessMouseDown für die Hit-Tests gelesen.
    private Rectangle _lastCheckinStartRect;
    private Rectangle _lastCheckinLaterRect;
    private Rectangle _lastCheckinCancelRect;
    private readonly List<Rectangle> _lastCheckinOptionRects = new();

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
        _initTime    = DateTime.UtcNow;
        _dailyStats  = new DailyStats();
        _positionTracker = new PositionTracker(_dailyStats);
        _positionTracker.PositionClosed += OnPositionClosed;

        // HttpClient einmalig erstellen (Socket-Exhaustion vermeiden)
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        _logoBytes = TryLoadLogo(_logoPath);

        if (!string.IsNullOrWhiteSpace(_csvPath))
            _csvWriter.Initialize(_csvPath);

        // Nur eine Instanz pro Symbol verarbeitet Trades — verhindert doppelte Recap-Karten,
        // wenn derselbe Markt in mehreren Charts gleichzeitig offen ist.
        _symbol = InstrumentInfo?.Instrument ?? "";
        _isMarketOwner = MarketOwnerGate.TryClaim(_symbol, _instanceId);

        // Sofort und dann alle 60s Verbindung prüfen
        _ = CheckTelegramAsync();
        SubscribeToTimer(TimeSpan.FromSeconds(60), () => _ = CheckTelegramAsync());

        _ = CheckVersionAsync();

        // Nur die Instanz, die CheckinGate als Fragesteller beansprucht (kein aktuelles
        // Ergebnis vorhanden, keine andere Instanz fragt gerade), zeigt den Fällig-Hinweis im
        // Panel. Andere Instanzen übernehmen ein Ergebnis passiv über das 3s-Polling unten.
        _ownsCheckinFlow = CheckinGate.TryClaimFlow(out var cachedCheckin);
        if (_ownsCheckinFlow)
        {
            _checkinDue = true;
        }
        else if (cachedCheckin != null)
        {
            _sessionTraderName = cachedCheckin.TraderName;
            _activeAmpel = cachedCheckin.Ampel is AmpelColor.Yellow or AmpelColor.Red ? cachedCheckin.Ampel : null;
        }
        SubscribeToTimer(TimeSpan.FromSeconds(3), PollCheckinCache);
    }

    /// <summary>
    /// Rein lokal (keine Telegram-Calls mehr nötig, der Fragebogen läuft im Panel) — prüft nur,
    /// ob eine andere Instanz (anderer Chart, gleicher ATAS-Prozess) den Sessioncheck inzwischen
    /// fertig ausgefüllt hat, damit diese Instanz das Ergebnis für ihre Ampel-Warnung übernimmt.
    /// </summary>
    private void PollCheckinCache()
    {
        if (_ownsCheckinFlow || _sessionTraderName != null) return;
        try
        {
            if (CheckinGate.TryGetValid(out var record) && record != null)
            {
                _sessionTraderName = record.TraderName;
                _activeAmpel = record.Ampel is AmpelColor.Yellow or AmpelColor.Red ? record.Ampel : null;
                RedrawChart();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TradeRecap] Sessioncheck-Cache-Poll Fehler: {ex.Message}");
        }
    }

    /// <summary>
    /// Wird aufgerufen, sobald der Trader im Panel die letzte Frage (Bias) beantwortet hat.
    /// Speichert das Ergebnis, hängt es ans CSV-/Server-Journal an und schickt EINE Telegram-
    /// Nachricht mit der fertigen Antwort (kein Hin-und-Her mehr über Telegram).
    /// </summary>
    private void FinalizePanelCheckin(CheckinRecord result)
    {
        _sessionTraderName = result.TraderName;
        _activeAmpel = result.Ampel is AmpelColor.Yellow or AmpelColor.Red ? result.Ampel : null;
        CheckinGate.Save(result);
        _csvWriter.AppendCheckin(result);
        _ = TradeRecapServerSender.SendCheckinAsync(_serverUrl, _serverToken, result, _httpClient);
        _ = TelegramSender.SendMessageAsync(_botToken, _chatId, PanelCheckinFlow.BuildAnswerSummary(result), _httpClient);

        _panelCheckin.Reset();
        _checkinDue = false;
    }

    // ── Bar-Berechnung (MAE/MFE-Tracking) ────────────────────────────────

    protected override void OnCalculate(int bar, decimal value)
    {
        _dailyStats.ResetIfNewDay();

        // Sicherheitsnetz: Kerzen-High/Low der laufenden Kerze zusätzlich zum
        // Live-Tick-Stream prüfen (siehe UpdateMAEMFEFromBar in PositionTracker.cs)
        if (_positionTracker?.IsPositionOpen == true)
        {
            try
            {
                var candle = GetCandle(bar);
                _positionTracker.UpdateMAEMFEFromBar(candle.High, candle.Low, candle.Time);
            }
            catch { /* Kerze evtl. noch nicht verfügbar */ }
        }

        // Offene Exit-Checks laufen unabhängig davon weiter, ob gerade eine neue
        // Position offen ist — GetCandle(bar).Time ist wie oben UTC-wertig.
        if (_postTradeEvaluator.HasPending)
        {
            try
            {
                var candle = GetCandle(bar);
                var barTimeUtc = DateTime.SpecifyKind(candle.Time, DateTimeKind.Utc);
                _postTradeEvaluator.UpdateFromBar(candle.High, candle.Low, barTimeUtc);
            }
            catch { /* Kerze evtl. noch nicht verfügbar */ }

            // Fällige Auswertungen (5 Minuten seit Close um) verschicken. Muss hier in
            // OnCalculate laufen, nicht im Timer-Thread — BuildMiniChart braucht GetCandle,
            // und das ist nur im Indikator-Callback-Kontext sicher aufrufbar.
            foreach (var eval in _postTradeEvaluator.PopDue(DateTime.UtcNow))
                SendExitVerdict(eval);
        }
    }

    // Jeder Markt-Tick → live MaxTicks/MinTicks updaten (kein Kerzen-Bezug)
    protected override void OnNewTrade(MarketDataArg trade)
    {
        if (_positionTracker?.IsPositionOpen == true)
            _positionTracker.UpdateMAEMFEFromTick(trade.Price);

        if (_postTradeEvaluator.HasPending)
            _postTradeEvaluator.UpdateFromTick(trade.Price);
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
                    Interlocked.Increment(ref _portfolioUpdateSeq);
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
        // Passive Instanz (gleicher Markt bereits in einem anderen Chart aktiv) verarbeitet
        // keine Fills — siehe MarketOwnerGate.
        if (!_isMarketOwner) return;
        _positionTracker.ProcessFill(trade);
    }

    // ── Trade abgeschlossen → Screenshot + Karte + Telegram ──────────────

    private void OnPositionClosed(PositionRecord record)
    {
        // Historische Trades beim Chart-Reload ignorieren
        if (DateTime.SpecifyKind(record.CloseTime, DateTimeKind.Utc) < _initTime) return;

        // Replay-Schutz: denselben Trade nicht zweimal verschicken (z. B. wenn ATAS beim
        // Schließen die Session-Fills nochmal durch OnNewMyTrade schickt)
        string tradeKey = $"{record.Symbol}|{record.Direction}|{record.OpenTime:O}|{record.CloseTime:O}|{record.AvgEntryPrice}|{record.AvgExitPrice}|{record.Contracts}";
        if (!_sentTradeKeys.Add(tradeKey)) return;

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
        _postTradeEvaluator.Add(recordSnapshot, TimeSpan.FromMinutes(5));
        int seqAtClose     = Volatile.Read(ref _portfolioUpdateSeq);
        decimal ddLimit    = _dailyDrawdownLimit;
        decimal balance    = _accountBalance;
        byte[]? logoSnap   = _logoBytes;
        string botToken    = _botToken;
        string chatId      = _chatId;
        string traderName  = _sessionTraderName ?? _traderIdentity.ToString();
        string serverUrl   = _serverUrl;
        string serverToken = _serverToken;

        _ = Task.Run(async () =>
        {
            try
            {
                // ATAS meldet den geschlossenen Konto-PnL dieses Trades erst mit leichter
                // Verzögerung über OnPortfolioChanged. Ohne diese Wartezeit würde die
                // Tages-P&L noch den Stand VOR diesem Trade zeigen (ein Trade "hinterher").
                var deadline = DateTime.UtcNow.AddMilliseconds(2000);
                while (Volatile.Read(ref _portfolioUpdateSeq) == seqAtClose && DateTime.UtcNow < deadline)
                    await Task.Delay(100).ConfigureAwait(false);

                var statsSnapshot = _dailyStats.Snapshot(_accountClosedPnl);

                byte[] cardBytes = CardRenderer.RenderCard(
                    recordSnapshot, statsSnapshot, logoSnap, chartBytes, ddLimit, balance, traderName);

                string caption = TelegramSender.BuildCaption(recordSnapshot, statsSnapshot, traderName);

                await TelegramSender.SendPhotoAsync(botToken, chatId, cardBytes, caption, _httpClient)
                    .ConfigureAwait(false);

                _csvWriter.AppendTrade(recordSnapshot, statsSnapshot);

                await TradeRecapServerSender.SendAsync(
                    serverUrl, serverToken, recordSnapshot, statsSnapshot, traderName, _httpClient)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// Baut den Mini-Chart für eine fällige Exit-Auswertung (zeigt Entry, Exit und die
    /// Kursbewegung seither — BuildMiniChart fenstert immer bis zum aktuellen Bar, deshalb
    /// reicht der Aufruf, um die 5 Minuten nach dem Exit automatisch mit abzubilden) und
    /// verschickt ihn zusammen mit dem Bewertungstext per Telegram.
    /// </summary>
    private void SendExitVerdict(PendingEvaluation eval)
    {
        byte[]? chartBytes = BuildMiniChart(eval.Record);
        string caption = TelegramSender.BuildExitVerdict(eval);

        string botToken = _botToken;
        string chatId    = _chatId;

        _ = Task.Run(async () =>
        {
            try
            {
                if (chartBytes != null)
                    await TelegramSender.SendPhotoAsync(botToken, chatId, chartBytes, caption, _httpClient)
                        .ConfigureAwait(false);
                else
                    await TelegramSender.SendMessageAsync(botToken, chatId, caption, _httpClient)
                        .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TradeRecap] Exit-Check Fehler: {ex.Message}");
            }
        });
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

    private readonly record struct StatusRow(Color Dot, string Text, Color TextColor);

    private const int PanelPadX     = 10;
    private const int PanelRowH     = 20;
    private const int PanelHeaderH  = 34;
    private const int PanelAccentH  = 3;
    private const int PanelAvatarSz = 24;
    private const int PanelRadius   = 10;

    private void DrawStatusPanel(RenderContext context)
    {
        _statusFont ??= new RenderFont("Calibri", 10);
        _titleFont  ??= new RenderFont("Calibri", 11, FontStyle.Bold);
        _smallFont  ??= new RenderFont("Calibri", 8);
        _avatarFont ??= new RenderFont("Calibri", 10, FontStyle.Bold);
        if (!_logoImageLoadAttempted)
        {
            _logoImageLoadAttempted = true;
            _logoImage = TryLoadLogoImage(_logoBytes);
        }

        var clip = context.ClipBounds;
        if (clip.Width < 100) return;   // kein sinnvoller Render-Bereich

        _lastCheckinStartRect  = Rectangle.Empty;
        _lastCheckinLaterRect  = Rectangle.Empty;
        _lastCheckinCancelRect = Rectangle.Empty;
        _lastCheckinOptionRects.Clear();
        _lastChevronRect = Rectangle.Empty;

        bool showDuePrompt = _ownsCheckinFlow && _checkinDue && !_checkinPromptDismissed
            && _panelCheckin.Step == CheckinStep.NotStarted;
        bool showWizard = _ownsCheckinFlow && _checkinDue && !_checkinPromptDismissed
            && _panelCheckin.Step != CheckinStep.NotStarted && _panelCheckin.Step != CheckinStep.Completed;

        if (showWizard)
            DrawCheckinWizard(context, clip);
        else
            DrawNormalPanel(context, clip, showDuePrompt);
    }

    private void DrawNormalPanel(RenderContext context, Rectangle clip, bool showDuePrompt)
    {
        // ── Zeilen zusammenstellen ───────────────────────────────────────
        var rows = new List<StatusRow>();

        if (!_isMarketOwner)
        {
            rows.Add(new StatusRow(_colorMuted,
                "Passiv – aktiv in anderem Chart", _colorTextMuted));
        }
        else
        {
            (Color dot, string text) tg = _tgStatus switch
            {
                1 => (_colorGreen,  "Telegram verbunden"),
                2 => (_colorRed,    "Telegram: Token/ID prüfen"),
                _ => (_colorYellow, "Telegram: Verbindung wird geprüft"),
            };
            rows.Add(new StatusRow(tg.dot, tg.text, _colorTextPrime));

            var active = _positionTracker?.ActiveRecord;
            if (active != null)
            {
                string dir = active.Direction == PositionDirection.Long ? "LONG" : "SHORT";
                rows.Add(new StatusRow(_colorGold,
                    $"{dir}  {active.Contracts}K  @ {active.AvgEntryPrice:F2}", _colorTextPrime));
            }
            else
            {
                rows.Add(new StatusRow(_colorMuted, "Kein Trade offen", _colorTextMuted));
            }
        }

        if (_activeAmpel is AmpelColor.Yellow or AmpelColor.Red)
        {
            bool isRed = _activeAmpel == AmpelColor.Red;
            rows.Add(new StatusRow(
                isRed ? _colorRed : _colorYellow,
                isRed ? "Kein Trading heute (Zustandscheck)" : "Risiko halbieren (Zustandscheck)",
                isRed ? _colorRed : _colorYellow));
        }

        bool hasUpdate       = _updateVersion != null || _installStatus > 0;
        bool showInstallHint = hasUpdate && _installStatus == 0;
        if (hasUpdate)
        {
            (Color dot, string text) upd = _installStatus switch
            {
                1 => (_colorYellow, "Update wird heruntergeladen..."),
                2 => (_colorGreen,  "Update installiert"),
                3 => (_colorRed,    "Download fehlgeschlagen"),
                _ => (_colorYellow, $"Update v{_updateVersion} verfügbar"),
            };
            rows.Add(new StatusRow(upd.dot, upd.text, upd.dot));
            if (showInstallHint)
                rows.Add(new StatusRow(_colorMuted,
                    "Einst. TradeRecap → Haken bei \"Update installieren\"", _colorTextMuted));
        }

        // ── Geometrie ────────────────────────────────────────────────────
        int maxTextLen = rows.Count == 0 ? 0 : rows.Max(r => r.Text.Length);
        int panelW = Math.Clamp(maxTextLen * 7 + 60, 230, 460);
        int promptH = showDuePrompt ? PanelRowH + 28 : 0;
        int bodyH  = _panelCollapsed ? 0 : rows.Count * PanelRowH + 8 + promptH;
        int footerH = _panelCollapsed ? 0 : 16;
        int panelH  = PanelHeaderH + bodyH + footerH;

        var (panX, panY) = ComputePanelPosition(clip, panelW, panelH);
        _lastPanelRect  = new Rectangle(panX, panY, panelW, panelH);
        _lastHeaderRect = new Rectangle(panX, panY, panelW, PanelHeaderH);

        DrawPanelChrome(context, panX, panY, panelW, panelH, showChevron: true);

        if (_panelCollapsed) return;

        context.DrawLine(new RenderPen(_colorBorder), panX + PanelPadX, panY + PanelHeaderH, panX + panelW - PanelPadX, panY + PanelHeaderH);

        // ── Statuszeilen ─────────────────────────────────────────────────
        int y = panY + PanelHeaderH + 6;
        foreach (var row in rows)
        {
            var dotRect = new Rectangle(panX + PanelPadX, y + 6, 7, 7);
            context.FillEllipse(row.Dot, dotRect);
            context.DrawString(row.Text, _statusFont!, row.TextColor, panX + PanelPadX + 14, y);
            y += PanelRowH;
        }

        // ── Sessioncheck fällig? ─────────────────────────────────────────
        if (showDuePrompt)
        {
            context.DrawString("Sessioncheck fällig", _statusFont!, _colorGold, panX + PanelPadX, y);
            y += PanelRowH;

            const int btnW = 74, btnH = 20, gap = 8;
            _lastCheckinStartRect = new Rectangle(panX + PanelPadX, y, btnW, btnH);
            _lastCheckinLaterRect = new Rectangle(panX + PanelPadX + btnW + gap, y, btnW, btnH);
            DrawPillButton(context, _lastCheckinStartRect, "Start", _colorGold, filled: true);
            DrawPillButton(context, _lastCheckinLaterRect, "Später", _colorTextMuted, filled: false);
            y += btnH + 8;
        }

        // ── Footer ───────────────────────────────────────────────────────
        context.DrawLine(new RenderPen(_colorBorder), panX + PanelPadX, y, panX + panelW - PanelPadX, y);
        context.DrawString($"Munich Traders  ·  v{CurrentVersion}", _smallFont!, _colorTextMuted, panX + PanelPadX, y + 3);
    }

    private void DrawCheckinWizard(RenderContext context, Rectangle clip)
    {
        var (question, options) = _panelCheckin.Step switch
        {
            CheckinStep.AwaitingTrader  => ("Wer bist du?",
                PanelCheckinFlow.Traders.Select(t => (_colorGold, t)).ToArray()),
            CheckinStep.AwaitingStateA  => ("Allgemeiner Zustand?",
                PanelCheckinFlow.StateListA.Select(s => (AmpelDotColor(s.Ampel), s.Label)).ToArray()),
            CheckinStep.AwaitingStateB  => ("Zustand bezüglich Trading?",
                PanelCheckinFlow.StateListB.Select(s => (AmpelDotColor(s.Ampel), s.Label)).ToArray()),
            CheckinStep.AwaitingBias    => ("Bias für heute?",
                PanelCheckinFlow.BiasOptions.Select(b => (_colorGold, b)).ToArray()),
            _ => ("", Array.Empty<(Color, string)>()),
        };
        int stepIndex = _panelCheckin.Step switch
        {
            CheckinStep.AwaitingTrader => 1, CheckinStep.AwaitingStateA => 2,
            CheckinStep.AwaitingStateB => 3, CheckinStep.AwaitingBias   => 4, _ => 0,
        };

        int maxTextLen = Math.Max(question.Length, options.Length == 0 ? 0 : options.Max(o => o.Item2.Length));
        int panelW = Math.Clamp(maxTextLen * 6 + 56, 260, 520);
        int bodyH  = PanelRowH /* Frage */ + options.Length * PanelRowH + PanelRowH /* Abbrechen */ + 10;
        int panelH = PanelHeaderH + bodyH;

        var (panX, panY) = ComputePanelPosition(clip, panelW, panelH);
        _lastPanelRect  = new Rectangle(panX, panY, panelW, panelH);
        _lastHeaderRect = new Rectangle(panX, panY, panelW, PanelHeaderH);

        DrawPanelChrome(context, panX, panY, panelW, panelH, showChevron: false,
            subtitleOverride: $"Sessioncheck · Schritt {stepIndex}/4");

        context.DrawLine(new RenderPen(_colorBorder), panX + PanelPadX, panY + PanelHeaderH, panX + panelW - PanelPadX, panY + PanelHeaderH);

        int y = panY + PanelHeaderH + 6;
        context.DrawString(question, _statusFont!, _colorTextPrime, panX + PanelPadX, y);
        y += PanelRowH + 2;

        foreach (var (dot, label) in options)
        {
            var rowRect = new Rectangle(panX + PanelPadX, y, panelW - 2 * PanelPadX, PanelRowH - 2);
            _lastCheckinOptionRects.Add(rowRect);
            context.FillRectangle(_colorAvatarBg, rowRect, 5);
            context.FillEllipse(dot, new Rectangle(rowRect.X + 6, rowRect.Y + 6, 7, 7));
            context.DrawString(label, _statusFont!, _colorTextPrime, rowRect.X + 18, rowRect.Y + 1);
            y += PanelRowH;
        }

        y += 4;
        _lastCheckinCancelRect = new Rectangle(panX + PanelPadX, y, 90, 18);
        DrawPillButton(context, _lastCheckinCancelRect, "Abbrechen", _colorTextMuted, filled: false);
    }

    private static Color AmpelDotColor(AmpelColor a) => a switch
    {
        AmpelColor.Green  => _colorGreen,
        AmpelColor.Yellow => _colorYellow,
        AmpelColor.Red    => _colorRed,
        _ => _colorMuted,
    };

    private void DrawPanelChrome(RenderContext context, int panX, int panY, int panelW, int panelH, bool showChevron, string? subtitleOverride = null)
    {
        context.FillRectangle(_colorBorder, new Rectangle(panX - 1, panY - 1, panelW + 2, panelH + 2), PanelRadius + 1);
        context.FillRectangle(_colorCardBg, new Rectangle(panX, panY, panelW, panelH), PanelRadius);
        context.FillRectangle(_colorGold,   new Rectangle(panX, panY, panelW, PanelAccentH), PanelRadius);

        var avatarRect = new Rectangle(panX + PanelPadX, panY + PanelAccentH + (PanelHeaderH - PanelAccentH - PanelAvatarSz) / 2, PanelAvatarSz, PanelAvatarSz);
        context.FillRectangle(_colorAvatarBg, avatarRect, PanelAvatarSz / 2);
        if (_logoImage != null)
        {
            const int inset = 4;
            context.DrawStaticImage(_logoImage, new Rectangle(avatarRect.X + inset, avatarRect.Y + inset, PanelAvatarSz - 2 * inset, PanelAvatarSz - 2 * inset));
        }
        else
        {
            var centered = new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            context.DrawString("MT", _avatarFont!, _colorGold, avatarRect, centered);
        }

        int textX = avatarRect.Right + 8;
        context.DrawString("Munich Traders", _titleFont!, _colorTextPrime, textX, panY + PanelAccentH + 3);
        string subtitle = subtitleOverride ?? (string.IsNullOrEmpty(_symbol)
            ? "Trade Recap"
            : $"{_symbol} · {(_isMarketOwner ? "aktiv" : "passiv")}");
        context.DrawString(subtitle, _smallFont!, _colorTextMuted, textX, panY + PanelAccentH + 18);

        if (!showChevron) return;

        int chevCx = panX + panelW - PanelPadX - 6;
        int chevCy = panY + PanelHeaderH / 2;
        _lastChevronRect = new Rectangle(chevCx - 10, chevCy - 10, 20, 20);
        Point[] tri = _panelCollapsed
            ? new[] { new Point(chevCx - 3, chevCy - 5), new Point(chevCx - 3, chevCy + 5), new Point(chevCx + 4, chevCy) }
            : new[] { new Point(chevCx - 5, chevCy - 3), new Point(chevCx + 5, chevCy - 3), new Point(chevCx, chevCy + 4) };
        context.FillPolygon(_colorTextMuted, tri);
    }

    private void DrawPillButton(RenderContext context, Rectangle rect, string text, Color accent, bool filled)
    {
        if (filled)
        {
            context.FillRectangle(accent, rect, rect.Height / 2);
            context.DrawString(text, _statusFont!, Color.FromArgb(255, 20, 18, 15),
                rect, new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        else
        {
            context.FillRectangle(_colorAvatarBg, rect, rect.Height / 2);
            context.DrawString(text, _statusFont!, accent, rect,
                new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
    }

    private (int panX, int panY) ComputePanelPosition(Rectangle clip, int panelW, int panelH)
    {
        int defaultX = clip.Right - panelW - 12;
        int defaultY = clip.Top   + 12;
        int panX = _panelX == int.MinValue ? defaultX : _panelX;
        int panY = _panelY == int.MinValue ? defaultY : _panelY;
        // innerhalb des sichtbaren Chart-Bereichs halten, auch nach einem Resize
        panX = Math.Max(clip.Left, Math.Min(panX, clip.Right  - 60));
        panY = Math.Max(clip.Top,  Math.Min(panY, clip.Bottom - PanelHeaderH));
        return (panX, panY);
    }

    private static System.Drawing.Image? TryLoadLogoImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return System.Drawing.Image.FromStream(new MemoryStream(bytes)); }
        catch { return null; }
    }

    // ── Panel-Interaktion (Drag & Einklappen) ────────────────────────────

    public override bool ProcessMouseDown(RenderControlMouseEventArgs e)
    {
        if (_lastCheckinStartRect.Contains(e.Location))
        {
            _panelCheckin.Start();
            RedrawChart();
            return true;
        }
        if (_lastCheckinLaterRect.Contains(e.Location))
        {
            _checkinPromptDismissed = true;
            RedrawChart();
            return true;
        }
        if (_lastCheckinCancelRect.Contains(e.Location))
        {
            _panelCheckin.Reset();
            _checkinPromptDismissed = true;
            RedrawChart();
            return true;
        }
        for (int i = 0; i < _lastCheckinOptionRects.Count; i++)
        {
            if (!_lastCheckinOptionRects[i].Contains(e.Location)) continue;

            switch (_panelCheckin.Step)
            {
                case CheckinStep.AwaitingTrader: _panelCheckin.ChooseTrader(i); break;
                case CheckinStep.AwaitingStateA: _panelCheckin.ChooseStateA(i); break;
                case CheckinStep.AwaitingStateB: _panelCheckin.ChooseStateB(i); break;
                case CheckinStep.AwaitingBias:   _panelCheckin.ChooseBias(i);   break;
            }
            if (_panelCheckin.Result != null)
                FinalizePanelCheckin(_panelCheckin.Result);

            RedrawChart();
            return true;
        }
        if (_lastChevronRect.Contains(e.Location))
        {
            _panelCollapsed = !_panelCollapsed;
            RedrawChart();
            return true;
        }
        if (_lastHeaderRect.Contains(e.Location))
        {
            _isDraggingPanel = true;
            _dragMouseStart  = e.Location;
            _dragPanelStart  = new Point(_lastPanelRect.X, _lastPanelRect.Y);
            return true;
        }
        return base.ProcessMouseDown(e);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_isDraggingPanel)
        {
            _panelX = _dragPanelStart.X + (e.Location.X - _dragMouseStart.X);
            _panelY = _dragPanelStart.Y + (e.Location.Y - _dragMouseStart.Y);
            RedrawChart();
            return true;
        }
        return base.ProcessMouseMove(e);
    }

    public override bool ProcessMouseUp(RenderControlMouseEventArgs e)
    {
        if (_isDraggingPanel)
        {
            _isDraggingPanel = false;
            return true;
        }
        return base.ProcessMouseUp(e);
    }

    public override StdCursor GetCursor(RenderControlMouseEventArgs e)
    {
        if (_isDraggingPanel || _lastHeaderRect.Contains(e.Location))
            return StdCursor.SizeAll;
        return base.GetCursor(e);
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────────────

    private byte[]? BuildMiniChart(PositionRecord record)
    {
        const int MinCandleCount   = 100;  // Mindestanzahl sichtbarer Kerzen
        const int MinCandlesBefore = 30;   // Mindestvorlauf vor der Entry-Kerze

        int newest = CurrentBar;
        if (newest < 2) return null;

        // Entry-Bar rückwärts suchen (gleiche Zeitlogik wie MiniChartRenderer)
        DateTime entryLocal = DateTime.SpecifyKind(record.OpenTime, DateTimeKind.Utc).ToLocalTime();
        int entryBarIdx = -1;
        for (int i = newest; i >= Math.Max(0, newest - 500); i--)
        {
            try
            {
                var barTime = DateTime.SpecifyKind(GetCandle(i).Time, DateTimeKind.Utc).ToLocalTime();
                if (barTime <= entryLocal) { entryBarIdx = i; break; }
            }
            catch { break; }
        }

        // Basis-Fenster: 100 Kerzen ab aktuellem Bar
        int firstBar = Math.Max(0, newest - MinCandleCount + 1);

        // Fenster nach hinten erweitern wenn Entry-Vorlauf < 30 Kerzen
        if (entryBarIdx >= 0 && entryBarIdx - firstBar < MinCandlesBefore)
            firstBar = Math.Max(0, entryBarIdx - MinCandlesBefore);

        var candles = new List<CandleData>(newest - firstBar + 1);
        for (int i = firstBar; i <= newest; i++)
        {
            try
            {
                var c         = GetCandle(i);
                // ATAS liefert Time als Unspecified mit UTC-Wert — erst als UTC markieren, dann konvertieren
                var localTime = DateTime.SpecifyKind(c.Time, DateTimeKind.Utc).ToLocalTime();
                candles.Add(new CandleData(c.Open, c.High, c.Low, c.Close, c.Volume, localTime));
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
        // 1. Nutzerpfad (überschreibt Standard-Logo)
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { return File.ReadAllBytes(path); }
            catch { }
        }
        // 2. Eingebettetes Standard-Logo als Fallback
        return LoadEmbeddedLogo();
    }

    private static byte[]? LoadEmbeddedLogo()
    {
        try
        {
            var asm  = typeof(TradeRecapIndicator).Assembly;
            string resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("munich-traders-logo.png", StringComparison.OrdinalIgnoreCase))
                ?? "";
            if (string.IsNullOrEmpty(resourceName)) return null;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
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

    // Korrekter Hook ist BaseIndicator.OnDispose() (kein bool-Parameter, per Reflection gegen
    // ATAS.Indicators.dll verifiziert) — die alte Signatur OnDispose(bool) war kein Override
    // und wurde von ATAS nie aufgerufen (HttpClient/Event-Unsubscribe liefen also nie).
    protected override void OnDispose()
    {
        _httpClient?.Dispose();
        if (_positionTracker != null)
            _positionTracker.PositionClosed -= OnPositionClosed;
        if (_isMarketOwner)
            MarketOwnerGate.Release(_symbol, _instanceId);
        if (_ownsCheckinFlow)
            CheckinGate.ReleaseFlow();
        _logoImage?.Dispose();
    }
}
