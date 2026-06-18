using ATAS.DataFeedsCore;
using ATAS.Indicators;

namespace MunichTraders.TradeRecap;

// Eigenes Enum — vermeidet Namenskonflikt mit ATAS.DataFeedsCore.TradeDirection
public enum PositionDirection { Long, Short }

public record FillInfo(decimal Price, int Qty, DateTime Time);

public class PositionRecord
{
    public string Symbol { get; set; } = "";
    public PositionDirection Direction { get; set; }
    public List<FillInfo> OpenFills { get; } = new();
    public List<FillInfo> CloseFills { get; } = new();
    public decimal AvgEntryPrice { get; set; }
    public decimal AvgExitPrice { get; set; }
    public DateTime OpenTime { get; set; }
    public DateTime CloseTime { get; set; }
    public int Contracts { get; set; }
    public decimal PnlPoints { get; set; }
    public decimal PnlUsd { get; set; }
    public decimal MAE { get; set; }
    public decimal MFE { get; set; }
    public string TradeTag { get; set; } = "";
    public string AccountId { get; set; } = "";
    // Tick-Daten aus Security beim ersten Fill — 0 bedeutet unbekannt (Fallback greift)
    public decimal TickSize { get; set; }
    public decimal TickCost { get; set; }
    public TimeSpan Duration => CloseTime - OpenTime;

    // Tick-basierte Werte (gerundet)
    public long PnlTicks  => TickSize > 0 ? (long)Math.Round(PnlPoints / TickSize) : (long)PnlPoints;
    public long MAETicks  => TickSize > 0 ? (long)Math.Round(MAE / TickSize) : (long)MAE;
    public long MFETicks  => TickSize > 0 ? (long)Math.Round(MFE / TickSize) : (long)MFE;

    // USD-Werte für MAE/MFE (gleiche Formel wie PnlUsd)
    public decimal MAEUsd => TickSize > 0 && TickCost > 0
        ? MAE / TickSize * TickCost * Contracts : 0m;
    public decimal MFEUsd => TickSize > 0 && TickCost > 0
        ? MFE / TickSize * TickCost * Contracts : 0m;


    public int OpenQty => OpenFills.Sum(f => f.Qty);
    public int CloseQty => CloseFills.Sum(f => f.Qty);

    public static decimal WeightedAvg(List<FillInfo> fills)
    {
        int totalQty = fills.Sum(f => f.Qty);
        if (totalQty == 0) return 0;
        return fills.Sum(f => f.Price * f.Qty) / totalQty;
    }
}

public class DailyStats
{
    public int TradesCount { get; private set; }
    public int Wins { get; private set; }
    public decimal TotalPnlToday { get; private set; }
    public DateTime LastResetDate { get; private set; } = DateTime.Today;

    public decimal WinRate => TradesCount == 0 ? 0 : (decimal)Wins / TradesCount * 100;

    public void ResetIfNewDay()
    {
        if (DateTime.Today != LastResetDate)
        {
            TradesCount = 0;
            Wins = 0;
            TotalPnlToday = 0;
            LastResetDate = DateTime.Today;
        }
    }

    public void AddTrade(PositionRecord record)
    {
        TradesCount++;
        TotalPnlToday += record.PnlUsd;
        if (record.PnlUsd > 0) Wins++;
    }

    public DailyStatsSnapshot Snapshot(decimal accountClosedPnl = 0m) =>
        new(TradesCount, TotalPnlToday, accountClosedPnl);
}

public record DailyStatsSnapshot(int TradesCount, decimal TotalPnlToday, decimal AccountClosedPnl)
{
    // Anzeigewert: Account-PnL wenn vorhanden, sonst berechneter Wert
    public decimal DisplayPnl => AccountClosedPnl != 0m ? AccountClosedPnl : TotalPnlToday;
};

public class PositionTracker
{
    private PositionRecord? _active;
    private readonly DailyStats _dailyStats;
    private string _pendingTag = "";

    public event Action<PositionRecord>? PositionClosed;

    public bool IsPositionOpen  => _active != null;
    public PositionRecord? ActiveRecord => _active;

