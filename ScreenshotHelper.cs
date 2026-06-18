#if ATASX

using System.Drawing;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace MunichTraders.TradeRecap;

/// <summary>
/// Chart-Screenshots in ATAS X (Windows) via GDI32 P/Invoke — kein System.Drawing.Common nötig.
/// Gibt null zurück auf macOS oder wenn die Aufnahme fehlschlägt.
/// </summary>
internal static class ScreenshotHelper
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool   BitBlt(IntPtr hDestDC, int x, int y, int w, int h, IntPtr hSrcDC, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")] private static extern bool   DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool   DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern int    GetDIBits(IntPtr hDC, IntPtr hBmp, uint start, uint lines, byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int   ReleaseDC(IntPtr hWnd, IntPtr hDC);

    private const uint SRCCOPY = 0x00CC0020;

    public static byte[]? CaptureRegion(Rectangle region)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        if (region.Width <= 0 || region.Height <= 0)
            return null;

        IntPtr desktop   = GetDesktopWindow();
        IntPtr desktopDC = GetDC(desktop);
        if (desktopDC == IntPtr.Zero) return null;

        IntPtr memDC = CreateCompatibleDC(desktopDC);
        IntPtr hBmp  = CreateCompatibleBitmap(desktopDC, region.Width, region.Height);

        try
        {
            // Bitmap in memDC einsetzen, Screen kopieren, sofort wieder herausnehmen.
            // GetDIBits setzt voraus dass hBmp zum Zeitpunkt des Aufrufs in KEINEM DC selektiert ist.
            IntPtr prev = SelectObject(memDC, hBmp);
            bool   ok   = BitBlt(memDC, 0, 0, region.Width, region.Height,
                                 desktopDC, region.X, region.Y, SRCCOPY);
            SelectObject(memDC, prev);   // hBmp deselektieren — Pflicht vor GetDIBits

            if (!ok) return null;

            return GdiBitmapToSkiaPng(hBmp, region.Width, region.Height, desktopDC);
        }
        finally
        {
            DeleteObject(hBmp);
            DeleteDC(memDC);
            ReleaseDC(desktop, desktopDC);
        }
    }

    private static byte[]? GdiBitmapToSkiaPng(IntPtr hBmp, int width, int height, IntPtr hdc)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize        = 40,
            biWidth       = width,
            biHeight      = -height, // negativ = top-down (Zeilen von oben nach unten)
            biPlanes      = 1,
            biBitCount    = 32,
            biCompression = 0,       // BI_RGB
        };

        byte[] pixelData = new byte[width * height * 4];

        // Übergabe von hdc (desktopDC) als Referenz-DC für Farbinformationen.
        // GetDIBits gibt die Anzahl kopierter Scanlines zurück; 0 = Fehler.
        int lines = GetDIBits(hdc, hBmp, 0, (uint)height, pixelData, ref bmi, 0);
        if (lines == 0) return null;

        // GDI liefert BGRA mit alpha = 0; wir brauchen RGBA mit alpha = 255
        for (int i = 0; i < pixelData.Length; i += 4)
        {
            (pixelData[i], pixelData[i + 2]) = (pixelData[i + 2], pixelData[i]);
            pixelData[i + 3] = 255;
        }

        // SKAlphaType.Opaque: alle Pixel sind volldeckend — kein Premultiply nötig
        using var skBmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        unsafe
        {
            fixed (byte* ptr = pixelData)
                Buffer.MemoryCopy(ptr, skBmp.GetPixels().ToPointer(), skBmp.ByteCount, pixelData.Length);
        }

        using var ms = new MemoryStream();
        skBmp.Encode(ms, SKEncodedImageFormat.Png, 100);
        return ms.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int   biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int   biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
}

#endif
