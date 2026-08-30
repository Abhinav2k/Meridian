using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

    // Compact resting size bounds (150px paused -> 206px playing)
    private const int CompactPausedWidth = 150;
    private const int CompactPlayingWidth = 206;
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

    private sealed class FastSurface : IDisposable
    {
        public IntPtr MemDC { get; private set; }
        public IntPtr HBitmap { get; private set; }
        public IntPtr BitsPtr { get; private set; }
        private IntPtr _oldBitmap;
        public int Width { get; }
        public int Height { get; }

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
                    biSize = (int)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = width,
                    biHeight = -height, // Top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            HBitmap = CreateDIBSection(MemDC, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            BitsPtr = bits;
            _oldBitmap = SelectObject(MemDC, HBitmap);
            ReleaseDC(IntPtr.Zero, screenDC);
        }

        public bool Capture(int x, int y, int width, int height)
        {
            if (MemDC == IntPtr.Zero) return false;
            IntPtr screenDC = GetDC(IntPtr.Zero);
            if (screenDC == IntPtr.Zero) return false;
            try
            {
                return BitBlt(MemDC, 0, 0, width, height, screenDC, x, y, 0x00CC0020);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public void Dispose()
        {
            if (MemDC != IntPtr.Zero)
            {
                SelectObject(MemDC, _oldBitmap);
                DeleteObject(HBitmap);
                DeleteDC(MemDC);
                MemDC = IntPtr.Zero;
            }
        }
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
        public string Artist { get; set; } = "The Weeknd • Daft Punk";
        public string Album { get; set; } = "Starboy";
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
            DurationSeconds = 230.0,
            CoverAccentColor = Color.FromArgb(255, 235, 40, 80)
        },
        new TrackInfo
        {
            Title = "Midnight City",
            Artist = "M83 • Anthony Gonzalez",
            Album = "Hurry Up, We're Dreaming",
            DurationSeconds = 243.0,
            CoverAccentColor = Color.FromArgb(255, 120, 60, 240)
        },
        new TrackInfo
        {
            Title = "Blinding Lights",
            Artist = "The Weeknd • Max Martin",
            Album = "After Hours",
            DurationSeconds = 200.0,
            CoverAccentColor = Color.FromArgb(255, 245, 140, 30)
        },
        new TrackInfo
        {
            Title = "Get Lucky",
            Artist = "Daft Punk • Pharrell Williams",
            Album = "Random Access Memories",
            DurationSeconds = 248.0,
            CoverAccentColor = Color.FromArgb(255, 240, 200, 50)
        },
        new TrackInfo
        {
            Title = "Nightcall",
            Artist = "Kavinsky • Lovefoxxx",
            Album = "OutRun",
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
        public bool IsPlaying { get; private set; } = false;
        public double PositionSeconds { get; private set; } = 0.0;
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
                else
                {
                    IsPlaying = false;
                    MediaUpdated?.Invoke();
                }
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

    // Tab state in expanded modal: 0 = Music, 1 = Weather, 2 = Chrono/Clock
    private int _activeTab = 0;

    private int _currentTrackIndex = 0;
    private bool _isPlaying = false; // Only true when real music is playing!
    private double _trackProgressSeconds = 0.0;
    private bool _isShuffle = false;
    private double _vinylRotationAngle = 0.0;
    private double _visualizerTime = 0.0;
    private double _lastExpandedMaskUpdateTime = 0.0;

    // Compact pill dynamic expansion when music plays (0.0 = paused/compact, 1.0 = playing/expanded)
    private double _playingExpandP = 0.0;

    private byte[]? _expandedMask;
    private int _expandedWidth;
    private int _expandedHeight;
    private readonly object _expandedLock = new();

    private readonly AutoResetEvent _renderSignal = new(false);
    private Thread? _renderThread;
    private volatile bool _running = true;
    private IntPtr _hwnd;
    private double _progress = 0.0;
    private double _hoverPos = 0.0; // Spring position (0.0 to 1.0+)
    private double _hoverVel = 0.0; // Spring velocity
    private double _animDirection = 1.0; // +1.0 = forward (expand), -1.0 = backward (retract)
    private readonly Stopwatch _totalStopwatch = Stopwatch.StartNew();
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

        _sysMedia.MediaUpdated += OnSystemMediaUpdated;

        Shown += async (_, _) =>
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

            await _sysMedia.InitializeAsync();
        };

        KeyPreview = true;
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
            else if (e.KeyCode == Keys.Space)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.TogglePlayPauseAsync();
                }
                else
                {
                    _isPlaying = !_isPlaying;
                    UpdateExpandedMask();
                    UpdateTimeMaskIfNeeded();
                }
            }
            else if (e.KeyCode is Keys.Right or Keys.N)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.SkipNextAsync();
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
                    UpdateExpandedMask();
                }
            }
            else if (e.KeyCode is Keys.Left or Keys.P)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.SkipPreviousAsync();
                }
                else
                {
                    _currentTrackIndex = (_currentTrackIndex - 1 + Playlist.Length) % Playlist.Length;
                    _trackProgressSeconds = 0.0;
                    UpdateExpandedMask();
                }
            }
            else if (e.KeyCode is Keys.S or Keys.Z or Keys.U)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.ChangeShuffleAsync(!_isShuffle);
                }
                else
                {
                    _isShuffle = !_isShuffle;
                    UpdateExpandedMask();
                }
            }
            else if (e.KeyCode == Keys.D1)
            {
                _activeTab = 0;
                UpdateExpandedMask();
            }
            else if (e.KeyCode == Keys.D2)
            {
                _activeTab = 1;
                UpdateExpandedMask();
            }
            else if (e.KeyCode == Keys.D3)
            {
                _activeTab = 2;
                UpdateExpandedMask();
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

        MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                Close();
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (_hoverPos > 0.3)
                {
                    await HandleExpandedClickAsync(e.Location);
                }
            }
        };
    }

    private void OnSystemMediaUpdated()
    {
        if (_sysMedia.HasActiveSession)
        {
            _isPlaying = _sysMedia.IsPlaying;
            _isShuffle = _sysMedia.IsShuffle;
            _trackProgressSeconds = _sysMedia.PositionSeconds;

            var track = Playlist[_currentTrackIndex];
            track.Title = _sysMedia.Title;
            track.Artist = _sysMedia.Artist;
            track.Album = _sysMedia.Album;
            track.DurationSeconds = _sysMedia.DurationSeconds;

            UpdateExpandedMask();
            UpdateTimeMaskIfNeeded();
        }
    }

    private async Task<bool> HandleExpandedClickAsync(Point pt)
    {
        // Check Top Tab Bar click: Y in [26, 60], X in [190, 410]
        if (pt.Y >= 26 && pt.Y <= 60)
        {
            if (pt.X >= 195 && pt.X < 265)
            {
                _activeTab = 0;
                UpdateExpandedMask();
                return true;
            }
            if (pt.X >= 265 && pt.X < 335)
            {
                _activeTab = 1;
                UpdateExpandedMask();
                return true;
            }
            if (pt.X >= 335 && pt.X <= 405)
            {
                _activeTab = 2;
                UpdateExpandedMask();
                return true;
            }
        }

        if (_activeTab == 0)
        {
            // Content area offset within Surface:
            // CenterX = 300, TargetW = 460 -> startX = 300 - 230 = 70
            // startY = TopPadding + 8 = 26
            float mx = pt.X - 70f;
            float my = pt.Y - 26f;

            // 1. Play / Pause Central Button: cx = 249, cy = 120, radius = 18
            if (Math.Sqrt(Math.Pow(mx - 249, 2) + Math.Pow(my - 120, 2)) <= 20)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.TogglePlayPauseAsync();
                }
                else
                {
                    _isPlaying = !_isPlaying;
                    UpdateExpandedMask();
                    UpdateTimeMaskIfNeeded();
                }
                return true;
            }

            // 2. Next Track: cx = 297, cy = 120, radius = 16
            if (Math.Sqrt(Math.Pow(mx - 297, 2) + Math.Pow(my - 120, 2)) <= 18)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.SkipNextAsync();
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
                    UpdateExpandedMask();
                }
                return true;
            }

            // 3. Prev Track: cx = 201, cy = 120, radius = 16
            if (Math.Sqrt(Math.Pow(mx - 201, 2) + Math.Pow(my - 120, 2)) <= 18)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.SkipPreviousAsync();
                }
                else
                {
                    _currentTrackIndex = (_currentTrackIndex - 1 + Playlist.Length) % Playlist.Length;
                    _trackProgressSeconds = 0.0;
                    UpdateExpandedMask();
                }
                return true;
            }

            // 4. Shuffle Toggle Button: cx = 94, cy = 120, radius = 15
            if (Math.Sqrt(Math.Pow(mx - 94, 2) + Math.Pow(my - 120, 2)) <= 16)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.ChangeShuffleAsync(!_isShuffle);
                }
                else
                {
                    _isShuffle = !_isShuffle;
                    UpdateExpandedMask();
                }
                return true;
            }

            // 5. Timeline Scrubbing: my in [76, 100], mx in [78, 446]
            if (my >= 76 && my <= 100 && mx >= 78 && mx <= 446)
            {
                double ratio = Math.Clamp((mx - 86) / (444.0 - 86.0), 0.0, 1.0);
                var track = Playlist[_currentTrackIndex];
                double targetSec = ratio * track.DurationSeconds;

                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.ChangePlaybackPositionAsync(targetSec);
                }
                else
                {
                    _trackProgressSeconds = targetSec;
                    UpdateExpandedMask();
                }
                return true;
            }
        }
        else if (_activeTab == 2)
        {
            // Chrono stopwatch start/reset button hit-test
            float mx = pt.X - 70f;
            float my = pt.Y - 26f;
            if (my >= 90 && my <= 126 && mx >= 140 && mx <= 320)
            {
                _isPlaying = !_isPlaying;
                UpdateExpandedMask();
                return true;
            }
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
        UpdateExpandedMask();
    }

    private void UpdateExpandedMask()
    {
        var track = Playlist[_currentTrackIndex];
        var (mask, w, h) = PrecomputeExpandedMask(
            _activeTab,
            track,
            _trackProgressSeconds,
            _isPlaying,
            _isShuffle,
            _vinylRotationAngle,
            _visualizerTime);

        lock (_expandedLock)
        {
            _expandedMask = mask;
            _expandedWidth = w;
            _expandedHeight = h;
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

        // Arrowheads
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

            PointF[] triBot = new[]
            {
                new PointF(x2 - arrowSize * 0.3f, yBot - arrowSize),
                new PointF(x2 + arrowSize * 1.0f, yBot),
                new PointF(x2 - arrowSize * 0.3f, yBot + arrowSize)
            };
            g.FillPolygon(brush, triBot);
        }

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

    private static (byte[] mask, int width, int height) PrecomputeExpandedMask(
        int activeTab,
        TrackInfo track,
        double progressSeconds,
        bool isPlaying,
        bool isShuffle,
        double rotationAngle,
        double visualizerTime)
    {
        const float superScale = 4.0f;
        int targetW = 460;
        int targetH = 150;
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var fontTab = GetPremiumFont(8.0f * superScale, FontStyle.Bold);
            using var fontTitle = GetPremiumFont(14.0f * superScale, FontStyle.Bold);
            using var fontArtist = GetPremiumFont(9.5f * superScale, FontStyle.Regular);
            using var fontTime = GetPremiumFont(7.5f * superScale, FontStyle.Bold);
            using var fontLarge = GetPremiumFont(22.0f * superScale, FontStyle.Bold);
            using var fontMedium = GetPremiumFont(11.0f * superScale, FontStyle.Bold);
            using var fontClockHeader = GetPremiumFont(8.5f * superScale, FontStyle.Bold);

            // ==========================================
            // TOP SECTION: Small Hover-Switchable Tabs & Live Clock Badge
            // ==========================================
            float tabBarCx = (targetW * 0.5f) * superScale;
            float tabBarCy = 14f * superScale;
            float tabWidth = 58f * superScale;
            float tabHeight = 22f * superScale;
            float totalTabsW = tabWidth * 3f;
            float tabStartX = tabBarCx - totalTabsW * 0.5f;

            // Tab Bar Background Container Pill
            float barPad = 2.5f * superScale;
            using (var pathBar = new GraphicsPath())
            {
                float bx = tabStartX - barPad;
                float by = tabBarCy - tabHeight * 0.5f - barPad;
                float bw = totalTabsW + barPad * 2f;
                float bh = tabHeight + barPad * 2f;
                float br = bh * 0.5f;

                pathBar.AddArc(bx, by, br * 2, br * 2, 180, 90);
                pathBar.AddArc(bx + bw - br * 2, by, br * 2, br * 2, 270, 90);
                pathBar.AddArc(bx + bw - br * 2, by + bh - br * 2, br * 2, br * 2, 0, 90);
                pathBar.AddArc(bx, by + bh - br * 2, br * 2, br * 2, 90, 90);
                pathBar.CloseFigure();

                using var brushBar = new SolidBrush(Color.FromArgb(25, 255, 255, 255));
                g.FillPath(brushBar, pathBar);
                using var penBar = new Pen(Color.FromArgb(70, 255, 255, 255), 1.0f * superScale);
                g.DrawPath(penBar, pathBar);
            }

            // Active Tab Sliding Indicator Pill
            float activeX = tabStartX + activeTab * tabWidth;
            using (var pathActive = new GraphicsPath())
            {
                float ax = activeX;
                float ay = tabBarCy - tabHeight * 0.5f;
                float aw = tabWidth;
                float ah = tabHeight;
                float ar = ah * 0.5f;

                pathActive.AddArc(ax, ay, ar * 2, ar * 2, 180, 90);
                pathActive.AddArc(ax + aw - ar * 2, ay, ar * 2, ar * 2, 270, 90);
                pathActive.AddArc(ax + aw - ar * 2, ay + ah - ar * 2, ar * 2, ar * 2, 0, 90);
                pathActive.AddArc(ax, ay + ah - ar * 2, ar * 2, ar * 2, 90, 90);
                pathActive.CloseFigure();

                using var brushActive = new SolidBrush(Color.FromArgb(65, 255, 255, 255));
                g.FillPath(brushActive, pathActive);
                using var penActive = new Pen(Color.FromArgb(160, 255, 255, 255), 1.0f * superScale);
                g.DrawPath(penActive, pathActive);
            }

            // Tab 0: Music, Tab 1: Weather, Tab 2: Chrono
            string[] tabLabels = new[] { "♫ Music", "☀ Weather", "⏱ Chrono" };
            for (int t = 0; t < 3; t++)
            {
                float tx = tabStartX + t * tabWidth;
                var strSize = g.MeasureString(tabLabels[t], fontTab, PointF.Empty, StringFormat.GenericDefault);
                float labelX = tx + (tabWidth - strSize.Width) * 0.5f;
                float labelY = tabBarCy - strSize.Height * 0.5f;

                Color tabColor = (t == activeTab) ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(145, 255, 255, 255);
                using var brushTab = new SolidBrush(tabColor);
                g.DrawString(tabLabels[t], fontTab, brushTab, labelX, labelY, StringFormat.GenericDefault);
            }

            // Live Time Badge in Header (Top-Right)
            string liveClockStr = DateTime.Now.ToString("h:mm tt");
            var clockSize = g.MeasureString(liveClockStr, fontClockHeader, PointF.Empty, StringFormat.GenericDefault);
            using (var brushHeaderClock = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.DrawString(liveClockStr, fontClockHeader, brushHeaderClock, (targetW - 18f) * superScale - clockSize.Width, tabBarCy - clockSize.Height * 0.5f, StringFormat.GenericDefault);
            }

            // ==========================================
            // CONTENT AREA BASED ON ACTIVE TAB
            // ==========================================
            if (activeTab == 0)
            {
                // ----------------------------------------------------
                // TAB 0: MUSIC PLAYER (Clean, Compact, Shifted Down)
                // ----------------------------------------------------
                float artX = 16f * superScale;
                float artY = 46f * superScale;
                float artSize = 54f * superScale;
                float artRadius = 12f * superScale;
                DrawRoundedSquareCover(g, artX, artY, artSize, artRadius, track.CoverAccentColor, rotationAngle, isPlaying, visualizerTime);

                float textStartX = artX + artSize + 16f * superScale;
                float rightEdge = (targetW - 16f) * superScale;
                float barW = rightEdge - textStartX;

                // Track Title (Pure Luminous White)
                using (var brushTitle = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.DrawString(track.Title, fontTitle, brushTitle, textStartX, 44f * superScale, StringFormat.GenericDefault);
                }

                // Artist & Album Subtitle
                using (var brushArtist = new SolidBrush(Color.FromArgb(195, 255, 255, 255)))
                {
                    g.DrawString(track.Artist, fontArtist, brushArtist, textStartX, 63f * superScale, StringFormat.GenericDefault);
                }

                // Timeline Scrubbing Rail
                float barY = 86f * superScale;
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

                float timeLabelY = 92f * superScale;
                using (var brushTime = new SolidBrush(Color.FromArgb(165, 255, 255, 255)))
                {
                    g.DrawString(elStr, fontTime, brushTime, textStartX, timeLabelY, StringFormat.GenericDefault);
                    var remSize = g.MeasureString(remStr, fontTime, PointF.Empty, StringFormat.GenericDefault);
                    g.DrawString(remStr, fontTime, brushTime, rightEdge - remSize.Width, timeLabelY, StringFormat.GenericDefault);
                }

                // Bottom Row: Media Transport Controls
                float ctrlY = 120f * superScale;
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
            else if (activeTab == 1)
            {
                // ----------------------------------------------------
                // TAB 1: LUXURY WEATHER CARD
                // ----------------------------------------------------
                float wX = 24f * superScale;
                float wY = 46f * superScale;

                // Sun & Cloud Procedural Icon
                float sunX = wX + 22f * superScale;
                float sunY = wY + 22f * superScale;
                using (var brushSun = new SolidBrush(Color.FromArgb(255, 255, 210, 60)))
                {
                    g.FillEllipse(brushSun, sunX - 14f * superScale, sunY - 14f * superScale, 28f * superScale, 28f * superScale);
                }
                using (var brushCloud = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
                {
                    g.FillEllipse(brushCloud, sunX - 8f * superScale, sunY - 2f * superScale, 22f * superScale, 18f * superScale);
                    g.FillEllipse(brushCloud, sunX + 4f * superScale, sunY + 2f * superScale, 18f * superScale, 14f * superScale);
                    g.FillEllipse(brushCloud, sunX - 16f * superScale, sunY + 4f * superScale, 16f * superScale, 12f * superScale);
                }

                // Large Temperature
                float tempX = wX + 68f * superScale;
                using (var brushTemp = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.DrawString("24°", fontLarge, brushTemp, tempX, wY - 2f * superScale, StringFormat.GenericDefault);
                }

                // Condition & Location
                using (var brushCond = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                {
                    g.DrawString("Partly Cloudy", fontMedium, brushCond, tempX + 58f * superScale, wY + 2f * superScale, StringFormat.GenericDefault);
                }
                using (var brushLoc = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
                {
                    g.DrawString("San Francisco • High: 28° Low: 19°", fontArtist, brushLoc, tempX + 58f * superScale, wY + 20f * superScale, StringFormat.GenericDefault);
                }

                // Bottom 3 Weather Metrics
                string[] metrics = new[] { "HUMIDITY  62%", "WIND  14 km/h", "UV INDEX  3 Mod" };
                float badgeStartX = wX + 8f * superScale;
                float badgeW = 126f * superScale;
                float badgeY = wY + 52f * superScale;
                float badgeH = 22f * superScale;

                for (int m = 0; m < 3; m++)
                {
                    float bx = badgeStartX + m * (badgeW + 12f * superScale);
                    using var pathBadge = new GraphicsPath();
                    float br = badgeH * 0.5f;
                    pathBadge.AddArc(bx, badgeY, br * 2, br * 2, 180, 90);
                    pathBadge.AddArc(bx + badgeW - br * 2, badgeY, br * 2, br * 2, 270, 90);
                    pathBadge.AddArc(bx + badgeW - br * 2, badgeY + badgeH - br * 2, br * 2, br * 2, 0, 90);
                    pathBadge.AddArc(bx, badgeY + badgeH - br * 2, br * 2, br * 2, 90, 90);
                    pathBadge.CloseFigure();

                    using var brushBadgeBg = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
                    g.FillPath(brushBadgeBg, pathBadge);
                    using var penBadge = new Pen(Color.FromArgb(80, 255, 255, 255), 1.0f * superScale);
                    g.DrawPath(penBadge, pathBadge);

                    using var brushMetric = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                    var mSize = g.MeasureString(metrics[m], fontTab, PointF.Empty, StringFormat.GenericDefault);
                    g.DrawString(metrics[m], fontTab, brushMetric, bx + (badgeW - mSize.Width) * 0.5f, badgeY + (badgeH - mSize.Height) * 0.5f, StringFormat.GenericDefault);
                }
            }
            else
            {
                // ----------------------------------------------------
                // TAB 2: SWISS CHRONO & CLOCK CARD
                // ----------------------------------------------------
                var now = DateTime.Now;
                string grandTime = now.ToString("hh:mm:ss tt");
                string grandDate = now.ToString("dddd, MMMM dd, yyyy");

                float cX = (targetW * 0.5f) * superScale;
                float cY = 46f * superScale;

                // Grand Horology Time
                var timeSize = g.MeasureString(grandTime, fontLarge, PointF.Empty, StringFormat.GenericDefault);
                using (var brushGrand = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.DrawString(grandTime, fontLarge, brushGrand, cX - timeSize.Width * 0.5f, cY, StringFormat.GenericDefault);
                }

                // Date Subtitle
                var dateSize = g.MeasureString(grandDate, fontArtist, PointF.Empty, StringFormat.GenericDefault);
                using (var brushDate = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
                {
                    g.DrawString(grandDate, fontArtist, brushDate, cX - dateSize.Width * 0.5f, cY + 34f * superScale, StringFormat.GenericDefault);
                }

                // Interactive Stopwatch / Timer Pill
                float timerW = 180f * superScale;
                float timerH = 24f * superScale;
                float timerX = cX - timerW * 0.5f;
                float timerY = cY + 54f * superScale;
                using var pathTimer = new GraphicsPath();
                float tr = timerH * 0.5f;
                pathTimer.AddArc(timerX, timerY, tr * 2, tr * 2, 180, 90);
                pathTimer.AddArc(timerX + timerW - tr * 2, timerY, tr * 2, tr * 2, 270, 90);
                pathTimer.AddArc(timerX + timerW - tr * 2, timerY + timerH - tr * 2, tr * 2, tr * 2, 0, 90);
                pathTimer.AddArc(timerX, timerY + timerH - tr * 2, tr * 2, tr * 2, 90, 90);
                pathTimer.CloseFigure();

                using var brushTimerBg = new SolidBrush(Color.FromArgb(35, 255, 255, 255));
                g.FillPath(brushTimerBg, pathTimer);
                using var penTimer = new Pen(Color.FromArgb(90, 255, 255, 255), 1.0f * superScale);
                g.DrawPath(penTimer, pathTimer);

                string timerStr = isPlaying ? "⏱ CHRONO: RUNNING (01:28.45)" : "⏱ CHRONO: PAUSED";
                var tSize = g.MeasureString(timerStr, fontTab, PointF.Empty, StringFormat.GenericDefault);
                using var brushTimerText = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                g.DrawString(timerStr, fontTab, brushTimerText, timerX + (timerW - tSize.Width) * 0.5f, timerY + (timerH - tSize.Height) * 0.5f, StringFormat.GenericDefault);
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
                            int pxOffset = rowOffset + sx * 4;
                            int a = scan[pxOffset + 3];
                            int lum = (a > 0) ? a : Math.Max((int)scan[pxOffset + 0], Math.Max((int)scan[pxOffset + 1], (int)scan[pxOffset + 2]));
                            sum += lum;
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
        double nowSec = _totalStopwatch.Elapsed.TotalSeconds;

        if (_isPlaying || now.ToString("hh:mm:ss tt") != _lastTimeString)
        {
            if (_isPlaying && nowSec - _lastTimeMaskUpdateTime < 0.030)
            {
                return;
            }
            _lastTimeMaskUpdateTime = nowSec;
            _lastTimeString = now.ToString("hh:mm:ss tt");

            var (mask, w, h) = PrecomputeClockMask(
                now,
                _vinylRotationAngle,
                _isPlaying,
                _visualizerTime,
                _playingExpandP,
                track.CoverAccentColor);

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
        double playingExpandP,
        Color trackAccent)
    {
        const float superScale = 4.0f;
        string timeMain = now.ToString("hh:mm");
        string timeSec = ":" + now.ToString("ss");
        string timeAmPm = now.ToString("tt");

        using var fontMain = GetPremiumFont(14.0f * superScale, FontStyle.Bold);
        using var fontSec = GetPremiumFont(10.0f * superScale, FontStyle.Bold);
        using var fontAmPm = GetPremiumFont(8.0f * superScale, FontStyle.Bold);

        using var bmpMeasure = new Bitmap(1, 1);
        using var gMeasure = Graphics.FromImage(bmpMeasure);
        var sizeMain = gMeasure.MeasureString(timeMain, fontMain, PointF.Empty, StringFormat.GenericTypographic);
        var sizeSec = gMeasure.MeasureString(timeSec, fontSec, PointF.Empty, StringFormat.GenericTypographic);
        var sizeAmPm = gMeasure.MeasureString(timeAmPm, fontAmPm, PointF.Empty, StringFormat.GenericTypographic);

        float spacingSec = 2f * superScale;
        float spacingAmPm = 4.5f * superScale;
        float totalTextWidth = sizeMain.Width + spacingSec + sizeSec.Width + spacingAmPm + sizeAmPm.Width;

        // Dynamic compact width: 150px paused -> 206px playing
        int targetW = (int)Math.Round(150.0 + (206.0 - 150.0) * playingExpandP);
        int targetH = CompactPillHeight; // 44px
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // 1. Left Section: Rotating Circular Vinyl Album Disc
            // ONLY APPEARS WHEN REAL MUSIC IS PLAYING (playingExpandP > 0.05)
            // EQUAL PADDING: Pill height = 44px, Disc Diameter = 26px (Radius = 13px)
            // Top Padding = 9px, Bottom Padding = 9px, Left Padding = 9px!
            // Disc Center: X = 22px (9 + 13), Y = 22px (9 + 13)
            if (playingExpandP > 0.05)
            {
                float discAlpha = (float)Math.Clamp((playingExpandP - 0.05) / 0.95, 0.0, 1.0);
                float discCx = 22.0f * superScale;
                float discCy = 22.0f * superScale;
                float discR = 13.0f * superScale;

                // Vinyl Base
                using (var brushDisc = new SolidBrush(Color.FromArgb((int)(40 * discAlpha), 255, 255, 255)))
                {
                    g.FillEllipse(brushDisc, discCx - discR, discCy - discR, discR * 2, discR * 2);
                }
                using (var penDisc = new Pen(Color.FromArgb((int)(160 * discAlpha), 255, 255, 255), 1.0f * superScale))
                {
                    g.DrawEllipse(penDisc, discCx - discR, discCy - discR, discR * 2, discR * 2);
                }

                // Concentric Micro-ring
                float ringR = discR * 0.65f;
                using (var penRing = new Pen(Color.FromArgb((int)(70 * discAlpha), 255, 255, 255), 0.8f * superScale))
                {
                    g.DrawEllipse(penRing, discCx - ringR, discCy - ringR, ringR * 2, ringR * 2);
                }

                // Rotating Specular Sheen (Spins continuously with vinylAngle!)
                using (var brushSheen = new SolidBrush(Color.FromArgb((int)(50 * discAlpha), 255, 255, 255)))
                {
                    using var sheenPath = new GraphicsPath();
                    sheenPath.AddPie(discCx - discR, discCy - discR, discR * 2, discR * 2, (float)(vinylAngle + 30), 45f);
                    sheenPath.AddPie(discCx - discR, discCy - discR, discR * 2, discR * 2, (float)(vinylAngle + 210), 45f);
                    g.FillPath(brushSheen, sheenPath);
                }

                // Center Label Hub
                float centerR = discR * 0.35f;
                using (var brushCenter = new SolidBrush(Color.FromArgb((int)(200 * discAlpha), 255, 255, 255)))
                {
                    g.FillEllipse(brushCenter, discCx - centerR, discCy - centerR, centerR * 2, centerR * 2);
                }

                // Center Hollow Spindle Hole
                float holeR = discR * 0.12f;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var brushHole = new SolidBrush(Color.Transparent))
                {
                    g.FillEllipse(brushHole, discCx - holeR, discCy - holeR, holeR * 2, holeR * 2);
                }
                g.CompositingMode = CompositingMode.SourceOver;
            }

            // 2. Middle Section: Swiss Horology Clock Typography
            float textStartX;
            if (playingExpandP > 0.05)
            {
                float leftBound = (22f + 13f + 8f) * superScale;
                float rightBound = (targetW - 9f - 14f) * superScale;
                textStartX = leftBound + Math.Max(0f, (rightBound - leftBound - totalTextWidth) * 0.5f);
            }
            else
            {
                textStartX = Math.Max(0f, (superW - totalTextWidth) * 0.5f);
            }

            float currX = textStartX;
            float baseLineY = (superH - sizeMain.Height) * 0.5f;

            using (var brushMain = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
            {
                g.DrawString(timeMain, fontMain, brushMain, currX, baseLineY, StringFormat.GenericTypographic);
            }
            currX += sizeMain.Width + spacingSec;

            using (var brushSec = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            {
                g.DrawString(timeSec, fontSec, brushSec, currX, baseLineY + 2.5f * superScale, StringFormat.GenericTypographic);
            }
            currX += sizeSec.Width + spacingAmPm;

            using (var brushAmPm = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
            {
                g.DrawString(timeAmPm, fontAmPm, brushAmPm, currX, baseLineY + 3.0f * superScale, StringFormat.GenericTypographic);
            }

            // 3. Right Section: Live 3-Bar Audio Visualizer (Equal 9px padding from right edge)
            if (playingExpandP > 0.10)
            {
                float eqCx = (targetW - 15f) * superScale;
                float eqCy = 22f * superScale;
                DrawEqualizerBars(g, eqCx, eqCy, 1.8f * superScale, 10f * superScale, isPlaying ? visualizerTime : 0.0);
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
                            int pxOffset = rowOffset + sx * 4;
                            int a = scan[pxOffset + 3];
                            int lum = (a > 0) ? a : Math.Max((int)scan[pxOffset + 0], Math.Max((int)scan[pxOffset + 1], (int)scan[pxOffset + 2]));
                            sum += lum;
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

    public void SaveSnapshot(string filename = "liquid-glass-snapshot.png")
    {
        try
        {
            var surface = _renderSurface;
            if (surface != null && surface.BitsPtr != IntPtr.Zero)
            {
                using var bmp = new Bitmap(SurfaceWidth, SurfaceHeight, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    using var bgBrush = new LinearGradientBrush(
                        new Rectangle(0, 0, SurfaceWidth, SurfaceHeight),
                        Color.FromArgb(28, 32, 52),
                        Color.FromArgb(12, 14, 24),
                        45f);
                    g.FillRectangle(bgBrush, 0, 0, SurfaceWidth, SurfaceHeight);
                }

                var data = bmp.LockBits(new Rectangle(0, 0, SurfaceWidth, SurfaceHeight), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                unsafe
                {
                    uint* pSrc = (uint*)surface.BitsPtr;
                    uint* pDstBmp = (uint*)data.Scan0;
                    for (int i = 0; i < SurfaceWidth * SurfaceHeight; i++)
                    {
                        uint src = pSrc[i];
                        byte sa = (byte)(src >> 24);
                        if (sa == 0) continue;

                        byte sr = (byte)(src >> 16);
                        byte sg = (byte)(src >> 8);
                        byte sb = (byte)src;

                        uint dst = pDstBmp[i];
                        byte dr = (byte)(dst >> 16);
                        byte dg = (byte)(dst >> 8);
                        byte db = (byte)dst;

                        double aNorm = sa / 255.0;
                        double invA = 1.0 - aNorm;

                        byte finalR = (byte)Math.Clamp(sr + dr * invA, 0, 255);
                        byte finalG = (byte)Math.Clamp(sg + dg * invA, 0, 255);
                        byte finalB = (byte)Math.Clamp(sb + db * invA, 0, 255);

                        pDstBmp[i] = (0xFFu << 24) | ((uint)finalR << 16) | ((uint)finalG << 8) | finalB;
                    }
                }
                bmp.UnlockBits(data);
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string rootDir = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
                string path = Path.Combine(rootDir, filename);
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

            // Compact pill dynamic expansion when music plays (smooth interpolation)
            double targetPlayingExpand = _isPlaying ? 1.0 : 0.0;
            _playingExpandP += (targetPlayingExpand - _playingExpandP) * Math.Min(1.0, 10.0 * dt);

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
                double insideDist = Math.Min(0.0, Math.Max(qx, qy));
                double mouseSdf = outDist + insideDist - _currentGeometry.Radius;

                if (mouseSdf <= 1.5)
                {
                    isHovered = true;
                }

                // ==========================================
                // HOVER TAB SWITCHING IN EXPANDED VIEW
                // ==========================================
                if (_hoverPos > 0.6)
                {
                    int mouseSurfaceX = cursorPos.x - rect.Left;
                    int mouseSurfaceY = cursorPos.y - rect.Top;

                    // Tab bar area: Y in [26, 60], centered around X = 300
                    if (mouseSurfaceY >= 26 && mouseSurfaceY <= 60)
                    {
                        if (mouseSurfaceX >= 195 && mouseSurfaceX < 265)
                        {
                            if (_activeTab != 0)
                            {
                                _activeTab = 0;
                                UpdateExpandedMask();
                            }
                        }
                        else if (mouseSurfaceX >= 265 && mouseSurfaceX < 335)
                        {
                            if (_activeTab != 1)
                            {
                                _activeTab = 1;
                                UpdateExpandedMask();
                            }
                        }
                        else if (mouseSurfaceX >= 335 && mouseSurfaceX <= 405)
                        {
                            if (_activeTab != 2)
                            {
                                _activeTab = 2;
                                UpdateExpandedMask();
                            }
                        }
                    }
                }
            }

            // Refined physical damped harmonic spring oscillator
            // Stiffness = 175.0, Damping = 15.0
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
                if (!_sysMedia.HasActiveSession)
                {
                    _trackProgressSeconds += dt;
                    var currTrack = Playlist[_currentTrackIndex];
                    if (_trackProgressSeconds >= currTrack.DurationSeconds)
                    {
                        _trackProgressSeconds = 0.0;
                        _currentTrackIndex = (_currentTrackIndex + 1) % Playlist.Length;
                    }
                }
                _vinylRotationAngle = (_vinylRotationAngle + 45.0 * dt) % 360.0;
                _visualizerTime += dt;
            }

            double nowSec = _totalStopwatch.Elapsed.TotalSeconds;
            if (_hoverPos > 0.05 && (nowSec - _lastExpandedMaskUpdateTime >= 0.035 || !_isPlaying))
            {
                _lastExpandedMaskUpdateTime = nowSec;
                UpdateExpandedMask();
            }

            UpdateTimeMaskIfNeeded();

            PillGeometry geom = ComputeGeometry(_progress, _hoverPos, _playingExpandP);
            _currentGeometry = geom;

            ProcessAndPresent(new Point(rect.Left, rect.Top), geom);
        }
    }

    private static PillGeometry ComputeGeometry(double spawnP, double hoverP, double playingExpandP)
    {
        double targetCenterX = SurfaceWidth * 0.5;

        // Compact pill width: 150px when paused, smoothly expanding to 206px when playing
        double activeCompactWidth = CompactPausedWidth + (CompactPlayingWidth - CompactPausedWidth) * playingExpandP;
        double restingHalfWidth = (activeCompactWidth * 0.5) + ((DefaultPillWidth * 0.5) - (activeCompactWidth * 0.5)) * hoverP;
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

        // 1. Drop descent
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
        if (capturer == null || surface == null || IsDisposed)
            return;

        capturer.Capture(screenPos.X, screenPos.Y, SurfaceWidth, SurfaceHeight);

        byte* pRaw = (byte*)capturer.BitsPtr;
        uint* pDst = (uint*)surface.BitsPtr;
        if (pRaw == null || pDst == null) return;

        fixed (byte* pHalfRaw = _halfRawBuffer)
        fixed (byte* pBlurH = _blurHBuffer)
        fixed (byte* pBlurred = _blurredBuffer)
        {
            // 1. Box downsample (600x250 -> 300x125): 4x fewer pixels, anti-aliased pre-filter
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

            // 2. Single-pass 5-tap Gaussian Blur on 300x125 (Crisp, crystal liquid glass diffusion)
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

                    // 1. Ultra-smooth Hermite cubic anti-aliased alpha falloff
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

                    // 2. Smooth Continuous Glass Border Lighting
                    // A. Outer Specular White Rim
                    double outerRimGauss = Math.Exp(-Math.Pow((sdf + 1.0) / 1.25, 2.0));
                    int outerRimLight = (int)(outerRimGauss * 225.0 * alphaVal);

                    // B. Inner Bevel Sheen
                    double innerRimGauss = Math.Exp(-Math.Pow((sdf + 3.2) / 1.6, 2.0));
                    int innerRimLight = (int)(innerRimGauss * 50.0 * alphaVal);

                    // C. Top Ambient Sky Highlight
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

            // ========================================================
            // COMPACT CLOCK & ROTATING VINYL COMPOSITING
            // ========================================================
            byte[]? timeMask;
            int timeW, timeH;
            lock (_timeLock)
            {
                timeMask = _timeMask;
                timeW = _timeWidth;
                timeH = _timeHeight;
            }

            double spawnTextAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverFadeOut = Math.Clamp(1.0 - (_hoverPos / 0.35), 0.0, 1.0);
            double textAlpha = EaseOutCubic(spawnTextAlpha) * hoverFadeOut;

            if (textAlpha > 0.005 && timeMask != null && timeW > 0 && timeH > 0)
            {
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - timeW * 0.5);
                int startY = TopPadding; // exactly top of pill (18px)

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

                // Pass 2: Ultra-luminous pure white typography
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

            // ========================================================
            // EXPANDED MODAL WITH HOVER TABS COMPOSITING
            // ========================================================
            byte[]? expMask;
            int expW, expH;
            lock (_expandedLock)
            {
                expMask = _expandedMask;
                expW = _expandedWidth;
                expH = _expandedHeight;
            }

            double spawnExpAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverExpAlpha = Math.Clamp((_hoverPos - 0.28) / 0.72, 0.0, 1.0);
            double expAlpha = EaseOutCubic(spawnExpAlpha) * EaseOutCubic(hoverExpAlpha);

            if (expAlpha > 0.005 && expMask != null && expW > 0 && expH > 0)
            {
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - expW * 0.5);
                int startY = TopPadding + 8;

                // Pass 1: Crisp Ambient Drop Shadow (1px offset)
                double shadowAlpha = expAlpha * 0.45;
                for (int ty = 0; ty < expH; ty++)
                {
                    int dstY = startY + ty + 1;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * expW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < expW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = expMask[srcRow + tx];
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

                // Pass 2: Ultra-luminous pure white content
                for (int ty = 0; ty < expH; ty++)
                {
                    int dstY = startY + ty;
                    if (dstY < 0 || dstY >= SurfaceHeight) continue;

                    int srcRow = ty * expW;
                    int dstRow = dstY * SurfaceWidth;

                    for (int tx = 0; tx < expW; tx++)
                    {
                        int dstX = startX + tx;
                        if (dstX < 0 || dstX >= SurfaceWidth) continue;

                        byte maskA = expMask[srcRow + tx];
                        if (maskA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        double fgA = (maskA / 255.0) * expAlpha * 0.98;
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

                IntPtr displayDC = GetDC(IntPtr.Zero);
                if (displayDC != IntPtr.Zero)
                {
                    try
                    {
                        UpdateLayeredWindow(_hwnd, displayDC, IntPtr.Zero, ref size, surface.MemDC, ref ptSrc, 0, ref blend, UlwAlpha);
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, displayDC);
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
            _renderThread?.Join(500);

            _screenCapturer?.Dispose();
            _renderSurface?.Dispose();
            _renderSignal.Dispose();
        }
        base.Dispose(disposing);
    }
}
