using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace MunichTraders.TradeRecap;

public record CandleData(
    decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, DateTime Time);

/// <summary>
/// Rendert eine gebrandete Mini-Candlestick-Karte (1080×630 px) aus OHLC-Rohdaten.
/// Entry/Exit werden als gestrichelte Linien eingezeichnet, die Trade-Zone hervorgehoben.
/// </summary>
public static class MiniChartRenderer
{
    private const int W     = 1080;
    private const int H     = 630;
    private const int PadL  = 16;
    private const int PadR  = 76;
    private const int PadT  = 16;
    private const int PadB  = 28;
    private static readonly int ChartW = W - PadL - PadR;
    private static readonly int ChartH = H - PadT - PadB;

    private static Color Hex(string h)
    {
        h = h.TrimStart('#');
        return Color.FromArgb(255,
            Convert.ToInt32(h[..2], 16),
            Convert.ToInt32(h[2..4], 16),
            Convert.ToInt32(h[4..6], 16));
    }

    private static readonly Color BgColor   = Hex("#0A0A0A");
    private static readonly Color Gold      = Hex("#B89648");
    private static readonly Color Silver    = Hex("#D8D8D8");
    private static readonly Color Bull      = Hex("#B89648");   // Gold
    private static readonly Color Bear      = Hex("#D8D8D8");   // Hellgrau
    private static readonly Color GridLine  = Color.FromArgb(20, 255, 255, 255);
    private static readonly Color ZoneLong  = Color.FromArgb(20, 34,  197, 94);
    private static readonly Color ZoneShort = Color.FromArgb(20, 239,  68, 68);

    public static byte[] Render(IReadOnlyList<CandleData> candles, PositionRecord record)
    {
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode       = SmoothingMode.AntiAlias;
        g.TextRenderingHint   = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.InterpolationMode   = InterpolationMode.HighQualityBicubic;
        g.Clear(BgColor);

        int n = candles.Count;

        // ── Preisbereich ──────────────────────────────────────────────────
        decimal lo = candles.Min(c => c.Low);
        decimal hi = candles.Max(c => c.High);
        lo = Math.Min(lo, Math.Min(record.AvgEntryPrice, record.AvgExitPrice));
        hi = Math.Max(hi, Math.Max(record.AvgEntryPrice, record.AvgExitPrice));
        decimal range = hi - lo;
        if (range == 0) range = 1;
        decimal margin = range * 0.10m;
        lo -= margin; hi += margin; range = hi - lo;

        float PriceToY(decimal p) =>
            PadT + (float)((double)(hi - p) / (double)range * ChartH);
        float BarX(int i) => PadL + (i + 0.5f) * ((float)ChartW / n);
        float candleAreaW = (float)ChartW / n;
        float bodyW = Math.Max(1.5f, candleAreaW * 0.62f);

        // ── Horizontale Gridlinien (5 Stufen) ────────────────────────────
        using var gridPen    = new Pen(GridLine, 1f);
        using var axisFont   = new Font("Courier New", 12f);
        using var axisBrush  = new SolidBrush(Color.FromArgb(180, Silver));
        for (int gi = 0; gi <= 4; gi++)
        {
            decimal gp = lo + range * gi / 4m;
            float gy = PriceToY(gp);
            g.DrawLine(gridPen, PadL, gy, PadL + ChartW, gy);
            g.DrawString(gp.ToString("F2"), axisFont, axisBrush, PadL + ChartW + 4, gy - 7f);
        }

        // ── Trade-Zone Highlight ──────────────────────────────────────────
        // Candle-Zeiten sind Lokalzeit (UTC→Local in BuildMiniChart).
        // trade.Time aus ATAS hat Kind=Unspecified, Wert=UTC → ebenfalls konvertieren.
        DateTime entryLocal = DateTime.SpecifyKind(record.OpenTime,  DateTimeKind.Utc).ToLocalTime();
        DateTime exitLocal  = DateTime.SpecifyKind(record.CloseTime, DateTimeKind.Utc).ToLocalTime();
        int entryIdx = FindBarIndex(candles, entryLocal);
        int exitIdx  = FindBarIndex(candles, exitLocal);
        if (entryIdx >= 0 && exitIdx >= entryIdx)
        {
            float zx = BarX(entryIdx) - candleAreaW / 2f;
            float zw = BarX(exitIdx)  + candleAreaW / 2f - zx;
            Color zc = record.Direction == PositionDirection.Long ? ZoneLong : ZoneShort;
            g.FillRectangle(new SolidBrush(zc), zx, PadT, zw, ChartH);
        }

        // ── Kerzen ────────────────────────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            var c   = candles[i];
            float cx     = BarX(i);
            float openY  = PriceToY(c.Open);
            float closeY = PriceToY(c.Close);
            float highY  = PriceToY(c.High);
            float lowY   = PriceToY(c.Low);
            bool  bull   = c.Close >= c.Open;
            Color col    = bull ? Bull : Bear;

            // Docht
            using var wickPen = new Pen(Color.FromArgb(150, col), 1f);
            g.DrawLine(wickPen, cx, highY, cx, lowY);

            // Körper
            float top = Math.Min(openY, closeY);
            float bh  = Math.Max(1f, Math.Abs(closeY - openY));
            g.FillRectangle(new SolidBrush(col), cx - bodyW / 2f, top, bodyW, bh);
        }

