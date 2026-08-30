using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Windows.Media.Control;

namespace LiquidGlassCircle;

internal sealed class OverlayForm : Form
{
    private const int SurfaceWidth = 600;
    private const int SurfaceHeight = 250;
    private const int HalfWidth = SurfaceWidth / 2;   // 300
    private const int HalfHeight = SurfaceHeight / 2; // 125

    // Default expanded size on hover (500x180 - spacious liquid glass card)
    private const int DefaultPillWidth = 500;
    private const int DefaultPillHeight = 180;

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

    internal sealed class SystemMediaController
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public event Action? MediaUpdated;

        public bool HasActiveSession => _currentSession != null;
        public string Title { get; private set; } = "Starboy";
        public string Artist { get; private set; } = "The Weeknd • Daft Punk";
        public string Album { get; private set; } = "Starboy";
        public bool IsPlaying { get; private set; } = true;
        public double PositionSeconds { get; private set; } = 88.0;
        public double DurationSeconds { get; private set; } = 230.0;
        public bool IsShuffle { get; private set; } = false;

        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += (s, e) => UpdateCurrentSession();
                    _manager.SessionsChanged += (s, e) => UpdateCurrentSession();
                    UpdateCurrentSession();
                }
            }
            catch { }
        }

        private void UpdateCurrentSession()
        {
            try
            {
                var session = _manager?.GetCurrentSession();
                _currentSession = session;
                if (session != null)
                {
                    session.MediaPropertiesChanged += (s, e) => RefreshMediaProperties();
                    session.PlaybackInfoChanged += (s, e) => RefreshPlaybackInfo();
                    session.TimelinePropertiesChanged += (s, e) => RefreshTimeline();
                    RefreshMediaProperties();
                    RefreshPlaybackInfo();
                    RefreshTimeline();
                }
                MediaUpdated?.Invoke();
            }
            catch { }
        }

        public async void RefreshMediaProperties()
        {
            try
            {
                if (_currentSession == null) return;
                var props = await _currentSession.TryGetMediaPropertiesAsync();
                if (props != null && !string.IsNullOrWhiteSpace(props.Title))
                {
                    Title = props.Title;
                    Artist = string.IsNullOrWhiteSpace(props.Artist) ? "Audio" : props.Artist;
                    Album = props.AlbumTitle ?? "";
                    MediaUpdated?.Invoke();
                }
            }
            catch { }
        }

        public void RefreshPlaybackInfo()
        {
            try
            {
                if (_currentSession == null) return;
                var info = _currentSession.GetPlaybackInfo();
                if (info != null)
                {
                    IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (info.IsShuffleActive.HasValue)
                    {
                        IsShuffle = info.IsShuffleActive.Value;
                    }
                    MediaUpdated?.Invoke();
                }
            }
            catch { }
        }

        public void RefreshTimeline()
        {
            try
            {
                if (_currentSession == null) return;
                var timeline = _currentSession.GetTimelineProperties();
                if (timeline != null)
                {
                    PositionSeconds = timeline.Position.TotalSeconds;
                    DurationSeconds = Math.Max(1.0, timeline.EndTime.TotalSeconds);
                    MediaUpdated?.Invoke();
                }
            }
            catch { }
        }

        public async Task<bool> TogglePlayPauseAsync()
        {
            try
            {
                if (_currentSession != null)
                {
                    return await _currentSession.TryTogglePlayPauseAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> SkipNextAsync()
        {
            try
            {
                if (_currentSession != null)
                {
                    return await _currentSession.TrySkipNextAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> SkipPreviousAsync()
        {
            try
            {
                if (_currentSession != null)
                {
                    return await _currentSession.TrySkipPreviousAsync();
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> ChangeShuffleAsync(bool shuffle)
        {
            try
            {
                if (_currentSession != null)
                {
                    return await _currentSession.TryChangeShuffleActiveAsync(shuffle);
                }
            }
            catch { }
            return false;
        }

        public async Task<bool> ChangePlaybackPositionAsync(double seconds)
        {
            try
            {
                if (_currentSession != null)
                {
                    long ticks = (long)(seconds * TimeSpan.TicksPerSecond);
                    return await _currentSession.TryChangePlaybackPositionAsync(ticks);
                }
            }
            catch { }
            return false;
        }
    }

    private FastSurface? _screenCapturer;
    private FastSurface? _renderSurface;
    private readonly byte[] _halfRawBuffer = new byte[HalfWidth * HalfHeight * 4];
    private readonly byte[] _blurHBuffer = new byte[HalfWidth * HalfHeight * 4];
    private readonly byte[] _blurredBuffer = new byte[HalfWidth * HalfHeight * 4];

    private readonly SystemMediaController _sysMedia = new();
    private string _lastTimeString = "";
    private double _lastTimeMaskUpdateTime = 0.0;
    private byte[]? _timeMask;
    private int _timeWidth;
    private int _timeHeight;
    private readonly object _timeLock = new();

    private int _currentTrackIndex = 0;
    private bool _isPlaying = true;
    private double _trackProgressSeconds = 88.0; // 1:28
    private bool _isShuffle = false;
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
                    if (_sysMedia.HasActiveSession)
                    {
                        _ = _sysMedia.TogglePlayPauseAsync();
                    }
                    else
                    {
                        _isPlaying = !_isPlaying;
                    }
                    UpdateMusicMask();
                    UpdateTimeMaskIfNeeded();
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
                    if (_sysMedia.HasActiveSession)
                    {
                        _ = _sysMedia.SkipNextAsync();
                    }
                    else
                    {
                        if (_isShuffle)
                        {
                            _currentTrackIndex = Random.Shared.Next(0, Playlist.Length);
                        }
                        else
                        {
                            _currentTrackIndex = (_currentTrackIndex + 1) % Playlist.Length;
                        }
                        _trackProgressSeconds = 0.0;
                    }
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
                    if (_sysMedia.HasActiveSession)
                    {
                        _ = _sysMedia.SkipPreviousAsync();
                    }
                    else
                    {
                        _currentTrackIndex = (_currentTrackIndex - 1 + Playlist.Length) % Playlist.Length;
                        _trackProgressSeconds = 0.0;
                    }
                    UpdateMusicMask();
                }
                else
                {
                    StepBackward();
                }
            }
            else if (e.KeyCode is Keys.S or Keys.Z or Keys.U)
            {
                _isShuffle = !_isShuffle;
                if (_sysMedia.HasActiveSession)
                {
                    _ = _sysMedia.ChangeShuffleAsync(_isShuffle);
                }
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
        // CenterX = 300, TargetW = 430 -> startX = 300 - 215 = 85
        // Moved down fully inside pill: startY = 90 (72px top padding vs 18px bottom padding)
        float mx = pt.X - 85f;
        float my = pt.Y - 90f;

        // Content layout: textStartX = 84, rightEdge = 414, barW = 330, ctrlCenterX = 84 + 165 = 249
        // 1. Play / Pause Central Button: cx = 249, cy = 76, radius = 18
        if (Math.Sqrt(Math.Pow(mx - 249, 2) + Math.Pow(my - 76, 2)) <= 20)
        {
            if (_sysMedia.HasActiveSession)
            {
                _ = _sysMedia.TogglePlayPauseAsync();
            }
            else
            {
                _isPlaying = !_isPlaying;
            }
            UpdateMusicMask();
            UpdateTimeMaskIfNeeded();
            return true;
        }

        // 2. Next Track: cx = 297, cy = 76, radius = 16
        if (Math.Sqrt(Math.Pow(mx - 297, 2) + Math.Pow(my - 76, 2)) <= 18)
        {
            if (_sysMedia.HasActiveSession)
            {
                _ = _sysMedia.SkipNextAsync();
            }
            else
            {
                if (_isShuffle)
                {
                    _currentTrackIndex = Random.Shared.Next(0, Playlist.Length);
                }
                else
                {
                    _currentTrackIndex = (_currentTrackIndex + 1) % Playlist.Length;
                }
                _trackProgressSeconds = 0.0;
            }
            UpdateMusicMask();
            return true;
        }

        // 3. Prev Track: cx = 201, cy = 76, radius = 16
        if (Math.Sqrt(Math.Pow(mx - 201, 2) + Math.Pow(my - 76, 2)) <= 18)
        {
            if (_sysMedia.HasActiveSession)
            {
                _ = _sysMedia.SkipPreviousAsync();
            }
            else
            {
                _currentTrackIndex = (_currentTrackIndex - 1 + Playlist.Length) % Playlist.Length;
                _trackProgressSeconds = 0.0;
            }
            UpdateMusicMask();
            return true;
        }

        // 4. Shuffle Toggle Button: cx = 94, cy = 76, radius = 15
        if (Math.Sqrt(Math.Pow(mx - 94, 2) + Math.Pow(my - 76, 2)) <= 16)
        {
            _isShuffle = !_isShuffle;
            if (_sysMedia.HasActiveSession)
            {
                _ = _sysMedia.ChangeShuffleAsync(_isShuffle);
            }
            UpdateMusicMask();
            return true;
        }

        // 5. Timeline Scrubbing: my in [36, 54], mx in [84, 414]
        if (my >= 34 && my <= 56 && mx >= 82 && mx <= 416)
        {
            double ratio = Math.Clamp((mx - 84) / (414.0 - 84.0), 0.0, 1.0);
            var track = Playlist[_currentTrackIndex];
            double newPos = ratio * track.DurationSeconds;
            if (_sysMedia.HasActiveSession)
            {
                _ = _sysMedia.ChangePlaybackPositionAsync(newPos);
            }
            else
            {
                _trackProgressSeconds = newPos;
            }
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

        _sysMedia.MediaUpdated += OnSystemMediaUpdated;
        _ = _sysMedia.InitializeAsync();

        UpdateTimeMaskIfNeeded();
        UpdateMusicMask();
    }

    private void OnSystemMediaUpdated()
    {
        if (_sysMedia.HasActiveSession)
        {
            _isPlaying = _sysMedia.IsPlaying;
            _isShuffle = _sysMedia.IsShuffle;
            _trackProgressSeconds = _sysMedia.PositionSeconds;
            Playlist[0].Title = _sysMedia.Title;
            Playlist[0].Artist = _sysMedia.Artist;
            Playlist[0].Album = _sysMedia.Album;
            Playlist[0].DurationSeconds = _sysMedia.DurationSeconds;
            _currentTrackIndex = 0;
        }
        UpdateMusicMask();
        UpdateTimeMaskIfNeeded();
    }

    private void UpdateMusicMask()
    {
        var track = Playlist[_currentTrackIndex];
        var (mask, w, h) = PrecomputeMusicMask(track, _trackProgressSeconds, _isPlaying, _isShuffle, _vinylRotationAngle, _visualizerTime);
        lock (_musicLock)
        {
            _musicMask = mask;
            _musicWidth = w;
            _musicHeight = h;
        }
    }

    private static Font GetPremiumFont(float sizeInPoints, FontStyle style)
    {
        string[] fontCandidates = new[]
        {
            "Segoe UI Variable Display",
            "Segoe UI Variable Text",
            "Aptos Display",
            "Segoe UI",
            "Bahnschrift"
        };

        foreach (var name in fontCandidates)
        {
            try
            {
                var font = new Font(name, sizeInPoints, style);
                if (font.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    font.FontFamily.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return font;
                }
                font.Dispose();
            }
            catch { }
        }

        return new Font("Segoe UI", sizeInPoints, style);
    }

    private static void DrawRoundedSquareCover(
        Graphics g,
        float x,
        float y,
        float size,
        float radius,
        Color accentColor,
        double rotationAngle,
        bool isPlaying,
        double visualizerTime)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = new GraphicsPath();
        path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
        path.AddArc(x + size - radius * 2, y, radius * 2, radius * 2, 270, 90);
        path.AddArc(x + size - radius * 2, y + size - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(x, y + size - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();

        // 1. Subtle Translucent Frosted Glass Album Card Fill
        using (var brushBg = new SolidBrush(Color.FromArgb(32, 255, 255, 255)))
        {
            g.FillPath(brushBg, path);
        }

        // 2. Concentric Vinyl Stylized Grooves
        float cx = x + size * 0.5f;
        float cy = y + size * 0.5f;
        float maxR = size * 0.42f;

        for (int i = 1; i <= 3; i++)
        {
            float r = maxR * (0.35f + i * 0.22f);
            using var penRing = new Pen(Color.FromArgb(65, 255, 255, 255), 1.0f);
            g.DrawEllipse(penRing, cx - r, cy - r, r * 2, r * 2);
        }

        // 3. Rotating Specular Light Sweep on Vinyl
        using (var brushSheen = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
        {
            using var sheenPath = new GraphicsPath();
            sheenPath.AddPie(cx - maxR, cy - maxR, maxR * 2, maxR * 2, (float)(rotationAngle + 30), 45f);
            sheenPath.AddPie(cx - maxR, cy - maxR, maxR * 2, maxR * 2, (float)(rotationAngle + 210), 45f);
            g.FillPath(brushSheen, sheenPath);
        }

        // 4. Center Luminous Disc
        float centerR = size * 0.16f;
        using (var brushCenter = new SolidBrush(Color.FromArgb(160, 255, 255, 255)))
        {
            g.FillEllipse(brushCenter, cx - centerR, cy - centerR, centerR * 2, centerR * 2);
        }

        // Inner Core Ring
        using (var penCore = new Pen(Color.FromArgb(200, 255, 255, 255), 1.0f))
        {
            g.DrawEllipse(penCore, cx - centerR * 0.55f, cy - centerR * 0.55f, centerR * 1.1f, centerR * 1.1f);
        }

        // 5. Hollow Center Spindle Core
        float holeR = size * 0.06f;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var brushHole = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(brushHole, cx - holeR, cy - holeR, holeR * 2, holeR * 2);
        }
        g.CompositingMode = CompositingMode.SourceOver;

        // 6. Equalizer Live Waveform Overlay
        if (isPlaying)
        {
            float eqCx = x + size - 14f;
            float eqCy = y + size - 14f;
            DrawEqualizerBars(g, eqCx, eqCy, 1.8f, 10f, visualizerTime);
        }

        // 7. Crisp Rounded Square Glass Rim
        using (var penOuterRim = new Pen(Color.FromArgb(140, 255, 255, 255), 1.2f))
        {
            g.DrawPath(penOuterRim, path);
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

    private static void DrawShuffleIcon(Graphics g, float cx, float cy, float size, bool isActive)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = size * 0.95f;
        float h = size * 0.65f;
        float x1 = cx - w * 0.5f;
        float x2 = cx + w * 0.5f;
        float yTop = cy - h * 0.5f;
        float yBot = cy + h * 0.5f;
        float midX = cx;

        Color color = isActive ? Color.FromArgb(255, 255, 75, 115) : Color.FromArgb(200, 255, 255, 255);
        using var pen = new Pen(color, size * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Line 1: Top-left to bottom-right curve
        using (var path1 = new GraphicsPath())
        {
            path1.AddBezier(x1, yTop, midX - w * 0.15f, yTop, midX + w * 0.15f, yBot, x2, yBot);
            g.DrawPath(pen, path1);
        }

        // Line 2: Bottom-left to top-right curve
        using (var path2 = new GraphicsPath())
        {
            path2.AddBezier(x1, yBot, midX - w * 0.15f, yBot, midX + w * 0.15f, yTop, x2, yTop);
            g.DrawPath(pen, path2);
        }

        // Arrowhead Top-Right
        using (var brush = new SolidBrush(color))
        {
            float arrowSize = size * 0.26f;
            PointF[] triTop = new[]
            {
                new PointF(x2 - arrowSize * 0.3f, yTop - arrowSize),
                new PointF(x2 + arrowSize * 1.0f, yTop),
                new PointF(x2 - arrowSize * 0.3f, yTop + arrowSize)
            };
            g.FillPolygon(brush, triTop);

            // Arrowhead Bottom-Right
            PointF[] triBot = new[]
            {
                new PointF(x2 - arrowSize * 0.3f, yBot - arrowSize),
                new PointF(x2 + arrowSize * 1.0f, yBot),
                new PointF(x2 - arrowSize * 0.3f, yBot + arrowSize)
            };
            g.FillPolygon(brush, triBot);
        }

        // Active state indicator dot
        if (isActive)
        {
            using var brushDot = new SolidBrush(Color.FromArgb(255, 255, 75, 115));
            g.FillEllipse(brushDot, cx - 1.5f, cy + h * 0.85f, 3.0f, 3.0f);
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
        bool isShuffle,
        double rotationAngle,
        double visualizerTime)
    {
        const float superScale = 4.0f;
        int targetW = 430;
        int targetH = 92;
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var fontTitle = GetPremiumFont(14.0f * superScale, FontStyle.Bold);
            using var fontArtist = GetPremiumFont(9.5f * superScale, FontStyle.Regular);
            using var fontTime = GetPremiumFont(7.5f * superScale, FontStyle.Bold);

            // 1. Left Section: Rounded Square Album Artwork
            float artX = 14f * superScale;
            float artY = 2f * superScale;
            float artSize = 54f * superScale;
            float artRadius = 12f * superScale;
            DrawRoundedSquareCover(g, artX, artY, artSize, artRadius, track.CoverAccentColor, rotationAngle, isPlaying, visualizerTime);

            // 2. Right Section: Track Details & Scrubbing Rail
            float textStartX = artX + artSize + 16f * superScale;
            float rightEdge = (targetW - 14f) * superScale;
            float barW = rightEdge - textStartX;

            // Track Title (Pure Luminous White)
            using (var brushTitle = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(track.Title, fontTitle, brushTitle, textStartX, 1f * superScale, StringFormat.GenericDefault);
            }

            // Artist & Album Subtitle
            using (var brushArtist = new SolidBrush(Color.FromArgb(195, 255, 255, 255)))
            {
                g.DrawString(track.Artist, fontArtist, brushArtist, textStartX, 20f * superScale, StringFormat.GenericDefault);
            }

            // Timeline Scrubbing Rail
            float barY = 44f * superScale;
            float barH = 2.5f * superScale;
            double progressRatio = Math.Clamp(progressSeconds / track.DurationSeconds, 0.0, 1.0);

            using (var penRail = new Pen(Color.FromArgb(50, 255, 255, 255), barH) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(penRail, textStartX + barH * 0.5f, barY, rightEdge - barH * 0.5f, barY);
            }

            float fillEnd = textStartX + (float)(progressRatio * barW);
            if (fillEnd > textStartX + barH)
            {
                using var penFill = new Pen(Color.FromArgb(250, 255, 255, 255), barH) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(penFill, textStartX + barH * 0.5f, barY, fillEnd, barY);
            }

            float beadR = 3.5f * superScale;
            using (var brushBead = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.FillEllipse(brushBead, fillEnd - beadR, barY - beadR, beadR * 2, beadR * 2);
            }

            // Time Labels (Elapsed on Left, Remaining on Right)
            int elMin = (int)(progressSeconds / 60);
            int elSec = (int)(progressSeconds % 60);
            string elStr = $"{elMin}:{elSec:D2}";

            double remSeconds = Math.Max(0.0, track.DurationSeconds - progressSeconds);
            int remMin = (int)(remSeconds / 60);
            int remSec = (int)(remSeconds % 60);
            string remStr = $"-{remMin}:{remSec:D2}";

            float timeLabelY = 50f * superScale;
            using (var brushTime = new SolidBrush(Color.FromArgb(165, 255, 255, 255)))
            {
                g.DrawString(elStr, fontTime, brushTime, textStartX, timeLabelY, StringFormat.GenericDefault);
                var remSize = g.MeasureString(remStr, fontTime, PointF.Empty, StringFormat.GenericDefault);
                g.DrawString(remStr, fontTime, brushTime, rightEdge - remSize.Width, timeLabelY, StringFormat.GenericDefault);
            }

            // 3. Bottom Row: Media Transport Controls
            float ctrlY = 76f * superScale;
            float ctrlCenterX = textStartX + barW * 0.5f;

            // Shuffle Button (Left)
            DrawShuffleIcon(g, textStartX + 8f * superScale, ctrlY, 13f * superScale, isShuffle);

            // Previous Track Button
            DrawTrackSkipButton(g, ctrlCenterX - 48f * superScale, ctrlY, 13f * superScale, isNext: false);

            // Center Play / Pause Hero Glass Button
            DrawPlayPauseButton(g, ctrlCenterX, ctrlY, 15f * superScale, isPlaying);

            // Next Track Button
            DrawTrackSkipButton(g, ctrlCenterX + 48f * superScale, ctrlY, 13f * superScale, isNext: true);

            // AirPlay / Streaming Icon (Right)
            DrawAirPlayIcon(g, rightEdge - 8f * superScale, ctrlY, 12f * superScale);
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
        var track = Playlist[_currentTrackIndex];
        double nowSec = _frameStopwatch.Elapsed.TotalSeconds;

        if (_isPlaying || now.ToString("hh:mm:ss tt") != _lastTimeString)
        {
            if (_isPlaying && nowSec - _lastTimeMaskUpdateTime < 0.030)
            {
                return;
            }
            _lastTimeMaskUpdateTime = nowSec;
            _lastTimeString = now.ToString("hh:mm:ss tt");

            var (mask, w, h) = PrecomputeClockMask(now, _vinylRotationAngle, _isPlaying, _visualizerTime, track.CoverAccentColor);
            lock (_timeLock)
            {
                _timeMask = mask;
                _timeWidth = w;
                _timeHeight = h;
            }
        }
    }

    private static (byte[] mask, int width, int height) PrecomputeClockMask(
        DateTime now,
        double vinylAngle,
        bool isPlaying,
        double visualizerTime,
        Color trackAccent)
    {
        const float superScale = 4.0f;
        string timeMain = now.ToString("hh:mm");
        string timeSec = ":" + now.ToString("ss");
        string timeAmPm = now.ToString("tt");

        using var fontMain = GetPremiumFont(13.5f * superScale, FontStyle.Bold);
        using var fontSec = GetPremiumFont(9.5f * superScale, FontStyle.Bold);
        using var fontAmPm = GetPremiumFont(7.5f * superScale, FontStyle.Bold);

        int targetW = 176;
        int targetH = 34;
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // 1. Left Section: Rotating Circular Vinyl Album Disc
            float discCx = 14f * superScale;
            float discCy = 17f * superScale;
            float discR = 10.5f * superScale;

            // Vinyl Disc Outer Translucent Base
            using (var brushDisc = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
            {
                g.FillEllipse(brushDisc, discCx - discR, discCy - discR, discR * 2, discR * 2);
            }
            using (var penDisc = new Pen(Color.FromArgb(160, 255, 255, 255), 1.0f * superScale))
            {
                g.DrawEllipse(penDisc, discCx - discR, discCy - discR, discR * 2, discR * 2);
            }

            // Concentric Vinyl Micro-ring
            float ringR = discR * 0.65f;
            using (var penRing = new Pen(Color.FromArgb(70, 255, 255, 255), 0.8f * superScale))
            {
                g.DrawEllipse(penRing, discCx - ringR, discCy - ringR, ringR * 2, ringR * 2);
            }

            // Rotating Specular Sheen (Spins when playing!)
            using (var brushSheen = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
            {
                using var sheenPath = new GraphicsPath();
                sheenPath.AddPie(discCx - discR, discCy - discR, discR * 2, discR * 2, (float)(vinylAngle + 30), 45f);
                sheenPath.AddPie(discCx - discR, discCy - discR, discR * 2, discR * 2, (float)(vinylAngle + 210), 45f);
                g.FillPath(brushSheen, sheenPath);
            }

            // Center Track Label Disc
            float centerR = discR * 0.35f;
            using (var brushCenter = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.FillEllipse(brushCenter, discCx - centerR, discCy - centerR, centerR * 2, centerR * 2);
            }

            // Hollow Center Spindle Hole
            float holeR = discR * 0.12f;
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var brushHole = new SolidBrush(Color.Transparent))
            {
                g.FillEllipse(brushHole, discCx - holeR, discCy - holeR, holeR * 2, holeR * 2);
            }
            g.CompositingMode = CompositingMode.SourceOver;

            // 2. Middle Section: Luxury Horology Typography
            float currX = 32f * superScale;
            float baseLineY = 7.5f * superScale;

            using (var brushMain = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(timeMain, fontMain, brushMain, currX, baseLineY, StringFormat.GenericTypographic);
            }
            var sizeMain = g.MeasureString(timeMain, fontMain, PointF.Empty, StringFormat.GenericTypographic);
            currX += sizeMain.Width + 2f * superScale;

            using (var brushSec = new SolidBrush(Color.FromArgb(215, 255, 255, 255)))
            {
                g.DrawString(timeSec, fontSec, brushSec, currX, baseLineY + 2.5f * superScale, StringFormat.GenericTypographic);
            }
            var sizeSec = g.MeasureString(timeSec, fontSec, PointF.Empty, StringFormat.GenericTypographic);
            currX += sizeSec.Width + 4.5f * superScale;

            using (var brushAmPm = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
            {
                g.DrawString(timeAmPm, fontAmPm, brushAmPm, currX, baseLineY + 3.0f * superScale, StringFormat.GenericTypographic);
            }

            // 3. Right Section: Live 3-Bar Audio Visualizer
            float eqCx = (targetW - 14f) * superScale;
            float eqCy = 17f * superScale;
            DrawEqualizerBars(g, eqCx, eqCy, 1.8f * superScale, 10f * superScale, isPlaying ? visualizerTime : 0.0);
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
                // Stable music player positioning: moved down fully inside pill (72px top padding vs 16px bottom padding)
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - musicW * 0.5);
                int startY = 90;

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
