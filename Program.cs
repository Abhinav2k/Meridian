using System;
using System.Windows.Forms;

namespace Meridian;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.ExceptionObject?.ToString() ?? "Unknown unhandled exception");
        };
        Application.ThreadException += (s, e) =>
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.Exception?.ToString() ?? "Unknown thread exception");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), e.Exception?.ToString() ?? "Unknown task exception");
        };

        try
        {
            ApplicationConfiguration.Initialize();

            bool isSettings = false;
            if (args != null && args.Length > 0)
            {
                foreach (var arg in args)
                {
                    if (arg.Equals("--generate-assets", StringComparison.OrdinalIgnoreCase))
                    {
                        GenerateAssets();
                        return;
                    }
                    if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase) ||
                        arg.Equals("-settings", StringComparison.OrdinalIgnoreCase) ||
                        arg.Equals("/settings", StringComparison.OrdinalIgnoreCase))
                    {
                        isSettings = true;
                        break;
                    }
                }
            }

            // Ensure desktop shortcuts are created on first run
            if (!AppSettings.IsDesktopShortcutPresent("Meridian Settings.lnk"))
            {
                AppSettings.CreateDesktopShortcut("Meridian Settings.lnk", "--settings", "Meridian Settings & Control");
            }
            if (!AppSettings.IsDesktopShortcutPresent("Meridian Island.lnk"))
            {
                AppSettings.CreateDesktopShortcut("Meridian Island.lnk", "", "Meridian Dynamic Island");
            }

            if (isSettings)
            {
                Application.Run(new SettingsForm());
            }
            else
            {
                Application.Run(new OverlayForm());
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
        }
    }

    private static void GenerateAssets()
    {
        try
        {
            // Target project Assets directory
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string projectAssets = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets"));
            string targetDir = Directory.Exists(projectAssets) ? projectAssets : Path.Combine(Directory.GetCurrentDirectory(), "Assets");

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string icoPath = Path.Combine(targetDir, "meridian.ico");
            string pngPath = Path.Combine(targetDir, "meridian-logo.png");
            string svgPath = Path.Combine(targetDir, "meridian-logo.svg");

            LogoRenderer.SaveIco(icoPath, new[] { 16, 24, 32, 48, 64, 128, 256 });
            using (var bmp = LogoRenderer.CreateBitmap(512, drawBackground: true))
            {
                bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            LogoRenderer.SaveSvg(svgPath, 512);

            Console.WriteLine($"[Meridian] Successfully generated branding assets in: {targetDir}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Meridian] Failed to generate assets: {ex.Message}");
        }
    }
}
