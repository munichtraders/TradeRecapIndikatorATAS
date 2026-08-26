using System.Net.Http;
using System.Text.Json;

namespace MunichTraders.TradeRecap;

/// <summary>Ein einzelner Button-Tap (callback_query) aus Telegrams getUpdates-Antwort.</summary>
public sealed record TelegramUpdate(
    long UpdateId,
    string CallbackQueryId,
    long MessageId,
    long FromUserId,
    string FromUserName,
    string Data);

/// <summary>
/// Kurzes Short-Polling über Telegrams getUpdates-API (timeout=0, kein blockierendes
/// Long-Polling) — passt damit ins bestehende Timer-getriebene Muster dieses Indikators
/// statt einen Request offen zu halten. Der Offset lebt nur im Arbeitsspeicher; ein
/// Neustart der Plattform verliert ihn (gleiche akzeptierte Lücke wie beim
/// PostTradeEvaluator — der Sessioncheck fängt in dem Fall einfach neu von vorne an).
/// </summary>
public sealed class TelegramUpdatePoller
{
    private const string ApiBase = "https://api.telegram.org/bot";
    private long _offset;

    public async Task<List<TelegramUpdate>> PollAsync(string botToken, HttpClient client)
    {
        var result = new List<TelegramUpdate>();
        if (string.IsNullOrWhiteSpace(botToken))
            return result;

        string url = $"{ApiBase}{botToken}/getUpdates?offset={_offset}&timeout=0";

        try
        {
            using var response = await client.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return result;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var updatesEl))
                return result;

            foreach (var update in updatesEl.EnumerateArray())
            {
                long updateId = update.GetProperty("update_id").GetInt64();
                _offset = updateId + 1; // vor jedem Tap-Filter setzen, damit auch nicht-relevante Updates nicht erneut geliefert werden

                if (!update.TryGetProperty("callback_query", out var cq))
                    continue;

                string callbackQueryId = cq.GetProperty("id").GetString() ?? "";
                string data = cq.TryGetProperty("data", out var dataEl) ? dataEl.GetString() ?? "" : "";

                var from = cq.GetProperty("from");
                long fromUserId = from.GetProperty("id").GetInt64();
                string fromUserName = from.TryGetProperty("first_name", out var nameEl) ? nameEl.GetString() ?? "" : "";

                long messageId = cq.TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("message_id", out var midEl)
                    ? midEl.GetInt64()
                    : 0;

                result.Add(new TelegramUpdate(updateId, callbackQueryId, messageId, fromUserId, fromUserName, data));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TradeRecap] Telegram getUpdates Exception: {ex.Message}");
        }

        return result;
    }
}
