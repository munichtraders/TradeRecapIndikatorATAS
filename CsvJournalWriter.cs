using System.IO;

namespace MunichTraders.TradeRecap;

public class CsvJournalWriter
{
    private string _path = "";
    private bool _initialized;

    private string? _checkinPath;
    private bool _checkinInitialized;

    private const string Header =
        "Date,Time,Symbol,Direction,Entry,Exit,Contracts,PnL_Points,PnL_USD," +
        "MAE_Points,MFE_Points,Duration_Seconds,TradeTag,DailyPnL_USD,TradesCount";

    private const string CheckinHeader = "Date,Time,TraderName,StateA,StateB,Ampel,Bias";

    public void Initialize(string path)
    {
        _path = path;
        _initialized = false;
    }

    public void AppendTrade(PositionRecord record, DailyStatsSnapshot stats)
    {
        if (string.IsNullOrWhiteSpace(_path)) return;

        try
        {
            bool fileExists = File.Exists(_path);
            using var writer = new StreamWriter(_path, append: true);

            if (!fileExists || !_initialized)
            {
                writer.WriteLine(Header);
                _initialized = true;
            }

            writer.WriteLine(string.Join(",",
                record.CloseTime.ToString("yyyy-MM-dd"),
                record.CloseTime.ToString("HH:mm:ss"),
                CsvEscape(record.Symbol),
                record.Direction == PositionDirection.Long ? "LONG" : "SHORT",
                record.AvgEntryPrice.ToString("F5"),
                record.AvgExitPrice.ToString("F5"),
                record.Contracts,
                record.PnlPoints.ToString("F4"),
                record.PnlUsd.ToString("F2"),
                record.MAE.ToString("F4"),
                record.MFE.ToString("F4"),
                ((int)record.Duration.TotalSeconds),
                CsvEscape(record.TradeTag),
                stats.DisplayPnl.ToString("F2"),
                stats.TradesCount
            ));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] CSV-Fehler: {ex.Message}");
        }
    }

    /// <summary>
    /// Sessioncheck-Antworten (Trader, Zustand, Bias) — eigene CSV-Datei, gleicher Ordner
    /// wie das Trade-Journal (Dateiname mit "_checkin"-Suffix vor der Endung abgeleitet),
    /// damit das bestehende Trade-Schema unangetastet bleibt.
    /// </summary>
    public void AppendCheckin(CheckinRecord record)
    {
        if (string.IsNullOrWhiteSpace(_path)) return;
        _checkinPath ??= DeriveCheckinPath(_path);

        try
        {
            bool fileExists = File.Exists(_checkinPath);
            using var writer = new StreamWriter(_checkinPath, append: true);

            if (!fileExists || !_checkinInitialized)
            {
                writer.WriteLine(CheckinHeader);
                _checkinInitialized = true;
            }

            writer.WriteLine(string.Join(",",
                record.Timestamp.ToString("yyyy-MM-dd"),
                record.Timestamp.ToString("HH:mm:ss"),
                CsvEscape(record.TraderName),
                CsvEscape(record.StateA),
                CsvEscape(record.StateB),
                record.Ampel,
                record.Bias
            ));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Checkin-CSV-Fehler: {ex.Message}");
        }
    }

    private static string DeriveCheckinPath(string tradePath)
    {
        string dir  = Path.GetDirectoryName(tradePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(tradePath);
        string ext  = Path.GetExtension(tradePath);
        return Path.Combine(dir, $"{name}_checkin{ext}");
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
