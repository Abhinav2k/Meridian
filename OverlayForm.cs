using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace LiquidGlassCircle;

internal sealed class OverlayForm : Form
{
    private const int SurfaceWidth = 600;
    private const int SurfaceHeight = 250;
    private const int HalfWidth = SurfaceWidth / 2;   // 300
    private const int HalfHeight = SurfaceHeight / 2; // 125

    // Default expanded size on hover (500x180 - doubled height)
    private const int DefaultPillWidth = 500;
    private const int DefaultPillHeight = 180;

    // Compact resting size with clock
    private const int CompactPillWidth = 190;
    private const int CompactPillHeight = 44;

    private const int TopPadding = 18;
    private const double AnimationDuration = 0.85; // seconds

    private const uint WdaExcludeFromCapture = 0x11;
    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;

    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;
    private const uint UlwAlpha = 0x02;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        IntPtr pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
        public POINT(int x, int y) { this.x = x; this.y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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

    private sealed class PillGeometry
    {
        public readonly double CenterX;
        public readonly double CenterY;
        public readonly double HalfWidth;
        public readonly double HalfHeight;
        public readonly double Radius;

        public PillGeometry(double cx, double cy, double hw, double hh, double r)
        {
            CenterX = cx;
            CenterY = cy;
            HalfWidth = hw;
            HalfHeight = hh;
            Radius = r;
        }
    }

    private enum WeatherIconType
    {
        ClearSky,
        PartlyCloudy,
        Overcast,
        Fog,
        Drizzle,
        Rain,
        Snow,
        Thunderstorm
    }

    private sealed class WeatherData
    {
        public string City { get; set; } = "Kochi";
        public int Temperature { get; set; } = 27;
        public string Condition { get; set; } = "Light Drizzle";
        public WeatherIconType IconType { get; set; } = WeatherIconType.Drizzle;
        public int HighTemp { get; set; } = 28;
        public int LowTemp { get; set; } = 25;
        public int Humidity { get; set; } = 80;
        public int WindSpeed { get; set; } = 7;
        public int RainProb { get; set; } = 85;
    }

    private FastSurface? _screenCapturer;
    private FastSurface? _renderSurface;
    private readonly byte[] _halfRawBuffer = new byte[HalfWidth * HalfHeight * 4];
    private readonly byte[] _blurHBuffer = new byte[HalfWidth * HalfHeight * 4];
    private readonly byte[] _blurredBuffer = new byte[HalfWidth * HalfHeight * 4];

    private string _lastTimeString = "";
    private byte[]? _timeMask;
    private int _timeWidth;
    private int _timeHeight;
    private readonly object _timeLock = new();

    private byte[]? _weatherMask;
    private int _weatherWidth;
    private int _weatherHeight;
    private readonly object _weatherLock = new();

    private readonly AutoResetEvent _renderSignal = new(false);
    private Thread? _renderThread;
    private volatile bool _running = true;
    private IntPtr _hwnd;
    private double _progress = 0.0;
    private double _hoverPos = 0.0; // Spring position (0.0 to 1.0+)
    private double _hoverVel = 0.0; // Spring velocity
    private double _animDirection = 1.0; // +1.0 = forward (expand), -1.0 = backward (retract)
    private readonly Stopwatch _frameStopwatch = new();
    private volatile PillGeometry _currentGeometry = new(SurfaceWidth * 0.5, -20.0, 14.0, 14.0, 14.0);

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(SurfaceWidth, SurfaceHeight);

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(
            workingArea.Left + (workingArea.Width - SurfaceWidth) / 2,
            Screen.PrimaryScreen?.Bounds.Top ?? 0);

        Shown += (_, _) =>
        {
            _frameStopwatch.Start();
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "LiquidPillRenderLoop"
            };
            _renderThread.Start();
            _renderSignal.Set();
        };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
            else if (e.KeyCode is Keys.Right or Keys.F or Keys.Down)
            {
                StepForward();
            }
            else if (e.KeyCode is Keys.Left or Keys.B or Keys.Up)
            {
                StepBackward();
            }
            else if (e.KeyCode == Keys.Space)
            {
                ToggleDirection();
            }
            else if (e.KeyCode == Keys.R)
            {
                ReplayFromStart();
            }
            else if (e.KeyCode == Keys.S)
            {
                SaveSnapshot();
            }
        };

        MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) Close();
            else if (e.Button == MouseButtons.Left) ToggleDirection();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hwnd = Handle;
        _screenCapturer = new FastSurface(SurfaceWidth, SurfaceHeight);
        _renderSurface = new FastSurface(SurfaceWidth, SurfaceHeight);
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);

        UpdateTimeMaskIfNeeded();

        // Initialize instant fallback weather mask
        var (wMask, wW, wH) = PrecomputeWeatherMask(new WeatherData());
        lock (_weatherLock)
        {
            _weatherMask = wMask;
            _weatherWidth = wW;
            _weatherHeight = wH;
        }

        // Fetch live real-time local weather in background
        FetchWeatherAsync();
    }

    private void FetchWeatherAsync()
    {
        Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string city = "Kochi";
                double lat = 9.9406, lon = 76.2653;

                try
                {
                    string ipJson = await client.GetStringAsync("http://ip-api.com/json/");
                    using var ipDoc = JsonDocument.Parse(ipJson);
                    if (ipDoc.RootElement.TryGetProperty("city", out var cityElem))
                        city = cityElem.GetString() ?? city;
                    if (ipDoc.RootElement.TryGetProperty("lat", out var latElem))
                        lat = latElem.GetDouble();
                    if (ipDoc.RootElement.TryGetProperty("lon", out var lonElem))
                        lon = lonElem.GetDouble();
                }
                catch { }

                string weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=auto";
                string weatherJson = await client.GetStringAsync(weatherUrl);
                using var wDoc = JsonDocument.Parse(weatherJson);

                var current = wDoc.RootElement.GetProperty("current");
                int temp = (int)Math.Round(current.GetProperty("temperature_2m").GetDouble());
                int humidity = (int)Math.Round(current.GetProperty("relative_humidity_2m").GetDouble());
                int wCode = current.GetProperty("weather_code").GetInt32();
                int wind = (int)Math.Round(current.GetProperty("wind_speed_10m").GetDouble());

                var daily = wDoc.RootElement.GetProperty("daily");
                int hi = (int)Math.Round(daily.GetProperty("temperature_2m_max")[0].GetDouble());
                int lo = (int)Math.Round(daily.GetProperty("temperature_2m_min")[0].GetDouble());

                var (cond, iconType) = GetWeatherInfo(wCode);
                var wData = new WeatherData
                {
                    City = city,
                    Temperature = temp,
                    Condition = cond,
                    IconType = iconType,
                    HighTemp = hi,
                    LowTemp = lo,
                    Humidity = humidity,
                    WindSpeed = wind,
                    RainProb = Math.Clamp(humidity + 10, 0, 100)
                };

                var (mask, w, h) = PrecomputeWeatherMask(wData);
                lock (_weatherLock)
                {
                    _weatherMask = mask;
                    _weatherWidth = w;
                    _weatherHeight = h;
                }
            }
            catch { }
        });
    }

    private static (string condition, WeatherIconType icon) GetWeatherInfo(int code) => code switch
    {
        0 => ("Clear Sky", WeatherIconType.ClearSky),
        1 or 2 => ("Partly Cloudy", WeatherIconType.PartlyCloudy),
        3 => ("Overcast", WeatherIconType.Overcast),
        45 or 48 => ("Foggy", WeatherIconType.Fog),
        51 or 53 or 55 => ("Light Drizzle", WeatherIconType.Drizzle),
        61 or 63 or 65 => ("Rain", WeatherIconType.Rain),
        71 or 73 or 75 => ("Snow", WeatherIconType.Snow),
        80 or 81 or 82 => ("Rain Showers", WeatherIconType.Rain),
        95 or 96 or 99 => ("Thunderstorm", WeatherIconType.Thunderstorm),
        _ => ("Pleasant", WeatherIconType.PartlyCloudy)
    };

    private static void DrawLocationPin(Graphics g, float cx, float cy, float size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);

        float r = size * 0.36f;
        float topY = cy - size * 0.40f;

        using var path = new GraphicsPath(FillMode.Alternate);
        path.AddArc(cx - r, topY, r * 2f, r * 2f, -145, 290);
        path.AddLine(cx + r * 0.82f, topY + r * 1.05f, cx, cy + size * 0.45f);
        path.AddLine(cx, cy + size * 0.45f, cx - r * 0.82f, topY + r * 1.05f);
        path.CloseFigure();

        float holeR = r * 0.38f;
        path.AddEllipse(cx - holeR, topY + r - holeR, holeR * 2f, holeR * 2f);

        g.FillPath(brush, path);
    }

    private static void DrawWaterDrop(Graphics g, float cx, float cy, float size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);

        float r = size * 0.36f;
        float botY = cy + size * 0.12f;
        g.FillEllipse(brush, cx - r, botY - r, r * 2f, r * 2f);

        using var path = new GraphicsPath();
        path.AddLine(cx - r * 0.85f, botY - r * 0.20f, cx, cy - size * 0.45f);
        path.AddLine(cx, cy - size * 0.45f, cx + r * 0.85f, botY - r * 0.20f);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawWindBreeze(Graphics g, float cx, float cy, float size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, size * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        g.DrawLine(pen, cx - size * 0.40f, cy - size * 0.18f, cx + size * 0.15f, cy - size * 0.18f);
        g.DrawArc(pen, cx - size * 0.05f, cy - size * 0.42f, size * 0.36f, size * 0.36f, 90, -220);

        g.DrawLine(pen, cx - size * 0.30f, cy + size * 0.18f, cx + size * 0.25f, cy + size * 0.18f);
        g.DrawArc(pen, cx + size * 0.10f, cy + size * 0.05f, size * 0.32f, size * 0.32f, 90, 220);
    }

    private static void DrawRainPrecip(Graphics g, float cx, float cy, float size, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, size * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        g.DrawLine(pen, cx - size * 0.25f, cy - size * 0.32f, cx - size * 0.35f, cy + size * 0.32f);
        g.DrawLine(pen, cx + size * 0.02f, cy - size * 0.32f, cx - size * 0.08f, cy + size * 0.32f);
        g.DrawLine(pen, cx + size * 0.30f, cy - size * 0.32f, cx + size * 0.20f, cy + size * 0.32f);
    }

    private static void DrawWeatherHeroIcon(Graphics g, WeatherIconType type, float cx, float cy, float size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brushWhite = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        using var penWhite = new Pen(Color.FromArgb(255, 255, 255, 255), size * 0.10f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        switch (type)
        {
            case WeatherIconType.ClearSky:
                float sunR = size * 0.28f;
                g.FillEllipse(brushWhite, cx - sunR, cy - sunR, sunR * 2, sunR * 2);
                float rayLen = size * 0.16f;
                float rayDist = size * 0.38f;
                for (int i = 0; i < 8; i++)
                {
                    double angle = i * Math.PI / 4.0;
                    float rx1 = cx + (float)(Math.Cos(angle) * rayDist);
                    float ry1 = cy + (float)(Math.Sin(angle) * rayDist);
                    float rx2 = cx + (float)(Math.Cos(angle) * (rayDist + rayLen));
                    float ry2 = cy + (float)(Math.Sin(angle) * (rayDist + rayLen));
                    g.DrawLine(penWhite, rx1, ry1, rx2, ry2);
                }
                break;

            case WeatherIconType.PartlyCloudy:
                float sR = size * 0.22f;
                float sCx = cx - size * 0.18f;
                float sCy = cy - size * 0.18f;
                g.FillEllipse(brushWhite, sCx - sR, sCy - sR, sR * 2, sR * 2);
                for (int i = 0; i < 6; i++)
                {
                    double angle = (i * Math.PI / 3.0) - Math.PI * 0.2;
                    float rx1 = sCx + (float)(Math.Cos(angle) * (sR + 2));
                    float ry1 = sCy + (float)(Math.Sin(angle) * (sR + 2));
                    float rx2 = sCx + (float)(Math.Cos(angle) * (sR + size * 0.12f));
                    float ry2 = sCy + (float)(Math.Sin(angle) * (sR + size * 0.12f));
                    g.DrawLine(penWhite, rx1, ry1, rx2, ry2);
                }
                DrawCloudShape(g, cx + size * 0.05f, cy + size * 0.10f, size * 0.75f, brushWhite);
                break;

            case WeatherIconType.Overcast:
                DrawCloudShape(g, cx, cy, size * 0.90f, brushWhite);
                break;

            case WeatherIconType.Drizzle:
            case WeatherIconType.Rain:
                DrawCloudShape(g, cx, cy - size * 0.12f, size * 0.85f, brushWhite);
                float rainY = cy + size * 0.22f;
                using (var rainPen = new Pen(Color.FromArgb(220, 255, 255, 255), size * 0.08f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(rainPen, cx - size * 0.22f, rainY, cx - size * 0.28f, rainY + size * 0.22f);
                    g.DrawLine(rainPen, cx, rainY, cx - size * 0.06f, rainY + size * 0.22f);
                    g.DrawLine(rainPen, cx + size * 0.22f, rainY, cx + size * 0.16f, rainY + size * 0.22f);
                }
                break;

            case WeatherIconType.Thunderstorm:
                DrawCloudShape(g, cx, cy - size * 0.15f, size * 0.85f, brushWhite);
                using (var boltPath = new GraphicsPath())
                {
                    boltPath.AddLine(cx + size * 0.04f, cy + size * 0.08f, cx - size * 0.12f, cy + size * 0.28f);
                    boltPath.AddLine(cx - size * 0.12f, cy + size * 0.28f, cx + size * 0.02f, cy + size * 0.28f);
                    boltPath.AddLine(cx + size * 0.02f, cy + size * 0.28f, cx - size * 0.08f, cy + size * 0.48f);
                    using var boltPen = new Pen(Color.FromArgb(255, 255, 255, 255), size * 0.08f) { LineJoin = LineJoin.Miter };
                    g.DrawPath(boltPen, boltPath);
                }
                break;

            case WeatherIconType.Snow:
                DrawCloudShape(g, cx, cy - size * 0.12f, size * 0.85f, brushWhite);
                float snowY = cy + size * 0.26f;
                float dotR = size * 0.06f;
                g.FillEllipse(brushWhite, cx - size * 0.22f - dotR, snowY - dotR, dotR * 2, dotR * 2);
                g.FillEllipse(brushWhite, cx - dotR, snowY - dotR, dotR * 2, dotR * 2);
                g.FillEllipse(brushWhite, cx + size * 0.22f - dotR, snowY - dotR, dotR * 2, dotR * 2);
                break;

            case WeatherIconType.Fog:
            default:
                float fogW = size * 0.70f;
                float lineSp = size * 0.18f;
                using (var fogPen = new Pen(Color.FromArgb(230, 255, 255, 255), size * 0.10f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(fogPen, cx - fogW * 0.5f, cy - lineSp, cx + fogW * 0.5f, cy - lineSp);
                    g.DrawLine(fogPen, cx - fogW * 0.4f, cy, cx + fogW * 0.4f, cy);
                    g.DrawLine(fogPen, cx - fogW * 0.5f, cy + lineSp, cx + fogW * 0.5f, cy + lineSp);
                }
                break;
        }
    }

    private static void DrawCloudShape(Graphics g, float cx, float cy, float size, Brush brush)
    {
        float w = size * 0.85f;
        float h = size * 0.45f;
        float botY = cy + h * 0.25f;

        g.FillEllipse(brush, cx - w * 0.45f, botY - h * 0.40f, w * 0.90f, h * 0.80f);
        g.FillEllipse(brush, cx - w * 0.35f, cy - h * 0.35f, w * 0.45f, w * 0.45f);
        g.FillEllipse(brush, cx - w * 0.15f, cy - h * 0.70f, w * 0.55f, w * 0.55f);
    }

    private static (byte[] mask, int width, int height) PrecomputeWeatherMask(WeatherData wData)
    {
        const float superScale = 4.0f;
        int targetW = 440;
        int targetH = 145;
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var fontCity = new Font("Segoe UI Variable Display", 13.5f * superScale, FontStyle.Bold);
            using var fontDate = new Font("Segoe UI Variable Display", 10.5f * superScale, FontStyle.Regular);
            using var fontTemp = new Font("Segoe UI Variable Display", 36.0f * superScale, FontStyle.Bold);
            using var fontCond = new Font("Segoe UI Variable Display", 13.0f * superScale, FontStyle.Bold);
            using var fontSub = new Font("Segoe UI Variable Display", 10.5f * superScale, FontStyle.Regular);
            using var fontPill = new Font("Segoe UI Variable Display", 10.0f * superScale, FontStyle.Bold);

            // 1. Header Row: Procedural Location Pin + City (Left) & Date (Right)
            float pinX = 20f * superScale;
            float pinY = 18f * superScale;
            DrawLocationPin(g, pinX, pinY, 14f * superScale, Color.FromArgb(255, 255, 255, 255));

            string locStr = wData.City;
            using (var brushWhite = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(locStr, fontCity, brushWhite, 32f * superScale, 10f * superScale, StringFormat.GenericDefault);
            }

            string dateStr = DateTime.Now.ToString("dddd, MMM d");
            using (var brushSub = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
            {
                var dateSize = g.MeasureString(dateStr, fontDate, PointF.Empty, StringFormat.GenericDefault);
                g.DrawString(dateStr, fontDate, brushSub, (targetW - 20f) * superScale - dateSize.Width, 12f * superScale, StringFormat.GenericDefault);
            }

            // 2. Middle Row: Hero Temperature + Vector Weather Condition Glyph + Condition Summary
            string tempStr = wData.Temperature + "°";
            using (var brushTemp = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(tempStr, fontTemp, brushTemp, 16f * superScale, 40f * superScale, StringFormat.GenericDefault);
            }

            var tempSize = g.MeasureString(tempStr, fontTemp, PointF.Empty, StringFormat.GenericDefault);
            float iconCx = 16f * superScale + tempSize.Width + 24f * superScale;
            float iconCy = 64f * superScale;
            DrawWeatherHeroIcon(g, wData.IconType, iconCx, iconCy, 34f * superScale);

            float condTextX = iconCx + 26f * superScale;
            using (var brushCond = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
            {
                g.DrawString(wData.Condition, fontCond, brushCond, condTextX, 46f * superScale, StringFormat.GenericDefault);
            }

            string hiLoStr = "H: " + wData.HighTemp + "°   L: " + wData.LowTemp + "°";
            using (var brushHiLo = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
            {
                g.DrawString(hiLoStr, fontSub, brushHiLo, condTextX, 68f * superScale, StringFormat.GenericDefault);
            }

            // 3. Bottom Row: 3 Micro-metric Badges with Procedural Vector Icons
            float badgeY = 110f * superScale;
            float badgeSpacing = 142f * superScale;

            // Metric 1: Humidity
            float b1X = 16f * superScale;
            DrawWaterDrop(g, b1X + 6f * superScale, badgeY + 6f * superScale, 13f * superScale, Color.FromArgb(220, 255, 255, 255));
            using (var brushB1 = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            {
                g.DrawString(wData.Humidity + "% Humidity", fontPill, brushB1, b1X + 18f * superScale, badgeY, StringFormat.GenericDefault);
            }

            // Metric 2: Wind
            float b2X = b1X + badgeSpacing;
            DrawWindBreeze(g, b2X + 6f * superScale, badgeY + 6f * superScale, 13f * superScale, Color.FromArgb(220, 255, 255, 255));
            using (var brushB2 = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            {
                g.DrawString(wData.WindSpeed + " km/h Wind", fontPill, brushB2, b2X + 18f * superScale, badgeY, StringFormat.GenericDefault);
            }

            // Metric 3: Precip
            float b3X = b2X + badgeSpacing;
            DrawRainPrecip(g, b3X + 6f * superScale, badgeY + 6f * superScale, 13f * superScale, Color.FromArgb(220, 255, 255, 255));
            using (var brushB3 = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            {
                g.DrawString(wData.RainProb + "% Precip", fontPill, brushB3, b3X + 18f * superScale, badgeY, StringFormat.GenericDefault);
            }
        }

        // Downsample 4x to target resolution with area-averaging
        byte[] mask = new byte[targetW * targetH];
        var data = superBmp.LockBits(new Rectangle(0, 0, superW, superH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < 4; dy++)
                    {
                        int sy = y * 4 + dy;
                        int rowOffset = sy * superW * 4;
                        for (int dx = 0; dx < 4; dx++)
                        {
                            int sx = x * 4 + dx;
                            sum += scan[rowOffset + sx * 4 + 3];
                        }
                    }
                    mask[y * targetW + x] = (byte)(sum >> 4);
                }
            }
        }
        superBmp.UnlockBits(data);
        return (mask, targetW, targetH);
    }

    private void UpdateTimeMaskIfNeeded()
    {
        var now = DateTime.Now;
        string currentKey = now.ToString("hh:mm:ss tt");
        if (currentKey != _lastTimeString)
        {
            _lastTimeString = currentKey;
            var (mask, w, h) = PrecomputeClockMask(now);
            lock (_timeLock)
            {
                _timeMask = mask;
                _timeWidth = w;
                _timeHeight = h;
            }
        }
    }

    private static (byte[] mask, int width, int height) PrecomputeClockMask(DateTime now)
    {
        const float superScale = 4.0f;
        string timeMain = now.ToString("hh:mm");
        string timeSec = ":" + now.ToString("ss");
        string timeAmPm = now.ToString("tt");

        using var fontMain = new Font("Segoe UI Variable Display", 14.5f * superScale, FontStyle.Bold);
        using var fontSec = new Font("Segoe UI Variable Display", 11.0f * superScale, FontStyle.Bold);
        using var fontAmPm = new Font("Segoe UI Variable Display", 9.0f * superScale, FontStyle.Bold);

        using var bmpMeasure = new Bitmap(1, 1);
        using var gMeasure = Graphics.FromImage(bmpMeasure);

        var sizeMain = gMeasure.MeasureString(timeMain, fontMain, PointF.Empty, StringFormat.GenericTypographic);
        var sizeSec = gMeasure.MeasureString(timeSec, fontSec, PointF.Empty, StringFormat.GenericTypographic);
        var sizeAmPm = gMeasure.MeasureString(timeAmPm, fontAmPm, PointF.Empty, StringFormat.GenericTypographic);

        float spacingSec = 2.5f * superScale;
        float spacingAmPm = 6.0f * superScale;

        float totalW = sizeMain.Width + spacingSec + sizeSec.Width + spacingAmPm + sizeAmPm.Width;
        float maxH = Math.Max(sizeMain.Height, Math.Max(sizeSec.Height, sizeAmPm.Height));

        int superW = (int)Math.Ceiling(totalW) + 24;
        int superH = (int)Math.Ceiling(maxH) + 24;

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            float currX = 12f;
            float baseLineY = 12f + (maxH - sizeMain.Height) * 0.5f;

            // 1. Hours & Minutes (Crisp Luminous White)
            using (var brushMain = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(timeMain, fontMain, brushMain, currX, baseLineY, StringFormat.GenericTypographic);
            }
            currX += sizeMain.Width + spacingSec;

            // 2. Seconds (Refined Ice Glow)
            float secY = baseLineY + (sizeMain.Height - sizeSec.Height) * 0.70f;
            using (var brushSec = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            {
                g.DrawString(timeSec, fontSec, brushSec, currX, secY, StringFormat.GenericTypographic);
            }
            currX += sizeSec.Width + spacingAmPm;

            // 3. AM/PM Tag (Subtle Luxury Status Badge)
            float ampmY = baseLineY + (sizeMain.Height - sizeAmPm.Height) * 0.72f;
            using (var brushAmPm = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
            {
                g.DrawString(timeAmPm, fontAmPm, brushAmPm, currX, ampmY, StringFormat.GenericTypographic);
            }
        }

        // Downsample 4x to target resolution with area-averaging
        int targetW = superW / (int)superScale;
        int targetH = superH / (int)superScale;
        byte[] mask = new byte[targetW * targetH];

        var data = superBmp.LockBits(new Rectangle(0, 0, superW, superH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < 4; dy++)
                    {
                        int sy = y * 4 + dy;
                        int rowOffset = sy * superW * 4;
                        for (int dx = 0; dx < 4; dx++)
                        {
                            int sx = x * 4 + dx;
                            sum += scan[rowOffset + sx * 4 + 3];
                        }
                    }
                    mask[y * targetW + x] = (byte)(sum >> 4);
                }
            }
        }
        superBmp.UnlockBits(data);
        return (mask, targetW, targetH);
    }

    public void StepForward()
    {
        _animDirection = 1.0;
        _renderSignal.Set();
    }

    public void StepBackward()
    {
        _animDirection = -1.0;
        _renderSignal.Set();
    }

    public void ToggleDirection()
    {
        if (_animDirection > 0.0 || (_animDirection == 0.0 && _progress > 0.5))
            _animDirection = -1.0;
        else
            _animDirection = 1.0;

        _renderSignal.Set();
    }

    public void ReplayFromStart()
    {
        _progress = 0.0;
        _animDirection = 1.0;
        _renderSignal.Set();
    }

    private void SaveSnapshot()
    {
        try
        {
            var surface = _renderSurface;
            if (surface != null && surface.BitsPtr != IntPtr.Zero)
            {
                using var bmp = new Bitmap(SurfaceWidth, SurfaceHeight, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, SurfaceWidth, SurfaceHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                unsafe
                {
                    Buffer.MemoryCopy((void*)surface.BitsPtr, (void*)data.Scan0, SurfaceWidth * SurfaceHeight * 4, SurfaceWidth * SurfaceHeight * 4);
                }
                bmp.UnlockBits(data);
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string rootDir = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
                string path = Path.Combine(rootDir, "liquid-glass-snapshot.png");
                bmp.Save(path, ImageFormat.Png);
            }
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var pt = PointToClient(Cursor.Position);
            var geom = _currentGeometry;

            double px = pt.X - geom.CenterX;
            double py = pt.Y - geom.CenterY;
            double qx = Math.Abs(px) - (geom.HalfWidth - geom.Radius);
            double qy = Math.Abs(py) - (geom.HalfHeight - geom.Radius);
            double outsideX = Math.Max(0.0, qx);
            double outsideY = Math.Max(0.0, qy);
            double outsideDist = Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
            double insideDist = Math.Min(0.0, Math.Max(qx, qy));
            double sdf = outsideDist + insideDist - geom.Radius;

            if (sdf > 0.5)
            {
                m.Result = (IntPtr)HtTransparent;
                return;
            }
        }
        base.WndProc(ref m);
    }

    private void RenderLoop()
    {
        while (_running)
        {
            // 6ms interval (~165 FPS ultra-high refresh fluidity)
            _renderSignal.WaitOne(6);
            if (!_running) break;

            IntPtr hwnd = _hwnd;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
                continue;

            double dt = _frameStopwatch.Elapsed.TotalSeconds;
            _frameStopwatch.Restart();
            dt = Math.Clamp(dt, 0.0, 0.05);

            // Update spawn/retract progress
            if (_animDirection != 0.0)
            {
                const double speed = 1.0 / AnimationDuration;
                _progress += _animDirection * speed * dt;

                if (_progress >= 1.0)
                {
                    _progress = 1.0;
                    _animDirection = 0.0;
                }
                else if (_progress <= 0.0)
                {
                    _progress = 0.0;
                    _animDirection = 0.0;
                }
            }

            // Real-time hover detection
            bool isHovered = false;
            if (_progress > 0.10 && GetCursorPos(out var cursorPos))
            {
                double px = (cursorPos.x - rect.Left) - _currentGeometry.CenterX;
                double py = (cursorPos.y - rect.Top) - _currentGeometry.CenterY;
                double straightW = Math.Max(0.0, _currentGeometry.HalfWidth - _currentGeometry.Radius);
                double straightH = Math.Max(0.0, _currentGeometry.HalfHeight - _currentGeometry.Radius);
                double qx = Math.Abs(px) - straightW;
                double qy = Math.Abs(py) - straightH;
                double outX = Math.Max(0.0, qx);
                double outY = Math.Max(0.0, qy);
                double outDist = Math.Sqrt(outX * outX + outY * outY);
                double inDist = Math.Min(0.0, Math.Max(qx, qy));
                double mouseSdf = outDist + inDist - _currentGeometry.Radius;

                if (mouseSdf <= 1.5)
                {
                    isHovered = true;
                }
            }

            // Real physical damped harmonic spring oscillator
            // Stiffness = 145.0 (snappy expansion), Damping = 9.8 (underdamped liquid wobble & spring oscillation)
            double target = isHovered ? 1.0 : 0.0;
            const double stiffness = 145.0;
            const double damping = 9.8;

            int subSteps = 4;
            double subDt = dt / subSteps;
            for (int s = 0; s < subSteps; s++)
            {
                double springForce = -stiffness * (_hoverPos - target);
                double dampingForce = -damping * _hoverVel;
                double totalAccel = springForce + dampingForce;

                _hoverVel += totalAccel * subDt;
                _hoverPos += _hoverVel * subDt;
            }

            if (Math.Abs(_hoverPos - target) < 0.0002 && Math.Abs(_hoverVel) < 0.0005)
            {
                _hoverPos = target;
                _hoverVel = 0.0;
            }

            UpdateTimeMaskIfNeeded();

            PillGeometry geom = ComputeGeometry(_progress, _hoverPos);
            _currentGeometry = geom;

            ProcessAndPresent(new Point(rect.Left, rect.Top), geom);
        }
    }

    private static PillGeometry ComputeGeometry(double spawnP, double hoverP)
    {
        double targetCenterX = SurfaceWidth * 0.5;

        // Physical spring position (expands, overshoots, oscillates, and settles with real physics)
        double restingHalfWidth = (CompactPillWidth * 0.5) + ((DefaultPillWidth * 0.5) - (CompactPillWidth * 0.5)) * hoverP;
        double restingHalfHeight = (CompactPillHeight * 0.5) + ((DefaultPillHeight * 0.5) - (CompactPillHeight * 0.5)) * hoverP;

        restingHalfWidth = Math.Max(20.0, restingHalfWidth);
        restingHalfHeight = Math.Max(18.0, restingHalfHeight);
        double restingCenterY = TopPadding + restingHalfHeight;

        double spawnRadius = 14.0;
        double spawnCenterY = -spawnRadius;

        if (spawnP <= 0.0)
        {
            return new PillGeometry(targetCenterX, spawnCenterY, spawnRadius, spawnRadius, spawnRadius);
        }

        // 1. Drop descent: moves from top edge into resting vertical position
        double dropProgress = Math.Clamp(spawnP / 0.45, 0.0, 1.0);
        double dropEase = EaseOutCubic(dropProgress);
        double currentCenterY = spawnCenterY + (restingCenterY - spawnCenterY) * dropEase;

        // 2. Spawn expansion
        double morphProgress = Math.Clamp(spawnP / 0.82, 0.0, 1.0);
        double morphEase = EaseOutCubic(morphProgress);

        double currentHalfWidth = spawnRadius + (restingHalfWidth - spawnRadius) * morphEase;
        double currentHalfHeight = spawnRadius + (restingHalfHeight - spawnRadius) * morphEase;

        currentHalfWidth = Math.Max(spawnRadius, currentHalfWidth);
        currentHalfHeight = Math.Max(spawnRadius, currentHalfHeight);
        
        double clampedHover = Math.Clamp(hoverP, 0.0, 1.0);
        double targetRadius = (CompactPillHeight * 0.5) + (38.0 - (CompactPillHeight * 0.5)) * clampedHover;
        double currentRadius = Math.Min(targetRadius, Math.Min(currentHalfWidth, currentHalfHeight));

        return new PillGeometry(targetCenterX, currentCenterY, currentHalfWidth, currentHalfHeight, currentRadius);
    }

    private static double EaseInOutCubic(double x)
    {
        return x < 0.5 ? 4.0 * x * x * x : 1.0 - Math.Pow(-2.0 * x + 2.0, 3.0) / 2.0;
    }

    private static double EaseOutCubic(double x)
    {
        return 1.0 - Math.Pow(1.0 - x, 3.0);
    }

    private unsafe void ProcessAndPresent(Point screenPos, PillGeometry geom)
    {
        var capturer = _screenCapturer;
        var surface = _renderSurface;
        if (capturer == null || surface == null || IsDisposed) return;

        if (!capturer.Capture(screenPos.X, screenPos.Y, SurfaceWidth, SurfaceHeight))
            return;

        byte* pRaw = (byte*)capturer.BitsPtr;
        uint* pDst = (uint*)surface.BitsPtr;
        if (pRaw == null || pDst == null) return;

        fixed (byte* pHalfRaw = _halfRawBuffer)
        fixed (byte* pBlurH = _blurHBuffer)
        fixed (byte* pBlurred = _blurredBuffer)
        {
            // 1. Box downsample (540x230 -> 270x115): 4x fewer pixels, anti-aliased pre-filter
            for (int y = 0; y < HalfHeight; y++)
            {
                int srcRow0 = (y * 2) * SurfaceWidth * 4;
                int srcRow1 = (y * 2 + 1) * SurfaceWidth * 4;
                int dstRow = y * HalfWidth * 4;

                for (int x = 0; x < HalfWidth; x++)
                {
                    int srcX0 = (x * 2) * 4;
                    int srcX1 = srcX0 + 4;
                    int dstX = dstRow + x * 4;

                    for (int c = 0; c < 3; c++)
                    {
                        int sum = pRaw[srcRow0 + srcX0 + c] +
                                  pRaw[srcRow0 + srcX1 + c] +
                                  pRaw[srcRow1 + srcX0 + c] +
                                  pRaw[srcRow1 + srcX1 + c];
                        pHalfRaw[dstX + c] = (byte)(sum >> 2);
                    }
                    pHalfRaw[dstX + 3] = 255;
                }
            }

            // 2. Single-pass 5-tap Gaussian Blur on 270x115 (Crisp, elegant frosted glass diffusion)
            // Horizontal (pHalfRaw -> pBlurH)
            for (int y = 0; y < HalfHeight; y++)
            {
                int rowOffset = y * HalfWidth * 4;
                for (int x = 0; x < HalfWidth; x++)
                {
                    int xm2 = Math.Max(0, x - 2);
                    int xm1 = Math.Max(0, x - 1);
                    int xp1 = Math.Min(HalfWidth - 1, x + 1);
                    int xp2 = Math.Min(HalfWidth - 1, x + 2);

                    int offM2 = rowOffset + xm2 * 4;
                    int offM1 = rowOffset + xm1 * 4;
                    int off0 = rowOffset + x * 4;
                    int offP1 = rowOffset + xp1 * 4;
                    int offP2 = rowOffset + xp2 * 4;

                    for (int c = 0; c < 3; c++)
                    {
                        int sum = pHalfRaw[offM2 + c] +
                                  (pHalfRaw[offM1 + c] << 2) +
                                  pHalfRaw[off0 + c] * 6 +
                                  (pHalfRaw[offP1 + c] << 2) +
                                  pHalfRaw[offP2 + c];
                        pBlurH[off0 + c] = (byte)(sum >> 4);
                    }
                    pBlurH[off0 + 3] = 255;
                }
            }

            // Vertical (pBlurH -> pBlurred)
            for (int y = 0; y < HalfHeight; y++)
            {
                int ym2 = Math.Max(0, y - 2) * HalfWidth * 4;
                int ym1 = Math.Max(0, y - 1) * HalfWidth * 4;
                int y0 = y * HalfWidth * 4;
                int yp1 = Math.Min(HalfHeight - 1, y + 1) * HalfWidth * 4;
                int yp2 = Math.Min(HalfHeight - 1, y + 2) * HalfWidth * 4;

                for (int x = 0; x < HalfWidth; x++)
                {
                    int colOffset = x * 4;
                    int offM2 = ym2 + colOffset;
                    int offM1 = ym1 + colOffset;
                    int off0 = y0 + colOffset;
                    int offP1 = yp1 + colOffset;
                    int offP2 = yp2 + colOffset;

                    for (int c = 0; c < 3; c++)
                    {
                        int sum = pBlurH[offM2 + c] +
                                  (pBlurH[offM1 + c] << 2) +
                                  pBlurH[off0 + c] * 6 +
                                  (pBlurH[offP1 + c] << 2) +
                                  pBlurH[offP2 + c];
                        pBlurred[off0 + c] = (byte)(sum >> 4);
                    }
                    pBlurred[off0 + 3] = 255;
                }
            }

            // 3. Liquid Glass Refraction, Continuous Bilinear Reconstruction & Specular Lighting
            double straightW = geom.HalfWidth - geom.Radius;
            double straightH = geom.HalfHeight - geom.Radius;

            for (int y = 0; y < SurfaceHeight; y++)
            {
                int rowIdx = y * SurfaceWidth;
                double py = y - geom.CenterY;
                double absPy = Math.Abs(py);
                double qy = absPy - straightH;

                for (int x = 0; x < SurfaceWidth; x++)
                {
                    int idx = rowIdx + x;
                    double px = x - geom.CenterX;
                    double absPx = Math.Abs(px);
                    double qx = absPx - straightW;

                    double outsideX = Math.Max(0.0, qx);
                    double outsideY = Math.Max(0.0, qy);
                    double outsideDist = Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
                    double insideDist = Math.Min(0.0, Math.Max(qx, qy));
                    double sdf = outsideDist + insideDist - geom.Radius;

                    // 1. Ultra-smooth Hermite cubic anti-aliased alpha falloff (smoothstep across 1.6px boundary)
                    double edgeFactor = Math.Clamp((-sdf + 0.8) / 1.6, 0.0, 1.0);
                    double alphaVal = edgeFactor * edgeFactor * (3.0 - 2.0 * edgeFactor);
                    byte a = (byte)Math.Round(alphaVal * 255.0);

                    if (a == 0)
                    {
                        pDst[idx] = 0;
                        continue;
                    }

                    // Compute continuous normal vector for smooth border refraction and specular lighting
                    double nx = 0.0, ny = 0.0;
                    if (outsideDist > 1e-4)
                    {
                        nx = (outsideX / outsideDist) * Math.Sign(px);
                        ny = (outsideY / outsideDist) * Math.Sign(py);
                    }
                    else if (insideDist > -1e-4)
                    {
                        if (qx > qy) nx = Math.Sign(px);
                        else ny = Math.Sign(py);
                    }
                    else
                    {
                        if (qx > qy) nx = Math.Sign(px) * Math.Clamp(1.0 + qx / geom.Radius, 0.0, 1.0);
                        else ny = Math.Sign(py) * Math.Clamp(1.0 + qy / geom.Radius, 0.0, 1.0);
                    }

                    // Meniscus lens refraction
                    double edgeDistance = Math.Max(0.0, -sdf);
                    double u = Math.Clamp(1.0 - (edgeDistance / 14.0), 0.0, 1.0);
                    double bend = Math.Pow(u, 2.5) * 6.5;

                    double sx = Math.Clamp(x - nx * bend, 0.0, SurfaceWidth - 2.0);
                    double sy = Math.Clamp(y - ny * bend, 0.0, SurfaceHeight - 2.0);

                    // Smooth bilinear sampling from half-resolution frosted glass buffer
                    double hx = Math.Clamp(sx * 0.5, 0.0, HalfWidth - 2.0);
                    double hy = Math.Clamp(sy * 0.5, 0.0, HalfHeight - 2.0);

                    int ix = (int)Math.Floor(hx);
                    int iy = (int)Math.Floor(hy);
                    double fx = hx - ix;
                    double fy = hy - iy;

                    int w00 = (int)Math.Round((1.0 - fx) * (1.0 - fy) * 256.0);
                    int w10 = (int)Math.Round(fx * (1.0 - fy) * 256.0);
                    int w01 = (int)Math.Round((1.0 - fx) * fy * 256.0);
                    int w11 = Math.Max(0, 256 - (w00 + w10 + w01));

                    int off00 = (iy * HalfWidth + ix) * 4;
                    int off10 = (iy * HalfWidth + ix + 1) * 4;
                    int off01 = ((iy + 1) * HalfWidth + ix) * 4;
                    int off11 = ((iy + 1) * HalfWidth + ix + 1) * 4;

                    int b = (pBlurred[off00 + 0] * w00 + pBlurred[off10 + 0] * w10 + pBlurred[off01 + 0] * w01 + pBlurred[off11 + 0] * w11) >> 8;
                    int g = (pBlurred[off00 + 1] * w00 + pBlurred[off10 + 1] * w10 + pBlurred[off01 + 1] * w01 + pBlurred[off11 + 1] * w11) >> 8;
                    int r = (pBlurred[off00 + 2] * w00 + pBlurred[off10 + 2] * w10 + pBlurred[off01 + 2] * w01 + pBlurred[off11 + 2] * w11) >> 8;

                    b = Math.Clamp(b, 0, 255);
                    g = Math.Clamp(g, 0, 255);
                    r = Math.Clamp(r, 0, 255);

                    // Apple Liquid Glass Crystal Tint
                    r = (r * 242 + 205 * 14) >> 8;
                    g = (g * 242 + 228 * 14) >> 8;
                    b = (b * 242 + 255 * 14) >> 8;

                    // 2. Smooth Continuous Glass Border Lighting (Visible & Silky on All Backgrounds)
                    // A. Outer Specular White Rim (1.5px Gaussian highlight centered at sdf = -1.0)
                    double outerRimGauss = Math.Exp(-Math.Pow((sdf + 1.0) / 1.25, 2.0));
                    int outerRimLight = (int)(outerRimGauss * 225.0 * alphaVal);

                    // B. Inner Bevel Sheen (2.0px soft inner reflection centered at sdf = -3.2)
                    double innerRimGauss = Math.Exp(-Math.Pow((sdf + 3.2) / 1.6, 2.0));
                    int innerRimLight = (int)(innerRimGauss * 50.0 * alphaVal);

                    // C. Top Ambient Sky Highlight (smooth crest light on upper curve py < 0)
                    double topNorm = Math.Clamp(-py / Math.Max(geom.HalfHeight, 1.0), 0.0, 1.0);
                    double topDomeGauss = Math.Exp(-Math.Pow((sdf + 8.0) / 9.0, 2.0)) * topNorm;
                    int topDomeLight = (int)(topDomeGauss * 35.0 * alphaVal);

                    int totalLight = outerRimLight + innerRimLight + topDomeLight;
                    if (totalLight > 0)
                    {
                        r = Math.Min(255, r + totalLight);
                        g = Math.Min(255, g + totalLight);
                        b = Math.Min(255, b + totalLight);
                    }

                    // Premultiplied 32-bit ARGB for GPU compositor
                    uint pR = (uint)((r * a) / 255);
                    uint pG = (uint)((g * a) / 255);
                    uint pB = (uint)((b * a) / 255);
                    pDst[idx] = ((uint)a << 24) | (pR << 16) | (pG << 8) | pB;
                }
            }

            // 4. Real-time Live Clock Typography with Hover Fade-Out
            byte[]? timeMask;
            int timeW, timeH;
            lock (_timeLock)
            {
                timeMask = _timeMask;
                timeW = _timeWidth;
                timeH = _timeHeight;
            }

            // Time opacity: fades out on mouse hover as the pill expands
            double spawnTextAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverFadeOut = Math.Clamp(1.0 - (_hoverPos / 0.35), 0.0, 1.0);
            double textAlpha = EaseOutCubic(spawnTextAlpha) * hoverFadeOut;

            if (textAlpha > 0.005 && timeMask != null && timeW > 0 && timeH > 0)
            {
                int startX = (int)Math.Round(geom.CenterX - timeW * 0.5);
                int startY = (int)Math.Round(geom.CenterY - timeH * 0.5);

                // Pass 1: Crisp Ambient Drop Shadow (1px offset)
                double shadowAlpha = textAlpha * 0.45;
                for (int ty = 0; ty < timeH; ty++)
                {
                    int dstY = startY + ty + 1;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * timeW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < timeW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = timeMask[srcRow + tx];
                        if (maskA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        double sFactor = (maskA / 255.0) * shadowAlpha;
                        double invS = 1.0 - sFactor;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        uint pR = (uint)Math.Round(bgR * invS);
                        uint pG = (uint)Math.Round(bgG * invS);
                        uint pB = (uint)Math.Round(bgB * invS);
                        pDst[dstIdx] = ((uint)bgA << 24) | (pR << 16) | (pG << 8) | pB;
                    }
                }

                // Pass 2: Razor-Sharp Pure Luminous White Text
                for (int ty = 0; ty < timeH; ty++)
                {
                    int dstY = startY + ty;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * timeW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < timeW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = timeMask[srcRow + tx];
                        if (maskA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        double fgA = (maskA / 255.0) * textAlpha * 0.98;
                        double invA = 1.0 - fgA;
                        double whiteVal = 255.0 * fgA * (bgA / 255.0);

                        uint pR = (uint)Math.Min(255, Math.Round(bgR * invA + whiteVal));
                        uint pG = (uint)Math.Min(255, Math.Round(bgG * invA + whiteVal));
                        uint pB = (uint)Math.Min(255, Math.Round(bgB * invA + whiteVal));

                        pDst[dstIdx] = ((uint)bgA << 24) | (pR << 16) | (pG << 8) | pB;
                    }
                }
            }

            // 5. Real-time Weather Card on Hover Expansion (1:1 Native Resolution with Y-glide)
            byte[]? weatherMask;
            int weatherW, weatherH;
            lock (_weatherLock)
            {
                weatherMask = _weatherMask;
                weatherW = _weatherWidth;
                weatherH = _weatherHeight;
            }

            // Weather opacity: fades in as hover expands to full modal
            double spawnWeatherAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverWeatherAlpha = Math.Clamp((_hoverPos - 0.28) / 0.72, 0.0, 1.0);
            double weatherAlpha = EaseOutCubic(spawnWeatherAlpha) * EaseOutCubic(hoverWeatherAlpha);

            if (weatherAlpha > 0.005 && weatherMask != null && weatherW > 0 && weatherH > 0)
            {
                // Silky 4px Y-glide during bloom without pixel distortion
                int glideY = (int)Math.Round((1.0 - weatherAlpha) * 4.0);
                int startX = (int)Math.Round(geom.CenterX - weatherW * 0.5);
                int startY = (int)Math.Round(geom.CenterY - weatherH * 0.5) + glideY;

                // Pass 1: Crisp Ambient Drop Shadow (1px offset)
                double shadowAlpha = weatherAlpha * 0.45;
                for (int ty = 0; ty < weatherH; ty++)
                {
                    int dstY = startY + ty + 1;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * weatherW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < weatherW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = weatherMask[srcRow + tx];
                        if (maskA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        double sFactor = (maskA / 255.0) * shadowAlpha;
                        double invS = 1.0 - sFactor;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        uint pR = (uint)Math.Round(bgR * invS);
                        uint pG = (uint)Math.Round(bgG * invS);
                        uint pB = (uint)Math.Round(bgB * invS);
                        pDst[dstIdx] = ((uint)bgA << 24) | (pR << 16) | (pG << 8) | pB;
                    }
                }

                // Pass 2: Razor-Sharp Pure Luminous White Text & Weather Icons
                for (int ty = 0; ty < weatherH; ty++)
                {
                    int dstY = startY + ty;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * weatherW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < weatherW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = weatherMask[srcRow + tx];
                        if (maskA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        double fgA = (maskA / 255.0) * weatherAlpha * 0.98;
                        double invA = 1.0 - fgA;
                        double whiteVal = 255.0 * fgA * (bgA / 255.0);

                        uint pR = (uint)Math.Min(255, Math.Round(bgR * invA + whiteVal));
                        uint pG = (uint)Math.Min(255, Math.Round(bgG * invA + whiteVal));
                        uint pB = (uint)Math.Min(255, Math.Round(bgB * invA + whiteVal));

                        pDst[dstIdx] = ((uint)bgA << 24) | (pR << 16) | (pG << 8) | pB;
                    }
                }
            }
        }

        if (!IsDisposed)
        {
            try
            {
                var size = new SIZE(SurfaceWidth, SurfaceHeight);
                var ptSrc = new POINT(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AcSrcOver,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AcSrcAlpha
                };

                IntPtr screenDC = GetDC(IntPtr.Zero);
                if (screenDC != IntPtr.Zero)
                {
                    try
                    {
                        UpdateLayeredWindow(_hwnd, screenDC, IntPtr.Zero, ref size, surface.MemDC, ref ptSrc, 0, ref blend, UlwAlpha);
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, screenDC);
                    }
                }
            }
            catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running = false;
            _renderSignal.Set();
            _renderThread?.Join(100);
            _renderSignal.Dispose();

            _screenCapturer?.Dispose();
            _renderSurface?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class FastSurface : IDisposable
    {
        private readonly IntPtr _memDC;
        private readonly IntPtr _hBitmap;
        private readonly IntPtr _oldBitmap;
        private readonly IntPtr _pBits;

        public IntPtr MemDC => _memDC;
        public IntPtr BitsPtr => _pBits;

        public FastSurface(int width, int height)
        {
            IntPtr screenDC = GetDC(IntPtr.Zero);
            _memDC = CreateCompatibleDC(screenDC);

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;

            _hBitmap = CreateDIBSection(screenDC, ref bmi, 0, out _pBits, IntPtr.Zero, 0);
            _oldBitmap = SelectObject(_memDC, _hBitmap);
            ReleaseDC(IntPtr.Zero, screenDC);
        }

        public bool Capture(int screenX, int screenY, int width, int height)
        {
            IntPtr screenDC = GetDC(IntPtr.Zero);
            if (screenDC == IntPtr.Zero) return false;
            try
            {
                return BitBlt(_memDC, 0, 0, width, height, screenDC, screenX, screenY, 0x00CC0020);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public void Dispose()
        {
            if (_memDC != IntPtr.Zero)
            {
                SelectObject(_memDC, _oldBitmap);
                DeleteDC(_memDC);
            }
            if (_hBitmap != IntPtr.Zero)
            {
                DeleteObject(_hBitmap);
            }
        }
    }
}
