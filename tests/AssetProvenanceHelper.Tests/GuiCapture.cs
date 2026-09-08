using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AssetProvenanceHelper.Tests;

/// <summary>
/// Saves a PNG of a form's real rendered window. PrintWindow is used rather
/// than a screen grab so the capture works for a background window and never
/// reads anything else on the desktop.
/// </summary>
internal static class GuiCapture
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    internal static void Save(Form form, string path)
    {
        using var bitmap = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            // 2 = PW_RENDERFULLCONTENT, required for DWM-composited content.
            PrintWindow(form.Handle, dc, 2);
            graphics.ReleaseHdc(dc);
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