    public PositionTracker(DailyStats dailyStats)
    {
        _dailyStats = dailyStats;
    }

    public void SetPendingTag(string tag) => _pendingTag = tag;

    public void ProcessFill(MyTrade trade)
    {
        // Korrekte ATAS API: trade.OrderDirection vom Typ OrderDirections (Buy/Sell)
        // trade.Time ist DateTime (nicht DateTimeOffset)
        // trade.Security?.Symbol liefert das Symbol
        bool isBuy = trade.OrderDirection == OrderDirections.Buy;
        var direction = isBuy ? PositionDirection.Long : PositionDirection.Short;
        var fill = new FillInfo(trade.Price, (int)trade.Volume, trade.Time);

        // ATAS 8.0.13+ / ATAS X 8.100+: Security.Symbol → Security.Code
        string symbol = trade.Security?.Code ?? trade.SecurityId ?? "";

        if (_active == null)
        {
            _active = new PositionRecord
            {
                Symbol    = symbol,
                Direction = direction,
                OpenTime  = fill.Time,
                MAE       = 0,
                MFE       = 0,
                TradeTag  = _pendingTag,
                AccountId = trade.AccountID ?? "",
                TickSize  = trade.Security?.TickSize ?? 0m,
                TickCost  = trade.Security?.TickCost ?? 0m,
            };
            _active.OpenFills.Add(fill);
            _active.AvgEntryPrice = fill.Price;
            _active.Contracts = fill.Qty;
            return;
        }

        if (direction == _active.Direction)
        {
            // Scale-In
            _active.OpenFills.Add(fill);
            _active.AvgEntryPrice = PositionRecord.WeightedAvg(_active.OpenFills);
            _active.Contracts = _active.OpenQty;
            return;
        }

        // Close (partial oder full)
        _active.CloseFills.Add(fill);
        int openQty = _active.OpenQty;
        int closeQty = _active.CloseQty;

        if (closeQty >= openQty)
        {
            FinalizeRecord(_active);

            if (closeQty > openQty)
            {
                // Flip: neue Position in Gegenrichtung mit Überschuss
                int excess = closeQty - openQty;
                var flipped = new PositionRecord
                {
                    Symbol = _active.Symbol,
                    Direction = direction,
                    OpenTime = fill.Time,
                    MAE = 0,
                    MFE = 0,
                    TradeTag = _pendingTag
                };
                flipped.OpenFills.Add(fill with { Qty = excess });
                flipped.AvgEntryPrice = fill.Price;
                flipped.Contracts = excess;
                _active = flipped;
            }
            else
            {
                _active = null;
                _pendingTag = "";
            }
        }
    }

    private void FinalizeRecord(PositionRecord record)
    {
        record.CloseTime = record.CloseFills.Last().Time;
        record.AvgExitPrice = PositionRecord.WeightedAvg(record.CloseFills);
        record.Contracts = record.OpenQty;

        decimal pointDiff = record.Direction == PositionDirection.Long
            ? record.AvgExitPrice - record.AvgEntryPrice
            : record.AvgEntryPrice - record.AvgExitPrice;

        record.PnlPoints = pointDiff;

        // PnlUsd wird erst im OnPositionClosed-Handler gesetzt (TickCost-Fallback im Indikator).
        // AddTrade MUSS DANACH aufgerufen werden — deshalb hier nur das Event feuern.
        PositionClosed?.Invoke(record);
    }

    /// <summary>
    /// Wird bei jedem Markt-Tick aufgerufen (OnNewTrade).
    /// Tracked live wie weit der Kurs seit dem Entry gegen/in Richtung des Trades lief.
    /// Kein Kerzen-Bezug → funktioniert auch für 5-Sekunden-Trades korrekt.
    /// </summary>
    public void UpdateMAEMFEFromTick(decimal price)
    {
        if (_active == null) return;

        decimal move = _active.Direction == PositionDirection.Long
            ? price - _active.AvgEntryPrice
            : _active.AvgEntryPrice - price;

        if (move < _active.MAE) _active.MAE = move;   // negativer = weiter gegen dich
        if (move > _active.MFE) _active.MFE = move;   // positiver = weiter in deine Richtung
    }
}
