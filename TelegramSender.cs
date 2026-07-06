using System.Net.Http;

namespace MunichTraders.TradeRecap;

public static class TelegramSender
{
    private const string ApiBase = "https://api.telegram.org/bot";

    public static async Task SendPhotoAsync(
        string botToken,
        string chatId,
        byte[] imageBytes,
        string caption,
        HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            return;

        string url = $"{ApiBase}{botToken}/sendPhoto";

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(chatId), "chat_id");
        content.Add(new ByteArrayContent(imageBytes), "photo", "trade_recap.png");
        content.Add(new StringContent(caption), "caption");
        content.Add(new StringContent("HTML"), "parse_mode");

        try
        {
            var response = await client.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.Error.WriteLine($"[TradeRecap] Telegram Fehler {response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Telegram Exception: {ex.Message}");
        }
    }

    public static string BuildCaption(PositionRecord record, DailyStatsSnapshot stats, string traderName = "")
    {
        bool isProfit = record.PnlUsd >= 0;
        string emoji = isProfit ? "🟢" : "🔴";
        string dir   = record.Direction == PositionDirection.Long ? "LONG" : "SHORT";
        string sign  = isProfit ? "+" : "";

        var lines = new List<string>();

        lines.Add($"{emoji} <b>{record.Symbol} {dir}</b>");

        if (!string.IsNullOrWhiteSpace(traderName))
            lines.Add($"👤 <b>{traderName}</b>");

        lines.AddRange(new[]
        {
            $"P&amp;L: <b>{sign}{record.PnlUsd:F2} $ ({sign}{record.PnlTicks} Ticks)</b>",
            $"Entry: {record.OpenTime:HH:mm:ss} @ {record.AvgEntryPrice:F2}",
            $"Exit:  {record.CloseTime:HH:mm:ss} @ {record.AvgExitPrice:F2}",
            $"Kontrakte: {record.Contracts}  |  Dauer: {FormatDuration(record.Duration)}",
            $"Min: {record.MAETicks:+0;-0} Ticks ({record.MAEUsd:+0.00;-0.00} $)  |  Max: {record.MFETicks:+0;-0} Ticks ({record.MFEUsd:+0.00;-0.00} $)",
        });

        if (!string.IsNullOrWhiteSpace(record.TradeTag))
            lines.Add($"Tag: <i>{record.TradeTag}</i>");

        if (!string.IsNullOrWhiteSpace(record.AccountId))
        {
            string maskedId = record.AccountId.Length > 4
                ? record.AccountId[..4] + new string('*', record.AccountId.Length - 4)
                : record.AccountId;
            lines.Add($"Konto: <i>{maskedId}</i>");
        }

        lines.Add("");
        lines.Add($"📊 Heute: {(stats.DisplayPnl >= 0 ? "+" : "")}{stats.DisplayPnl:F2} $  |  Trades: {stats.TradesCount}");
        lines.Add($"<i>Munich Traders · {DateTime.Now:dd.MM.yyyy HH:mm} CET</i>");

        return string.Join("\n", lines);
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.Seconds}s";
        if (d.TotalHours < 1)  return $"{d.Minutes}m {d.Seconds:D2}s";
        return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
    }
}
