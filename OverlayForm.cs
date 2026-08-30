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
    private const int SurfaceWidth = 480;
    private const int SurfaceHeight = 110;
    private const int HalfWidth = SurfaceWidth / 2;   // 240
    private const int HalfHeight = SurfaceHeight / 2; // 55

    // Sleek compact dynamic island music bar on hover (390x56)
    private const int DefaultPillWidth = 390;
    private const int DefaultPillHeight = 56;

    // Compact resting size with clock (190x44)
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

    private sealed class TrackInfo
    {
        public string Title { get; set; } = "Starboy";
        public string Artist { get; set; } = "The Weeknd (feat. Daft Punk)";
        public string Album { get; set; } = "Starboy";
        public string Badge { get; set; } = "LOSSLESS • 24-BIT/96kHz";
        public double DurationSeconds { get; set; } = 230.0;
        public Color CoverAccentColor { get; set; } = Color.FromArgb(255, 235, 45, 75);
    }

    private static readonly TrackInfo[] Playlist = new[]
    {
        new TrackInfo
        {
            Title = "Starboy",
            Artist = "The Weeknd • Daft Punk",
            Album = "Starboy",
            Badge = "LOSSLESS • 24-BIT/96kHz",
            DurationSeconds = 230.0,
            CoverAccentColor = Color.FromArgb(255, 235, 40, 80)
        },
        new TrackInfo
        {
            Title = "Midnight City",
            Artist = "M83 • Anthony Gonzalez",
            Album = "Hurry Up, We're Dreaming",
            Badge = "DOLBY ATMOS • SPATIAL",
            DurationSeconds = 243.0,
            CoverAccentColor = Color.FromArgb(255, 120, 60, 240)
        },
        new TrackInfo
        {
            Title = "Blinding Lights",
            Artist = "The Weeknd • Max Martin",
            Album = "After Hours",
            Badge = "HI-RES AUDIO • 96kHz",
            DurationSeconds = 200.0,
            CoverAccentColor = Color.FromArgb(255, 245, 140, 30)
        },
        new TrackInfo
        {
            Title = "Get Lucky",
            Artist = "Daft Punk • Pharrell Williams",
            Album = "Random Access Memories",
            Badge = "STUDIO MASTER • LOSSLESS",
            DurationSeconds = 248.0,
            CoverAccentColor = Color.FromArgb(255, 240, 200, 50)
        },
        new TrackInfo
        {
            Title = "Nightcall",
            Artist = "Kavinsky • Lovefoxxx",
            Album = "OutRun",
            Badge = "LOSSLESS • SPATIAL AUDIO",
            DurationSeconds = 258.0,
            CoverAccentColor = Color.FromArgb(255, 20, 180, 240)
        }
    };

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

    private int _currentTrackIndex = 0;
    private bool _isPlaying = true;
    private double _trackProgressSeconds = 88.0; // 1:28
    private bool _isHearted = true;
    private double _vinylRotationAngle = 0.0;
    private double _visualizerTime = 0.0;
    private double _lastMusicMaskUpdateTime = 0.0;

    private byte[]? _musicMask;
    private int _musicWidth;
    private int _musicHeight;
    private readonly object _musicLock = new();

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
            else if (e.KeyCode == Keys.Space)
            {
                if (_hoverPos > 0.4)
                {
                    _isPlaying = !_isPlaying;
                    UpdateMusicMask();
                }
                else
                {
                    ToggleDirection();
                }
            }
            else if (e.KeyCode is Keys.Right or Keys.N)
            {
                if (_hoverPos > 0.4)
                {
                    _currentTrackIndex = (_currentTrackIndex + 1) % Playlist.Length;
                    _trackProgressSeconds = 0.0;
                    UpdateMusicMask();
                }
                else
                {
                    StepForward();
                }
            }
            else if (e.KeyCode is Keys.Left or Keys.P)
            {
                if (_hoverPos > 0.4)
                {
                    _currentTrackIndex = (_currentTrackIndex - 1 + Playlist.Length) % Playlist.Length;
                    _trackProgressSeconds = 0.0;
                    UpdateMusicMask();
                }
                else
                {
                    StepBackward();
                }
            }
            else if (e.KeyCode == Keys.H)
            {
                _isHearted = !_isHearted;
                UpdateMusicMask();
            }
            else if (e.KeyCode is Keys.F or Keys.Down)
            {
                StepForward();
            }
            else if (e.KeyCode is Keys.B or Keys.Up)
            {
                StepBackward();
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
            if (e.Button == MouseButtons.Right)
            {
                Close();
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (_hoverPos > 0.6)
                {
                    if (!HandleMusicClick(e.Location))
                    {
                        ToggleDirection();
                    }
                }
                else
                {
                    ToggleDirection();
                }
            }
        };
    }

    private bool HandleMusicClick(Point pt)
    {
        // Content area offset within Surface:
        // CenterX = 240, TargetW = 360 -> startX = 240 - 180 = 60
        // CenterY = 46, TargetH = 46 -> startY = 46 - 23 = 23
        float mx = pt.X - 60f;
        float my = pt.Y - 23f;

        // 1. Play / Pause Central Button: cx = 298, cy = 23, radius = 15
        if (Math.Sqrt(Math.Pow(mx - 298, 2) + Math.Pow(my - 23, 2)) <= 18)
        {
            _isPlaying = !_isPlaying;
            UpdateMusicMask();
            return true;
        }

        // 2. Next Track: cx = 330, cy = 23, radius = 14
        if (Math.Sqrt(Math.Pow(mx - 330, 2) + Math.Pow(my - 23, 2)) <= 16)
        {
            _currentTrackIndex = (_currentTrackIndex + 1) % Playlist.Length;
            _trackProgressSeconds = 0.0;
            UpdateMusicMask();
            return true;
        }

        // 3. Prev Track: cx = 266, cy = 23, radius = 14
        if (Math.Sqrt(Math.Pow(mx - 266, 2) + Math.Pow(my - 23, 2)) <= 16)
        {
            _currentTrackIndex = (_currentTrackIndex - 1 + Playlist.Length) % Playlist.Length;
            _trackProgressSeconds = 0.0;
            UpdateMusicMask();
            return true;
        }

        // 4. Heart Favorite: cx = 234, cy = 23, radius = 13
        if (Math.Sqrt(Math.Pow(mx - 234, 2) + Math.Pow(my - 23, 2)) <= 15)
        {
            _isHearted = !_isHearted;
            UpdateMusicMask();
            return true;
        }

        // 5. Timeline Scrubbing: my in [26, 44], mx in [48, 210]
        if (my >= 26 && my <= 44 && mx >= 48 && mx <= 210)
        {
            double ratio = Math.Clamp((mx - 48) / (208.0 - 48.0), 0.0, 1.0);
            var track = Playlist[_currentTrackIndex];
            _trackProgressSeconds = ratio * track.DurationSeconds;
            UpdateMusicMask();
            return true;
        }

        return false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hwnd = Handle;
        _screenCapturer = new FastSurface(SurfaceWidth, SurfaceHeight);
        _renderSurface = new FastSurface(SurfaceWidth, SurfaceHeight);
        SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);

        UpdateTimeMaskIfNeeded();
        UpdateMusicMask();
    }

    private void UpdateMusicMask()
    {
        var track = Playlist[_currentTrackIndex];
        var (mask, w, h) = PrecomputeMusicMask(track, _trackProgressSeconds, _isPlaying, _isHearted, _vinylRotationAngle, _visualizerTime);
        lock (_musicLock)
        {
            _musicMask = mask;
            _musicWidth = w;
            _musicHeight = h;
        }
    }

    private static void DrawVinylRecord(Graphics g, float cx, float cy, float radius, double rotationAngle, Color labelColor)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 1. Vinyl Base Outer Disc (Dark Charcoal Gloss)
        using (var brushVinyl = new SolidBrush(Color.FromArgb(240, 22, 22, 28)))
        {
            g.FillEllipse(brushVinyl, cx - radius, cy - radius, radius * 2f, radius * 2f);
        }

        // 2. Vinyl Micro-groove Rings (Frosted concentric circular tracks)
        for (int i = 1; i <= 6; i++)
        {
            float r = radius * (0.45f + i * 0.085f);
            using var penGroove = new Pen(Color.FromArgb(35, 255, 255, 255), 1.0f);
            g.DrawEllipse(penGroove, cx - r, cy - r, r * 2f, r * 2f);
        }

        // 3. Ambient Vinyl Specular Sheen (Dual cone reflections)
        using (var brushSheen = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
        {
            using var sheenPath = new GraphicsPath();
            sheenPath.AddPie(cx - radius, cy - radius, radius * 2f, radius * 2f, (float)(rotationAngle + 35), 45f);
            sheenPath.AddPie(cx - radius, cy - radius, radius * 2f, radius * 2f, (float)(rotationAngle + 215), 45f);
            g.FillPath(brushSheen, sheenPath);
        }

        // 4. Center Colored Label Disc
        float labelR = radius * 0.40f;
        using (var brushLabel = new SolidBrush(labelColor))
        {
            g.FillEllipse(brushLabel, cx - labelR, cy - labelR, labelR * 2f, labelR * 2f);
        }

        // Inner label ring
        float innerLabelR = labelR * 0.65f;
        using (var penInnerLabel = new Pen(Color.FromArgb(80, 255, 255, 255), 1.5f))
        {
            g.DrawEllipse(penInnerLabel, cx - innerLabelR, cy - innerLabelR, innerLabelR * 2f, innerLabelR * 2f);
        }

        // 5. Center Spindle Hole (Hollow Transparent Core)
        float holeR = radius * 0.12f;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var brushHole = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(brushHole, cx - holeR, cy - holeR, holeR * 2f, holeR * 2f);
        }
        g.CompositingMode = CompositingMode.SourceOver;

        // Outer glass edge border on vinyl
        using (var penOuter = new Pen(Color.FromArgb(90, 255, 255, 255), 1.2f))
        {
            g.DrawEllipse(penOuter, cx - radius, cy - radius, radius * 2f, radius * 2f);
        }
    }

    private static void DrawEqualizerBars(Graphics g, float cx, float cy, float barWidth, float maxHeight, double time)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(245, 255, 255, 255));

        const int barCount = 4;
        float spacing = barWidth * 0.65f;
        float totalW = barCount * barWidth + (barCount - 1) * spacing;
        float startX = cx - totalW * 0.5f;

        for (int i = 0; i < barCount; i++)
        {
            double wave = (Math.Sin(time * 8.0 + i * 1.6) * 0.5 + 0.5) * 0.65 +
                          (Math.Cos(time * 13.0 + i * 2.4) * 0.5 + 0.5) * 0.35;
            float h = (float)Math.Clamp(maxHeight * (0.25 + 0.75 * wave), barWidth, maxHeight);

            float bx = startX + i * (barWidth + spacing);
            float by = cy - h * 0.5f;

            using var path = new GraphicsPath();
            float r = barWidth * 0.5f;
            path.AddArc(bx, by, barWidth, barWidth, 180, 180);
            path.AddArc(bx, by + h - barWidth, barWidth, barWidth, 0, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }

    private static void DrawPlayPauseButton(Graphics g, float cx, float cy, float radius, bool isPlaying)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Frosted glass circular pill button
        using (var brushBtn = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
        {
            g.FillEllipse(brushBtn, cx - radius, cy - radius, radius * 2f, radius * 2f);
        }
        using (var penBtn = new Pen(Color.FromArgb(140, 255, 255, 255), 1.2f))
        {
            g.DrawEllipse(penBtn, cx - radius, cy - radius, radius * 2f, radius * 2f);
        }

        using var brushGlyph = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        if (isPlaying)
        {
            // Pause symbol: 2 rounded pillars
            float barW = radius * 0.22f;
            float barH = radius * 0.72f;
            float barGap = radius * 0.24f;

            float b1X = cx - (barW + barGap * 0.5f);
            float b2X = cx + (barGap * 0.5f);
            float barY = cy - barH * 0.5f;

            g.FillRectangle(brushGlyph, b1X, barY, barW, barH);
            g.FillRectangle(brushGlyph, b2X, barY, barW, barH);
        }
        else
        {
            // Play symbol: Triangle pointing right
            float triSize = radius * 0.70f;
            float triH = triSize * 0.90f;
            float triW = triSize * 0.85f;
            float leftX = cx - triW * 0.40f;
            float topY = cy - triH * 0.50f;

            PointF[] tri = new[]
            {
                new PointF(leftX, topY),
                new PointF(leftX + triW, cy),
                new PointF(leftX, topY + triH)
            };
            g.FillPolygon(brushGlyph, tri);
        }
    }

    private static void DrawTrackSkipButton(Graphics g, float cx, float cy, float size, bool isNext)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(240, 255, 255, 255));

        float dir = isNext ? 1.0f : -1.0f;
        float h = size * 0.65f;
        float w = size * 0.38f;
        float gap = size * 0.22f;

        // Chevron 1
        PointF[] tri1 = new[]
        {
            new PointF(cx - (w + gap * 0.5f) * dir, cy - h * 0.5f),
            new PointF(cx - (gap * 0.5f) * dir, cy),
            new PointF(cx - (w + gap * 0.5f) * dir, cy + h * 0.5f)
        };
        g.FillPolygon(brush, tri1);

        // Chevron 2
        PointF[] tri2 = new[]
        {
            new PointF(cx + (gap * 0.5f) * dir, cy - h * 0.5f),
            new PointF(cx + (w + gap * 0.5f) * dir, cy),
            new PointF(cx + (gap * 0.5f) * dir, cy + h * 0.5f)
        };
        g.FillPolygon(brush, tri2);
    }

    private static void DrawHeartIcon(Graphics g, float cx, float cy, float size, bool isFilled)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = new GraphicsPath();
        float r = size * 0.28f;
        float topY = cy - size * 0.35f;

        path.AddArc(cx - r * 1.95f, topY, r * 1.95f, r * 1.95f, 135, 225);
        path.AddArc(cx, topY, r * 1.95f, r * 1.95f, 180, 225);
        path.AddLine(cx + r * 1.90f, topY + r * 1.4f, cx, cy + size * 0.45f);
        path.AddLine(cx, cy + size * 0.45f, cx - r * 1.90f, topY + r * 1.4f);
        path.CloseFigure();

        if (isFilled)
        {
            using var brush = new SolidBrush(Color.FromArgb(255, 255, 65, 105)); // Vibrant Apple Music Ruby
            g.FillPath(brush, path);
        }
        else
        {
            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.DrawPath(pen, path);
        }
    }

    private static void DrawAirPlayIcon(Graphics g, float cx, float cy, float size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), size * 0.12f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        float sw = size * 0.85f;
        float sh = size * 0.55f;
        float topY = cy - size * 0.45f;

        g.DrawLine(pen, cx - sw * 0.5f, topY + sh, cx - sw * 0.5f, topY);
        g.DrawLine(pen, cx - sw * 0.5f, topY, cx + sw * 0.5f, topY);
        g.DrawLine(pen, cx + sw * 0.5f, topY, cx + sw * 0.5f, topY + sh);

        using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        float triW = size * 0.50f;
        float triH = size * 0.38f;
        float triBot = cy + size * 0.45f;

        PointF[] tri = new[]
        {
            new PointF(cx, triBot - triH),
            new PointF(cx + triW * 0.5f, triBot),
            new PointF(cx - triW * 0.5f, triBot)
        };
        g.FillPolygon(brush, tri);
    }

    private static (byte[] mask, int width, int height) PrecomputeMusicMask(
        TrackInfo track,
        double progressSeconds,
        bool isPlaying,
        bool isHearted,
        double rotationAngle,
        double visualizerTime)
    {
        const float superScale = 4.0f;
        int targetW = 360;
        int targetH = 46;
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var fontTitle = new Font("Segoe UI Variable Display", 10.5f * superScale, FontStyle.Bold);
            using var fontArtist = new Font("Segoe UI Variable Display", 8.0f * superScale, FontStyle.Regular);

            // 1. Left Section: Rotating Vinyl Record Disc
            float vinylCx = 22f * superScale;
            float vinylCy = 23f * superScale;
            float vinylRadius = 17f * superScale;
            DrawVinylRecord(g, vinylCx, vinylCy, vinylRadius, rotationAngle, track.CoverAccentColor);

            // Equalizer Bars mini-overlay on vinyl center
            if (isPlaying)
            {
                float eqCx = vinylCx;
                float eqCy = vinylCy;
                DrawEqualizerBars(g, eqCx, eqCy, 1.8f * superScale, 10f * superScale, visualizerTime);
            }

            // 2. Middle Section: Track Title, Subtitle & Slim Live Progress Line
            float textStartX = 48f * superScale;
            float trackBarW = 160f * superScale;

            int elMin = (int)(progressSeconds / 60);
            int elSec = (int)(progressSeconds % 60);
            string elStr = $"{elMin}:{elSec:D2}";
            string metaStr = $"{track.Artist} · {elStr}";

            // Track Title
            using (var brushTitle = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(track.Title, fontTitle, brushTitle, textStartX, 4f * superScale, StringFormat.GenericDefault);
            }

            // Artist & Time
            using (var brushArtist = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.DrawString(metaStr, fontArtist, brushArtist, textStartX, 19f * superScale, StringFormat.GenericDefault);
            }

            // Slim Live Progress Line
            float barY = 35f * superScale;
            float barH = 2.0f * superScale;
            double progressRatio = Math.Clamp(progressSeconds / track.DurationSeconds, 0.0, 1.0);

            using (var penRail = new Pen(Color.FromArgb(50, 255, 255, 255), barH) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(penRail, textStartX + barH * 0.5f, barY, textStartX + trackBarW - barH * 0.5f, barY);
            }

            float fillEnd = textStartX + (float)(progressRatio * trackBarW);
            if (fillEnd > textStartX + barH)
            {
                using var penFill = new Pen(Color.FromArgb(245, 255, 255, 255), barH) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(penFill, textStartX + barH * 0.5f, barY, fillEnd, barY);
            }

            float beadR = 3.0f * superScale;
            using (var brushBead = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.FillEllipse(brushBead, fillEnd - beadR, barY - beadR, beadR * 2, beadR * 2);
            }

            // 3. Right Section: Inline Media Controls
            float ctrlY = 23f * superScale;

            // Heart / Favorite Icon
            DrawHeartIcon(g, 234f * superScale, ctrlY, 11f * superScale, isHearted);

            // Previous Track Button
            DrawTrackSkipButton(g, 266f * superScale, ctrlY, 12f * superScale, isNext: false);

            // Center Play / Pause Hero Glass Button
            DrawPlayPauseButton(g, 298f * superScale, ctrlY, 13f * superScale, isPlaying);

            // Next Track Button
            DrawTrackSkipButton(g, 330f * superScale, ctrlY, 12f * superScale, isNext: true);
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

            // Refined physical damped harmonic spring oscillator
            // Stiffness = 175.0, Damping = 15.0 (refined luxury fluid bounce with ~10% overshoot & smooth settle)
            double target = isHovered ? 1.0 : 0.0;
            const double stiffness = 175.0;
            const double damping = 15.0;

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

            // Update music playback timeline & visualizer animation
            if (_isPlaying)
            {
                _trackProgressSeconds += dt;
                var currTrack = Playlist[_currentTrackIndex];
                if (_trackProgressSeconds >= currTrack.DurationSeconds)
                {
                    _trackProgressSeconds = 0.0;
                    _currentTrackIndex = (_currentTrackIndex + 1) % Playlist.Length;
                }
                _vinylRotationAngle = (_vinylRotationAngle + 45.0 * dt) % 360.0;
                _visualizerTime += dt;
            }

            double nowSec = _frameStopwatch.Elapsed.TotalSeconds;
            if (_hoverPos > 0.05 && (nowSec - _lastMusicMaskUpdateTime >= 0.035 || !_isPlaying))
            {
                _lastMusicMaskUpdateTime = nowSec;
                UpdateMusicMask();
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
        double targetRadius = (CompactPillHeight * 0.5) + ((DefaultPillHeight * 0.5) - (CompactPillHeight * 0.5)) * clampedHover;
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
            // 1. Box downsample (480x110 -> 240x55): 4x fewer pixels, anti-aliased pre-filter
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

            // 2. Single-pass 5-tap Gaussian Blur on 300x125 (Crisp, elegant frosted glass diffusion)
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
                // Stable text positioning: anchored to compact pill center (does not bump with glass spring oscillations)
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - timeW * 0.5);
                int startY = (int)Math.Round((TopPadding + CompactPillHeight * 0.5) - timeH * 0.5);

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

            // 5. Real-time Luxury Music Player on Hover Expansion (1:1 Native Resolution without text bumping)
            byte[]? musicMask;
            int musicW, musicH;
            lock (_musicLock)
            {
                musicMask = _musicMask;
                musicW = _musicWidth;
                musicH = _musicHeight;
            }

            // Music player opacity: fades in smoothly as hover expands to full modal
            double spawnMusicAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverMusicAlpha = Math.Clamp((_hoverPos - 0.28) / 0.72, 0.0, 1.0);
            double musicAlpha = EaseOutCubic(spawnMusicAlpha) * EaseOutCubic(hoverMusicAlpha);

            if (musicAlpha > 0.005 && musicMask != null && musicW > 0 && musicH > 0)
            {
                // Stable music player positioning: anchored to modal center
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - musicW * 0.5);
                int startY = (int)Math.Round((TopPadding + DefaultPillHeight * 0.5) - musicH * 0.5);

                // Pass 1: Crisp Ambient Drop Shadow (1px offset)
                double shadowAlpha = musicAlpha * 0.45;
                for (int ty = 0; ty < musicH; ty++)
                {
                    int dstY = startY + ty + 1;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * musicW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < musicW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = musicMask[srcRow + tx];
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

                // Pass 2: Razor-Sharp Pure Luminous White Text & Vector Music Controls
                for (int ty = 0; ty < musicH; ty++)
                {
                    int dstY = startY + ty;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * musicW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < musicW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = musicMask[srcRow + tx];
                        if (maskA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        double fgA = (maskA / 255.0) * musicAlpha * 0.98;
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
