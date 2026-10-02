using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Meridian;

public sealed partial class SettingsForm : Form
{
    private const int CardX = 0;
    private const int CardY = 0;
    private const int CardWidth = 860;
    private const int CardHeight = 580;
    private const int SidebarWidth = 220;

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, [In] ref BITMAPINFO pbmi, uint pila,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdc, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x00000002;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_MICA_EFFECT = 1029;

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;

    private sealed class FastSurface : IDisposable
    {
        public IntPtr MemDC { get; private set; }
        public IntPtr HBitmap { get; private set; }
        public IntPtr BitsPtr { get; private set; }
        private IntPtr _oldBitmap;
        public int Width { get; }
        public int Height { get; }
        public Bitmap Bmp { get; }
        public Graphics G { get; }

        public FastSurface(int width, int height)
        {
            Width = width;
            Height = height;

            IntPtr screenDC = GetDC(IntPtr.Zero);
            MemDC = CreateCompatibleDC(screenDC);

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // Top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            HBitmap = CreateDIBSection(MemDC, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            BitsPtr = bits;
            _oldBitmap = SelectObject(MemDC, HBitmap);
            ReleaseDC(IntPtr.Zero, screenDC);

            Bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            G = Graphics.FromImage(Bmp);
            G.SmoothingMode = SmoothingMode.AntiAlias;
            G.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            G.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public void Dispose()
        {
            G.Dispose();
            Bmp.Dispose();
            if (MemDC != IntPtr.Zero)
            {
                SelectObject(MemDC, _oldBitmap);
                DeleteObject(HBitmap);
                DeleteDC(MemDC);
                MemDC = IntPtr.Zero;
            }
        }
    }

    private FastSurface? _surface;
    private Graphics? _reusableUiGraphics => _surface?.G;
    private bool _uiDirty = true;

    // Navigation Tabs
    private enum SettingsTab
    {
        IslandNotch = 0,
        LiveWeather = 1,
        MediaPlayers = 2,
        ShortcutsSystem = 3,
        AboutKeys = 4
    }

    private SettingsTab _activeTab = SettingsTab.IslandNotch;
    private int _hoveredTab = -1;

    // Toast feedback message
    private string _toastMessage = "";
    private DateTime _toastUntil = DateTime.MinValue;

    // Interactive Fields State
    private string _activeInputField = "";
    private bool _caretVisible = true;
    private readonly System.Windows.Forms.Timer _caretTimer = new();
    private readonly System.Windows.Forms.Timer _renderTimer = new();

    // Weather Form Fields
    private bool _weatherUseAuto;
    private string _weatherCity = "";
    private string _weatherCountry = "";
    private string _weatherLat = "";
    private string _weatherLon = "";
    private string _weatherStatus = "Ready";

    // Player Form Fields
    private List<PlayerItem> _players = new();
    private string _newPlayerName = "";
    private string _newPlayerUrl = "";
    private string _newPlayerIcon = "music";

    // macOS Window controls & search
    private bool _isTrafficLightsHovered;
    private int _hoveredTrafficLight = -1; // 0 = close, 1 = min, 2 = zoom
    private string _searchText = "";

