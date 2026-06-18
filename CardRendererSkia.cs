#if ATASX

using System.Runtime.InteropServices;
using SkiaSharp;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Cross-platform Karten-Renderer für ATAS X (SkiaSharp).
/// Identisches Layout wie CardRenderer (GDI+), aber läuft auf Windows UND macOS.
/// </summary>
public static class CardRenderer
{
    private static readonly SKColor BgColor       = SKColor.Parse("#1A1A1A");
    private static readonly SKColor BgLight        = SKColor.Parse("#242424");
    private static readonly SKColor GoldColor      = SKColor.Parse("#B89648");
    private static readonly SKColor TextPrimary    = SKColors.White;
    private static readonly SKColor TextMuted      = SKColor.Parse("#A0A0A0");
    private static readonly SKColor PnlGreen       = SKColor.Parse("#22C55E");
    private static readonly SKColor PnlRed         = SKColor.Parse("#EF4444");
    private static readonly SKColor DrawdownSafe   = SKColor.Parse("#22C55E");
    private static readonly SKColor DrawdownWarn   = SKColor.Parse("#F59E0B");
    private static readonly SKColor DrawdownDanger = SKColor.Parse("#EF4444");

    private const int CardW  = 900;
    private const int CardH  = 520;
    private const int ChartW = 320;
    private const int Pad    = 28;

    // Hochqualitatives Resampling für Bitmap-Scaling (Logo, Chart-Screenshot)
    private static readonly SKPaint BitmapPaint = new()
    {
        IsAntialias  = true,
        FilterQuality = SKFilterQuality.High,
    };

