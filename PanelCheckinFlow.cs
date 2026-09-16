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
/// Start-Fragebogen (Trader bestätigen, Zustandscheck, Bias) — wird direkt im ATAS-
/// Status-Panel per Mausklick ausgefüllt (siehe TradeRecapIndicator.ProcessMouseDown/
/// DrawStatusPanel), kein Telegram-Hin-und-Her mehr. Telegram bekommt nach Abschluss nur
/// noch EINE Nachricht mit der fertigen Antwort (siehe TradeRecapIndicator.OnPositionClosed-
/// Pendant für den Checkin, FinalizePanelCheckin).
/// </summary>
public sealed class PanelCheckinFlow
{
    // 01_Strategie/Brain/Strategie/Munich_Traders_Tradingplan.md, Abschnitt "Zustandscheck —
    // Detailregeln", Tabelle A/B — Texte 1:1 übernommen, nicht umformulieren.
    public static readonly (string Label, AmpelColor Ampel)[] StateListA =
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

    public static readonly (string Label, AmpelColor Ampel)[] StateListB =
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

    public static readonly string[] Traders = { "Martin", "Tobi", "Mario" };
    public static readonly string[] BiasOptions = { "Long", "Neutral", "Short" };

    public CheckinStep Step { get; private set; } = CheckinStep.NotStarted;
    public CheckinRecord? Result { get; private set; }

    // Wird gesetzt, sobald beide Zustandslisten beantwortet sind — noch VOR dem Bias-Schritt,
    // damit die Panel-Warnung auch dann erscheint, wenn der Bias-Schritt (noch) offen bleibt.
    public AmpelColor? PendingAmpel { get; private set; }

    private string? _chosenTrader;
    private string? _chosenALabel;
    private AmpelColor? _chosenA;
    private string? _chosenBLabel;

    public void Start() => Step = CheckinStep.AwaitingTrader;

    public void ChooseTrader(int index)
    {
        if (index < 0 || index >= Traders.Length) return;
        _chosenTrader = Traders[index];
        Step = CheckinStep.AwaitingStateA;
    }

    public void ChooseStateA(int index)
    {
        if (index < 0 || index >= StateListA.Length) return;
        (_chosenALabel, _chosenA) = StateListA[index];
        Step = CheckinStep.AwaitingStateB;
    }

    public void ChooseStateB(int index)
    {
        if (index < 0 || index >= StateListB.Length || _chosenA is null) return;
        var (label, ampel) = StateListB[index];
        _chosenBLabel = label;
        PendingAmpel = (AmpelColor)Math.Max((int)_chosenA.Value, (int)ampel);
        Step = CheckinStep.AwaitingBias;
    }

    public void ChooseBias(int index)
    {
        if (index < 0 || index >= BiasOptions.Length || PendingAmpel is null) return;
        Result = new CheckinRecord
        {
            TraderName = _chosenTrader!,
            StateA     = _chosenALabel!,
            StateB     = _chosenBLabel!,
            Ampel      = PendingAmpel.Value,
            Bias       = BiasOptions[index],
            Timestamp  = DateTime.Now,
        };
        Step = CheckinStep.Completed;
    }

    /// <summary>Bricht den Fragebogen ab (Panel-Button "Abbrechen") — kein Ergebnis, kein Speichern.</summary>
    public void Reset()
    {
        Step = CheckinStep.NotStarted;
        Result = null;
        PendingAmpel = null;
        _chosenTrader = null;
        _chosenALabel = null;
        _chosenA = null;
        _chosenBLabel = null;
    }

    public static string Emoji(AmpelColor a) => a switch
    {
        AmpelColor.Green  => "🟢",
        AmpelColor.Yellow => "🟡",
        AmpelColor.Red    => "🔴",
        _ => "",
    };

    public static string ConsequenceText(AmpelColor a) => a switch
    {
        AmpelColor.Green  => "Normales Risiko wie im Tradingplan.",
        AmpelColor.Yellow => "Risiko heute halbieren, Frequenzlimits nicht ausreizen.",
        AmpelColor.Red    => "Kein Trading heute laut Zustandscheck.",
        _ => "",
    };

    public static string BuildAnswerSummary(CheckinRecord r) =>
        $"✅ <b>Sessioncheck</b> (im Panel ausgefüllt)\n" +
        $"👤 {r.TraderName}\n" +
        $"1️⃣ {r.StateA}\n" +
        $"2️⃣ {r.StateB}\n" +
        $"{Emoji(r.Ampel)} {ConsequenceText(r.Ampel)}\n" +
        $"📈 Bias: {r.Bias}";
}
