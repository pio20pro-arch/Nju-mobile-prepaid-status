using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NjuPrepaidStatus.UI;

public static class TrayIconFactory
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon CreateNumberIcon(string text)
    {
        var size = Math.Max(SystemInformation.SmallIconSize.Width, 16);
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        var fontSize = text.Length switch
        {
            <= 2 => size * 0.80f,
            3 => size * 0.64f,
            _ => size * 0.55f
        };
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var measured = TextRenderer.MeasureText(graphics, text, font, Size.Empty, flags);
        var x = (size - measured.Width) / 2;
        var y = (size - measured.Height) / 2;
        if (text.Length == 2)
        {
            y -= 1;
        }

        TextRenderer.DrawText(graphics, text, font, new Rectangle(x, y, measured.Width, measured.Height), Color.White, flags);

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
