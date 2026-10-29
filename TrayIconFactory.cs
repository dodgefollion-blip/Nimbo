using System;
using System.Runtime.InteropServices;

namespace Nimbo;

internal static class TrayIconFactory
{
    public static System.Drawing.Icon Create(double usagePercent)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            var color = usagePercent >= 90
                ? System.Drawing.Color.FromArgb(255, 255, 125, 134)
                : usagePercent >= 75
                    ? System.Drawing.Color.FromArgb(255, 244, 196, 109)
                    : System.Drawing.Color.FromArgb(255, 98, 224, 193);
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 19, 27, 40));
            using var ring = new System.Drawing.Pen(color, 3.2f);
            using var font = new System.Drawing.Font("Segoe UI", 17, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var text = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var format = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
            graphics.FillEllipse(fill, 1, 1, 30, 30);
            graphics.DrawEllipse(ring, 2, 2, 28, 28);
            graphics.DrawString("N", font, text, new System.Drawing.RectangleF(0, -1, 32, 32), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var source = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)source.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
