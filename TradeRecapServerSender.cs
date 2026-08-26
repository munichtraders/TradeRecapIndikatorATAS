using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Sendet jeden abgeschlossenen Trade als JSON an den Munich-Traders-Server,
/// der die Daten in die zentrale CSV (TradeRecapTrades.csv) anhaengt.
/// </summary>
internal static class TradeRecapServerSender
{
    internal static async Task SendAsync(
        string serverUrl,
        string authToken,
        PositionRecord record,
        DailyStatsSnapshot stats,
        string traderName,
        HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(authToken))
            return;

        var payload = new Dictionary<string, object?>
        {
            ["TraderName"]       = traderName,
            ["Date"]             = record.CloseTime.ToString("yyyy-MM-dd"),
            ["Time"]             = record.CloseTime.ToString("HH:mm:ss"),
            ["Symbol"]           = record.Symbol,
            ["Direction"]        = record.Direction == PositionDirection.Long ? "LONG" : "SHORT",
            ["Entry"]            = record.AvgEntryPrice.ToString("F5"),
            ["Exit"]             = record.AvgExitPrice.ToString("F5"),
            ["Contracts"]        = record.Contracts,
            ["PnL_Points"]       = record.PnlPoints.ToString("F4"),
            ["PnL_Ticks"]        = record.PnlTicks,
            ["PnL_USD"]          = record.PnlUsd.ToString("F2"),
            ["MAE_Points"]       = record.MAE.ToString("F4"),
            ["MAE_Ticks"]        = record.MAETicks,
            ["MAE_USD"]          = record.MAEUsd.ToString("F2"),
            ["MFE_Points"]       = record.MFE.ToString("F4"),
            ["MFE_Ticks"]        = record.MFETicks,
            ["MFE_USD"]          = record.MFEUsd.ToString("F2"),
            ["Duration_Seconds"] = (int)record.Duration.TotalSeconds,
            ["OpenTime"]         = record.OpenTime.ToString("O"),
            ["CloseTime"]        = record.CloseTime.ToString("O"),
            ["TradeTag"]         = record.TradeTag,
            ["AccountId"]        = record.AccountId,
            ["TickSize"]         = record.TickSize.ToString("F5"),
            ["TickCost"]         = record.TickCost.ToString("F5"),
            ["DailyPnL_USD"]     = stats.DisplayPnl.ToString("F2"),
            ["DailyTradesCount"] = stats.TradesCount,
        };

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, serverUrl) { Content = content };
            req.Headers.Add("X-Auth-Token", authToken);

            var response = await client.SendAsync(req).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.Error.WriteLine($"[TradeRecap] Server-Fehler {response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Server-Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Sendet eine Sessioncheck-Antwort (Trader, Zustandscheck, Bias) an den zentralen
    /// Server — eigener Endpunkt, von der Trade-URL abgeleitet (.../trade → .../checkin),
    /// damit kein zusätzliches Settings-Feld nötig ist.
    /// </summary>
    internal static async Task SendCheckinAsync(
        string serverUrl,
        string authToken,
        CheckinRecord record,
        HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(authToken))
            return;

        var payload = new Dictionary<string, object?>
        {
            ["TraderName"] = record.TraderName,
            ["Date"]       = record.Timestamp.ToString("yyyy-MM-dd"),
            ["Time"]       = record.Timestamp.ToString("HH:mm:ss"),
            ["StateA"]     = record.StateA,
            ["StateB"]     = record.StateB,
            ["Ampel"]      = record.Ampel.ToString(),
            ["Bias"]       = record.Bias,
        };

        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, DeriveCheckinUrl(serverUrl)) { Content = content };
            req.Headers.Add("X-Auth-Token", authToken);

            var response = await client.SendAsync(req).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.Error.WriteLine($"[TradeRecap] Checkin-Server-Fehler {response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Checkin-Server-Exception: {ex.Message}");
        }
    }

    private static string DeriveCheckinUrl(string tradeUrl) =>
        tradeUrl.Contains("/trade") ? tradeUrl.Replace("/trade", "/checkin") : tradeUrl.TrimEnd('/') + "/checkin";
}