        // ── Entry / Exit Preislinien ──────────────────────────────────────
        float entryLineX2 = entryIdx >= 0 ? BarX(entryIdx) : PadL + ChartW;
        DrawHLine(g, PriceToY(record.AvgEntryPrice),
            PadL, entryLineX2, Gold, Gold,
            "ENTRY", $"{record.AvgEntryPrice:F2}");

        Color exitCol      = record.PnlUsd >= 0 ? Bull : Bear;
        Color exitLineGray = Color.FromArgb(180, 180, 180, 180);
        float exitX2 = PadL + ChartW;
        float exitX1 = Math.Max(PadL, exitX2 - 10f * candleAreaW);
        DrawHLine(g, PriceToY(record.AvgExitPrice),
            exitX1, exitX2, exitLineGray, exitCol,
            "EXIT", $"{record.AvgExitPrice:F2}");

        // ── Entry / Exit Marker-Pfeile (GDI+-Vektoren, keine externen Dateien) ──
        bool entryIsLong = record.Direction == PositionDirection.Long;
        if (entryIdx >= 0)
            DrawArrowMarker(g, BarX(entryIdx), PriceToY(record.AvgEntryPrice),
                            pointUp: entryIsLong);
        if (exitIdx >= 0)
            DrawArrowMarker(g, BarX(exitIdx), PriceToY(record.AvgExitPrice),
                            pointUp: !entryIsLong);

        // ── Zeit-Labels (5 gleichmäßige Punkte) ──────────────────────────
        using var timeFont  = new Font("Calibri", 12f);
        using var timeBrush = new SolidBrush(Color.FromArgb(180, Silver));
        float timeY = PadT + ChartH + 5f;
        foreach (int ti in new[] { 0, n / 4, n / 2, 3 * n / 4, n - 1 }.Distinct())
        {
            string ts = candles[ti].Time.ToString("HH:mm");
            g.DrawString(ts, timeFont, timeBrush, BarX(ti) - 13f, timeY);
        }

        // ── Timeframe-Label oben links ────────────────────────────────────
        string tf = InferTimeframe(candles);
        if (!string.IsNullOrEmpty(tf))
        {
            using var tfFont  = new Font("Calibri", 17f, FontStyle.Bold);
            using var tfBrush = new SolidBrush(Color.FromArgb(200, Gold));
            g.DrawString($"Timeframe: {tf}", tfFont, tfBrush, PadL + 4, PadT + 4);
        }

