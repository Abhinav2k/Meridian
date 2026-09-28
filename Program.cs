using System;
using System.Windows.Forms;

namespace LiquidGlassCircle;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm());
    }
}
