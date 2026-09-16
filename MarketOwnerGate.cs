using System.Collections.Concurrent;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Verhindert doppelte Trade-Verarbeitung, wenn derselbe Markt gleichzeitig in mehreren
/// Charts geladen ist: Nur die zuerst geladene Instanz pro Symbol verarbeitet Fills, baut
/// Karten und verschickt sie an Telegram/Server. Prozessweiter Zustand (statisch), nicht
/// über einen ATAS-Neustart hinweg persistiert — muss es auch nicht, da beim Neustart alle
/// Chart-Instanzen ohnehin gemeinsam neu geladen werden und der Claim wieder frei ist.
/// </summary>
internal static class MarketOwnerGate
{
    private static readonly ConcurrentDictionary<string, Guid> Owners = new();

    public static bool TryClaim(string symbol, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return true; // Symbol unbekannt -> nicht blockieren
        return Owners.TryAdd(symbol, instanceId);
    }

    public static void Release(string symbol, Guid instanceId)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        Owners.TryRemove(new KeyValuePair<string, Guid>(symbol, instanceId));
    }
}
