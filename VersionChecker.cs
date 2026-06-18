using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MunichTraders.TradeRecap;

internal static class VersionChecker
{
    // GitHub Raw URL zur version.json — nach dem ersten Deploy anpassen:
    // https://raw.githubusercontent.com/DEIN_GITHUB_USER/DEIN_REPO/main/TradeRecap/version.json
    internal const string VersionUrl =
        "https://raw.githubusercontent.com/munichtraders/TradeRecapIndikatorATAS/main/version.json";

    /// <summary>
    /// Gibt die neue Versionsnummer zurück falls ein Update verfügbar ist, sonst null.
    /// Version-Format YYMMDD — größere Zahl = neuere Version.
    /// </summary>
    internal static async Task<string?> CheckAsync(HttpClient http, string currentVersion)
    {
        try
        {
            using var cts  = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            string    json = await http.GetStringAsync(VersionUrl, cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var vEl))
            {
                string remote = vEl.GetString() ?? "";
                if (int.TryParse(remote,         out int r) &&
                    int.TryParse(currentVersion,  out int c) &&
                    r > c)
                    return remote;
            }
        }
        catch { /* keine Verbindung oder ungültiges JSON → still ignorieren */ }

        return null;
    }
}
