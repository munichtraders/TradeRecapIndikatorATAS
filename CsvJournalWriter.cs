using System.IO;

namespace MunichTraders.TradeRecap;

public class CsvJournalWriter
{
    private string _path = "";
    private bool _initialized;

    private const string Header =
        "Date,Time,Symbol,Direction,Entry,Exit,Contracts,PnL_Points,PnL_USD," +
        "MAE_Points,MFE_Points,Duration_Seconds,TradeTag,DailyPnL_USD,TradesCount";

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

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
