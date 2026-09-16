using System.IO;
using System.Text.Json;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Sorgt dafür, dass der Start-Fragebogen (PanelCheckinFlow) nicht bei jedem
/// Chart-Neuladen erneut losläuft: Ergebnis wird prozessweit + auf Disk zwischengespeichert
/// und gilt als aktuell, solange derselbe Tag UND weniger als 4 Stunden seit der Abfrage
/// vergangen sind (deckt Vormittags-/Nachmittagssession ab). Zusätzlich verhindert ein
/// In-Process-Claim, dass zwei gleichzeitig ladende Chart-Instanzen parallel zwei Fragebögen
/// an Telegram schicken.
/// </summary>
internal static class CheckinGate
{
    private static readonly TimeSpan ReaskInterval = TimeSpan.FromHours(4);
    private static readonly object Lock = new();

    // Bewusst plattformneutraler Pfad (nicht "ATAS\Indicators") — falls beide Plattformen auf
    // derselben Maschine laufen, teilen sie sich denselben Tages-Sessioncheck.
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MunichTraders", "TradeRecap_checkin_state.json");

    private static bool _flowOwnerClaimed;

    /// <summary>
    /// True → der Aufrufer soll den Fragebogen starten (StartAsync). False + validCached=null →
    /// eine andere Instanz fragt gerade (einfach per TryGetValid weiter pollen). False +
    /// validCached!=null → Ergebnis ist noch aktuell genug, kein neuer Fragebogen nötig.
    /// </summary>
    public static bool TryClaimFlow(out CheckinRecord? validCached)
    {
        lock (Lock)
        {
            var onDisk = LoadFromDisk();
            if (onDisk != null && IsStillValid(onDisk.Timestamp))
            {
                validCached = onDisk;
                return false;
            }

            validCached = null;
            if (_flowOwnerClaimed) return false;

            _flowOwnerClaimed = true;
            return true;
        }
    }

    public static void ReleaseFlow()
    {
        lock (Lock) { _flowOwnerClaimed = false; }
    }

    public static bool TryGetValid(out CheckinRecord? record)
    {
        lock (Lock)
        {
            record = LoadFromDisk();
            return record != null && IsStillValid(record.Timestamp);
        }
    }

    public static void Save(CheckinRecord record)
    {
        lock (Lock)
        {
            _flowOwnerClaimed = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(record));
            }
            catch { /* Zwischenspeicher ist best-effort — schlimmstenfalls fragt der nächste Reload erneut */ }
        }
    }

    private static bool IsStillValid(DateTime ts) =>
        ts.Date == DateTime.Today && (DateTime.Now - ts) < ReaskInterval;

    private static CheckinRecord? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize<CheckinRecord>(File.ReadAllText(StatePath));
        }
        catch { return null; }
    }
}