    public static byte[] RenderCard(
        PositionRecord record,
        DailyStatsSnapshot stats,
        byte[]? logoBytes,
        byte[]? chartBytes,
        decimal dailyDrawdownLimit,
        decimal accountBalance)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(CardW, CardH, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        canvas.Clear(BgColor);

        DrawGoldTopBorder(canvas);
        if (chartBytes != null) DrawChartSeparator(canvas);
        DrawLogo(canvas, logoBytes);
        DrawHeader(canvas, record);
        DrawPnl(canvas, record);
        DrawDataGrid(canvas, record);
        DrawDailyStats(canvas, stats, dailyDrawdownLimit, accountBalance);
        if (chartBytes != null) DrawChartPanel(canvas, chartBytes);

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── Hintergrund & Rahmen ──────────────────────────────────────────────

    private static void DrawGoldTopBorder(SKCanvas c)
    {
        using var p = new SKPaint
        {
            Color       = GoldColor,
            StrokeWidth = 3f,
            IsStroke    = true,
            IsAntialias = true,
        };
        c.DrawLine(0, 1.5f, CardW, 1.5f, p);
    }

    private static void DrawChartSeparator(SKCanvas c)
    {
        using var p = new SKPaint
        {
            Color       = SKColor.Parse("#333333"),
            StrokeWidth = 1f,
            IsStroke    = true,
            IsAntialias = true,
        };
        c.DrawLine(CardW - ChartW - 0.5f, 0, CardW - ChartW - 0.5f, CardH, p);
    }

    // ── Logo ──────────────────────────────────────────────────────────────

    private static void DrawLogo(SKCanvas c, byte[]? logoBytes)
    {
        const int logoSize = 40;
        if (logoBytes != null)
        {
            try
            {
                using var bmp  = SKBitmap.Decode(logoBytes);
                var dest = new SKRect(Pad, 18, Pad + logoSize, 18 + logoSize);
                c.DrawBitmap(bmp, dest, BitmapPaint);
            }
            catch { DrawTextLogo(c, Pad); }
        }
        else
        {
            DrawTextLogo(c, Pad);
        }

        using var font  = MakeFont("Montserrat", 11f, SKFontStyle.Bold);
        using var paint = new SKPaint { Color = GoldColor, IsAntialias = true };
        c.DrawText("MUNICH TRADERS", Pad + logoSize + 10, 28 + 11, font, paint);
    }

    private static void DrawTextLogo(SKCanvas c, float x)
    {
        using var font  = MakeFont("Montserrat", 22f, SKFontStyle.Bold);
        using var paint = new SKPaint { Color = GoldColor, IsAntialias = true };
        c.DrawText("MT", x, 12 + 22, font, paint);
    }

    // ── Header ────────────────────────────────────────────────────────────

    private static void DrawHeader(SKCanvas c, PositionRecord record)
    {
        bool isLong    = record.Direction == PositionDirection.Long;
        string dirText = isLong ? "▲ LONG" : "▼ SHORT";
        SKColor dirColor = isLong ? PnlGreen : PnlRed;

        using var font = MakeFont("Montserrat", 13f, SKFontStyle.Bold);
        float textW  = font.MeasureText(MemoryMarshal.Cast<char, ushort>(dirText.AsSpan()));
        float badgeX = CardW - ChartW - Pad - textW - 16;
        float badgeY = 20f;
        var badgeRect = new SKRect(badgeX - 8, badgeY - 4, badgeX + textW + 8, badgeY + 20);

        using var bgPaint = new SKPaint
        {
            Color       = dirColor.WithAlpha(40),
            IsAntialias = true,
        };
        using var borderPaint = new SKPaint
        {
            Color       = dirColor,
            IsStroke    = true,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        };
        using var textPaint = new SKPaint { Color = dirColor, IsAntialias = true };

        c.DrawRoundRect(badgeRect, 6, 6, bgPaint);
        c.DrawRoundRect(badgeRect, 6, 6, borderPaint);
        c.DrawText(dirText, badgeX, badgeY + 13, font, textPaint);

        // Symbol
        using var symFont  = MakeFont("Montserrat", 26f, SKFontStyle.Bold);
        using var symPaint = new SKPaint { Color = TextPrimary, IsAntialias = true };
        c.DrawText(record.Symbol, Pad, 60 + 26, symFont, symPaint);

        // Trennlinie
        using var linePaint = new SKPaint
        {
            Color       = SKColor.Parse("#333333"),
            StrokeWidth = 1f,
            IsStroke    = true,
            IsAntialias = true,
        };
        c.DrawLine(Pad, 106, CardW - ChartW - Pad, 106, linePaint);
    }

    // ── PnL ───────────────────────────────────────────────────────────────

    private static void DrawPnl(SKCanvas c, PositionRecord record)
    {
        bool isProfit  = record.PnlUsd >= 0;
        SKColor pnlColor = isProfit ? PnlGreen : PnlRed;
        string sign    = isProfit ? "+" : "";

        using var bigFont  = MakeFont("Montserrat", 42f, SKFontStyle.Bold);
        using var subFont  = MakeFont("Montserrat", 16f, SKFontStyle.Normal);
        using var pnlPaint = new SKPaint { Color = pnlColor, IsAntialias = true };
        using var mutPaint = new SKPaint { Color = TextMuted,  IsAntialias = true };

        c.DrawText($"{sign}{record.PnlUsd:F2} USD", Pad, 115 + 42, bigFont, pnlPaint);
        c.DrawText(
            $"{sign}{record.PnlPoints:F2} Punkte  ·  {record.Contracts} Kontrakt{(record.Contracts != 1 ? "e" : "")}",
            Pad + 4, 168 + 16, subFont, mutPaint);
    }

    // ── Daten-Grid ────────────────────────────────────────────────────────

    private static void DrawDataGrid(SKCanvas c, PositionRecord record)
    {
        var rows = new (string Label, string Value)[]
        {
            ("Einstieg", $"{record.OpenTime:HH:mm:ss}  @  {record.AvgEntryPrice:F2}"),
            ("Ausstieg", $"{record.CloseTime:HH:mm:ss}  @  {record.AvgExitPrice:F2}"),
            ("Dauer",    FormatDuration(record.Duration)),
            ("MAE",      $"{record.MAE:+0.00;-0.00} Pkt"),
            ("MFE",      $"{record.MFE:+0.00;-0.00} Pkt"),
            ("Tag",      string.IsNullOrWhiteSpace(record.TradeTag) ? "—" : record.TradeTag),
        };

        using var labelFont  = MakeFont("Inter", 10f, SKFontStyle.Normal);
        using var valueFont  = MakeFont("Inter", 11f, SKFontStyle.Bold);
        using var tagFont    = MakeFont("Montserrat", 10f, SKFontStyle.Bold);
        using var labelPaint = new SKPaint { Color = TextMuted,    IsAntialias = true };
        using var valuePaint = new SKPaint { Color = TextPrimary,  IsAntialias = true };
        using var maePaint   = new SKPaint { Color = PnlRed,       IsAntialias = true };
        using var mfePaint   = new SKPaint { Color = PnlGreen,     IsAntialias = true };
        using var tagPaint   = new SKPaint { Color = GoldColor,    IsAntialias = true };

        int colWidth = (CardW - ChartW - Pad * 2) / 2;
        float startY = 210f;
        float rowH   = 46f;

        for (int i = 0; i < rows.Length; i++)
        {
            int col = i % 2;
            int row = i / 2;
            float x = Pad + col * colWidth;
            float y = startY + row * rowH;

            c.DrawText(rows[i].Label.ToUpper(), x, y + 10, labelFont, labelPaint);

            var vPaint = rows[i].Label == "MAE" ? maePaint
                       : rows[i].Label == "MFE" ? mfePaint
                       : valuePaint;

            if (rows[i].Label == "Tag" && !string.IsNullOrWhiteSpace(record.TradeTag))
                c.DrawText(rows[i].Value, x, y + 25, tagFont, tagPaint);
            else
                c.DrawText(rows[i].Value, x, y + 25, valueFont, vPaint);
        }
    }

    // ── Tages-Stats ───────────────────────────────────────────────────────

    private static void DrawDailyStats(SKCanvas c, DailyStatsSnapshot stats,
        decimal drawdownLimit, decimal balance)
    {
        float barY = CardH - 72f;

        using var bgPaint = new SKPaint { Color = BgLight, IsAntialias = true };
        c.DrawRect(0, barY, CardW - ChartW, 72, bgPaint);

        using var topPaint = new SKPaint
        {
            Color       = SKColor.Parse("#2E2E2E"),
            IsStroke    = true,
            StrokeWidth = 1f,
            IsAntialias = true,
        };
        c.DrawLine(0, barY, CardW - ChartW, barY, topPaint);

        float cellW = (CardW - ChartW) / 3f;
        using var labelFont  = MakeFont("Inter", 9f, SKFontStyle.Normal);
        using var valueFont  = MakeFont("Montserrat", 13f, SKFontStyle.Bold);
        using var labelPaint = new SKPaint { Color = TextMuted, IsAntialias = true };

        DrawStatCell(c, labelFont, valueFont, labelPaint,
            "TAGES-P&L",
            $"{(stats.TotalPnlToday >= 0 ? "+" : "")}{stats.TotalPnlToday:F2} $",
            stats.TotalPnlToday >= 0 ? PnlGreen : PnlRed,
            Pad, barY + 12);

        DrawStatCell(c, labelFont, valueFont, labelPaint,
            "WIN RATE",
            $"{stats.WinRate:F0}%  ({stats.Wins}/{stats.TradesCount})",
            TextPrimary,
            cellW + Pad / 2f, barY + 12);

        if (drawdownLimit > 0)
        {
            decimal used   = Math.Abs(Math.Min(0, stats.TotalPnlToday));
            decimal pct    = drawdownLimit > 0 ? used / drawdownLimit * 100 : 0;
            SKColor ddColor = pct < 50 ? DrawdownSafe : pct < 80 ? DrawdownWarn : DrawdownDanger;

            DrawStatCell(c, labelFont, valueFont, labelPaint,
                "DRAWDOWN",
                $"{used:F0} / {drawdownLimit:F0} $  ({pct:F0}%)",
                ddColor,
                cellW * 2 + Pad / 2f, barY + 12);

            float barLeft  = cellW * 2 + Pad / 2f;
            float barRight = CardW - ChartW - Pad / 2f;
            float barWidth = barRight - barLeft;
            float fillW    = Math.Min((float)(pct / 100m) * barWidth, barWidth);

            using var trackPaint = new SKPaint { Color = SKColor.Parse("#333333"), IsAntialias = true };
            using var fillPaint  = new SKPaint { Color = ddColor,                  IsAntialias = true };
            c.DrawRect(barLeft, barY + 56, barWidth, 6, trackPaint);
            if (fillW > 0) c.DrawRect(barLeft, barY + 56, fillW, 6, fillPaint);
        }

        // Zeitstempel
        string ts = $"Munich Traders  ·  {DateTime.Now:dd.MM.yyyy  HH:mm} CET";
        using var tsFont  = MakeFont("Inter", 8f, SKFontStyle.Normal);
        using var tsPaint = new SKPaint { Color = SKColor.Parse("#555555"), IsAntialias = true };
        float tsW = tsFont.MeasureText(MemoryMarshal.Cast<char, ushort>(ts.AsSpan()));
        c.DrawText(ts, CardW - ChartW - tsW - Pad, barY - 6, tsFont, tsPaint);
    }

    private static void DrawStatCell(SKCanvas c, SKFont lf, SKFont vf, SKPaint lp,
        string label, string value, SKColor valueColor, float x, float y)
    {
        c.DrawText(label, x, y + 9, lf, lp);
        using var vp = new SKPaint { Color = valueColor, IsAntialias = true };
        c.DrawText(value, x, y + 23, vf, vp);
    }

    // ── Chart-Panel ───────────────────────────────────────────────────────

    private static void DrawChartPanel(SKCanvas c, byte[] chartBytes)
    {
        try
        {
            using var bmp = SKBitmap.Decode(chartBytes);
            var dest = new SKRect(CardW - ChartW, 0, CardW, CardH);
            c.DrawBitmap(bmp, dest, BitmapPaint);

            using var font  = MakeFont("Montserrat", 8f, SKFontStyle.Bold);
            using var paint = new SKPaint { Color = GoldColor.WithAlpha(120), IsAntialias = true };
            c.DrawText("CHART", CardW - ChartW + 8, 18, font, paint);
        }
        catch { }
    }

    // ── Hilfsmethoden ─────────────────────────────────────────────────────

    private static SKFont MakeFont(string family, float size, SKFontStyle style)
    {
        var typeface = SKTypeface.FromFamilyName(family, style)
                    ?? SKTypeface.FromFamilyName("Segoe UI", style)
                    ?? SKTypeface.FromFamilyName("Arial", style)
                    ?? SKTypeface.Default;
        return new SKFont(typeface, size)
        {
            Subpixel     = true,
            LinearMetrics = true,
        };
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.Seconds}s";
        if (d.TotalHours < 1)  return $"{d.Minutes}m {d.Seconds:D2}s";
        return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
    }
}

#endif
