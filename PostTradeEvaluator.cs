namespace MunichTraders.TradeRecap;

/// <summary>
/// Eine noch offene Nach-Trade-Auswertung: verfolgt, wie sich der Kurs in den
/// ersten Minuten nach dem Exit weiterbewegt hat.
/// </summary>
public class PendingEvaluation
{
    public PositionRecord Record { get; }
    public DateTime DueTimeUtc { get; }

    // Beste Bewegung seit dem Exit IN Trade-Richtung (>= 0) — "liegen gelassener" Gewinn.
    public decimal RunFavorable { get; private set; }
    // Größte Bewegung seit dem Exit GEGEN die Trade-Richtung (<= 0) — bestätigt den Exit.
    public decimal RunAdverse { get; private set; }

    public PendingEvaluation(PositionRecord record, TimeSpan delay)
    {
        Record = record;
        DueTimeUtc = DateTime.SpecifyKind(record.CloseTime, DateTimeKind.Utc).Add(delay);
    }

    public void UpdateWithPrice(decimal price)
    {
        decimal move = Record.Direction == PositionDirection.Long
            ? price - Record.AvgExitPrice
            : Record.AvgExitPrice - price;

        if (move > RunFavorable) RunFavorable = move;
        if (move < RunAdverse)   RunAdverse   = move;
    }
}

/// <summary>
/// Sammelt abgeschlossene Trades ein und wertet 5 Minuten nach dem Exit aus,
/// ob der Ausstiegszeitpunkt im Nachhinein gut war (Kurs dreht) oder ob noch
/// Gewinn liegen gelassen wurde (Kurs läuft weiter in die ursprüngliche Richtung).
/// Plattformunabhängig — hängt nur von PositionRecord (decimal/DateTime) ab,
/// keine ATAS- oder Quantower-Typen, daher 1:1 auf beide Plattformen kopierbar.
/// </summary>
public class PostTradeEvaluator
{
    private readonly List<PendingEvaluation> _pending = new();
    private readonly object _lock = new();

    public bool HasPending
    {
        get { lock (_lock) return _pending.Count > 0; }
    }

    public void Add(PositionRecord record, TimeSpan delay)
    {
        lock (_lock) _pending.Add(new PendingEvaluation(record, delay));
    }

    public void UpdateFromTick(decimal price)
    {
        lock (_lock)
        {
            foreach (var p in _pending) p.UpdateWithPrice(price);
        }
    }

    /// <summary>
    /// Sicherheitsnetz analog zu PositionTracker.UpdateMAEMFEFromBar: prüft zusätzlich
    /// High/Low der laufenden Kerze, falls der Live-Tick-Stream einzelne Ticks verpasst.
    /// </summary>
    public void UpdateFromBar(decimal high, decimal low, DateTime barTimeUtc)
    {
        lock (_lock)
        {
            foreach (var p in _pending)
            {
                if (barTimeUtc < DateTime.SpecifyKind(p.Record.CloseTime, DateTimeKind.Utc)) continue;
                p.UpdateWithPrice(low);
                p.UpdateWithPrice(high);
            }
        }
    }

    /// <summary>Gibt alle fälligen Auswertungen zurück und entfernt sie aus der Warteliste.</summary>
    public List<PendingEvaluation> PopDue(DateTime nowUtc)
    {
        lock (_lock)
        {
            var due = _pending.FindAll(p => nowUtc >= p.DueTimeUtc);
            foreach (var d in due) _pending.Remove(d);
            return due;
        }
    }
}