    private float _scale = 1.0f;
    public float ScaleFactor => _scale;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00040000; // WS_THICKFRAME
            cp.Style &= ~0x00C00000; // Explicitly remove WS_CAPTION to suppress native titlebar
            return cp;
        }
    }

    public SettingsForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        Text = "Meridian Settings";
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "meridian.ico");
            if (File.Exists(icoPath))
            {
                Icon = new Icon(icoPath);
            }
        }
        catch { }
        StartPosition = FormStartPosition.CenterScreen;
        _scale = DeviceDpi / 96.0f;
        int scaledW = (int)Math.Round(CardWidth * _scale);
        int scaledH = (int)Math.Round(CardHeight * _scale);
        ClientSize = new Size(scaledW, scaledH);
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);

        _surface = new FastSurface(scaledW, scaledH);

        LoadFormData();

        _caretTimer.Interval = 500;
        _caretTimer.Tick += (s, e) =>
        {
            _caretVisible = !_caretVisible;
            if (!string.IsNullOrEmpty(_activeInputField))
            {
                InvalidateAndRender();
            }
        };
        _caretTimer.Start();

        _renderTimer.Interval = 16; // ~60 FPS on interaction / animations
        _renderTimer.Tick += (s, e) =>
        {
            if (DateTime.UtcNow < _toastUntil)
            {
                InvalidateAndRender();
            }
        };
        _renderTimer.Start();
    }

    private void LoadFormData()
    {
        // Weather
        var (auto, c, co, la, lo) = LiveWeatherService.LoadLocationConfig();
        _weatherUseAuto = auto;
        _weatherCity = c;
        _weatherCountry = co;
        _weatherLat = la.ToString("F4");
        _weatherLon = lo.ToString("F4");

        // Players
        _players = new List<PlayerItem>(PlayerService.GetPlayers());
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96.0f;
        int scaledW = (int)Math.Round(CardWidth * _scale);
        int scaledH = (int)Math.Round(CardHeight * _scale);
        ClientSize = new Size(scaledW, scaledH);
        _surface?.Dispose();
        _surface = new FastSurface(scaledW, scaledH);
        InvalidateAndRender();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
        int colorNone = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: completely eliminate native titlebar caption painting
        DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
        int corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int backdrop = AppSettings.Current.BackdropType;
        if (backdrop < 2 || backdrop > 4) backdrop = 3; // Default to Desktop Acrylic (translucent)
        if (DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
        {
            int micaLegacy = 1;
            DwmSetWindowAttribute(Handle, DWMWA_MICA_EFFECT, ref micaLegacy, sizeof(int));
        }
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref margins);

        double opacity = AppSettings.Current.WindowOpacity;
        if (opacity < 0.5 || opacity > 1.0) opacity = 0.85;
        ApplyTransparency(opacity, showToast: false);
    }

    public void ApplyTransparency(double opacity, bool showToast = true)
    {
        if (opacity < 0.5) opacity = 0.5;
        if (opacity > 1.0) opacity = 1.0;
        AppSettings.Current.WindowOpacity = opacity;
        AppSettings.Save();

        int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
        if (opacity >= 0.999)
        {
            if ((exStyle & WS_EX_LAYERED) != 0)
            {
                SetWindowLong(Handle, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
            }
        }
        else
        {
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(Handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }
            byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);
            SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);
        }

        if (showToast)
        {
            string pct = ((int)Math.Round(opacity * 100.0)).ToString();
            ShowToast($"✓ Glass Transparency set to {pct}%");
        }
        InvalidateAndRender();
    }

    public void ApplyBackdrop(int type)
    {
        if (type < 2 || type > 4) type = 3;
        AppSettings.Current.BackdropType = type;
        AppSettings.Save();

        int dark = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
        int colorNone = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref colorNone, sizeof(int));
        int corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        if (DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0)
        {
            int micaLegacy = 1;
            DwmSetWindowAttribute(Handle, DWMWA_MICA_EFFECT, ref micaLegacy, sizeof(int));
        }
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref margins);

        string name = type switch
        {
            3 => "Translucent Acrylic",
            4 => "Mica Alt",
            2 => "Mica",
            _ => "System Backdrop"
        };
        ShowToast($"Backdrop set to {name}");
        InvalidateAndRender();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE)
        {
            // Unconditionally return IntPtr.Zero for both WParam != 0 and WParam == 0
            // This eliminates native non-client titlebar calculations completely.
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_ERASEBKGND)
        {
            m.Result = (IntPtr)1;
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            int hit = (int)m.Result.ToInt64();
            if (hit >= 10 && hit <= 17) // HTLEFT through HTBOTTOMRIGHT
            {
                m.Result = (IntPtr)HTCLIENT;
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Suppress default background rendering so Mica shines through
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_surface == null || IsDisposed) return;

        if (_uiDirty)
        {
            RenderUI();
            _uiDirty = false;
        }

        IntPtr hdc = e.Graphics.GetHdc();
        try
        {
            int scaledW = (int)Math.Round(CardWidth * _scale);
            int scaledH = (int)Math.Round(CardHeight * _scale);
            BitBlt(hdc, 0, 0, scaledW, scaledH, _surface.MemDC, 0, 0, 0x00CC0020);
        }
        finally
        {
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    public void InvalidateAndRender()
    {
        _uiDirty = true;
        Invalidate();
        Update();
    }

    public void InvalidateBackdrop()
    {
        InvalidateAndRender();
    }

    public void ShowToast(string message, double seconds = 3.0)
    {
        _toastMessage = message;
        _toastUntil = DateTime.UtcNow.AddSeconds(seconds);
        InvalidateAndRender();
    }
}