        // ── Gold-Separator oben ───────────────────────────────────────────
        using var sepPen = new Pen(Color.FromArgb(90, Gold), 1f);
        g.DrawLine(sepPen, 0, 1, W, 1);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Leitet den Timeframe aus den Zeitabständen zwischen Bars ab.
    /// Der kleinste positive Abstand entspricht exakt der Barperiode.
    /// Für Tick-/Volume-Charts (variierende Abstände) wird "" zurückgegeben.
    /// </summary>
    private static string InferTimeframe(IReadOnlyList<CandleData> candles)
    {
        if (candles.Count < 3) return "";
        var diffs = new List<double>(10);
        for (int i = 1; i < Math.Min(candles.Count, 11); i++)
        {
            double d = (candles[i].Time - candles[i - 1].Time).TotalSeconds;
            if (d > 0) diffs.Add(d);
        }
        if (diffs.Count == 0) return "";
        diffs.Sort();
        double sec = diffs[0];   // Minimum = Barperiode; Lücken an Session-Opens sind immer größer
        if (sec < 60)    return $"{(int)sec}s";
        if (sec < 3600)  return $"{(int)(sec / 60)}m";
        if (sec < 86400) return $"{(int)(sec / 3600)}h";
        return $"{(int)(sec / 86400)}d";
    }

    private static int FindBarIndex(IReadOnlyList<CandleData> candles, DateTime time)
    {
        int idx = -1;
        for (int i = 0; i < candles.Count; i++)
            if (candles[i].Time <= time) idx = i;
        return idx;
    }

    private static void DrawHLine(Graphics g, float y, float x1, float x2, Color lineCol, Color labelCol, string tag, string price)
    {
        using var pen = new Pen(Color.FromArgb(210, lineCol), 1.5f)
            { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, x1, y, x2, y);

        using var font  = new Font("Calibri", 11f, FontStyle.Bold);
        using var brush = new SolidBrush(labelCol);
        g.DrawString(tag,   font, brush, PadL + ChartW + 4, y - 16f);
        g.DrawString(price, font, brush, PadL + ChartW + 4, y -  2f);
    }

    // Pfeil als GDI+-Vektor zeichnen — keine externen Dateien nötig.
    // pointUp=true → grüner ↑ (Long-Entry / Short-Exit), Spitze liegt auf y
    // pointUp=false → roter ↓ (Short-Entry / Long-Exit), Spitze liegt auf y
    private static void DrawArrowMarker(Graphics g, float x, float y, bool pointUp)
    {
        const float size   = 44f;  // Gesamthöhe in Pixel, fix unabhängig vom Zoom
        const float headH  = size * 0.55f;
        const float shaftW = size * 0.38f;

        Color col = pointUp
            ? Color.FromArgb(255, 52, 199, 89)   // grün  ↑
            : Color.FromArgb(255, 220, 50,  50);  // rot   ↓

        using var path  = new GraphicsPath();
        using var brush = new SolidBrush(col);

        if (pointUp)
        {
            // Spitze oben bei y, Schaft hängt nach unten
            float tipY      = y;
            float headBase  = y + headH;
            float bottom    = y + size;
            path.AddPolygon(new PointF[]
            {
                new(x,              tipY),
                new(x + size / 2f,  headBase),
                new(x + shaftW / 2f, headBase),
                new(x + shaftW / 2f, bottom),
                new(x - shaftW / 2f, bottom),
                new(x - shaftW / 2f, headBase),
                new(x - size / 2f,  headBase),
            });
        }
        else
        {
            // Spitze unten bei y, Schaft zeigt nach oben
            float tipY      = y;
            float headBase  = y - headH;
            float top       = y - size;
            path.AddPolygon(new PointF[]
            {
                new(x,              tipY),
                new(x + size / 2f,  headBase),
                new(x + shaftW / 2f, headBase),
                new(x + shaftW / 2f, top),
                new(x - shaftW / 2f, top),
                new(x - shaftW / 2f, headBase),
                new(x - size / 2f,  headBase),
            });
        }

        g.FillPath(brush, path);
    }
}
