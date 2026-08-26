using System.Net.Http;

namespace MunichTraders.TradeRecap;

public enum CheckinStep { NotStarted, AwaitingTrader, AwaitingStateA, AwaitingStateB, AwaitingBias, Completed }

// Reihenfolge ist die Schwere-Reihenfolge (Green < Yellow < Red) — wird für die
// "schlechterer Wert gewinnt"-Logik direkt als int verglichen, nicht umsortieren.
public enum AmpelColor { Green, Yellow, Red }

public sealed class CheckinRecord
{
    public string TraderName { get; set; } = "";
    public string StateA { get; set; } = "";
    public string StateB { get; set; } = "";
    public AmpelColor Ampel { get; set; }
    public string Bias { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Start-Fragebogen über Telegram: Trader bestätigen, Zustandscheck (zwei Listen aus dem
/// Tradingplan, Abschnitt "Zustandscheck — Detailregeln"), Bias. Treibt sich selbst über
/// wiederholte <see cref="ProcessUpdatesAsync"/>-Aufrufe aus dem Polling-Timer voran —
/// keine Persistenz über einen Neustart hinweg (gleiche akzeptierte Lücke wie beim
/// PostTradeEvaluator: nach einem Neustart beginnt der Fragebogen einfach neu).
/// </summary>
public sealed class SessionCheckinFlow
{
    // 01_Strategie/Brain/Strategie/Munich_Traders_Tradingplan.md, Abschnitt "Zustandscheck —
    // Detailregeln", Tabelle A/B — Texte 1:1 übernommen, nicht umformulieren.
    private static readonly (string Label, AmpelColor Ampel)[] StateListA =
    {
        ("Ausgeruht & erholt", AmpelColor.Green),
        ("Energiegeladen", AmpelColor.Green),
        ("Ausgeglichen & ruhig", AmpelColor.Green),
        ("Motiviert & klar", AmpelColor.Green),
        ("Leicht müde, aber wach", AmpelColor.Yellow),
        ("Abgelenkt / Kopf ist woanders", AmpelColor.Yellow),
        ("Gereizt / dünnhäutig", AmpelColor.Yellow),
        ("Übermüdet / Schlafmangel", AmpelColor.Red),
        ("Gestresst (privat oder beruflich)", AmpelColor.Red),
        ("Überfordert / viele offene Baustellen", AmpelColor.Red),
    };

    private static readonly (string Label, AmpelColor Ampel)[] StateListB =
    {
        ("Fokussiert & neutral", AmpelColor.Green),
        ("Selbstsicher, ohne Übermut", AmpelColor.Green),
        ("Ängstlich / zögerlich", AmpelColor.Yellow),
        ("Ungeduldig (will unbedingt jetzt einen Trade)", AmpelColor.Yellow),
        ("FOMO-getrieben (Angst, eine Bewegung zu verpassen)", AmpelColor.Yellow),
        ("Übermütig nach einer Gewinnserie", AmpelColor.Yellow),
        ("Gelangweilt / unterfordert, sucht Action statt Setup", AmpelColor.Yellow),
        ("Gierig (will \"mehr rausholen\" als der Plan vorsieht)", AmpelColor.Red),
        ("Im Rache-Modus nach einem Verlust (Revenge-Trading-Gefahr)", AmpelColor.Red),
        ("Unter Erfolgsdruck (muss heute unbedingt gewinnen)", AmpelColor.Red),
    };

    private static readonly string[] Traders = { "Martin", "Tobi", "Mario" };
    private static readonly string[] BiasOptions = { "Long", "Neutral", "Short" };

    public CheckinStep Step { get; private set; } = CheckinStep.NotStarted;
    public CheckinRecord? Result { get; private set; }

    // Wird gesetzt, sobald beide Zustandslisten beantwortet sind — noch VOR dem Bias-Schritt,
    // damit die Panel-Warnung auch dann erscheint, wenn der Bias-Schritt (noch) offen bleibt.
    public AmpelColor? PendingAmpel { get; private set; }

    private long? _currentMessageId;
    private string? _chosenTrader;
    private string? _chosenALabel;
    private AmpelColor? _chosenA;
    private string? _chosenBLabel;

    public async Task StartAsync(TraderIdentity defaultTrader, string botToken, string chatId, HttpClient client)
    {
        Step = CheckinStep.AwaitingTrader;
        var buttons = Traders.Select((name, i) => new TelegramButton(name, $"t:{i}")).ToArray();
        _currentMessageId = await TelegramSender.SendMessageWithKeyboardAsync(
            botToken, chatId,
            $"🧭 <b>Sessioncheck</b>\nWer bist du? (Vorschlag: {defaultTrader})",
            buttons, client, buttonsPerRow: 1).ConfigureAwait(false);
    }

    public async Task ProcessUpdatesAsync(IEnumerable<TelegramUpdate> updates, string botToken, string chatId, HttpClient client)
    {
        foreach (var u in updates)
        {
            if (_currentMessageId is null || u.MessageId != _currentMessageId.Value)
                continue; // Tap gehört nicht zum aktuell offenen Schritt (alte/fremde Nachricht) — ignorieren

            _ = TelegramSender.AnswerCallbackQueryAsync(botToken, u.CallbackQueryId, client);

            long stepMessageId = _currentMessageId.Value;

            switch (Step)
            {
                case CheckinStep.AwaitingTrader:
                    if (!TryParseIndex(u.Data, "t:", Traders.Length, out int ti)) continue;
                    _chosenTrader = Traders[ti];
                    Step = CheckinStep.AwaitingStateA;
                    var buttonsA = StateListA.Select((s, i) => new TelegramButton($"{Emoji(s.Ampel)} {s.Label}", $"a:{i}")).ToArray();
                    await TelegramSender.EditMessageWithKeyboardAsync(
                        botToken, chatId, stepMessageId,
                        $"👤 {_chosenTrader}\n\n1️⃣ <b>Allgemeiner Zustand</b> — wie geht's dir gerade?",
                        buttonsA, client, buttonsPerRow: 1).ConfigureAwait(false);
                    break;

                case CheckinStep.AwaitingStateA:
                    if (!TryParseIndex(u.Data, "a:", StateListA.Length, out int ai)) continue;
                    (_chosenALabel, _chosenA) = StateListA[ai];
                    Step = CheckinStep.AwaitingStateB;
                    var buttonsB = StateListB.Select((s, i) => new TelegramButton($"{Emoji(s.Ampel)} {s.Label}", $"b:{i}")).ToArray();
                    await TelegramSender.EditMessageWithKeyboardAsync(
                        botToken, chatId, stepMessageId,
                        "2️⃣ <b>Zustand bezüglich Trading</b> — Mindset vor dem ersten Trade?",
                        buttonsB, client, buttonsPerRow: 1).ConfigureAwait(false);
                    break;

                case CheckinStep.AwaitingStateB:
                    if (!TryParseIndex(u.Data, "b:", StateListB.Length, out int bi)) continue;
                    var (bLabel, bAmpel) = StateListB[bi];
                    _chosenBLabel = bLabel;
                    PendingAmpel = (AmpelColor)Math.Max((int)_chosenA!.Value, (int)bAmpel);
                    Step = CheckinStep.AwaitingBias;
                    var buttonsBias = BiasOptions.Select((b, i) => new TelegramButton(b, $"bias:{i}")).ToArray();
                    await TelegramSender.EditMessageWithKeyboardAsync(
                        botToken, chatId, stepMessageId,
                        $"{Emoji(PendingAmpel.Value)} {ConsequenceText(PendingAmpel.Value)}\n\n3️⃣ <b>Bias für heute?</b>",
                        buttonsBias, client, buttonsPerRow: 1).ConfigureAwait(false);
                    break;

                case CheckinStep.AwaitingBias:
                    if (!TryParseIndex(u.Data, "bias:", BiasOptions.Length, out int biasIdx)) continue;
                    Result = new CheckinRecord
                    {
                        TraderName = _chosenTrader!,
                        StateA = _chosenALabel!,
                        StateB = _chosenBLabel!,
                        Ampel = PendingAmpel!.Value,
                        Bias = BiasOptions[biasIdx],
                        Timestamp = DateTime.Now,
                    };
                    Step = CheckinStep.Completed;
                    await TelegramSender.EditMessageWithKeyboardAsync(
                        botToken, chatId, stepMessageId, BuildSummary(Result), Array.Empty<TelegramButton>(), client).ConfigureAwait(false);
                    _currentMessageId = null;
                    break;
            }
        }
    }

    private static bool TryParseIndex(string data, string prefix, int count, out int index)
    {
        index = -1;
        if (!data.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return int.TryParse(data.AsSpan(prefix.Length), out index) && index >= 0 && index < count;
    }

    private static string Emoji(AmpelColor a) => a switch
    {
        AmpelColor.Green => "🟢",
        AmpelColor.Yellow => "🟡",
        AmpelColor.Red => "🔴",
        _ => "",
    };

    public static string ConsequenceText(AmpelColor a) => a switch
    {
        AmpelColor.Green => "Normales Risiko wie im Tradingplan.",
        AmpelColor.Yellow => "Risiko heute halbieren, Frequenzlimits nicht ausreizen.",
        AmpelColor.Red => "Kein Trading heute laut Zustandscheck.",
        _ => "",
    };

    private static string BuildSummary(CheckinRecord r) =>
        $"✅ <b>Sessioncheck abgeschlossen</b>\n" +
        $"👤 {r.TraderName}\n" +
        $"1️⃣ {r.StateA}\n" +
        $"2️⃣ {r.StateB}\n" +
        $"{Emoji(r.Ampel)} {ConsequenceText(r.Ampel)}\n" +
        $"📈 Bias: {r.Bias}";
}
