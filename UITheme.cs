using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;

namespace Meridian;

public static class UITheme
{
    private static readonly PrivateFontCollection _privateFonts = new();
    private static FontFamily? _eternaloFamily = null;
    private static FontFamily? _sfProFamily = null;
    private static bool _customFontsLoaded = false;
    private static readonly object _fontLock = new();

    public static void EnsureFontsLoaded()
    {
        if (_customFontsLoaded) return;
        lock (_fontLock)
        {
            if (_customFontsLoaded) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] searchDirs = new[]
                {
                    Path.Combine(baseDir, "Fonts"),
                    Path.Combine(baseDir, @"..\..\..", "Fonts"),
                    @"C:\Users\abhin\Workspace\liquid glass\Fonts",
                    @"C:\Users\abhin\Workspace\meridian\Fonts"
                };

                foreach (var dir in searchDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    string eternalPath = Path.Combine(dir, "Eternalo.ttf");
                    if (File.Exists(eternalPath))
                    {
                        try { _privateFonts.AddFontFile(eternalPath); } catch { }
                    }

                    string sfPath = Path.Combine(dir, "SF-Pro.ttf");
                    if (File.Exists(sfPath))
                    {
                        try { _privateFonts.AddFontFile(sfPath); } catch { }
                    }

                    string sfItalicPath = Path.Combine(dir, "SF-Pro-Italic.ttf");
                    if (File.Exists(sfItalicPath))
                    {
                        try { _privateFonts.AddFontFile(sfItalicPath); } catch { }
                    }

                    break;
                }

                foreach (var fam in _privateFonts.Families)
                {
                    if (_eternaloFamily == null && fam.Name.Equals("Eternalo", StringComparison.OrdinalIgnoreCase))
                    {
                        _eternaloFamily = fam;
                    }
                    if (_sfProFamily == null && (fam.Name.Equals("SF Pro", StringComparison.OrdinalIgnoreCase) || fam.Name.StartsWith("SF Pro", StringComparison.OrdinalIgnoreCase)))
                    {
                        _sfProFamily = fam;
                    }
                }
            }
            catch { }
            _customFontsLoaded = true;
        }
    }

    public static Font GetFont(float sizeInPoints, FontStyle style = FontStyle.Regular)
    {
        EnsureFontsLoaded();
        lock (_fontLock)
        {
            string preferredFamily = sizeInPoints >= 18f ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
            string fallbackFamily = sizeInPoints >= 18f ? "Segoe UI Variable Text" : "Segoe UI Variable Display";

            string[] candidates = { preferredFamily, fallbackFamily, "Segoe UI Variable Small", "Segoe UI", "Aptos", "Arial" };

            foreach (var name in candidates)
            {
                try
                {
                    var f = new Font(name, sizeInPoints, style);
                    if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        f.FontFamily.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                    f.Dispose();
                }
                catch { }
            }

            if (_sfProFamily != null)
            {
                try
                {
                    if (_sfProFamily.IsStyleAvailable(style))
                    {
                        return new Font(_sfProFamily, sizeInPoints, style);
                    }
                    return new Font(_sfProFamily, sizeInPoints, FontStyle.Regular);
                }
                catch { }
            }
            return new Font("Segoe UI", sizeInPoints, style);
        }
    }

    public static Font GetTitleFont(float sizeInPoints, FontStyle style = FontStyle.Bold)
    {
        EnsureFontsLoaded();
        lock (_fontLock)
        {
            string preferredFamily = sizeInPoints >= 16f ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
            string fallbackFamily = sizeInPoints >= 16f ? "Segoe UI Variable Text" : "Segoe UI Variable Display";

            string[] candidates = { preferredFamily, fallbackFamily, "Segoe UI", "Aptos", "Arial" };

            foreach (var name in candidates)
            {
                try
                {
                    var f = new Font(name, sizeInPoints, style);
                    if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        f.FontFamily.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                    f.Dispose();
                }
                catch { }
            }

            if (_eternaloFamily != null)
            {
                try
                {
                    if (_eternaloFamily.IsStyleAvailable(style))
                    {
                        return new Font(_eternaloFamily, sizeInPoints, style);
                    }
                    return new Font(_eternaloFamily, sizeInPoints, FontStyle.Regular);
                }
                catch { }
            }
            return GetFont(sizeInPoints, style);
        }
    }

    public static Font GetEternaloFont(float sizeInPoints, FontStyle style = FontStyle.Regular)
    {
        EnsureFontsLoaded();
        lock (_fontLock)
        {
            if (_eternaloFamily != null)
            {
                try
                {
                    if (_eternaloFamily.IsStyleAvailable(style))
                    {
                        return new Font(_eternaloFamily, sizeInPoints, style);
                    }
                    return new Font(_eternaloFamily, sizeInPoints, FontStyle.Regular);
                }
                catch { }
            }
            return GetFont(sizeInPoints, style);
        }
    }
}
