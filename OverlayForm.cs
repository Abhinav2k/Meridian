using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Media.Control;
using Windows.Storage.Streams;

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

    // Compact resting size bounds (150px paused/clock -> 206px playing with clock -> 96px playing without clock)
    private const int CompactPausedWidth = 150;
    private const int CompactPlayingWidth = 206;
    private const int CompactMusicOnlyWidth = 96;
    private const int CompactPillHeight = 44;

    private const int TopPadding = 18;
    private const double AnimationDuration = 0.85; // seconds

    private const uint WdaExcludeFromCapture = 0x11;
    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;

    private const int WsExTopMost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private const uint GW_HWNDPREV = 3;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

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

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid id, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
        int GetMeteringChannelCount(out int pnChannelCount);
        int GetChannelsPeakValues(int u32ChannelCount, [In, Out] float[] afPeakValues);
        int QueryHardwareSupport(out int pdwHardwareSupportMask);
    }

    private sealed class WindowsAudioMeter : IDisposable
    {
        private static readonly Guid MeterIid = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        private IAudioMeterInformation? _meter;
        private DateTime _lastAttempt = DateTime.MinValue;

        public float GetPeak()
        {
            try
            {
                if (_meter == null)
                {
                    if ((DateTime.UtcNow - _lastAttempt).TotalSeconds < 2.0)
                        return 0f;

                    _lastAttempt = DateTime.UtcNow;
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) == 0 && device != null)
                    {
                        var iid = MeterIid;
                        if (device.Activate(ref iid, 1, IntPtr.Zero, out var meterObj) == 0 && meterObj is IAudioMeterInformation meter)
                        {
                            _meter = meter;
                        }
                    }
                }

                if (_meter != null)
                {
                    if (_meter.GetPeakValue(out float peak) == 0)
                    {
                        return Math.Clamp(peak, 0f, 1f);
                    }
                    _meter = null;
                }
            }
            catch
            {
                _meter = null;
            }

            return 0f;
        }

        public void Dispose()
        {
            _meter = null;
        }
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
        public string Title { get; set; } = "No Media Playing";
        public string Artist { get; set; } = "Play music on Windows to control";
        public string Album { get; set; } = "";
        public double DurationSeconds { get; set; } = 0.0;
        public Color CoverAccentColor { get; set; } = Color.FromArgb(255, 60, 65, 80);
        public Bitmap? CustomCover { get; set; }
    }

    internal sealed class SystemMediaController
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public event Action? MediaUpdated;

        public bool HasActiveSession => _currentSession != null;
        public string Title { get; private set; } = "No Media Playing";
        public string Artist { get; private set; } = "Play music on Windows to control";
        public string Album { get; private set; } = "";
        public bool IsPlaying { get; private set; } = false;
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; private set; }
            = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        public DateTimeOffset LastUpdatedTime { get; private set; } = DateTimeOffset.UtcNow;
        public double PositionSeconds { get; private set; } = 0.0;
        public double DurationSeconds { get; private set; } = 0.0;
        public bool IsShuffle { get; private set; } = false;

        public bool HasActiveTrack =>
            HasActiveSession &&
            PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed &&
            PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped &&
            !string.IsNullOrWhiteSpace(Title) &&
            Title != "No Media Playing";

        public readonly object CoverLock = new();
        private Bitmap? _coverBitmap;

        public Bitmap? CoverBitmap
        {
            get
            {
                lock (CoverLock)
                {
                    return _coverBitmap;
                }
            }
            private set
            {
                lock (CoverLock)
                {
                    _coverBitmap = value;
                }
            }
        }

        public double GetCurrentPositionSeconds()
        {
            if (!IsPlaying) return PositionSeconds;
            double elapsed = (DateTimeOffset.UtcNow - LastUpdatedTime).TotalSeconds;
            if (elapsed < 0) elapsed = 0;
            return Math.Clamp(PositionSeconds + elapsed, 0.0, DurationSeconds);
        }

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
                    RefreshPlaybackInfo();
                    RefreshMediaProperties();
                    RefreshTimeline();
                }
                else
                {
                    PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    IsPlaying = false;
                    Title = "No Media Playing";
                    Artist = "Play music on Windows to control";
                    Album = "";
                    PositionSeconds = 0.0;
                    DurationSeconds = 0.0;
                    Bitmap? old;
                    lock (CoverLock)
                    {
                        old = _coverBitmap;
                        _coverBitmap = null;
                    }
                    if (old != null)
                    {
                        _ = Task.Delay(500).ContinueWith(_ => { try { old.Dispose(); } catch { } });
                    }
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

                    if (props.Thumbnail != null)
                    {
                        try
                        {
                            using var stream = await props.Thumbnail.OpenReadAsync();
                            if (stream != null && stream.Size > 0)
                            {
                                using var reader = new DataReader(stream.GetInputStreamAt(0));
                                await reader.LoadAsync((uint)stream.Size);
                                byte[] bytes = new byte[stream.Size];
                                reader.ReadBytes(bytes);
                                using var memStream = new MemoryStream(bytes);
                                using var rawBmp = new Bitmap(memStream);
                                var bmp = new Bitmap(rawBmp.Width, rawBmp.Height, PixelFormat.Format32bppArgb);
                                using (var g = Graphics.FromImage(bmp))
                                {
                                    g.DrawImage(rawBmp, 0, 0, rawBmp.Width, rawBmp.Height);
                                }
                                Bitmap? old;
                                lock (CoverLock)
                                {
                                    old = _coverBitmap;
                                    _coverBitmap = bmp;
                                }
                                if (old != null)
                                {
                                    _ = Task.Delay(500).ContinueWith(_ => { try { old.Dispose(); } catch { } });
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        Bitmap? old;
                        lock (CoverLock)
                        {
                            old = _coverBitmap;
                            _coverBitmap = null;
                        }
                        if (old != null)
                        {
                            _ = Task.Delay(500).ContinueWith(_ => { try { old.Dispose(); } catch { } });
                        }
                    }

                    MediaUpdated?.Invoke();
                }
                else if (props != null && string.IsNullOrWhiteSpace(props.Title))
                {
                    Title = "No Media Playing";
                    Artist = "Play music on Windows to control";
                    Album = "";
                    Bitmap? old;
                    lock (CoverLock)
                    {
                        old = _coverBitmap;
                        _coverBitmap = null;
                    }
                    if (old != null)
                    {
                        _ = Task.Delay(500).ContinueWith(_ => { try { old.Dispose(); } catch { } });
                    }
                    MediaUpdated?.Invoke();
                }
            }
            catch { }
        }

        public void RefreshPlaybackInfo()
        {
            try
            {
                if (_currentSession == null)
                {
                    PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                    IsPlaying = false;
                    return;
                }
                var info = _currentSession.GetPlaybackInfo();
                if (info != null)
                {
                    PlaybackStatus = info.PlaybackStatus;
                    IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (info.IsShuffleActive.HasValue)
                    {
                        IsShuffle = info.IsShuffleActive.Value;
                    }
                    RefreshTimeline();
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
                    LastUpdatedTime = timeline.LastUpdatedTime > DateTimeOffset.MinValue
                        ? timeline.LastUpdatedTime
                        : DateTimeOffset.UtcNow;
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
                    PositionSeconds = seconds;
                    LastUpdatedTime = DateTimeOffset.UtcNow;
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
    private readonly byte[] _heavyBlurBuffer = new byte[HalfWidth * HalfHeight * 4];

    private readonly SystemMediaController _sysMedia = new();
    private string _lastTimeString = "";
    private double _lastTimeMaskUpdateTime = 0.0;
    private uint[]? _timeColors;
    private int _timeWidth;
    private int _timeHeight;
    private readonly object _timeLock = new();

    // Tab constants: 0 = Home, 1 = Music, 2 = Weather, 3 = Chrono/Clock
    private const int TabHome = 0;
    private const int TabMusic = 1;
    private const int TabWeather = 2;
    private const int TabChrono = 3;

    // Projected 3D Button IDs for Media Player tab
    private const int BtnNone = 0;
    private const int BtnShuffle = 1;
    private const int BtnPrev = 2;
    private const int BtnPlayPause = 3;
    private const int BtnNext = 4;
    private const int BtnAirPlay = 5;

    private int _activeTab = TabHome;
    private readonly TrackInfo _currentTrack = new();
    private bool _isPlaying = false; // Only true when real music is playing!
    private bool _hasActiveMedia = false; // True when active media track exists (playing or paused)
    private bool _lastRenderedIsPlaying = false;
    private volatile bool _needExpandedUpdate = false;
    private double _trackProgressSeconds = 0.0;
    private bool _isShuffle = false;
    private double _vinylRotationAngle = 0.0;
    private double _visualizerTime = 0.0;
    private readonly WindowsAudioMeter _audioMeter = new();
    private readonly float[] _eqBarHeights = new float[4] { 0f, 0f, 0f, 0f };
    private double _lastExpandedMaskUpdateTime = 0.0;
    private int _hoveredButton = BtnNone;
    private int _clickedButton = BtnNone;
    private double _clickAnimTimer = 0.0;
    private bool _wasHovered = false;

    private void UpdateEqualizerPhysics(double dt)
    {
        if (!_isPlaying)
        {
            for (int i = 0; i < 4; i++)
            {
                _eqBarHeights[i] += (0.0f - _eqBarHeights[i]) * Math.Min(1.0f, 12.0f * (float)dt);
                if (_eqBarHeights[i] < 0.001f) _eqBarHeights[i] = 0.0f;
            }
            return;
        }

        float livePeak = _audioMeter.GetPeak();
        float rawEnergy = (float)Math.Clamp(Math.Pow(livePeak, 0.65), 0.0, 1.0);
        float audioEnergy = rawEnergy >= 0.02f ? rawEnergy : 0.35f;

        // Multi-band acoustic frequency distribution (Bass, Low-Mids, High-Mids, Treble)
        double pulse0 = Math.Sin(_visualizerTime * 8.5) * 0.5 + 0.5;
        float target0 = audioEnergy * (0.55f + 0.45f * (float)pulse0) * 1.15f;

        double pulse1 = (Math.Sin(_visualizerTime * 13.0 + 1.2) * 0.5 + 0.5) * 0.7 + (Math.Cos(_visualizerTime * 6.5) * 0.5 + 0.5) * 0.3;
        float target1 = audioEnergy * (0.50f + 0.50f * (float)pulse1) * 1.30f;

        double pulse2 = (Math.Sin(_visualizerTime * 18.0 + 2.5) * 0.5 + 0.5) * 0.6 + (Math.Sin(_visualizerTime * 11.2) * 0.5 + 0.5) * 0.4;
        float target2 = audioEnergy * (0.45f + 0.55f * (float)pulse2) * 1.10f;

        double pulse3 = Math.Sin(_visualizerTime * 24.0 + 4.1) * 0.5 + 0.5;
        float target3 = audioEnergy * (0.40f + 0.60f * (float)pulse3) * 0.95f;

        float[] targets = new float[4]
        {
            Math.Clamp(target0, 0.08f, 1.0f),
            Math.Clamp(target1, 0.08f, 1.0f),
            Math.Clamp(target2, 0.08f, 0.95f),
            Math.Clamp(target3, 0.08f, 0.88f)
        };

        for (int i = 0; i < 4; i++)
        {
            float target = targets[i];
            if (target > _eqBarHeights[i])
            {
                // Fast Attack (< 20ms) for punchy transient response
                _eqBarHeights[i] += (target - _eqBarHeights[i]) * Math.Min(1.0f, 36.0f * (float)dt);
            }
            else
            {
                // Smooth Gravity Decay with lower frequencies decaying slightly slower
                float decaySpeed = 7.5f + i * 1.6f;
                _eqBarHeights[i] += (target - _eqBarHeights[i]) * Math.Min(1.0f, decaySpeed * (float)dt);
            }
        }
    }

    // Compact pill dynamic expansion when music plays (0.0 = paused/compact, 1.0 = playing/expanded)
    private double _playingExpandP = 0.0;
    private double _mediaElementsAlpha = 0.0;

    // Dynamic visibility & sizing intelligence
    private DateTime _musicTimeDisplayUntil = DateTime.MinValue;
    private DateTime _unhoverShowTimeUntil = DateTime.MinValue;
    private bool _lastWasPlaying = false;
    private string _lastPlayingTrackTitle = string.Empty;
    private double _compactTimeAlpha = 1.0;
    private double _currentCompactWidth = CompactPausedWidth;
    private double _lastRenderedTimeAlpha = -1.0;
    private double _lastRenderedCompactWidth = -1.0;
    private double _lastZOrderCheckTime = 0.0;

    private uint[]? _expandedColors;
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
            // Initial spawn shows time briefly on launch
            _unhoverShowTimeUntil = DateTime.UtcNow.AddMinutes(1);
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
                    _hasActiveMedia = _isPlaying;
                    UpdateExpandedMask();
                    UpdateTimeMaskIfNeeded(force: true);
                }
            }
            else if (e.KeyCode == Keys.X)
            {
                if (!_sysMedia.HasActiveSession)
                {
                    _isPlaying = false;
                    _hasActiveMedia = false;
                    UpdateExpandedMask();
                    UpdateTimeMaskIfNeeded(force: true);
                }
            }
            else if (e.KeyCode is Keys.Right or Keys.N)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.SkipNextAsync();
                }
            }
            else if (e.KeyCode is Keys.Left or Keys.P)
            {
                if (_sysMedia.HasActiveSession)
                {
                    await _sysMedia.SkipPreviousAsync();
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
                _activeTab = TabHome;
                UpdateExpandedMask();
            }
            else if (e.KeyCode == Keys.D2)
            {
                _activeTab = TabMusic;
                UpdateExpandedMask();
            }
            else if (e.KeyCode == Keys.D3)
            {
                _activeTab = TabWeather;
                UpdateExpandedMask();
            }
            else if (e.KeyCode == Keys.D4)
            {
                _activeTab = TabChrono;
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
        try
        {
            if (_sysMedia.HasActiveSession)
            {
                bool prevPlaying = _isPlaying;
                string prevTitle = _currentTrack.Title;

                _isPlaying = _sysMedia.IsPlaying;
                _hasActiveMedia = _sysMedia.HasActiveTrack;
                _isShuffle = _sysMedia.IsShuffle;
                _trackProgressSeconds = _sysMedia.PositionSeconds;

                _currentTrack.Title = string.IsNullOrWhiteSpace(_sysMedia.Title) ? "No Media Playing" : _sysMedia.Title;
                _currentTrack.Artist = string.IsNullOrWhiteSpace(_sysMedia.Artist) ? "Audio" : _sysMedia.Artist;
                _currentTrack.Album = _sysMedia.Album;
                _currentTrack.DurationSeconds = _sysMedia.DurationSeconds;

                if ((!prevPlaying && _isPlaying) || (_isPlaying && !string.IsNullOrEmpty(_currentTrack.Title) && _currentTrack.Title != prevTitle && _currentTrack.Title != "No Media Playing"))
                {
                    _musicTimeDisplayUntil = DateTime.UtcNow.AddMinutes(2);
                }
            }
            else
            {
                _isPlaying = false;
                _hasActiveMedia = false;
                _currentTrack.Title = "No Media Playing";
                _currentTrack.Artist = "Play music on Windows to control";
                _currentTrack.Album = "";
                _currentTrack.DurationSeconds = 0.0;
            }

            _needExpandedUpdate = true;
            _renderSignal.Set();
        }
        catch { }
    }

    private static int HitTestMediaButton(float mx, float my)
    {
        float cy = 120f;
        if (Math.Abs(my - cy) > 22f) return BtnNone;

        if (Math.Abs(mx - 98f) <= 15f && Math.Abs(my - cy) <= 15f) return BtnShuffle;
        if (Math.Abs(mx - 217f) <= 17f && Math.Abs(my - cy) <= 17f) return BtnPrev;
        if (Math.Abs(mx - 265f) <= 21f && Math.Abs(my - cy) <= 21f) return BtnPlayPause;
        if (Math.Abs(mx - 313f) <= 17f && Math.Abs(my - cy) <= 17f) return BtnNext;
        if (Math.Abs(mx - 432f) <= 15f && Math.Abs(my - cy) <= 15f) return BtnAirPlay;

        return BtnNone;
    }

    private async Task<bool> HandleExpandedClickAsync(Point pt)
    {
        // Check Top Tab Bar click: Y in [24, 54]
        // 4 Tabs: Home [86, 108), Music [108, 130), Weather [130, 152), Chrono [152, 176]
        if (pt.Y >= 24 && pt.Y <= 54)
        {
            if (pt.X >= 86 && pt.X < 108)
            {
                _activeTab = TabHome;
                UpdateExpandedMask();
                return true;
            }
            if (pt.X >= 108 && pt.X < 130)
            {
                _activeTab = TabMusic;
                UpdateExpandedMask();
                return true;
            }
            if (pt.X >= 130 && pt.X < 152)
            {
                _activeTab = TabWeather;
                UpdateExpandedMask();
                return true;
            }
            if (pt.X >= 152 && pt.X <= 176)
            {
                _activeTab = TabChrono;
                UpdateExpandedMask();
                return true;
            }
        }

        if (_activeTab == TabMusic)
        {
            float mx = pt.X - 70f;
            float my = pt.Y - 26f;

            int btn = HitTestMediaButton(mx, my);
            if (btn != BtnNone)
            {
                _clickedButton = btn;
                _clickAnimTimer = 1.0;
                UpdateExpandedMask();

                switch (btn)
                {
                    case BtnPlayPause:
                        if (_sysMedia.HasActiveSession)
                        {
                            await _sysMedia.TogglePlayPauseAsync();
                        }
                        else
                        {
                            _isPlaying = !_isPlaying;
                            _hasActiveMedia = _isPlaying;
                            UpdateExpandedMask();
                            UpdateTimeMaskIfNeeded(force: true);
                        }
                        return true;

                    case BtnNext:
                        if (_sysMedia.HasActiveSession)
                        {
                            await _sysMedia.SkipNextAsync();
                        }
                        return true;

                    case BtnPrev:
                        if (_sysMedia.HasActiveSession)
                        {
                            await _sysMedia.SkipPreviousAsync();
                        }
                        return true;

                    case BtnShuffle:
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

                    case BtnAirPlay:
                        return true;
                }
            }

            // Timeline Scrubbing: my in [76, 100], mx in [86, 444]
            if (my >= 76 && my <= 100 && mx >= 86 && mx <= 444)
            {
                if (_currentTrack.DurationSeconds > 0)
                {
                    double ratio = Math.Clamp((mx - 86) / (444.0 - 86.0), 0.0, 1.0);
                    double targetSec = ratio * _currentTrack.DurationSeconds;

                    if (_sysMedia.HasActiveSession)
                    {
                        await _sysMedia.ChangePlaybackPositionAsync(targetSec);
                    }
                    else
                    {
                        _trackProgressSeconds = targetSec;
                        UpdateExpandedMask();
                    }
                }
                return true;
            }
        }
        else if (_activeTab == TabChrono)
        {
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
        EnsureSystemZOrder();

        UpdateTimeMaskIfNeeded();
        UpdateExpandedMask();
    }

    private Bitmap? GetCurrentCoverArt(TrackInfo track)
    {
        if (_sysMedia.HasActiveSession && _sysMedia.CoverBitmap != null)
        {
            return _sysMedia.CoverBitmap;
        }

        if (track.Title == "No Media Playing" || string.IsNullOrEmpty(track.Title))
        {
            return null;
        }

        if (track.CustomCover == null)
        {
            track.CustomCover = CreateProceduralCover(track, 216);
        }
        return track.CustomCover;
    }

    private static Bitmap CreateProceduralCover(TrackInfo track, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Rich luxurious multi-stop gradient
        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, size, size),
            track.CoverAccentColor,
            Color.FromArgb(255, 14, 18, 30),
            45f);
        g.FillRectangle(bgBrush, 0, 0, size, size);

        // Concentric artistic rings
        float cx = size * 0.5f;
        float cy = size * 0.5f;
        for (int i = 1; i <= 4; i++)
        {
            float r = size * (0.15f + i * 0.08f);
            using var pen = new Pen(Color.FromArgb(35 + i * 12, 255, 255, 255), 1.5f);
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        }

        // Bold Stylized Initial Monogram
        string initial = !string.IsNullOrEmpty(track.Title) && track.Title != "No Media Playing"
            ? track.Title.Substring(0, 1).ToUpperInvariant()
            : "♪";
        using var font = GetPremiumFont(size * 0.35f, FontStyle.Bold);
        var strSize = g.MeasureString(initial, font, PointF.Empty, StringFormat.GenericTypographic);
        using var brushText = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
        g.DrawString(initial, font, brushText, cx - strSize.Width * 0.5f, cy - strSize.Height * 0.5f, StringFormat.GenericTypographic);

        return bmp;
    }

    private void UpdateExpandedMask()
    {
        var track = _currentTrack;
        var cover = GetCurrentCoverArt(track);
        var (colors, w, h) = PrecomputeExpandedContent(
            _activeTab,
            track,
            cover,
            _trackProgressSeconds,
            _isPlaying,
            _isShuffle,
            _vinylRotationAngle,
            _eqBarHeights,
            _hoveredButton,
            _clickedButton,
            _clickAnimTimer);

        lock (_expandedLock)
        {
            _expandedColors = colors;
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
        Bitmap? coverBmp,
        Color accentColor,
        double rotationAngle,
        bool isPlaying,
        float[]? eqBarHeights)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = new GraphicsPath();
        path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
        path.AddArc(x + size - radius * 2, y, radius * 2, radius * 2, 270, 90);
        path.AddArc(x + size - radius * 2, y + size - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(x, y + size - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();

        var state = g.Save();
        g.SetClip(path);

        bool coverDrawn = false;
        if (coverBmp != null)
        {
            try
            {
                lock (coverBmp)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(coverBmp, x, y, size, size);

                    // Dynamic specular light gleam on top half of album art
                    using var brushHighlight = new LinearGradientBrush(
                        new RectangleF(x, y, size, size * 0.5f),
                        Color.FromArgb(60, 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255),
                        90f);
                    g.FillRectangle(brushHighlight, x, y, size, size * 0.5f);
                    coverDrawn = true;
                }
            }
            catch
            {
                coverDrawn = false;
            }
        }

        if (!coverDrawn)
        {
            // Subtle Translucent Frosted Glass Album Card Fill
            using var brushBg = new SolidBrush(Color.FromArgb(35, 255, 255, 255));
            g.FillPath(brushBg, path);

            float cx = x + size * 0.5f;
            float cy = y + size * 0.5f;
            float maxR = size * 0.42f;

            for (int i = 1; i <= 3; i++)
            {
                float r = maxR * (0.35f + i * 0.22f);
                using var penRing = new Pen(Color.FromArgb(50, 255, 255, 255), 1.0f);
                g.DrawEllipse(penRing, cx - r, cy - r, r * 2, r * 2);
            }

            // Minimal elegant note icon centered in idle frosted card
            using var fontNote = GetPremiumFont(16.0f * (size / 216f), FontStyle.Bold);
            var noteSize = g.MeasureString("♫", fontNote, PointF.Empty, StringFormat.GenericTypographic);
            using var brushNote = new SolidBrush(Color.FromArgb(130, 255, 255, 255));
            g.DrawString("♫", fontNote, brushNote, cx - noteSize.Width * 0.5f, cy - noteSize.Height * 0.5f, StringFormat.GenericTypographic);
        }

        // Live Equalizer Waveform on Bottom-Right of Album Card
        if (isPlaying)
        {
            float eqScale = size / 54f;
            float eqCx = x + size - 14f * eqScale;
            float eqCy = y + size - 14f * eqScale;
            DrawEqualizerBars(g, eqCx, eqCy, 1.85f * eqScale, 11.5f * eqScale, eqBarHeights, accentColor, 1.0f);
        }

        g.Restore(state);

        // Crisp Rounded Square Glass Outer Rim
        using (var penOuterRim = new Pen(Color.FromArgb(160, 255, 255, 255), 1.2f))
        {
            g.DrawPath(penOuterRim, path);
        }
    }

    private static void DrawEqualizerBars(
        Graphics g,
        float cx,
        float cy,
        float barWidth,
        float maxHeight,
        float[]? barHeights,
        Color accentColor,
        float alpha = 1.0f)
    {
        if (alpha <= 0.01f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int barCount = 4;
        float spacing = barWidth * 0.65f;
        float totalW = barCount * barWidth + (barCount - 1) * spacing;
        float startX = cx - totalW * 0.5f;
        float minH = barWidth * 1.20f;

        // Subtle Album Accent Tint:
        // Blend luminous white with track album accent color
        int baseA = (int)Math.Clamp(245f * alpha, 0f, 255f);
        int rTop = (int)Math.Clamp(255f * 0.82f + accentColor.R * 0.18f, 0f, 255f);
        int gTop = (int)Math.Clamp(255f * 0.82f + accentColor.G * 0.18f, 0f, 255f);
        int bTop = (int)Math.Clamp(255f * 0.82f + accentColor.B * 0.18f, 0f, 255f);

        int rBot = (int)Math.Clamp(255f * 0.58f + accentColor.R * 0.42f, 0f, 255f);
        int gBot = (int)Math.Clamp(255f * 0.58f + accentColor.G * 0.42f, 0f, 255f);
        int bBot = (int)Math.Clamp(255f * 0.58f + accentColor.B * 0.42f, 0f, 255f);

        Color topColor = Color.FromArgb(baseA, rTop, gTop, bTop);
        Color botColor = Color.FromArgb(baseA, rBot, gBot, bBot);

        for (int i = 0; i < barCount; i++)
        {
            float normVal = (barHeights != null && i < barHeights.Length) ? barHeights[i] : 0f;
            float h = (float)Math.Clamp(minH + (maxHeight - minH) * normVal, minH, maxHeight);

            float bx = startX + i * (barWidth + spacing);
            float by = cy - h * 0.5f;

            using var path = new GraphicsPath();
            float r = barWidth * 0.5f;
            path.AddArc(bx, by, barWidth, barWidth, 180, 180);
            path.AddArc(bx, by + h - barWidth, barWidth, barWidth, 0, 180);
            path.CloseFigure();

            if (h > minH + 0.5f)
            {
                using var brush = new LinearGradientBrush(
                    new RectangleF(bx, by, barWidth, h),
                    topColor,
                    botColor,
                    90f);
                g.FillPath(brush, path);
            }
            else
            {
                using var brush = new SolidBrush(topColor);
                g.FillPath(brush, path);
            }
        }
    }

    private static GraphicsPath CreateRoundedRectanglePath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2f;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawProjectedButtonContainer(
        Graphics g,
        float cx,
        float cy,
        float halfSize,
        float cornerRadius,
        bool isHovered,
        bool isClicked,
        double clickProgress,
        float superScale,
        Action drawGlyph)
    {
        var state = g.Save();

        // Tactile micro-spring press on click
        if (isClicked && clickProgress > 0.0)
        {
            float pulse = (float)Math.Sin(clickProgress * Math.PI);
            float scale = 1.0f - 0.12f * pulse;
            float shiftY = 1.5f * pulse * superScale;
            g.TranslateTransform(cx, cy + shiftY);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-cx, -cy);
        }

        float x = cx - halfSize;
        float y = cy - halfSize;
        float size = halfSize * 2f;

        // 1. Soft Ambient Drop Shadow underneath the rounded square
        float shadowOffset = 2.0f * superScale;
        using (var shadowPath = CreateRoundedRectanglePath(x, y + shadowOffset, size, size, cornerRadius))
        using (var brushShadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
        {
            g.FillPath(brushShadow, shadowPath);
        }

        // 2. Rounded Square Tactile Glass Body (Frosted Diffusion, Strictly NO OUTLINE)
        using (var bodyPath = CreateRoundedRectanglePath(x, y, size, size, cornerRadius))
        {
            if (isHovered)
            {
                // Darkened glass interaction on mouse hover
                using var brushBody = new LinearGradientBrush(
                    new RectangleF(x, y, size, size),
                    Color.FromArgb(160, 10, 15, 25),
                    Color.FromArgb(195, 5, 8, 15),
                    90f);
                g.FillPath(brushBody, bodyPath);
            }
            else
            {
                // Distinct frosted diffusion glass fill (translucent frosted density, completely without outline)
                using var brushBody = new LinearGradientBrush(
                    new RectangleF(x, y, size, size),
                    Color.FromArgb(50, 255, 255, 255),
                    Color.FromArgb(18, 255, 255, 255),
                    90f);
                g.FillPath(brushBody, bodyPath);
            }
        }

        // 3. Button Glyph
        drawGlyph();

        g.Restore(state);
    }

    private static void DrawPlayPauseGlyph(Graphics g, float cx, float cy, float radius, bool isPlaying)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brushGlyph = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        if (isPlaying)
        {
            float barW = radius * 0.22f;
            float barH = radius * 0.68f;
            float barGap = radius * 0.24f;

            float b1X = cx - (barW + barGap * 0.5f);
            float b2X = cx + (barGap * 0.5f);
            float barY = cy - barH * 0.5f;

            g.FillRectangle(brushGlyph, b1X, barY, barW, barH);
            g.FillRectangle(brushGlyph, b2X, barY, barW, barH);
        }
        else
        {
            float triSize = radius * 0.68f;
            float triH = triSize * 0.90f;
            float triW = triSize * 0.85f;
            float leftX = cx - triW * 0.38f;
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

    private static void DrawTrackSkipGlyph(Graphics g, float cx, float cy, float size, bool isNext)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(240, 255, 255, 255));

        float dir = isNext ? 1.0f : -1.0f;
        float h = size * 0.65f;
        float w = size * 0.38f;
        float gap = size * 0.22f;

        PointF[] tri1 = new[]
        {
            new PointF(cx - (w + gap * 0.5f) * dir, cy - h * 0.5f),
            new PointF(cx - (gap * 0.5f) * dir, cy),
            new PointF(cx - (w + gap * 0.5f) * dir, cy + h * 0.5f)
        };
        g.FillPolygon(brush, tri1);

        PointF[] tri2 = new[]
        {
            new PointF(cx + (gap * 0.5f) * dir, cy - h * 0.5f),
            new PointF(cx + (w + gap * 0.5f) * dir, cy),
            new PointF(cx + (gap * 0.5f) * dir, cy + h * 0.5f)
        };
        g.FillPolygon(brush, tri2);
    }

    private static void DrawShuffleGlyph(Graphics g, float cx, float cy, float size, bool isActive)
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

        using (var path1 = new GraphicsPath())
        {
            path1.AddBezier(x1, yTop, midX - w * 0.15f, yTop, midX + w * 0.15f, yBot, x2, yBot);
            g.DrawPath(pen, path1);
        }

        using (var path2 = new GraphicsPath())
        {
            path2.AddBezier(x1, yBot, midX - w * 0.15f, yBot, midX + w * 0.15f, yTop, x2, yTop);
            g.DrawPath(pen, path2);
        }

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

    private static void DrawAirPlayGlyph(Graphics g, float cx, float cy, float size)
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

    private static (uint[] colors, int width, int height) PrecomputeExpandedContent(
        int activeTab,
        TrackInfo track,
        Bitmap? coverBmp,
        double progressSeconds,
        bool isPlaying,
        bool isShuffle,
        double rotationAngle,
        float[]? eqBarHeights,
        int hoveredButton,
        int clickedButton,
        double clickAnimProgress)
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
            // TOP SECTION: Compact Left-Aligned Icon Tabs & Right Live Clock
            // 4 Tabs: Home (⌂), Music (♫), Weather (☀), Chrono (⏱)
            // ==========================================
            float tabStartX = 16f * superScale;
            float tabBarCy = 14f * superScale;
            float tabItemW = 22f * superScale;
            float tabHeight = 20f * superScale;
            float totalTabsW = tabItemW * 4f; // 88px at 1x

            // Tab Bar Background Container Pill
            float barPad = 2.0f * superScale;
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

                using var brushBar = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
                g.FillPath(brushBar, pathBar);
                using var penBar = new Pen(Color.FromArgb(70, 255, 255, 255), 1.0f * superScale);
                g.DrawPath(penBar, pathBar);
            }

            // Active Tab Sliding Indicator Pill
            float activeX = tabStartX + activeTab * tabItemW;
            using (var pathActive = new GraphicsPath())
            {
                float ax = activeX;
                float ay = tabBarCy - tabHeight * 0.5f;
                float aw = tabItemW;
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

            // 4 Compact Icon-Only Tab Labels (⌂, ♫, ☀, ⏱)
            string[] tabIcons = new[] { "⌂", "♫", "☀", "⏱" };
            using var fontIconTab = GetPremiumFont(9.0f * superScale, FontStyle.Bold);
            for (int t = 0; t < 4; t++)
            {
                float tx = tabStartX + t * tabItemW;
                var strSize = g.MeasureString(tabIcons[t], fontIconTab, PointF.Empty, StringFormat.GenericTypographic);
                float labelX = tx + (tabItemW - strSize.Width) * 0.5f;
                float labelY = tabBarCy - strSize.Height * 0.5f;

                Color tabColor = (t == activeTab) ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255);
                using var brushTab = new SolidBrush(tabColor);
                g.DrawString(tabIcons[t], fontIconTab, brushTab, labelX, labelY, StringFormat.GenericTypographic);
            }

            // Live Time Badge in Header (Top-Right)
            string liveClockStr = DateTime.Now.ToString("h:mm tt");
            var clockSize = g.MeasureString(liveClockStr, fontClockHeader, PointF.Empty, StringFormat.GenericDefault);
            using (var brushHeaderClock = new SolidBrush(Color.FromArgb(205, 255, 255, 255)))
            {
                g.DrawString(liveClockStr, fontClockHeader, brushHeaderClock, (targetW - 16f) * superScale - clockSize.Width, tabBarCy - clockSize.Height * 0.5f, StringFormat.GenericDefault);
            }

            // ==========================================
            // CONTENT AREA
            // ==========================================
            if (activeTab == TabHome)
            {
                // TAB 0: HOME TAB (Blank / pristine liquid glass workspace ready for future widgets)
                // Left intentionally empty as requested.
            }
            else if (activeTab == TabMusic)
            {
                // TAB 1: MUSIC PLAYER
                float artX = 16f * superScale;
                float artY = 46f * superScale;
                float artSize = 54f * superScale;
                float artRadius = 12f * superScale;
                DrawRoundedSquareCover(g, artX, artY, artSize, artRadius, coverBmp, track.CoverAccentColor, rotationAngle, isPlaying, eqBarHeights);

                float textStartX = artX + artSize + 16f * superScale;
                float rightEdge = (targetW - 16f) * superScale;
                float barW = rightEdge - textStartX;

                using (var brushTitle = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.DrawString(track.Title, fontTitle, brushTitle, textStartX, 44f * superScale, StringFormat.GenericDefault);
                }

                using (var brushArtist = new SolidBrush(Color.FromArgb(195, 255, 255, 255)))
                {
                    g.DrawString(track.Artist, fontArtist, brushArtist, textStartX, 63f * superScale, StringFormat.GenericDefault);
                }

                float barY = 86f * superScale;
                float barH = 2.5f * superScale;
                double progressRatio = (track.DurationSeconds > 0)
                    ? Math.Clamp(progressSeconds / track.DurationSeconds, 0.0, 1.0)
                    : 0.0;

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

                string elStr;
                string remStr;
                if (track.DurationSeconds > 0)
                {
                    int elMin = (int)(progressSeconds / 60);
                    int elSec = (int)(progressSeconds % 60);
                    elStr = $"{elMin}:{elSec:D2}";

                    double remSeconds = Math.Max(0.0, track.DurationSeconds - progressSeconds);
                    int remMin = (int)(remSeconds / 60);
                    int remSec = (int)(remSeconds % 60);
                    remStr = $"-{remMin}:{remSec:D2}";
                }
                else
                {
                    elStr = "0:00";
                    remStr = "--:--";
                }

                float timeLabelY = 92f * superScale;
                using (var brushTime = new SolidBrush(Color.FromArgb(165, 255, 255, 255)))
                {
                    g.DrawString(elStr, fontTime, brushTime, textStartX, timeLabelY, StringFormat.GenericDefault);
                    var remSize = g.MeasureString(remStr, fontTime, PointF.Empty, StringFormat.GenericDefault);
                    g.DrawString(remStr, fontTime, brushTime, rightEdge - remSize.Width, timeLabelY, StringFormat.GenericDefault);
                }

                float ctrlY = 120f * superScale;

                // Tactile Rounded Square Glass Buttons with Different Blur Intensity, Dark Hover, and Click Animation (NO OUTLINE)
                DrawProjectedButtonContainer(g, 98f * superScale, ctrlY, 15f * superScale, 7f * superScale,
                    hoveredButton == BtnShuffle, clickedButton == BtnShuffle, clickAnimProgress, superScale,
                    () => DrawShuffleGlyph(g, 98f * superScale, ctrlY, 13f * superScale, isShuffle));

                DrawProjectedButtonContainer(g, 217f * superScale, ctrlY, 17f * superScale, 8.5f * superScale,
                    hoveredButton == BtnPrev, clickedButton == BtnPrev, clickAnimProgress, superScale,
                    () => DrawTrackSkipGlyph(g, 217f * superScale, ctrlY, 13f * superScale, isNext: false));

                DrawProjectedButtonContainer(g, 265f * superScale, ctrlY, 21f * superScale, 11f * superScale,
                    hoveredButton == BtnPlayPause, clickedButton == BtnPlayPause, clickAnimProgress, superScale,
                    () => DrawPlayPauseGlyph(g, 265f * superScale, ctrlY, 18f * superScale, isPlaying));

                DrawProjectedButtonContainer(g, 313f * superScale, ctrlY, 17f * superScale, 8.5f * superScale,
                    hoveredButton == BtnNext, clickedButton == BtnNext, clickAnimProgress, superScale,
                    () => DrawTrackSkipGlyph(g, 313f * superScale, ctrlY, 13f * superScale, isNext: true));

                DrawProjectedButtonContainer(g, 432f * superScale, ctrlY, 15f * superScale, 7f * superScale,
                    hoveredButton == BtnAirPlay, clickedButton == BtnAirPlay, clickAnimProgress, superScale,
                    () => DrawAirPlayGlyph(g, 432f * superScale, ctrlY, 12f * superScale));
            }
            else if (activeTab == TabWeather)
            {
                // TAB 2: LUXURY WEATHER
                float wX = 24f * superScale;
                float wY = 46f * superScale;

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

                float tempX = wX + 68f * superScale;
                using (var brushTemp = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.DrawString("24°", fontLarge, brushTemp, tempX, wY - 2f * superScale, StringFormat.GenericDefault);
                }

                using (var brushCond = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                {
                    g.DrawString("Partly Cloudy", fontMedium, brushCond, tempX + 58f * superScale, wY + 2f * superScale, StringFormat.GenericDefault);
                }
                using (var brushLoc = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
                {
                    g.DrawString("San Francisco • High: 28° Low: 19°", fontArtist, brushLoc, tempX + 58f * superScale, wY + 20f * superScale, StringFormat.GenericDefault);
                }

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
                // TAB 3: SWISS CHRONO & CLOCK
                var now = DateTime.Now;
                string grandTime = now.ToString("hh:mm:ss tt");
                string grandDate = now.ToString("dddd, MMMM dd, yyyy");

                float cX = (targetW * 0.5f) * superScale;
                float cY = 46f * superScale;

                var timeSize = g.MeasureString(grandTime, fontLarge, PointF.Empty, StringFormat.GenericDefault);
                using (var brushGrand = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    g.DrawString(grandTime, fontLarge, brushGrand, cX - timeSize.Width * 0.5f, cY, StringFormat.GenericDefault);
                }

                var dateSize = g.MeasureString(grandDate, fontArtist, PointF.Empty, StringFormat.GenericDefault);
                using (var brushDate = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
                {
                    g.DrawString(grandDate, fontArtist, brushDate, cX - dateSize.Width * 0.5f, cY + 34f * superScale, StringFormat.GenericDefault);
                }

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

        uint[] colorBuffer = new uint[targetW * targetH];
        var data = superBmp.LockBits(new Rectangle(0, 0, superW, superH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    for (int dy = 0; dy < 4; dy++)
                    {
                        int sy = y * 4 + dy;
                        int rowOffset = sy * superW * 4;
                        for (int dx = 0; dx < 4; dx++)
                        {
                            int sx = x * 4 + dx;
                            int pxOffset = rowOffset + sx * 4;
                            byte b = scan[pxOffset + 0];
                            byte gVal = scan[pxOffset + 1];
                            byte r = scan[pxOffset + 2];
                            byte a = scan[pxOffset + 3];

                            int trueA = (a > 0) ? a : Math.Max(r, Math.Max(gVal, b));

                            sumB += (b * trueA) >> 8;
                            sumG += (gVal * trueA) >> 8;
                            sumR += (r * trueA) >> 8;
                            sumA += trueA;
                        }
                    }

                    int avgA = sumA >> 4;
                    if (avgA == 0)
                    {
                        colorBuffer[y * targetW + x] = 0;
                    }
                    else
                    {
                        int avgR = Math.Min(255, sumR >> 4);
                        int avgG = Math.Min(255, sumG >> 4);
                        int avgB = Math.Min(255, sumB >> 4);
                        colorBuffer[y * targetW + x] = ((uint)avgA << 24) | ((uint)avgR << 16) | ((uint)avgG << 8) | (uint)avgB;
                    }
                }
            }
        }
        superBmp.UnlockBits(data);
        return (colorBuffer, targetW, targetH);
    }

    private void UpdateTimeMaskIfNeeded(bool force = false)
    {
        var now = DateTime.Now;
        var track = _currentTrack;
        var cover = GetCurrentCoverArt(track);
        double nowSec = _totalStopwatch.Elapsed.TotalSeconds;

        bool isTransitioning = (_playingExpandP > 0.001 && _playingExpandP < 0.999) ||
                               (_mediaElementsAlpha > 0.001 && _mediaElementsAlpha < 0.999) ||
                               (Math.Abs(_currentCompactWidth - _lastRenderedCompactWidth) > 0.25) ||
                               (Math.Abs(_compactTimeAlpha - _lastRenderedTimeAlpha) > 0.005);

        bool stateChanged = (_isPlaying != _lastRenderedIsPlaying);

        bool isEqDecaying = !_isPlaying && (_eqBarHeights[0] > 0.005f || _eqBarHeights[1] > 0.005f || _eqBarHeights[2] > 0.005f || _eqBarHeights[3] > 0.005f);

        if (_isPlaying || isTransitioning || isEqDecaying || stateChanged || force || now.ToString("h:mm:ss tt") != _lastTimeString)
        {
            if ((_isPlaying || isTransitioning || isEqDecaying) && !force && !stateChanged && nowSec - _lastTimeMaskUpdateTime < 0.016)
            {
                return;
            }
            _lastRenderedIsPlaying = _isPlaying;
            _lastTimeMaskUpdateTime = nowSec;
            _lastTimeString = now.ToString("h:mm:ss tt");
            _lastRenderedCompactWidth = _currentCompactWidth;
            _lastRenderedTimeAlpha = _compactTimeAlpha;

            var (colors, w, h) = PrecomputeClockContent(
                now,
                cover,
                _vinylRotationAngle,
                _isPlaying,
                _eqBarHeights,
                _currentCompactWidth,
                _mediaElementsAlpha,
                _compactTimeAlpha,
                track.CoverAccentColor);

            lock (_timeLock)
            {
                _timeColors = colors;
                _timeWidth = w;
                _timeHeight = h;
            }
        }
    }

    private static (uint[] colors, int width, int height) PrecomputeClockContent(
        DateTime now,
        Bitmap? coverBmp,
        double vinylAngle,
        bool isPlaying,
        float[]? eqBarHeights,
        double currentCompactWidth,
        double mediaElementsAlpha,
        double timeAlpha,
        Color trackAccent)
    {
        const float superScale = 4.0f;
        string timeMain = now.ToString("h:mm");
        string timeSec = ":" + now.ToString("ss");
        string timeAmPm = now.ToString("tt");

        using var fontMain = GetPremiumFont(14.0f * superScale, FontStyle.Bold);
        using var fontSub = GetPremiumFont(6.5f * superScale, FontStyle.Bold);

        using var bmpMeasure = new Bitmap(1, 1);
        using var gMeasure = Graphics.FromImage(bmpMeasure);
        gMeasure.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var sizeMain = gMeasure.MeasureString(timeMain, fontMain, PointF.Empty, StringFormat.GenericTypographic);
        var sizeSec = gMeasure.MeasureString(timeSec, fontSub, PointF.Empty, StringFormat.GenericTypographic);
        var sizeAmPm = gMeasure.MeasureString(timeAmPm, fontSub, PointF.Empty, StringFormat.GenericTypographic);

        float subW = Math.Max(sizeSec.Width, sizeAmPm.Width);
        float spacingSub = 2.5f * superScale;
        float totalClockWidth = sizeMain.Width + spacingSub + subW;

        int targetW = Math.Max(40, (int)Math.Round(currentCompactWidth));
        int targetH = CompactPillHeight; // 44px
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // 1. Left Section: Rotating Circular Vinyl Album Disc (Controlled by mediaElementsAlpha)
            if (mediaElementsAlpha > 0.01)
            {
                float discAlpha = (float)Math.Clamp(mediaElementsAlpha, 0.0, 1.0);
                float discScale = 0.85f + 0.15f * discAlpha;
                float discCx = 22.0f * superScale;
                float discCy = 22.0f * superScale;
                float discR = 15.0f * superScale * discScale;

                var state = g.Save();

                using (var fullDiscPath = new GraphicsPath())
                {
                    fullDiscPath.AddEllipse(discCx - discR, discCy - discR, discR * 2, discR * 2);
                    g.SetClip(fullDiscPath);

                    // 1. FULL ROTATING ALBUM ART FILLING THE ENTIRE DISC
                    bool coverDrawn = false;
                    if (coverBmp != null)
                    {
                        try
                        {
                            lock (coverBmp)
                            {
                                g.TranslateTransform(discCx, discCy);
                                g.RotateTransform((float)vinylAngle);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.DrawImage(coverBmp, -discR, -discR, discR * 2, discR * 2);
                                g.ResetTransform();
                                coverDrawn = true;
                            }
                        }
                        catch
                        {
                            g.ResetTransform();
                            coverDrawn = false;
                        }
                    }

                    if (!coverDrawn)
                    {
                        using var brushCenter = new SolidBrush(Color.FromArgb((int)(220 * discAlpha), trackAccent));
                        g.FillPath(brushCenter, fullDiscPath);
                    }

                    // 2. Subtle Concentric Vinyl Micro-Grooves over Album Art
                    for (int i = 1; i <= 3; i++)
                    {
                        float r = discR * (0.35f + i * 0.18f);
                        using var penGroove = new Pen(Color.FromArgb((int)(35 * discAlpha), 255, 255, 255), 0.8f * superScale);
                        g.DrawEllipse(penGroove, discCx - r, discCy - r, r * 2, r * 2);
                    }

                    using (var brushSheen = new SolidBrush(Color.FromArgb((int)(35 * discAlpha), 255, 255, 255)))
                    {
                        using var sheenPath = new GraphicsPath();
                        sheenPath.AddPie(discCx - discR, discCy - discR, discR * 2, discR * 2, (float)(vinylAngle + 30), 45f);
                        sheenPath.AddPie(discCx - discR, discCy - discR, discR * 2, discR * 2, (float)(vinylAngle + 210), 45f);
                        g.FillPath(brushSheen, sheenPath);
                    }

                    // 4. Center Hollow Spindle Hole (SourceCopy transparent core)
                    float holeR = discR * 0.14f;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var brushHole = new SolidBrush(Color.Transparent))
                    {
                        g.FillEllipse(brushHole, discCx - holeR, discCy - holeR, holeR * 2, holeR * 2);
                    }
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                g.ResetClip();

                // 5. Crisp Outer Glass Rim Pen
                using (var penDisc = new Pen(Color.FromArgb((int)(160 * discAlpha), 255, 255, 255), 1.0f * superScale))
                {
                    g.DrawEllipse(penDisc, discCx - discR, discCy - discR, discR * 2, discR * 2);
                }

                g.Restore(state);
            }

            // 2. Middle Section: Optical Clock Typography (Only when timeAlpha > 0.01)
            if (timeAlpha > 0.01)
            {
                float clockAlpha = (float)Math.Clamp(timeAlpha, 0.0, 1.0);

                float availCenter;
                if (mediaElementsAlpha > 0.01)
                {
                    float leftBound = (22f + 15f + 8f) * superScale;
                    float rightBound = (targetW - 16f - 8f) * superScale;
                    availCenter = (leftBound + rightBound) * 0.5f;
                }
                else
                {
                    availCenter = superW * 0.5f;
                }

                float textStartX = availCenter - totalClockWidth * 0.5f;
                float cy = superH * 0.5f;
                float mainY = cy - sizeMain.Height * 0.5f - 1.25f * superScale;
                float subTopY = cy - sizeSec.Height - 1.25f * superScale;
                float subBotY = cy - 1.0f * superScale;

                using (var brushMain = new SolidBrush(Color.FromArgb((int)(255 * clockAlpha), 255, 255, 255)))
                {
                    g.DrawString(timeMain, fontMain, brushMain, textStartX, mainY, StringFormat.GenericTypographic);
                }

                float subStartX = textStartX + sizeMain.Width + spacingSub;
                using (var brushSec = new SolidBrush(Color.FromArgb((int)(215 * clockAlpha), 255, 255, 255)))
                {
                    g.DrawString(timeSec, fontSub, brushSec, subStartX, subTopY, StringFormat.GenericTypographic);
                }

                using (var brushAmPm = new SolidBrush(Color.FromArgb((int)(185 * clockAlpha), 255, 255, 255)))
                {
                    g.DrawString(timeAmPm, fontSub, brushAmPm, subStartX, subBotY, StringFormat.GenericTypographic);
                }
            }

            // 3. Right Section: Live 4-Bar Equalizer (Controlled by mediaElementsAlpha)
            if (mediaElementsAlpha > 0.01)
            {
                float eqCx = (targetW - 16f) * superScale;
                float eqCy = 22f * superScale;
                DrawEqualizerBars(g, eqCx, eqCy, 1.85f * superScale, 11.5f * superScale, eqBarHeights, trackAccent, (float)mediaElementsAlpha);
            }
        }

        uint[] colorBuffer = new uint[targetW * targetH];
        var data = superBmp.LockBits(new Rectangle(0, 0, superW, superH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < targetH; y++)
            {
                for (int x = 0; x < targetW; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    for (int dy = 0; dy < 4; dy++)
                    {
                        int sy = y * 4 + dy;
                        int rowOffset = sy * superW * 4;
                        for (int dx = 0; dx < 4; dx++)
                        {
                            int sx = x * 4 + dx;
                            int pxOffset = rowOffset + sx * 4;
                            byte b = scan[pxOffset + 0];
                            byte gVal = scan[pxOffset + 1];
                            byte r = scan[pxOffset + 2];
                            byte a = scan[pxOffset + 3];

                            int trueA = (a > 0) ? a : Math.Max(r, Math.Max(gVal, b));

                            sumB += (b * trueA) >> 8;
                            sumG += (gVal * trueA) >> 8;
                            sumR += (r * trueA) >> 8;
                            sumA += trueA;
                        }
                    }

                    int avgA = sumA >> 4;
                    if (avgA == 0)
                    {
                        colorBuffer[y * targetW + x] = 0;
                    }
                    else
                    {
                        int avgR = Math.Min(255, sumR >> 4);
                        int avgG = Math.Min(255, sumG >> 4);
                        int avgB = Math.Min(255, sumB >> 4);
                        colorBuffer[y * targetW + x] = ((uint)avgA << 24) | ((uint)avgR << 16) | ((uint)avgG << 8) | (uint)avgB;
                    }
                }
            }
        }
        superBmp.UnlockBits(data);
        return (colorBuffer, targetW, targetH);
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

    public void SaveDesktopScreenshotWithPill(string filename = "screenshot.png")
    {
        try
        {
            var surface = _renderSurface;
            if (surface == null || surface.BitsPtr == IntPtr.Zero) return;

            Rectangle bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            int posX = Location.X;
            int posY = Location.Y;

            var data = bmp.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            unsafe
            {
                uint* pSrc = (uint*)surface.BitsPtr;
                uint* pDstBmp = (uint*)data.Scan0;
                int stride = data.Stride / 4;

                for (int y = 0; y < SurfaceHeight; y++)
                {
                    int dstY = posY + y;
                    if (dstY < 0 || dstY >= bounds.Height) continue;

                    int srcRow = y * SurfaceWidth;
                    int dstRow = dstY * stride;

                    for (int x = 0; x < SurfaceWidth; x++)
                    {
                        int dstX = posX + x;
                        if (dstX < 0 || dstX >= bounds.Width) continue;

                        uint src = pSrc[srcRow + x];
                        byte sa = (byte)(src >> 24);
                        if (sa == 0) continue;

                        byte sr = (byte)(src >> 16);
                        byte sg = (byte)(src >> 8);
                        byte sb = (byte)src;

                        uint dst = pDstBmp[dstRow + dstX];
                        byte dr = (byte)(dst >> 16);
                        byte dg = (byte)(dst >> 8);
                        byte db = (byte)dst;

                        double aNorm = sa / 255.0;
                        double invA = 1.0 - aNorm;

                        byte finalR = (byte)Math.Clamp(sr + dr * invA, 0, 255);
                        byte finalG = (byte)Math.Clamp(sg + dg * invA, 0, 255);
                        byte finalB = (byte)Math.Clamp(sb + db * invA, 0, 255);

                        pDstBmp[dstRow + dstX] = (0xFFu << 24) | ((uint)finalR << 16) | ((uint)finalG << 8) | finalB;
                    }
                }
            }
            bmp.UnlockBits(data);

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string rootDir = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
            string parentDir = Path.GetFullPath(Path.Combine(rootDir, @".."));

            bmp.Save(Path.Combine(rootDir, filename), ImageFormat.Png);
            bmp.Save(Path.Combine(parentDir, filename), ImageFormat.Png);
        }
        catch { }
    }

    private int _screenshotFrameCounter = 0;
    private void CheckScreenshotTrigger()
    {
        if ((++_screenshotFrameCounter % 10) != 0) return;

        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string rootDir = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
            string triggerPath = Path.Combine(rootDir, "take_screenshot.trigger");
            if (File.Exists(triggerPath))
            {
                SaveDesktopScreenshotWithPill("screenshot.png");
                File.Delete(triggerPath);
            }
        }
        catch { }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered;
            parameters.ExStyle |= WsExToolWindow;
            parameters.ExStyle |= WsExTopMost;
            parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }

    private void EnsureSystemZOrder()
    {
        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || IsDisposed) return;

        // Place immediately below the Windows Taskbar (Shell_TrayWnd).
        // This guarantees that the auto-hide taskbar unhides smoothly in front without obstruction,
        // while our pill stays persistently above all application and regular windows!
        IntPtr trayHwnd = FindWindow("Shell_TrayWnd", null);
        IntPtr targetAfter = (trayHwnd != IntPtr.Zero) ? trayHwnd : HWND_TOPMOST;

        IntPtr windowAbove = GetWindow(hwnd, GW_HWNDPREV);
        if (windowAbove == targetAfter)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            targetAfter,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            // Do not activate or steal focus from active windows on click
            m.Result = (IntPtr)MaNoActivate;
            return;
        }

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
            _renderSignal.WaitOne(6);
            if (!_running) break;

            try
            {
                IntPtr hwnd = _hwnd;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
                    continue;

            double dt = _frameStopwatch.Elapsed.TotalSeconds;
            _frameStopwatch.Restart();
            dt = Math.Clamp(dt, 0.0, 0.05);

            DateTime now = DateTime.Now;
            DateTime utcNow = DateTime.UtcNow;

            // Track music playback start and track change to trigger the 2-minute time display
            bool justStartedPlaying = !_lastWasPlaying && _isPlaying;
            bool trackChanged = _isPlaying && !string.IsNullOrEmpty(_currentTrack.Title) && _currentTrack.Title != _lastPlayingTrackTitle && _currentTrack.Title != "No Media Playing";
            if (justStartedPlaying || trackChanged)
            {
                _musicTimeDisplayUntil = utcNow.AddMinutes(2);
            }
            _lastWasPlaying = _isPlaying;
            if (_isPlaying)
            {
                _lastPlayingTrackTitle = _currentTrack.Title;
            }

            bool isHovered = false;
            if (GetCursorPos(out var cursorPos))
            {
                int mouseSurfaceX = cursorPos.x - rect.Left;
                int mouseSurfaceY = cursorPos.y - rect.Top;

                if (_progress > 0.10)
                {
                    double px = mouseSurfaceX - _currentGeometry.CenterX;
                    double py = mouseSurfaceY - _currentGeometry.CenterY;
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
                }
                else
                {
                    // Notch hover trigger to summon pill when despawned / hidden
                    if (mouseSurfaceY >= 0 && mouseSurfaceY <= 26 && Math.Abs(mouseSurfaceX - 300) <= 60)
                    {
                        isHovered = true;
                    }
                }

                // Intelligence: on hover expansion, auto-select Media Tab if active media, Home Tab if no media
                if (!_wasHovered && isHovered)
                {
                    _activeTab = _hasActiveMedia ? TabMusic : TabHome;
                    UpdateExpandedMask();
                }

                if (_hoverPos > 0.6)
                {
                    // 4 Tab Switcher Hover: Home [86, 108), Music [108, 130), Weather [130, 152), Chrono [152, 176]
                    if (mouseSurfaceY >= 24 && mouseSurfaceY <= 54)
                    {
                        if (mouseSurfaceX >= 86 && mouseSurfaceX < 108)
                        {
                            if (_activeTab != TabHome)
                            {
                                _activeTab = TabHome;
                                UpdateExpandedMask();
                            }
                        }
                        else if (mouseSurfaceX >= 108 && mouseSurfaceX < 130)
                        {
                            if (_activeTab != TabMusic)
                            {
                                _activeTab = TabMusic;
                                UpdateExpandedMask();
                            }
                        }
                        else if (mouseSurfaceX >= 130 && mouseSurfaceX < 152)
                        {
                            if (_activeTab != TabWeather)
                            {
                                _activeTab = TabWeather;
                                UpdateExpandedMask();
                            }
                        }
                        else if (mouseSurfaceX >= 152 && mouseSurfaceX <= 176)
                        {
                            if (_activeTab != TabChrono)
                            {
                                _activeTab = TabChrono;
                                UpdateExpandedMask();
                            }
                        }
                    }

                    // Media Player Projected Buttons Hover Interaction (darker tint on hover)
                    if (_activeTab == TabMusic)
                    {
                        float mx = mouseSurfaceX - 70f;
                        float my = mouseSurfaceY - 26f;
                        int hoveredBtn = HitTestMediaButton(mx, my);
                        if (hoveredBtn != _hoveredButton)
                        {
                            _hoveredButton = hoveredBtn;
                            UpdateExpandedMask();
                        }
                    }
                    else if (_hoveredButton != BtnNone)
                    {
                        _hoveredButton = BtnNone;
                        UpdateExpandedMask();
                    }
                }
            }

            if (!isHovered && _hoveredButton != BtnNone)
            {
                _hoveredButton = BtnNone;
                UpdateExpandedMask();
            }

            if (_wasHovered && !isHovered)
            {
                // Unhovered: time displays for 1 minute
                _unhoverShowTimeUntil = utcNow.AddMinutes(1);
            }
            _wasHovered = isHovered;

            // Spawning Intelligence:
            // 1. O'clocks: spawns and displays time for 3 minutes (e.g. HH:00:00 to HH:02:59)
            // 2. Music active: spawns
            // 3. Unhovered: stays spawned showing time for 1 minute
            // 4. Hovered: stays spawned
            // Otherwise: despawns and hides
            bool isOClock = (now.Minute < 3);
            bool isUnhoverActive = (utcNow < _unhoverShowTimeUntil);
            bool shouldBeSpawned = isHovered || _hasActiveMedia || isOClock || isUnhoverActive;

            if (shouldBeSpawned)
            {
                if (_animDirection < 0.0 || (_animDirection == 0.0 && _progress < 1.0))
                {
                    _animDirection = 1.0;
                }
            }
            else
            {
                if (_progress > 0.0 && !isHovered && (_animDirection > 0.0 || _animDirection == 0.0))
                {
                    _animDirection = -1.0;
                }
            }

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

            // Compact Time Visibility & Width Dynamics:
            // Music playing: time displays for 2 minutes (or 1 min on unhover, or 3 mins on o'clock).
            // Then time disappears, leaving only rotating disc + visualizer, and the pill gets shorter in length!
            bool isMusicTimeActive = (utcNow < _musicTimeDisplayUntil);

            bool shouldShowTime;
            if (!_hasActiveMedia)
            {
                shouldShowTime = true;
            }
            else
            {
                shouldShowTime = isMusicTimeActive || isUnhoverActive || isOClock;
            }

            double targetTimeAlpha = shouldShowTime ? 1.0 : 0.0;
            _compactTimeAlpha += (targetTimeAlpha - _compactTimeAlpha) * Math.Min(1.0, 8.0 * dt);

            if (_hasActiveMedia)
            {
                // Smoothly expand pill for media
                _playingExpandP += (1.0 - _playingExpandP) * Math.Min(1.0, 10.0 * dt);
                if (Math.Abs(1.0 - _playingExpandP) < 0.001) _playingExpandP = 1.0;

                // Once expanded sufficiently, smoothly fade in rotating album art disc and visualizer
                if (_playingExpandP > 0.45)
                {
                    _mediaElementsAlpha += (1.0 - _mediaElementsAlpha) * Math.Min(1.0, 14.0 * dt);
                    if (Math.Abs(1.0 - _mediaElementsAlpha) < 0.001) _mediaElementsAlpha = 1.0;
                }
                else
                {
                    _mediaElementsAlpha = 0.0;
                }

                // Shorter length (96px) when time disappears, full playing length (206px) when time is shown
                double targetWidth = CompactMusicOnlyWidth + (CompactPlayingWidth - CompactMusicOnlyWidth) * _compactTimeAlpha;
                _currentCompactWidth += (targetWidth - _currentCompactWidth) * Math.Min(1.0, 10.0 * dt);
            }
            else
            {
                // When stopped/closed: Album art disc and visualizer disappear FIRST before the collapse
                _mediaElementsAlpha = Math.Max(0.0, _mediaElementsAlpha - dt * 14.0);

                if (_mediaElementsAlpha <= 0.08)
                {
                    _playingExpandP += (0.0 - _playingExpandP) * Math.Min(1.0, 12.0 * dt);
                    if (_playingExpandP < 0.001) _playingExpandP = 0.0;
                }

                double targetWidth = CompactPausedWidth;
                _currentCompactWidth += (targetWidth - _currentCompactWidth) * Math.Min(1.0, 10.0 * dt);
            }

            // Animate tactile button click bounce
            if (_clickAnimTimer > 0.0)
            {
                _clickAnimTimer -= dt * 7.0;
                if (_clickAnimTimer <= 0.0)
                {
                    _clickAnimTimer = 0.0;
                    _clickedButton = BtnNone;
                }
                UpdateExpandedMask();
            }

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

            if (_isPlaying)
            {
                if (_sysMedia.HasActiveSession)
                {
                    _trackProgressSeconds = _sysMedia.GetCurrentPositionSeconds();
                }
                else
                {
                    _trackProgressSeconds += dt;
                    if (_currentTrack.DurationSeconds > 0 && _trackProgressSeconds >= _currentTrack.DurationSeconds)
                    {
                        _trackProgressSeconds = 0.0;
                    }
                }
                _vinylRotationAngle = (_vinylRotationAngle + 45.0 * dt) % 360.0;
                _visualizerTime += dt;
            }

            UpdateEqualizerPhysics(dt);

            double nowSec = _totalStopwatch.Elapsed.TotalSeconds;
            if (_hoverPos > 0.6 && _isPlaying && (nowSec - _lastExpandedMaskUpdateTime >= 0.050))
            {
                _lastExpandedMaskUpdateTime = nowSec;
                UpdateExpandedMask();
            }

            if (nowSec - _lastZOrderCheckTime >= 0.20)
            {
                _lastZOrderCheckTime = nowSec;
                EnsureSystemZOrder();
            }

            if (_needExpandedUpdate)
            {
                _needExpandedUpdate = false;
                UpdateExpandedMask();
            }

            UpdateTimeMaskIfNeeded();

            PillGeometry geom = ComputeGeometry(_progress, _hoverPos, _currentCompactWidth);
            _currentGeometry = geom;

            ProcessAndPresent(new Point(rect.Left, rect.Top), geom);
        }
        catch (Exception)
        {
            // Prevent unexpected transient GDI+ or OS rendering exceptions from killing the render thread
        }
    }
}

    private static PillGeometry ComputeGeometry(double spawnP, double hoverP, double compactWidth)
    {
        double targetCenterX = SurfaceWidth * 0.5;

        double restingHalfWidth = (compactWidth * 0.5) + ((DefaultPillWidth * 0.5) - (compactWidth * 0.5)) * hoverP;
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

        double dropProgress = Math.Clamp(spawnP / 0.45, 0.0, 1.0);
        double dropEase = EaseOutCubic(dropProgress);
        double currentCenterY = spawnCenterY + (restingCenterY - spawnCenterY) * dropEase;

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
        fixed (byte* pHeavyBlurred = _heavyBlurBuffer)
        {
            // 1. Box downsample (600x250 -> 300x125)
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

            // 2a. Single-pass 5-tap Gaussian Blur on 300x125 (standard interior blur)
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

            // 2b. Second-pass cascade Gaussian Blur on 300x125 (deep, creamy edge frosting)
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
                        int sum = pBlurred[offM2 + c] +
                                  (pBlurred[offM1 + c] << 2) +
                                  pBlurred[off0 + c] * 6 +
                                  (pBlurred[offP1 + c] << 2) +
                                  pBlurred[offP2 + c];
                        pBlurH[off0 + c] = (byte)(sum >> 4);
                    }
                    pBlurH[off0 + 3] = 255;
                }
            }

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
                        pHeavyBlurred[off0 + c] = (byte)(sum >> 4);
                    }
                    pHeavyBlurred[off0 + 3] = 255;
                }
            }

            // 3. Liquid Glass Refraction & Specular Lighting
            double straightW = geom.HalfWidth - geom.Radius;
            double straightH = geom.HalfHeight - geom.Radius;

            double spawnExpAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverExpLinear = Math.Clamp((_hoverPos - 0.62) / 0.38, 0.0, 1.0);
            double hoverExpHermite = hoverExpLinear * hoverExpLinear * (3.0 - 2.0 * hoverExpLinear);
            double expAlpha = EaseOutCubic(spawnExpAlpha) * hoverExpHermite;
            bool isMusicTabActive = (_activeTab == TabMusic) && (expAlpha > 0.01);

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

                    double edgeFactor = Math.Clamp((-sdf + 0.8) / 1.6, 0.0, 1.0);
                    double alphaVal = edgeFactor * edgeFactor * (3.0 - 2.0 * edgeFactor);
                    byte a = (byte)Math.Round(alphaVal * 255.0);

                    // Soft ambient drop shadow computation (evaluated in the outer penumbra band)
                    byte shadowA = 0;
                    if (sdf >= -1.5 && sdf <= 22.0)
                    {
                        double spy = py - 4.0;
                        double absSpy = Math.Abs(spy);
                        double sqy = absSpy - straightH;
                        double sOutX = Math.Max(0.0, qx);
                        double sOutY = Math.Max(0.0, sqy);
                        double sOutDist = Math.Sqrt(sOutX * sOutX + sOutY * sOutY);
                        double sInDist = Math.Min(0.0, Math.Max(qx, sqy));
                        double shadowSdf = sOutDist + sInDist - geom.Radius;

                        if (shadowSdf < 16.0)
                        {
                            double sNorm = Math.Clamp(shadowSdf / 14.0, 0.0, 1.0);
                            double sFalloff = (1.0 - sNorm) * (1.0 - sNorm);
                            shadowA = (byte)(sFalloff * 45.0);
                        }
                    }

                    if (a == 0)
                    {
                        pDst[idx] = (shadowA > 0) ? ((uint)shadowA << 24) : 0;
                        continue;
                    }

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

                    double edgeDistance = Math.Max(0.0, -sdf);
                    double u = Math.Clamp(1.0 - (edgeDistance * (1.0 / 14.0)), 0.0, 1.0);
                    double bend = (u * u * Math.Sqrt(u)) * 6.5;

                    double sx = Math.Clamp(x - nx * bend, 0.0, SurfaceWidth - 2.0);
                    double sy = Math.Clamp(y - ny * bend, 0.0, SurfaceHeight - 2.0);

                    double hx = Math.Clamp(sx * 0.5, 0.0, HalfWidth - 2.0);
                    double hy = Math.Clamp(sy * 0.5, 0.0, HalfHeight - 2.0);

                    int ix = (int)hx;
                    int iy = (int)hy;
                    double fx = hx - ix;
                    double fy = hy - iy;

                    int w00 = (int)((1.0 - fx) * (1.0 - fy) * 256.0);
                    int w10 = (int)(fx * (1.0 - fy) * 256.0);
                    int w01 = (int)((1.0 - fx) * fy * 256.0);
                    int w11 = Math.Max(0, 256 - (w00 + w10 + w01));

                    int off00 = (iy * HalfWidth + ix) * 4;
                    int off10 = (iy * HalfWidth + ix + 1) * 4;
                    int off01 = ((iy + 1) * HalfWidth + ix) * 4;
                    int off11 = ((iy + 1) * HalfWidth + ix + 1) * 4;

                    int bStd = (pBlurred[off00 + 0] * w00 + pBlurred[off10 + 0] * w10 + pBlurred[off01 + 0] * w01 + pBlurred[off11 + 0] * w11) >> 8;
                    int gStd = (pBlurred[off00 + 1] * w00 + pBlurred[off10 + 1] * w10 + pBlurred[off01 + 1] * w01 + pBlurred[off11 + 1] * w11) >> 8;
                    int rStd = (pBlurred[off00 + 2] * w00 + pBlurred[off10 + 2] * w10 + pBlurred[off01 + 2] * w01 + pBlurred[off11 + 2] * w11) >> 8;

                    int bHvy = (pHeavyBlurred[off00 + 0] * w00 + pHeavyBlurred[off10 + 0] * w10 + pHeavyBlurred[off01 + 0] * w01 + pHeavyBlurred[off11 + 0] * w11) >> 8;
                    int gHvy = (pHeavyBlurred[off00 + 1] * w00 + pHeavyBlurred[off10 + 1] * w10 + pHeavyBlurred[off01 + 1] * w01 + pHeavyBlurred[off11 + 1] * w11) >> 8;
                    int rHvy = (pHeavyBlurred[off00 + 2] * w00 + pHeavyBlurred[off10 + 2] * w10 + pHeavyBlurred[off01 + 2] * w01 + pHeavyBlurred[off11 + 2] * w11) >> 8;

                    // Edge Frosting Outline: smooth optical transition from clear/standard blur to dense milky blur at outer rim (within 8px)
                    double frostFactor = Math.Clamp(1.0 - (edgeDistance * (1.0 / 8.0)), 0.0, 1.0);
                    double frostSmooth = frostFactor * frostFactor * (3.0 - 2.0 * frostFactor);

                    // Distinct Blur Intensity under Rounded Square Media Buttons
                    double btnBlurFactor = 0.0;
                    if (isMusicTabActive && y >= 124 && y <= 168)
                    {
                        float bcx = 0, bhs = 0, br = 0;
                        if (x >= 152 && x <= 184) { bcx = 168f; bhs = 15f; br = 7f; }
                        else if (x >= 269 && x <= 305) { bcx = 287f; bhs = 17f; br = 8.5f; }
                        else if (x >= 313 && x <= 357) { bcx = 335f; bhs = 21f; br = 11f; }
                        else if (x >= 365 && x <= 401) { bcx = 383f; bhs = 17f; br = 8.5f; }
                        else if (x >= 486 && x <= 518) { bcx = 502f; bhs = 15f; br = 7f; }

                        if (bhs > 0)
                        {
                            double bqx = Math.Abs(x - bcx) - (bhs - br);
                            double bqy = Math.Abs(y - 146.0) - (bhs - br);
                            double oX = Math.Max(0.0, bqx);
                            double oY = Math.Max(0.0, bqy);
                            double oDist = Math.Sqrt(oX * oX + oY * oY);
                            double iDist = Math.Min(0.0, Math.Max(bqx, bqy));
                            double btnSdf = oDist + iDist - br;

                            if (btnSdf <= 1.0)
                            {
                                btnBlurFactor = Math.Clamp(-btnSdf + 0.5, 0.0, 1.0) * expAlpha;
                            }
                        }
                    }

                    double effectiveHeavyFactor = Math.Max(frostSmooth, btnBlurFactor * 0.95);

                    int b = (int)(bStd + (bHvy - bStd) * effectiveHeavyFactor);
                    int g = (int)(gStd + (gHvy - gStd) * effectiveHeavyFactor);
                    int r = (int)(rStd + (rHvy - rStd) * effectiveHeavyFactor);

                    b = Math.Clamp(b, 0, 255);
                    g = Math.Clamp(g, 0, 255);
                    r = Math.Clamp(r, 0, 255);

                    // Balanced liquid glass tint: gently dimmed (~76% brightness) without being dark or murky
                    r = (r * 195 + 40 * 61) >> 8;
                    g = (g * 195 + 44 * 61) >> 8;
                    b = (b * 195 + 56 * 61) >> 8;

                    // Composite glass over ambient drop shadow
                    byte finalA = (shadowA > 0 && a < 255)
                        ? (byte)Math.Min(255, a + ((shadowA * (255 - a)) >> 8))
                        : a;

                    uint pR = (uint)((r * a) / 255);
                    uint pG = (uint)((g * a) / 255);
                    uint pB = (uint)((b * a) / 255);
                    pDst[idx] = ((uint)finalA << 24) | (pR << 16) | (pG << 8) | pB;
                }
            }

            // ========================================================
            // COMPACT CLOCK & ROTATING VINYL COMPOSITING (32-bit ARGB)
            // ========================================================
            uint[]? timeColors;
            int timeW, timeH;
            lock (_timeLock)
            {
                timeColors = _timeColors;
                timeW = _timeWidth;
                timeH = _timeHeight;
            }

            double spawnTextAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverFadeOut = Math.Clamp(1.0 - (_hoverPos / 0.22), 0.0, 1.0);
            double textAlpha = EaseOutCubic(spawnTextAlpha) * (hoverFadeOut * hoverFadeOut * (3.0 - 2.0 * hoverFadeOut));

            if (textAlpha > 0.005 && timeColors != null && timeW > 0 && timeH > 0)
            {
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - timeW * 0.5);
                int startY = TopPadding;

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

                        uint src = timeColors[srcRow + tx];
                        byte srcA = (byte)(src >> 24);
                        if (srcA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        // SDF Physical Containment Check: ensure clock, disc, and equalizer pixels lie strictly inside the glass pill
                        double ppx = dstX - geom.CenterX;
                        double ppy = dstY - geom.CenterY;
                        double pqx = Math.Abs(ppx) - straightW;
                        double pqy = Math.Abs(ppy) - straightH;
                        double pOutX = Math.Max(0.0, pqx);
                        double pOutY = Math.Max(0.0, pqy);
                        double pOutDist = Math.Sqrt(pOutX * pOutX + pOutY * pOutY);
                        double pInDist = Math.Min(0.0, Math.Max(pqx, pqy));
                        double pSdf = pOutDist + pInDist - geom.Radius;

                        if (pSdf > -0.5) continue;

                        byte srcR = (byte)(src >> 16);
                        byte srcG = (byte)(src >> 8);
                        byte srcB = (byte)src;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        double alphaNorm = (srcA / 255.0) * textAlpha;
                        double invAlpha = 1.0 - alphaNorm;

                        uint pR = (uint)Math.Clamp(Math.Round(srcR * textAlpha + bgR * invAlpha), 0, 255);
                        uint pG = (uint)Math.Clamp(Math.Round(srcG * textAlpha + bgG * invAlpha), 0, 255);
                        uint pB = (uint)Math.Clamp(Math.Round(srcB * textAlpha + bgB * invAlpha), 0, 255);

                        pDst[dstIdx] = ((uint)bgA << 24) | (pR << 16) | (pG << 8) | pB;
                    }
                }
            }

            // ========================================================
            // EXPANDED MODAL FULL 32-BIT ARGB COLOR COMPOSITING
            // (Premium Hermite Fade + Geometric SDF Containment Clipping)
            // ========================================================
            uint[]? expColors;
            int expW, expH;
            lock (_expandedLock)
            {
                expColors = _expandedColors;
                expW = _expandedWidth;
                expH = _expandedHeight;
            }

            // (expAlpha already precomputed before the glass rendering loop)

            if (expAlpha > 0.005 && expColors != null && expW > 0 && expH > 0)
            {
                int startX = (int)Math.Round((SurfaceWidth * 0.5) - expW * 0.5);
                int startY = TopPadding + 8;

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

                        uint src = expColors[srcRow + tx];
                        byte srcA = (byte)(src >> 24);
                        if (srcA == 0) continue;

                        int dstIdx = dstRow + dstX;
                        uint bg = pDst[dstIdx];
                        byte bgA = (byte)(bg >> 24);
                        if (bgA == 0) continue;

                        // SDF Physical Containment Check: ensure pixel lies strictly inside the pill body
                        double ppx = dstX - geom.CenterX;
                        double ppy = dstY - geom.CenterY;
                        double pqx = Math.Abs(ppx) - straightW;
                        double pqy = Math.Abs(ppy) - straightH;
                        double pOutX = Math.Max(0.0, pqx);
                        double pOutY = Math.Max(0.0, pqy);
                        double pOutDist = Math.Sqrt(pOutX * pOutX + pOutY * pOutY);
                        double pInDist = Math.Min(0.0, Math.Max(pqx, pqy));
                        double pSdf = pOutDist + pInDist - geom.Radius;

                        if (pSdf > -2.0) continue;
                        double pEdgeMask = Math.Clamp((-pSdf - 2.0) / 4.0, 0.0, 1.0);
                        double finalAlpha = expAlpha * pEdgeMask;
                        if (finalAlpha < 0.005) continue;

                        byte srcR = (byte)(src >> 16);
                        byte srcG = (byte)(src >> 8);
                        byte srcB = (byte)src;

                        byte bgR = (byte)(bg >> 16);
                        byte bgG = (byte)(bg >> 8);
                        byte bgB = (byte)bg;

                        double alphaNorm = (srcA / 255.0) * finalAlpha;
                        double invAlpha = 1.0 - alphaNorm;

                        uint pR = (uint)Math.Clamp(Math.Round(srcR * finalAlpha + bgR * invAlpha), 0, 255);
                        uint pG = (uint)Math.Clamp(Math.Round(srcG * finalAlpha + bgG * invAlpha), 0, 255);
                        uint pB = (uint)Math.Clamp(Math.Round(srcB * finalAlpha + bgB * invAlpha), 0, 255);

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

            CheckScreenshotTrigger();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running = false;
            _renderSignal.Set();
            _renderThread?.Join(500);

            _audioMeter.Dispose();
            _screenCapturer?.Dispose();
            _renderSurface?.Dispose();
            _renderSignal.Dispose();
        }
        base.Dispose(disposing);
    }
}
