#if !ATASX

using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace MunichTraders.TradeRecap;

public static class CardRenderer
{
    // ── Farben ────────────────────────────────────────────────────────────
    private static readonly Color BgColor       = ColorFromHex("#1A1A1A");
    private static readonly Color BgSection     = ColorFromHex("#202020");
    private static readonly Color GoldColor     = ColorFromHex("#B89648");
    private static readonly Color TextPrimary   = Color.White;
    private static readonly Color TextMuted     = ColorFromHex("#A0A0A0");
    private static readonly Color PnlGreen      = ColorFromHex("#22C55E");
    private static readonly Color PnlRed        = ColorFromHex("#EF4444");
    private static readonly Color DrawdownSafe  = ColorFromHex("#22C55E");
    private static readonly Color DrawdownWarn  = ColorFromHex("#F59E0B");
    private static readonly Color DrawdownDanger = ColorFromHex("#EF4444");

    // ── Dimensionen (9:16 — Instagram Stories / TikTok) ──────────────────
    private const int W   = 1080;
    private const int H   = 1920;
    private const int Pad = 56;

    // ── Entry Point ───────────────────────────────────────────────────────

    public static byte[] RenderCard(
        PositionRecord record,
        DailyStatsSnapshot stats,
        byte[]? logoBytes,
        byte[]? chartBytes,       // aktuell immer null (Screenshot deaktiviert)
        decimal dailyDrawdownLimit,
        decimal accountBalance,
        string traderName = "")
    {
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(BgColor);

        DrawHeader(g, record, logoBytes, traderName);
        DrawSymbolRow(g, record);
        DrawPnlBlock(g, record, accountBalance, stats, out float pnlSepY);
        float chartY = pnlSepY + 8f;
        DrawChartArea(g, chartBytes, chartY);
        float gridY = chartY + 620f + 10f;
        DrawDetailsGrid(g, record, gridY, out float statsStartY);
        DrawDailyStats(g, stats, dailyDrawdownLimit, statsStartY + 8f);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // ── Header (y 0–130) ──────────────────────────────────────────────────

    private static void DrawHeader(Graphics g, PositionRecord record, byte[]? logoBytes, string traderName = "")
    {
        // Gold-Akzentstreifen oben
        using var goldBrush = new SolidBrush(GoldColor);
        g.FillRectangle(goldBrush, 0, 0, W, 5);

        // Logo — Höhe fixiert (120px), Breite aus Seitenverhältnis
        const float LogoH = 120f;
        DrawLogoScaled(g, logoBytes, Pad, 14, LogoH);

        // Datum rechtsbündig
        string dateStr = record.CloseTime.ToString("dd.MM.yyyy");
        using var dateFont   = GetFont("Montserrat", 32f, FontStyle.Bold);
        using var mutedBrush = new SolidBrush(TextMuted);
        var dateSz = g.MeasureString(dateStr, dateFont);
        g.DrawString(dateStr, dateFont, mutedBrush, W - Pad - dateSz.Width, 40);

        // Trader-Name rechtsbündig unter dem Datum
        if (!string.IsNullOrWhiteSpace(traderName))
        {
            using var nameFont  = GetFont("Montserrat", 26f, FontStyle.Bold);
            using var nameBrush = new SolidBrush(GoldColor);
            var nameSz = g.MeasureString(traderName, nameFont);
            g.DrawString(traderName, nameFont, nameBrush, W - Pad - nameSz.Width, 82);
        }

        // Trennlinie
        using var sep = new Pen(GoldColor, 1.5f);
        g.DrawLine(sep, Pad, 152, W - Pad, 152);
    }

    // ── Symbol + Richtungs-Badge (y 135–310) ─────────────────────────────

    private static void DrawSymbolRow(Graphics g, PositionRecord record)
    {
        const float Y = 176f;

        // Symbol
        using var symFont  = GetFont("Montserrat", 80f, FontStyle.Bold);
        using var symBrush = new SolidBrush(TextPrimary);
        g.DrawString(record.Symbol, symFont, symBrush, Pad, Y);

        // Richtungs-Badge (rechts)
        bool isLong   = record.Direction == PositionDirection.Long;
        string dirTxt = isLong ? "▲  LONG" : "▼  SHORT";
        Color  dirClr = isLong ? PnlGreen : PnlRed;

        using var badgeFont = GetFont("Montserrat", 24f, FontStyle.Bold);
        var bSz = g.MeasureString(dirTxt, badgeFont);
        float bx = W - Pad - bSz.Width - 32f;
        float by = Y + 18f;

        var badgeRect = new RectangleF(bx - 16, by - 10, bSz.Width + 32, bSz.Height + 20);
        using var bgBrush = new SolidBrush(Color.FromArgb(55, dirClr));
        using var bgPen   = new Pen(dirClr, 2f);
        g.FillRoundedRect(bgBrush, badgeRect, 12);
        g.DrawRoundedRect(bgPen, badgeRect, 12);

        using var dirBrush = new SolidBrush(dirClr);
        g.DrawString(dirTxt, badgeFont, dirBrush, bx, by);

        // Konto-Name — erste 4 Zeichen sichtbar, Rest mit * maskiert
        if (!string.IsNullOrWhiteSpace(record.AccountId))
        {
            string displayId = record.AccountId.Length > 4
                ? record.AccountId[..4] + new string('*', record.AccountId.Length - 4)
                : record.AccountId;
            using var accFont  = GetFont("Montserrat", 34f, FontStyle.Bold);
            using var accBrush = new SolidBrush(GoldColor);
            g.DrawString(displayId, accFont, accBrush, Pad, Y + 92f);
        }

        // Trennlinie (mit etwas mehr Platz wenn Konto angezeigt)
        float sepY = string.IsNullOrWhiteSpace(record.AccountId) ? 286f : 346f;
        using var sep = new Pen(ColorFromHex("#2A2A2A"), 1f);
        g.DrawLine(sep, Pad, sepY, W - Pad, sepY);
    }

    // ── PnL Block (y 330–520) ─────────────────────────────────────────────

    private static void DrawPnlBlock(Graphics g, PositionRecord record, decimal accountBalance,
        DailyStatsSnapshot stats, out float sepY)
    {
        float Y = string.IsNullOrWhiteSpace(record.AccountId) ? 306f : 366f;
        bool isProfit = record.PnlUsd >= 0;
        Color pnlClr  = isProfit ? PnlGreen : PnlRed;
        string sign   = isProfit ? "+" : "";

        using var bigFont  = GetFont("Montserrat", 96f, FontStyle.Bold);
        using var pnlBrush = new SolidBrush(pnlClr);
        g.DrawString($"{sign}{record.PnlUsd:F2} $", bigFont, pnlBrush, Pad, Y);

        // ── Tages-P&L rechts, kleiner ─────────────────────────────────────
        {
            string daySign  = stats.DisplayPnl >= 0 ? "+" : "";
            Color  dayColor = stats.DisplayPnl >= 0 ? PnlGreen : PnlRed;
            using var dayLabFont = GetFont("Inter", 16f, FontStyle.Regular);
            using var dayValFont = GetFont("Montserrat", 44f, FontStyle.Bold);
            using var dayLabB    = new SolidBrush(TextMuted);
            using var dayValB    = new SolidBrush(dayColor);

            string dayLabel = "TAGES-P&L";
            string dayVal   = $"{daySign}{stats.DisplayPnl:F2} $";
            var dayValSz    = g.MeasureString(dayVal,   dayValFont);
            var dayLabSz    = g.MeasureString(dayLabel, dayLabFont);
            float dayX      = W - Pad - Math.Max(dayValSz.Width, dayLabSz.Width);

            g.DrawString(dayLabel, dayLabFont, dayLabB, dayX, Y + 10);
            g.DrawString(dayVal,   dayValFont, dayValB, dayX, Y + 32);
        }

        string sub = $"{sign}{record.PnlTicks} Ticks  ·  {record.Contracts} Kontrakt{(record.Contracts != 1 ? "e" : "")}";
        using var subFont  = GetFont("Montserrat", 28f, FontStyle.Regular);
        using var subBrush = new SolidBrush(TextMuted);
        g.DrawString(sub, subFont, subBrush, Pad + 4, Y + 112);

        // %-Anteil am Konto
        if (accountBalance > 0)
        {
            decimal pct    = record.PnlUsd / accountBalance * 100m;
            string  pctStr = $"{(pct >= 0 ? "+" : "")}{pct:F2}% vom Konto";
            using var pctFont  = GetFont("Inter", 24f, FontStyle.Regular);
            using var pctBrush = new SolidBrush(Color.FromArgb(180, pnlClr));
            g.DrawString(pctStr, pctFont, pctBrush, Pad + 4, Y + 150);
        }

        sepY = Y + 196f;
        using var sep = new Pen(ColorFromHex("#2A2A2A"), 1f);
        g.DrawLine(sep, Pad, sepY, W - Pad, sepY);
    }

    // ── Details-Grid (y 534–960) ──────────────────────────────────────────

    private static void DrawDetailsGrid(Graphics g, PositionRecord record, float gridY, out float afterSepY)
    {
        const float RowH = 130f;
        float colW       = (W - Pad * 2) / 2f;

        // MIN/MAX zuerst, dann Einstieg/Ausstieg, dann Dauer/Kontrakte
        var cells = new (string Label, string Value, bool Highlight)[]
        {
            ("MIN TICKS",  $"{record.MAETicks:+#;-#;0} Ticks  ·  {record.MAEUsd:+0.00;-0.00} $", false),
            ("MAX TICKS",  $"{record.MFETicks:+#;-#;0} Ticks  ·  {record.MFEUsd:+0.00;-0.00} $", false),
            ("EINSTIEG",   $"{record.OpenTime:HH:mm:ss}  @  {record.AvgEntryPrice:F2}",           false),
            ("AUSSTIEG",   $"{record.CloseTime:HH:mm:ss}  @  {record.AvgExitPrice:F2}",           false),
            ("DAUER",      FormatDuration(record.Duration),                                        false),
            ("KONTRAKTE",  record.Contracts.ToString(),                                            false),
        };

        using var labelFont  = GetFont("Inter", 16f, FontStyle.Regular);
        using var valueFont  = GetFont("Inter", 24f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(TextMuted);
        using var valueBrush = new SolidBrush(TextPrimary);
        using var maeBrush   = new SolidBrush(PnlRed);
        using var mfeBrush   = new SolidBrush(PnlGreen);

        for (int i = 0; i < cells.Length; i++)
        {
            int   col = i % 2;
            int   row = i / 2;
            float x   = Pad + col * colW;
            float y   = gridY + row * RowH;

            // Zebra-Hintergrund
            if (row % 2 == 0)
            {
                using var rowBrush = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
                g.FillRectangle(rowBrush, x, y, colW, RowH - 4);
            }

            g.DrawString(cells[i].Label, labelFont, labelBrush, x + 18, y + 18);

            Brush vBrush = cells[i].Label == "MIN TICKS" ? maeBrush
                         : cells[i].Label == "MAX TICKS" ? mfeBrush
                         : valueBrush;
            g.DrawString(cells[i].Value, valueFont, vBrush, x + 18, y + 50);
        }

        // Trade-Tag (falls gesetzt)
        float afterGridY = gridY + 3 * RowH;
        if (!string.IsNullOrWhiteSpace(record.TradeTag))
        {
            using var tagFont  = GetFont("Montserrat", 22f, FontStyle.Bold);
            using var tagBrush = new SolidBrush(GoldColor);
            g.DrawString($"#  {record.TradeTag}", tagFont, tagBrush, Pad, afterGridY + 12);
            afterGridY += 52f;
        }

        using var sep = new Pen(ColorFromHex("#2A2A2A"), 1f);
        g.DrawLine(sep, Pad, afterGridY + 16, W - Pad, afterGridY + 16);
        afterSepY = afterGridY + 16f;
    }

    // ── Tages-Statistik (y ~1060–1290) ───────────────────────────────────

    private static void DrawDailyStats(
        Graphics g, DailyStatsSnapshot stats, decimal drawdownLimit, float statsY)
    {
        if (drawdownLimit <= 0) return;

        const float StatsH = 90f;

        using var bgBrush = new SolidBrush(BgSection);
        g.FillRectangle(bgBrush, 0, statsY, W, StatsH);

        decimal used  = Math.Abs(Math.Min(0, stats.DisplayPnl));
        decimal pct   = used / drawdownLimit * 100;

        // Gold bei 0% → Rot bei 100%
        float t = Math.Min(1f, (float)(pct / 100m));
        Color fillColor = Color.FromArgb(255,
            (int)(GoldColor.R + t * (DrawdownDanger.R - GoldColor.R)),
            (int)(GoldColor.G + t * (DrawdownDanger.G - GoldColor.G)),
            (int)(GoldColor.B + t * (DrawdownDanger.B - GoldColor.B)));

        float barY = statsY + 46f;

        using var ddLabF = GetFont("Inter", 14f, FontStyle.Regular);
        using var ddValF = GetFont("Montserrat", 16f, FontStyle.Bold);
        using var ddLabB = new SolidBrush(TextMuted);
        using var ddValB = new SolidBrush(fillColor);
        g.DrawString("DRAWDOWN", ddLabF, ddLabB, Pad, barY - 24);
        string ddVal = $"{used:F0} / {drawdownLimit:F0} $  ({pct:F0}%)";
        var ddValSz = g.MeasureString(ddVal, ddValF);
        g.DrawString(ddVal, ddValF, ddValB, W - Pad - ddValSz.Width, barY - 24);

        float bLeft  = Pad;
        float bWidth = W - Pad * 2;
        float bFill  = Math.Min((float)(pct / 100m) * bWidth, bWidth);
        using var trackB = new SolidBrush(ColorFromHex("#2A2A2A"));
        using var fillB  = new SolidBrush(fillColor);
        g.FillRectangle(trackB, bLeft, barY, bWidth, 18);
        if (bFill > 0) g.FillRectangle(fillB, bLeft, barY, bFill, 18);
    }

    // ── Chart-Bereich (y 1290–1920) ───────────────────────────────────────

    private static void DrawChartArea(Graphics g, byte[]? chartBytes, float chartY)
    {
        const float ChartH = 620f;

        if (chartBytes == null || chartBytes.Length == 0) return;

        try
        {
            using var ms    = new MemoryStream(chartBytes);
            using var chart = Image.FromStream(ms);

            float scaleW = (float)W / chart.Width;
            float scaleH = ChartH   / chart.Height;
            float scale  = Math.Min(scaleW, scaleH);

            float drawW   = chart.Width  * scale;
            float drawH   = chart.Height * scale;
            float offsetX = (W    - drawW) / 2f;
            float offsetY = chartY + (ChartH - drawH) / 2f;

            g.DrawImage(chart,
                new RectangleF(offsetX, offsetY, drawW, drawH),
                new RectangleF(0, 0, chart.Width, chart.Height),
                GraphicsUnit.Pixel);
        }
        catch { }
    }

    // ── Logo-Hilfsmethoden ────────────────────────────────────────────────

    /// <summary>Header: Höhe fixiert, Breite aus Seitenverhältnis (kein Stretching).</summary>
    private static void DrawLogoScaled(Graphics g, byte[]? logoBytes, float x, float y, float height)
    {
        if (logoBytes != null)
        {
            try
            {
                using var ms   = new MemoryStream(logoBytes);
                using var logo = Image.FromStream(ms);
                float scale = height / logo.Height;
                float drawW = logo.Width  * scale;
                g.DrawImage(logo, x, y, drawW, height);
                return;
            }
            catch { }
        }
        using var brush = new SolidBrush(GoldColor);
        using var font  = GetFont("Montserrat", height * 0.6f, FontStyle.Bold);
        g.DrawString("MT", font, brush, x, y);
    }


    private static void DrawStatCell(Graphics g, Font labelFont, Font valueFont, Brush labelBrush,
        string label, string value, Color valueColor, float x, float y)
    {
        g.DrawString(label, labelFont, labelBrush, x, y);
        using var vBrush = new SolidBrush(valueColor);
        g.DrawString(value, valueFont, vBrush, x, y + 22);
    }

    private static Font GetFont(string name, float size, FontStyle style)
    {
        foreach (var family in new[] { name, "Segoe UI", "Arial" })
            try { return new Font(family, size, style, GraphicsUnit.Pixel); }
            catch { }
        return new Font(SystemFonts.DefaultFont.FontFamily, size, style, GraphicsUnit.Pixel);
    }

    private static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            Convert.ToInt32(hex[..2], 16),
            Convert.ToInt32(hex[2..4], 16),
            Convert.ToInt32(hex[4..6], 16));
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.Seconds}s";
        if (d.TotalHours < 1)  return $"{d.Minutes}m {d.Seconds:D2}s";
        return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRect(this Graphics g, Brush brush, RectangleF rect, float radius)
    {
        using var path = RoundedRectPath(rect, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRect(this Graphics g, Pen pen, RectangleF rect, float radius)
    {
        using var path = RoundedRectPath(rect, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

#endif
