using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Media.Control;
using Windows.Media.Devices;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace LiquidGlassCircle;

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public bool IsDefault { get; set; }
}

internal static class AudioDeviceManager
{
    private static readonly object _lock = new();
    private static List<AudioDeviceInfo> _cachedDevices = new();
    private static DateTime _lastFetchTime = DateTime.MinValue;
    private static bool _isRefreshing = false;

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, int role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }

    public static List<AudioDeviceInfo> GetDevices()
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastFetchTime).TotalSeconds > 2.5 && !_isRefreshing)
            {
                _isRefreshing = true;
                Task.Run(RefreshDevicesAsync);
            }
            return new List<AudioDeviceInfo>(_cachedDevices);
        }
    }

    public static async Task RefreshDevicesAsync()
    {
        try
        {
            string defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            var selector = MediaDevice.GetAudioRenderSelector();
            var collection = await DeviceInformation.FindAllAsync(selector);

            var list = new List<AudioDeviceInfo>();
            foreach (var dev in collection)
            {
                if (!dev.IsEnabled) continue;

                string fullId = dev.Id;
                string endpointId = ExtractEndpointId(fullId);
                bool isDef = string.Equals(fullId, defaultId, StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrEmpty(endpointId) && defaultId.Contains(endpointId, StringComparison.OrdinalIgnoreCase));

                string name = dev.Name;
                string shortName = SimplifyDeviceName(name);

                list.Add(new AudioDeviceInfo
                {
                    Id = fullId,
                    EndpointId = endpointId,
                    Name = name,
                    ShortName = shortName,
                    IsDefault = isDef
                });
            }

            lock (_lock)
            {
                _cachedDevices = list;
                _lastFetchTime = DateTime.UtcNow;
                _isRefreshing = false;
            }
        }
        catch
        {
            lock (_lock) { _isRefreshing = false; }
        }
    }

    public static string ExtractEndpointId(string winRtId)
    {
        if (string.IsNullOrEmpty(winRtId)) return "";
        int idx = winRtId.IndexOf("{0.0.0.", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int endIdx = winRtId.IndexOf('#', idx);
            return endIdx >= 0 ? winRtId.Substring(idx, endIdx - idx) : winRtId.Substring(idx);
        }
        return winRtId;
    }

    private static string SimplifyDeviceName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Speaker";
        int p = name.IndexOf('(');
        if (p > 0)
        {
            name = name.Substring(0, p).Trim();
        }
        if (name.Length > 12)
        {
            name = name.Substring(0, 10).TrimEnd() + "…";
        }
        return name;
    }

    public static bool SetDefaultDevice(string endpointOrWinRtId)
    {
        try
        {
            string endpointId = ExtractEndpointId(endpointOrWinRtId);
            if (string.IsNullOrEmpty(endpointId)) return false;

            var policy = (IPolicyConfig)new PolicyConfigClient();
            int hr0 = policy.SetDefaultEndpoint(endpointId, 0); // eConsole
            int hr1 = policy.SetDefaultEndpoint(endpointId, 1); // eMultimedia
            int hr2 = policy.SetDefaultEndpoint(endpointId, 2); // eCommunications

            lock (_lock)
            {
                foreach (var d in _cachedDevices)
                {
                    d.IsDefault = (string.Equals(d.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(d.Id, endpointOrWinRtId, StringComparison.OrdinalIgnoreCase));
                }
            }
            return hr0 == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetDefaultDevice error: {ex.Message}");
            return false;
        }
    }
}

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
    private const int CsDblClks = 0x0008;

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int WmLButtonDblClk = 0x0203;

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

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
        public readonly double NotchP;

        public PillGeometry(double cx, double cy, double hw, double hh, double r, double notchP = 0.0)
        {
            CenterX = cx;
            CenterY = cy;
            HalfWidth = hw;
            HalfHeight = hh;
            Radius = r;
            NotchP = notchP;
        }
    }

    internal sealed class TrackInfo
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
        private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();
        private int _selectedSessionIndex = -1;

        public event Action? MediaUpdated;

        public bool HasActiveSession => _currentSession != null;
        public int SessionCount => _sessions.Count;
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
                var sessions = _manager?.GetSessions()?.ToList() ?? new List<GlobalSystemMediaTransportControlsSession>();
                _sessions.Clear();
                _sessions.AddRange(sessions);

                var systemSession = _manager?.GetCurrentSession();
                GlobalSystemMediaTransportControlsSession? session = null;
                if (_currentSession != null)
                {
                    session = _sessions.FirstOrDefault(candidate => candidate == _currentSession);
                }
                session ??= systemSession;
                _selectedSessionIndex = session == null ? -1 : _sessions.FindIndex(candidate => candidate == session);
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

        public async Task<bool> SelectNextSessionAsync()
        {
            try
            {
                var sessions = _manager?.GetSessions()?.ToList() ?? new List<GlobalSystemMediaTransportControlsSession>();
                if (sessions.Count < 2) return false;

                _sessions.Clear();
                _sessions.AddRange(sessions);
                int currentIndex = _sessions.FindIndex(candidate => candidate == _currentSession);
                if (currentIndex < 0) currentIndex = _selectedSessionIndex;
                _selectedSessionIndex = (currentIndex + 1 + _sessions.Count) % _sessions.Count;
                _currentSession = _sessions[_selectedSessionIndex];
                RefreshPlaybackInfo();
                await RefreshMediaPropertiesAsync();
                RefreshTimeline();
                MediaUpdated?.Invoke();
                return true;
            }
            catch { return false; }
        }

        public async Task<bool> ResumeSpotifyAsync(TimeSpan waitTimeout)
        {
            try
            {
                if (_manager == null)
                {
                    await InitializeAsync();
                }

                DateTime deadline = DateTime.UtcNow + waitTimeout;
                do
                {
                    var sessions = _manager?.GetSessions()?.ToList() ?? new List<GlobalSystemMediaTransportControlsSession>();
                    var spotifySession = sessions.FirstOrDefault(IsSpotifySession);
                    if (spotifySession != null)
                    {
                        SelectSession(spotifySession, sessions);
                        var playback = spotifySession.GetPlaybackInfo();
                        if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            return true;
                        }

                        if (await spotifySession.TryPlayAsync())
                        {
                            RefreshPlaybackInfo();
                            return true;
                        }
                    }

                    if (DateTime.UtcNow >= deadline) break;
                    await Task.Delay(200);
                }
                while (true);
            }
            catch { }

            return false;
        }

        private static bool IsSpotifySession(GlobalSystemMediaTransportControlsSession session) =>
            session.SourceAppUserModelId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true;

        private void SelectSession(
            GlobalSystemMediaTransportControlsSession session,
            List<GlobalSystemMediaTransportControlsSession> sessions)
        {
            bool sessionChanged = _currentSession != session;
            _sessions.Clear();
            _sessions.AddRange(sessions);
            _selectedSessionIndex = _sessions.FindIndex(candidate => candidate == session);
            _currentSession = session;

            if (sessionChanged)
            {
                session.MediaPropertiesChanged += (s, e) => RefreshMediaProperties();
                session.PlaybackInfoChanged += (s, e) => RefreshPlaybackInfo();
                session.TimelinePropertiesChanged += (s, e) => RefreshTimeline();
            }

            RefreshPlaybackInfo();
            RefreshMediaProperties();
            RefreshTimeline();
        }

        private async Task RefreshMediaPropertiesAsync()
        {
            RefreshMediaProperties();
            await Task.CompletedTask;
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
    private const int BtnAudioDevice = 1;
    private const int BtnPrev = 2;
    private const int BtnPlayPause = 3;
    private const int BtnNext = 4;
    private const int BtnSleepTimer = 5;
    private const int BtnMediaSessionNext = 6;

    // Expanded Audio Picker IDs
    private const int AudioBtnChip0 = 10;
    private const int AudioBtnChip1 = 11;
    private const int AudioBtnChip2 = 12;
    private const int AudioBtnChip3 = 13;
    private const int AudioBtnClose = 14;

    // Expanded Music Sleep Timer IDs
    private const int MusicSleepBtn15m = 20;
    private const int MusicSleepBtn30m = 21;
    private const int MusicSleepBtn45m = 22;
    private const int MusicSleepBtn60m = 23;
    private const int MusicSleepBtnCancel = 24;
    private const int MusicSleepBtnClose = 25;
    private const int MusicSleepBtnAdd5m = 26;

    private int _activeTab = TabHome;
    private int _prevTab = TabHome;
    private double _tabIndicatorPos = TabHome;
    private double _tabIndicatorVel = 0.0;
    private double _tabTransitionP = 1.0;
    private static readonly uint[] _prevContentSnapshot = new uint[460 * 150];
    private static readonly uint[] _currContentSnapshot = new uint[460 * 150];
    private static readonly uint[]?[] _tabBufferCache = new uint[4][];
    private static readonly uint[] _transitionBuffer = new uint[460 * 150];
    private static uint[]? _expandedBufferA = null;
    private static uint[]? _expandedBufferB = null;
    private static bool _expandedFlip = false;
    private static Bitmap? _reusableSuperBmp = null;
    private static Bitmap? _reusableFastBmp = null;
    private static Bitmap? _tabPrecomputeBmp = null;
    private static Bitmap? _topBarBmp = null;
    private static readonly object _expandedRenderLock = new();

    private readonly TrackInfo _currentTrack = new();
    private bool _isPlaying = false; // Only true when real music is playing!
    private static bool _hasActiveMedia = false; // True when active media track exists (playing or paused)
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
    private bool _wasCursorInPill = false;
    private double _notchAttachment = 0.0;

    // Home Tab Sleep Timer Widget State
    private const int HomeSleepBtnNone = 0;
    private const int HomeSleepBtn15m = 1;
    private const int HomeSleepBtn30m = 2;
    private const int HomeSleepBtn45m = 3;
    private const int HomeSleepBtnCancel = 4;
    private const int HomeSleepBtnMoon = 5;

    // Production Timer Configuration:
    public static bool DebugSleepTimerInSeconds = false;
    public static bool DebugChronoTimerInSeconds = false;

    private static bool _sleepTimerActive = false;
    private static DateTime _sleepTimerTargetUtc = DateTime.MinValue;
    private static int _sleepTimerDurationMinutes = 0;
    private static bool _homeSleepPickerOpen = false;
    private static double _homeSleepExpandP = 0.0;
    private static int _hoveredHomeSleepBtn = HomeSleepBtnNone;
    private static bool _hoveredHomeTimer = false;
    private static int _lastHoverCountdownSec = -1;
    private static bool _hoveredHomeWeather = false;
    private static int _hoveredHomePlayerIndex = -1;
    public static int CurrentWeatherCondition = 1;
    public const int MaxWeatherConditions = 15;
    public static int WeatherCardStyle { get => CurrentWeatherCondition; set => CurrentWeatherCondition = value; }
    public const int MaxWeatherCardStyles = MaxWeatherConditions;
    public static bool IsLiveWeatherMode = true;

    // Music Tab Audio Picker & Sleep Timer States
    private static bool _audioPickerOpen = false;
    private static double _audioPickerExpandP = 0.0;
    private static int _hoveredAudioBtn = BtnNone;

    private static bool _musicSleepPickerOpen = false;
    private static double _musicSleepExpandP = 0.0;
    private static int _hoveredMusicSleepBtn = BtnNone;
    private static int _lastMusicHoverCountdownSec = -1;

    // Chrono Tab Timer State & Physics
    private const int ChronoBtnNone = 0;
    private const int ChronoBtnIcon = 1;
    private const int ChronoBtn5m = 2;
    private const int ChronoBtn10m = 3;
    private const int ChronoBtn15m = 4;
    private const int ChronoBtn30m = 5;
    private const int ChronoBtnPlus = 6;
    private const int ChronoBtnCancel = 7;
    private const int ChronoBtnRunning = 8;

    private const double ChronoActiveWidth = 76.0;
    private const float ChronoActiveHeight = 44.0f;

    private static bool _chronoTimerRunning = false;
    private static DateTime _chronoTimerTargetUtc = DateTime.MinValue;
    private static int _chronoTimerDurationMinutes = 0;
    private static int _chronoTimerTotalSeconds = 0;
    private static double _chronoTimerAnimWidth = 44.0;
    private static bool _chronoTimerHovered = false;
    private static bool _chronoRunningHovered = false;
    private static double _chronoRunningHoverP = 0.0;
    private static double _chronoMorphTimer = 0.0;
    private static float _chronoMorphFromW = 44.0f;
    private static int _hoveredChronoBtn = ChronoBtnNone;
    private static int _lastChronoRemainingSec = -1;

    // ========================================================
    // MANUAL TIMER STATE (TabChrono)
    // ========================================================
    private const int ManualBtnNone = 0;
    private const int ManualBtnUpH = 1;
    private const int ManualBtnDownH = 2;
    private const int ManualBtnUpM = 3;
    private const int ManualBtnDownM = 4;
    private const int ManualBtnUpS = 5;
    private const int ManualBtnDownS = 6;
    private const int ManualBtnStart = 7;
    private const int ManualBtnCancel = 8;
    private const int ManualBtnIcon = 9;

    private static int _hoveredManualBtn = ManualBtnNone;
    private static bool _manualRunningHovered = false;
    private static double _manualRunningHoverP = 0.0;
    private static bool _manualTimerRunning = false;
    private static DateTime _manualTimerTargetUtc = DateTime.MinValue;
    private static int _manualTimerTotalSeconds = 0;
    private static int _lastManualRemainingSec = -1;
    private static double _manualMorphTimer = 0.0;

    // Time setting components (00:00:00)
    private static int _manualHours = 0;
    private static int _manualMinutes = 0;
    private static int _manualSeconds = 0;

    // Roll animation timers and directions
    private static double _animHourTimer = 0.0;
    private static double _animHourDelta = 0.0;
    private static double _animMinuteTimer = 0.0;
    private static double _animMinuteDelta = 0.0;
    private static double _animSecondTimer = 0.0;
    private static double _animSecondDelta = 0.0;

    // Press-and-hold continuous adjustment state
    private static int _heldManualBtn = ManualBtnNone;
    private static DateTime _manualHoldStartTime = DateTime.MinValue;
    private static DateTime _manualNextRepeatTime = DateTime.MinValue;
    private static bool _manualHoldDidAction = false;

    // ========================================================
    // TIMER ALARM STATE (Ring, Bell Vector, Circle Shaking, Chime)
    // ========================================================
    private static bool _timerAlarmActive = false;
    private static bool _timerAlarmPendingCollapse = false;
    private static DateTime _timerAlarmStartTime = DateTime.MinValue;
    private static double _timerAlarmDuration = 8.0;
    private static bool _timerAlarmDismissed = false;
    private static DateTime _lastChimePlayTime = DateTime.MinValue;
    private static byte[]? _bellChimeWavBytes = null;
    private static int _lastRenderedTimerMinutes = -1;
    private static int _lastHomeTimerRemainingSec = -1;

    private void SwitchTab(int newTab, bool immediate = false)
    {
        if (newTab < TabHome || newTab > TabChrono) return;
        if (_activeTab == newTab && !immediate) return;

        _homeSleepPickerOpen = false;
        _homeSleepExpandP = 0.0;
        _audioPickerOpen = false;
        _audioPickerExpandP = 0.0;
        _hoveredAudioBtn = BtnNone;
        _musicSleepPickerOpen = false;
        _musicSleepExpandP = 0.0;
        _hoveredMusicSleepBtn = BtnNone;
        _tabBufferCache[TabHome] = null;
        _tabBufferCache[TabMusic] = null;
        _tabBufferCache[TabChrono] = null;
        if (newTab == TabMusic)
        {
            Task.Run(AudioDeviceManager.RefreshDevicesAsync);
        }
        if (newTab != TabHome)
        {
            _hoveredHomeTimer = false;
            _hoveredHomeWeather = false;
        }
        if (newTab != TabChrono)
        {
            _chronoTimerHovered = false;
            _chronoRunningHovered = false;
            _hoveredChronoBtn = ChronoBtnNone;
        }

        if (immediate)
        {
            _activeTab = newTab;
            _prevTab = newTab;
            _tabIndicatorPos = newTab;
            _tabIndicatorVel = 0.0;
            _tabTransitionP = 1.0;
            _needExpandedUpdate = true;
            return;
        }

        // Sub-microsecond memory snapshot of current screen for outgoing tab
        lock (_expandedLock)
        {
            if (_expandedColors != null && _expandedColors.Length == 460 * 150)
            {
                Array.Copy(_expandedColors, _prevContentSnapshot, 460 * 150);
            }
            else if (_tabBufferCache[_activeTab] != null)
            {
                Array.Copy(_tabBufferCache[_activeTab]!, _prevContentSnapshot, 460 * 150);
            }
        }

        // Ensure target tab content is pre-rendered in cache
        if (newTab == TabMusic || _tabBufferCache[newTab] == null)
        {
            if (_tabBufferCache[newTab] == null)
            {
                _tabBufferCache[newTab] = new uint[460 * 150];
            }
            var track = _currentTrack;
            var cover = GetCurrentCoverArt(track);
            RenderFullTabBuffer(_tabBufferCache[newTab]!, newTab, track, cover, _trackProgressSeconds, _isPlaying, _isShuffle, _vinylRotationAngle, _eqBarHeights, _hoveredButton, _clickedButton, _clickAnimTimer, _sysMedia.SessionCount);
        }
        Array.Copy(_tabBufferCache[newTab]!, _currContentSnapshot, 460 * 150);

        _prevTab = _activeTab;
        _activeTab = newTab;
        _tabTransitionP = 0.0;
        _needExpandedUpdate = true;
    }

    private void UpdateTabTransitionPhysics(double dt)
    {
        if (_hoverPos < 0.1)
        {
            _tabIndicatorPos = _activeTab;
            _tabIndicatorVel = 0.0;
            _tabTransitionP = 1.0;
            _prevTab = _activeTab;
            return;
        }

        bool animating = false;

        // Content transition progress
        if (_tabTransitionP < 1.0)
        {
            _tabTransitionP += dt * 3.8; // ~0.26s transition
            if (_tabTransitionP >= 1.0)
            {
                _tabTransitionP = 1.0;
                _prevTab = _activeTab;
            }
            animating = true;
        }

        // Sub-stepped spring physics for the tab indicator capsule
        double targetPos = _activeTab;
        const double stiffness = 260.0;
        const double damping = 32.0; // Near critical damping: smooth, zero overshoot
        int subSteps = 4;
        double subDt = dt / subSteps;
        for (int s = 0; s < subSteps; s++)
        {
            double springForce = -stiffness * (_tabIndicatorPos - targetPos);
            double dampingForce = -damping * _tabIndicatorVel;
            double totalAccel = springForce + dampingForce;
            _tabIndicatorVel += totalAccel * subDt;
            _tabIndicatorPos += _tabIndicatorVel * subDt;
        }

        if (Math.Abs(_tabIndicatorPos - targetPos) < 0.0005 && Math.Abs(_tabIndicatorVel) < 0.001)
        {
            _tabIndicatorPos = targetPos;
            _tabIndicatorVel = 0.0;
        }
        else
        {
            animating = true;
        }

        if (animating)
        {
            _needExpandedUpdate = true;
        }
    }

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

    private void UpdateChronoPhysics(double dt, DateTime utcNow)
    {
        bool chronoChanged = false;

        // 1. Morph animation (physical collapse and re-expansion on start or cancel)
        if (_chronoMorphTimer > 0.0)
        {
            _chronoMorphTimer -= dt;
            if (_chronoMorphTimer <= 0.0)
            {
                _chronoMorphTimer = 0.0;
                _chronoTimerAnimWidth = _chronoTimerRunning ? ChronoActiveWidth : 44.0;
            }
            else
            {
                if (_chronoTimerRunning)
                {
                    // Starts timer: total morph time 0.36s
                    double p = 1.0 - (_chronoMorphTimer / 0.36);
                    if (p < 0.40)
                    {
                        // Phase 1: quadratic collapse down to 44px
                        double t1 = p / 0.40;
                        _chronoTimerAnimWidth = _chronoMorphFromW + (44.0 - _chronoMorphFromW) * (t1 * t1);
                    }
                    else
                    {
                        // Phase 2: cubic ease-out expand up to ChronoActiveWidth
                        double t2 = (p - 0.40) / 0.60;
                        double easeOut = 1.0 - Math.Pow(1.0 - t2, 3.0);
                        _chronoTimerAnimWidth = 44.0 + (ChronoActiveWidth - 44.0) * easeOut;
                    }
                }
                else
                {
                    // Cancels timer: collapse down to 44px (0.25s)
                    double p = 1.0 - (_chronoMorphTimer / 0.25);
                    double easeOut = 1.0 - Math.Pow(1.0 - p, 3.0);
                    _chronoTimerAnimWidth = _chronoMorphFromW + (44.0 - _chronoMorphFromW) * easeOut;
                }
            }
            chronoChanged = true;
        }
        else
        {
            // 2. Idle hover expansion / collapse spring
            if (!_chronoTimerRunning)
            {
                double targetW = _chronoTimerHovered ? 372.0 : 44.0;
                double diff = targetW - _chronoTimerAnimWidth;
                if (Math.Abs(diff) > 0.1)
                {
                    _chronoTimerAnimWidth += diff * Math.Min(1.0, 18.0 * dt);
                    if (Math.Abs(targetW - _chronoTimerAnimWidth) < 0.1)
                    {
                        _chronoTimerAnimWidth = targetW;
                    }
                    chronoChanged = true;
                }
            }
            else
            {
                if (Math.Abs(_chronoTimerAnimWidth - ChronoActiveWidth) > 0.1)
                {
                    _chronoTimerAnimWidth = ChronoActiveWidth;
                    chronoChanged = true;
                }
            }
        }

        // 3. Running hover cross-fade to reveal "✕ Cancel"
        if (_chronoTimerRunning)
        {
            double targetHover = _chronoRunningHovered ? 1.0 : 0.0;
            double hDiff = targetHover - _chronoRunningHoverP;
            if (Math.Abs(hDiff) > 0.005)
            {
                _chronoRunningHoverP += hDiff * Math.Min(1.0, 18.0 * dt);
                if (Math.Abs(targetHover - _chronoRunningHoverP) < 0.005)
                {
                    _chronoRunningHoverP = targetHover;
                }
                chronoChanged = true;
            }
        }
        else if (_chronoRunningHoverP > 0.0)
        {
            _chronoRunningHoverP = 0.0;
            chronoChanged = true;
        }

        // 4. Live countdown ticker & expiration check
        if (_chronoTimerRunning && _chronoTimerTargetUtc != DateTime.MinValue)
        {
            if (utcNow >= _chronoTimerTargetUtc)
            {
                // Timer reached 00:00
                _chronoTimerRunning = false;
                _chronoTimerTargetUtc = DateTime.MinValue;
                _chronoTimerDurationMinutes = 0;
                _chronoMorphTimer = 0.25;
                _chronoMorphFromW = (float)_chronoTimerAnimWidth;
                _lastChronoRemainingSec = -1;
                _tabBufferCache[TabHome] = null;
                _lastHomeTimerRemainingSec = -1;
                chronoChanged = true;
                TriggerTimerAlarm();
            }
            else
            {
                int remainingSec = (int)Math.Ceiling((_chronoTimerTargetUtc - utcNow).TotalSeconds);
                if (remainingSec != _lastChronoRemainingSec)
                {
                    _lastChronoRemainingSec = remainingSec;
                    chronoChanged = true;
                }
            }
        }
        else if (_lastChronoRemainingSec != -1)
        {
            _lastChronoRemainingSec = -1;
        }

        // 5. Manual Timer height morph
        if (_manualMorphTimer > 0.0)
        {
            _manualMorphTimer -= dt;
            if (_manualMorphTimer <= 0.0)
            {
                _manualMorphTimer = 0.0;
            }
            chronoChanged = true;
        }

        // 6. Manual Timer number roll animations
        if (_animHourTimer > 0.0)
        {
            _animHourTimer -= dt;
            if (_animHourTimer <= 0.0) _animHourTimer = 0.0;
            chronoChanged = true;
        }
        if (_animMinuteTimer > 0.0)
        {
            _animMinuteTimer -= dt;
            if (_animMinuteTimer <= 0.0) _animMinuteTimer = 0.0;
            chronoChanged = true;
        }
        if (_animSecondTimer > 0.0)
        {
            _animSecondTimer -= dt;
            if (_animSecondTimer <= 0.0) _animSecondTimer = 0.0;
            chronoChanged = true;
        }

        // 7. Manual Timer running hover cross-fade
        if (_manualTimerRunning)
        {
            double targetHover = _manualRunningHovered ? 1.0 : 0.0;
            double hDiff = targetHover - _manualRunningHoverP;
            if (Math.Abs(hDiff) > 0.005)
            {
                _manualRunningHoverP += hDiff * Math.Min(1.0, 18.0 * dt);
                if (Math.Abs(targetHover - _manualRunningHoverP) < 0.005)
                {
                    _manualRunningHoverP = targetHover;
                }
                chronoChanged = true;
            }
        }
        else if (_manualRunningHoverP > 0.0)
        {
            _manualRunningHoverP = 0.0;
            chronoChanged = true;
        }

        // 8. Manual Timer live countdown ticker & expiration check
        if (_manualTimerRunning && _manualTimerTargetUtc != DateTime.MinValue)
        {
            if (utcNow >= _manualTimerTargetUtc)
            {
                _manualTimerRunning = false;
                _manualTimerTargetUtc = DateTime.MinValue;
                _manualMorphTimer = 0.25;
                _lastManualRemainingSec = -1;
                _tabBufferCache[TabHome] = null;
                _lastHomeTimerRemainingSec = -1;
                chronoChanged = true;
                TriggerTimerAlarm();
            }
            else
            {
                int remainingSec = (int)Math.Ceiling((_manualTimerTargetUtc - utcNow).TotalSeconds);
                if (remainingSec != _lastManualRemainingSec)
                {
                    _lastManualRemainingSec = remainingSec;
                    chronoChanged = true;
                }
            }
        }
        else if (_lastManualRemainingSec != -1)
        {
            _lastManualRemainingSec = -1;
            chronoChanged = true;
        }

        // 9. Timer Alarm pending collapse or active ringing
        if (_timerAlarmPendingCollapse)
        {
            if (_hoverPos <= 0.03)
            {
                _timerAlarmPendingCollapse = false;
                _userDismissed = false;
                _timerAlarmActive = true;
                _timerAlarmStartTime = utcNow;
                _lastChimePlayTime = utcNow;
                PlayDefaultChime();
                UpdateTimeMaskIfNeeded(force: true);
                _renderSignal.Set();
            }
        }
        else if (_timerAlarmActive && !_timerAlarmDismissed)
        {
            double alarmElapsed = (utcNow - _timerAlarmStartTime).TotalSeconds;
            if (alarmElapsed >= _timerAlarmDuration)
            {
                _timerAlarmActive = false;
                _timerAlarmDismissed = true;
                _userDismissed = false;
                _unhoverShowTimeUntil = utcNow.AddMinutes(1);
                _tabBufferCache[TabChrono] = null;
                UpdateTimeMaskIfNeeded(force: true);
                _renderSignal.Set();
            }
            else if ((utcNow - _lastChimePlayTime).TotalSeconds >= 1.8 && alarmElapsed <= _timerAlarmDuration - 1.5)
            {
                _lastChimePlayTime = utcNow;
                PlayDefaultChime();
            }
        }

        if (chronoChanged)
        {
            _tabBufferCache[TabChrono] = null;
            _needExpandedUpdate = true;
        }
    }

    private void TriggerTimerAlarm()
    {
        _timerAlarmDismissed = false;
        _unhoverShowTimeUntil = DateTime.UtcNow.AddMinutes(1);

        if (_hoverPos > 0.05)
        {
            // Island is open: smoothly animate collapse down first before ringing
            _timerAlarmPendingCollapse = true;
            _timerAlarmActive = false;
            _hoverVel = Math.Min(_hoverVel, -14.0);
            _userDismissed = false;
            _cursorWasInsideNotch = true;
        }
        else
        {
            // Already in collapsed state: directly activate ringing bell and bubble shake
            _timerAlarmPendingCollapse = false;
            _timerAlarmActive = true;
            _timerAlarmStartTime = DateTime.UtcNow;
            _lastChimePlayTime = DateTime.UtcNow;
            _userDismissed = false;
            PlayDefaultChime();
            UpdateTimeMaskIfNeeded(force: true);
        }

        _renderSignal.Set();
    }

    private static void PlayDefaultChime()
    {
        Task.Run(() =>
        {
            try
            {
                _bellChimeWavBytes ??= GenerateBellChimeWav();
                using var ms = new MemoryStream(_bellChimeWavBytes);
                using var player = new System.Media.SoundPlayer(ms);
                player.Play();
            }
            catch
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
        });
    }

    private static byte[] GenerateBellChimeWav()
    {
        const int sampleRate = 44100;
        const double durationSec = 1.8;
        int numSamples = (int)(sampleRate * durationSec);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + numSamples * 2);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // "fmt " chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // subchunk1 size
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // byte rate
        bw.Write((short)2); // block align
        bw.Write((short)16); // bits per sample

        // "data" chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(numSamples * 2);

        // Bell chime harmonics:
        // Fundamental A5 (880 Hz) with natural overtones E6 (1320 Hz) and A6 (1760 Hz)
        const double f0 = 880.0;
        const double f1 = 1320.0;
        const double f2 = 1760.0;
        const double fStrike = 2640.0;

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;

            // Envelopes: fast attack, exponential natural acoustic ring decay
            double env0 = Math.Exp(-t * 2.8);
            double env1 = Math.Exp(-t * 4.2);
            double env2 = Math.Exp(-t * 6.5);
            double strikeEnv = Math.Exp(-t * 35.0); // sharp mallet strike

            double s0 = Math.Sin(2.0 * Math.PI * f0 * t) * env0 * 0.55;
            double s1 = Math.Sin(2.0 * Math.PI * f1 * t) * env1 * 0.28;
            double s2 = Math.Sin(2.0 * Math.PI * f2 * t) * env2 * 0.12;
            double sStrike = Math.Sin(2.0 * Math.PI * fStrike * t) * strikeEnv * 0.20;

            double sampleVal = s0 + s1 + s2 + sStrike;
            sampleVal = Math.Clamp(sampleVal, -1.0, 1.0);

            short pcmSample = (short)(sampleVal * 28000.0);
            bw.Write(pcmSample);
        }

        bw.Flush();
        return ms.ToArray();
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
    private volatile bool _userDismissed = false;
    private bool _spawnedFromBodyTrigger = false;
    private DateTime _bodyTriggerSpawnedAtUtc = DateTime.MinValue;
    private DateTime _bodyTriggerSuppressedUntilUtc = DateTime.MinValue;
    private DateTime _suppressedTopTriggerDwellStartedAtUtc = DateTime.MinValue;
    private Point _suppressedTopTriggerDwellPoint = Point.Empty;
    private volatile bool _cursorWasInsideNotch = false;
    private long _lastLeftClickTime = 0;
    private Point _lastLeftClickPos = Point.Empty;
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
        Task.Run(AudioDeviceManager.RefreshDevicesAsync);
        LiveWeatherService.WeatherUpdated += OnLiveWeatherUpdated;
        LiveWeatherService.Initialize();
        PlayerService.PlayersChanged += () =>
        {
            _tabBufferCache[TabHome] = null;
            _needExpandedUpdate = true;
            _renderSignal.Set();
        };

        Shown += async (_, _) =>
        {
            TimeBeginPeriod(1);
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
            else if (e.KeyCode is Keys.Z or Keys.U)
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
                SwitchTab(TabHome);
            }
            else if (e.KeyCode == Keys.D2)
            {
                SwitchTab(TabMusic);
            }
            else if (e.KeyCode == Keys.D3)
            {
                SwitchTab(TabWeather);
            }
            else if (e.KeyCode == Keys.D4)
            {
                SwitchTab(TabChrono);
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
                // Debug: cycle through all 15 weather conditions (S → 1 → 2 → … → 15 → live)
                CycleWeatherCardStyle();
            }
        };

        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);

        MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (IsPointInManualTimerArrows(e.Location))
                {
                    return;
                }
                CollapseAndDespawnIsland();
            }
        };

        MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_hoverPos > 0.3 && _activeTab == TabChrono && !_manualTimerRunning)
                {
                    if (_hoveredManualBtn is >= ManualBtnUpH and <= ManualBtnDownS)
                    {
                        _heldManualBtn = _hoveredManualBtn;
                        _manualHoldStartTime = DateTime.UtcNow;
                        _manualNextRepeatTime = DateTime.UtcNow.AddMilliseconds(320);
                        ExecuteManualArrowAction(_heldManualBtn);
                        _manualHoldDidAction = true;
                    }
                }
            }
        };

        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _heldManualBtn = ManualBtnNone;
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
                long nowTicks = Environment.TickCount64;
                int dcTime = SystemInformation.DoubleClickTime;
                int dcDistX = SystemInformation.DoubleClickSize.Width;
                int dcDistY = SystemInformation.DoubleClickSize.Height;

                bool isDblClick = (nowTicks - _lastLeftClickTime <= dcTime) &&
                                  Math.Abs(e.X - _lastLeftClickPos.X) <= dcDistX &&
                                  Math.Abs(e.Y - _lastLeftClickPos.Y) <= dcDistY;

                _lastLeftClickTime = nowTicks;
                _lastLeftClickPos = e.Location;

                if (isDblClick)
                {
                    if (!IsPointInManualTimerArrows(e.Location))
                    {
                        CollapseAndDespawnIsland();
                        return;
                    }
                }

                if (_manualHoldDidAction)
                {
                    _manualHoldDidAction = false;
                    return;
                }

                if (_timerAlarmActive && !_timerAlarmDismissed)
                {
                    _timerAlarmActive = false;
                    _timerAlarmDismissed = true;
                    _timerAlarmPendingCollapse = false;
                    _userDismissed = false;
                    _unhoverShowTimeUntil = DateTime.UtcNow.AddMinutes(1);
                    _needExpandedUpdate = true;
                    _tabBufferCache[TabChrono] = null;
                    UpdateTimeMaskIfNeeded(force: true);
                    _renderSignal.Set();
                    return;
                }

                if (_hoverPos > 0.3 && !_userDismissed)
                {
                    await HandleExpandedClickAsync(e.Location);
                }
            }
        };
    }

    private bool IsPointInManualTimerArrows(Point pt)
    {
        if (_hoverPos <= 0.3 || _activeTab != TabChrono || _manualTimerRunning)
            return false;

        float mx = pt.X - 70f;
        float my = pt.Y - 26f;

        float curQuickW = (float)_chronoTimerAnimWidth;
        float curManX = 16f + curQuickW + 12f;
        float curManW = 444f - curManX;
        if (curManW <= 150f) return false;

        float colCenter = curManX + curManW * 0.46f;
        float colSpacing = 38f;
        float colLeft = colCenter - colSpacing - 18f;
        float colRight = colCenter + colSpacing + 18f;

        return (mx >= colLeft && mx <= colRight && my >= 44f && my <= 88f);
    }

    private void ExecuteManualArrowAction(int btn)
    {
        switch (btn)
        {
            case ManualBtnUpH:   AdjustManualTime(1, 0, 0); break;
            case ManualBtnDownH: AdjustManualTime(-1, 0, 0); break;
            case ManualBtnUpM:   AdjustManualTime(0, 1, 0); break;
            case ManualBtnDownM: AdjustManualTime(0, -1, 0); break;
            case ManualBtnUpS:   AdjustManualTime(0, 0, 1); break;
            case ManualBtnDownS: AdjustManualTime(0, 0, -1); break;
        }
    }

    private void CollapseAndDespawnIsland()
    {
        DateTime utcNow = DateTime.UtcNow;
        bool quickBodyTriggerDismissal = _spawnedFromBodyTrigger &&
            _bodyTriggerSpawnedAtUtc != DateTime.MinValue &&
            (utcNow - _bodyTriggerSpawnedAtUtc).TotalSeconds <= 4.0;

        // Double clicking collapses the island and despawns it completely off-screen
        _userDismissed = true;
        _cursorWasInsideNotch = true;
        if (quickBodyTriggerDismissal)
        {
            // Keep the notch available, but temporarily remove the larger body trigger
            // so an accidental activation does not immediately steal the next click.
            _bodyTriggerSuppressedUntilUtc = utcNow.AddSeconds(4.0);
            _suppressedTopTriggerDwellStartedAtUtc = DateTime.MinValue;
            _suppressedTopTriggerDwellPoint = Point.Empty;
        }
        _spawnedFromBodyTrigger = false;
        _unhoverShowTimeUntil = DateTime.MinValue;
        _musicTimeDisplayUntil = DateTime.MinValue;
        _hoverVel = Math.Min(_hoverVel, -8.0);
        _animDirection = -1.0;
        _hoveredButton = BtnNone;
        _clickedButton = BtnNone;
        UpdateExpandedMask();
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

    private void OnLiveWeatherUpdated()
    {
        if (IsLiveWeatherMode)
        {
            CurrentWeatherCondition = LiveWeatherService.Current.BauhausConditionIndex;
            _tabBufferCache[TabHome] = null;
            _tabBufferCache[TabWeather] = null;
            _needExpandedUpdate = true;
            _renderSignal.Set();
        }
    }

    private int HitTestMediaButton(float mx, float my)
    {
        // 1. Audio output device picker expanded state
        if (_audioPickerOpen || _audioPickerExpandP > 0.05)
        {
            float ease = (float)(_audioPickerExpandP * _audioPickerExpandP * (3.0 - 2.0 * _audioPickerExpandP));
            float curX = 83f + 1f * ease;
            float curY = 105f + (38f - 105f) * ease;
            float curW = 30f + (362f - 30f) * ease;
            float curH = 30f + (100f - 30f) * ease;

            if (mx >= curX && mx <= curX + curW && my >= curY && my <= curY + curH)
            {
                // Close button at top right
                if (mx >= 412f && mx <= 440f && my >= 38f && my <= 66f) return AudioBtnClose;

                // Device chips in body
                if (my >= 68f && my <= 134f)
                {
                    var devices = AudioDeviceManager.GetDevices();
                    int count = Math.Min(3, devices.Count > 0 ? devices.Count : 2);
                    float gap = 8f;
                    float cardW = (338f - (count - 1) * gap) / count;
                    for (int i = 0; i < count; i++)
                    {
                        float chX = 96f + i * (cardW + gap);
                        if (mx >= chX && mx <= chX + cardW) return AudioBtnChip0 + i;
                    }
                }

                return BtnAudioDevice;
            }
        }

        // 2. Music sleep timer picker expanded state
        if (_musicSleepPickerOpen || _musicSleepExpandP > 0.05)
        {
            float ease = (float)(_musicSleepExpandP * _musicSleepExpandP * (3.0 - 2.0 * _musicSleepExpandP));
            float curX = 417f + (84f - 417f) * ease;
            float curY = 105f + (38f - 105f) * ease;
            float curW = 30f + (362f - 30f) * ease;
            float curH = 30f + (100f - 30f) * ease;

            if (mx >= curX && mx <= curX + curW && my >= curY && my <= curY + curH)
            {
                // Close button at top right
                if (mx >= 412f && mx <= 440f && my >= 38f && my <= 66f) return MusicSleepBtnClose;

                // Header Stop button if active
                if (_sleepTimerActive && mx >= 334f && mx <= 410f && my >= 38f && my <= 66f) return MusicSleepBtnCancel;

                // Body area
                if (my >= 68f && my <= 134f)
                {
                    if (_sleepTimerActive && _sleepTimerTargetUtc > DateTime.UtcNow)
                    {
                        // Right side controls: +5m at [260, 314], +15m at [320, 374], Stop at [380, 434]
                        if (mx >= 260f && mx <= 314f) return MusicSleepBtnAdd5m;
                        if (mx >= 320f && mx <= 374f) return MusicSleepBtn30m; // Adds 15m
                        if (mx >= 380f && mx <= 434f) return MusicSleepBtnCancel;
                    }
                    else
                    {
                        // 4 Presets: 15, 30, 45, 60
                        float gap = 8f;
                        float cardW = (338f - 3 * gap) / 4f;
                        for (int j = 0; j < 4; j++)
                        {
                            float chX = 96f + j * (cardW + gap);
                            if (mx >= chX && mx <= chX + cardW) return MusicSleepBtn15m + j;
                        }
                    }
                }

                return BtnSleepTimer;
            }
        }

        // 3. Resting / collapsed buttons
        float cy = 120f;
        if (_sysMedia.SessionCount > 1 && mx >= 418f && mx <= 446f && my >= 40f && my <= 68f) return BtnMediaSessionNext;
        if (my >= 100f && my <= 140f)
        {
            if (Math.Abs(mx - 98f) <= 15f && Math.Abs(my - cy) <= 15f) return BtnAudioDevice;
            if (Math.Abs(mx - 432f) <= 15f && Math.Abs(my - cy) <= 15f) return BtnSleepTimer;

            // Transport controls (only active if neither picker is expanded)
            if (_audioPickerExpandP < 0.1 && _musicSleepExpandP < 0.1)
            {
                if (Math.Abs(mx - 217f) <= 17f && Math.Abs(my - cy) <= 17f) return BtnPrev;
                if (Math.Abs(mx - 265f) <= 21f && Math.Abs(my - cy) <= 21f) return BtnPlayPause;
                if (Math.Abs(mx - 313f) <= 17f && Math.Abs(my - cy) <= 17f) return BtnNext;
            }
        }

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
                SwitchTab(TabHome);
                return true;
            }
            if (pt.X >= 108 && pt.X < 130)
            {
                SwitchTab(TabMusic);
                return true;
            }
            if (pt.X >= 130 && pt.X < 152)
            {
                SwitchTab(TabWeather);
                return true;
            }
            if (pt.X >= 152 && pt.X <= 176)
            {
                SwitchTab(TabChrono);
                return true;
            }
        }

        if (_activeTab == TabHome)
        {
            float mx = pt.X - 70f;
            float my = pt.Y - 26f;

            if (IsAnyTimerLive() && IsPointInHomeTimer(mx, my))
            {
                CancelAllActiveTimers();
                return true;
            }

            bool isSplit = _isPlaying || _hasActiveMedia || _sleepTimerActive || _homeSleepPickerOpen || (_homeSleepExpandP > 0.001);

            // Card 1: Media Widget & Sleep Timer (mx in [216, 444], my in [36, 80])
            if (my >= 36 && my <= 80 && mx >= 216 && mx <= 444)
            {
                if (_homeSleepExpandP > 0.4)
                {
                    // Joined Sleep Timer Template Buttons: [15m], [30m], [45m], [Cancel]
                    if (mx >= 224 && mx < 274)
                    {
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(15) : DateTime.UtcNow.AddMinutes(15);
                        _sleepTimerDurationMinutes = 15;
                        _sleepTimerActive = true;
                        _homeSleepPickerOpen = false;
                        _tabBufferCache[TabHome] = null;
                        _needExpandedUpdate = true;
                        return true;
                    }
                    else if (mx >= 274 && mx < 323)
                    {
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(30) : DateTime.UtcNow.AddMinutes(30);
                        _sleepTimerDurationMinutes = 30;
                        _sleepTimerActive = true;
                        _homeSleepPickerOpen = false;
                        _tabBufferCache[TabHome] = null;
                        _needExpandedUpdate = true;
                        return true;
                    }
                    else if (mx >= 323 && mx < 372)
                    {
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(45) : DateTime.UtcNow.AddMinutes(45);
                        _sleepTimerDurationMinutes = 45;
                        _sleepTimerActive = true;
                        _homeSleepPickerOpen = false;
                        _tabBufferCache[TabHome] = null;
                        _needExpandedUpdate = true;
                        return true;
                    }
                    else if (mx >= 372 && mx <= 438)
                    {
                        _sleepTimerActive = false;
                        _sleepTimerTargetUtc = DateTime.MinValue;
                        _sleepTimerDurationMinutes = 0;
                        _homeSleepPickerOpen = false;
                        _tabBufferCache[TabHome] = null;
                        _needExpandedUpdate = true;
                        return true;
                    }
                }
                else if (isSplit)
                {
                    // Split Piece 2: Smaller square-type sleep timer end (moon with stars)
                    if (mx >= 398 && mx <= 444)
                    {
                        _homeSleepPickerOpen = true;
                        _tabBufferCache[TabHome] = null;
                        _needExpandedUpdate = true;
                        return true;
                    }
                    else if (mx >= 216 && mx < 398)
                    {
                        // Split Piece 1: Bigger media end -> opens player
                        SwitchTab(TabMusic);
                        return true;
                    }
                }
                else
                {
                    var players = PlayerService.GetPlayers();
                    int count = Math.Min(3, players.Count);
                    if (count > 0)
                    {
                        float btnSize = 28f;
                        float btnY = 36f + (44f - btnSize) * 0.5f;
                        float gap = 6f;
                        float rightMargin = 10f;
                        float cardX = 216f;
                        float cardW = 228f;
                        float startX = cardX + cardW - rightMargin - (count * btnSize + (count - 1) * gap);
                        if (my >= btnY && my <= btnY + btnSize)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                float bx = startX + i * (btnSize + gap);
                                if (mx >= bx && mx <= bx + btnSize)
                                {
                                    await StartOrResumeHomePlayerAsync(players[i]);
                                    return true;
                                }
                            }
                        }
                    }

                    if (_hoveredHomePlayerIndex >= 0 && _hoveredHomePlayerIndex < players.Count)
                    {
                        await StartOrResumeHomePlayerAsync(players[_hoveredHomePlayerIndex]);
                        return true;
                    }

                    SwitchTab(TabMusic);
                    return true;
                }
            }
            else if (_homeSleepPickerOpen)
            {
                // Clicking outside the sleep picker closes it
                _homeSleepPickerOpen = false;
                _tabBufferCache[TabHome] = null;
                _needExpandedUpdate = true;
            }

            // Card 2: Weather Quick-Glance (mx in [216, 444], my in [87, 132])
            if (mx >= 216 && mx <= 444 && my >= 87 && my <= 132)
            {
                SwitchTab(TabWeather);
                return true;
            }
        }

        if (_activeTab == TabMusic)
        {
            float mx = pt.X - 70f;
            float my = pt.Y - 26f;

            // Handle Audio Picker Expanded Clicks
            if (_audioPickerOpen)
            {
                int btn = HitTestMediaButton(mx, my);
                if (btn == AudioBtnClose)
                {
                    _audioPickerOpen = false;
                    _tabBufferCache[TabMusic] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn >= AudioBtnChip0 && btn <= AudioBtnChip3)
                {
                    int devIdx = btn - AudioBtnChip0;
                    var devices = AudioDeviceManager.GetDevices();
                    if (devIdx < devices.Count)
                    {
                        AudioDeviceManager.SetDefaultDevice(devices[devIdx].Id);
                    }
                    _audioPickerOpen = false;
                    _tabBufferCache[TabMusic] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else
                {
                    float ease = (float)(_audioPickerExpandP * _audioPickerExpandP * (3.0 - 2.0 * _audioPickerExpandP));
                    float curX = 83f + 1f * ease;
                    float curY = 105f + (38f - 105f) * ease;
                    float curW = 30f + (362f - 30f) * ease;
                    float curH = 30f + (100f - 30f) * ease;
                    if (mx < curX || mx > curX + curW || my < curY || my > curY + curH)
                    {
                        _audioPickerOpen = false;
                        _tabBufferCache[TabMusic] = null;
                        _needExpandedUpdate = true;
                    }
                }
            }

            // Handle Music Sleep Picker Expanded Clicks
            if (_musicSleepPickerOpen)
            {
                int btn = HitTestMediaButton(mx, my);
                if (btn == MusicSleepBtnClose)
                {
                    _musicSleepPickerOpen = false;
                    _tabBufferCache[TabMusic] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn == MusicSleepBtnCancel)
                {
                    _sleepTimerActive = false;
                    _sleepTimerTargetUtc = DateTime.MinValue;
                    _sleepTimerDurationMinutes = 0;
                    _musicSleepPickerOpen = false;
                    _tabBufferCache[TabMusic] = null;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn == MusicSleepBtnAdd5m)
                {
                    if (_sleepTimerTargetUtc != DateTime.MinValue)
                    {
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds
                            ? _sleepTimerTargetUtc.AddSeconds(5)
                            : _sleepTimerTargetUtc.AddMinutes(5);
                    }
                    else
                    {
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(5) : DateTime.UtcNow.AddMinutes(5);
                        _sleepTimerActive = true;
                    }
                    _tabBufferCache[TabMusic] = null;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn == MusicSleepBtn15m)
                {
                    _sleepTimerDurationMinutes = 15;
                    _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(15) : DateTime.UtcNow.AddMinutes(15);
                    _sleepTimerActive = true;
                    _tabBufferCache[TabMusic] = null;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn == MusicSleepBtn30m)
                {
                    if (_sleepTimerActive && _sleepTimerTargetUtc > DateTime.UtcNow)
                    {
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? _sleepTimerTargetUtc.AddSeconds(15) : _sleepTimerTargetUtc.AddMinutes(15);
                    }
                    else
                    {
                        _sleepTimerDurationMinutes = 30;
                        _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(30) : DateTime.UtcNow.AddMinutes(30);
                        _sleepTimerActive = true;
                    }
                    _tabBufferCache[TabMusic] = null;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn == MusicSleepBtn45m)
                {
                    _sleepTimerDurationMinutes = 45;
                    _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(45) : DateTime.UtcNow.AddMinutes(45);
                    _sleepTimerActive = true;
                    _tabBufferCache[TabMusic] = null;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else if (btn == MusicSleepBtn60m)
                {
                    _sleepTimerDurationMinutes = 60;
                    _sleepTimerTargetUtc = DebugSleepTimerInSeconds ? DateTime.UtcNow.AddSeconds(60) : DateTime.UtcNow.AddMinutes(60);
                    _sleepTimerActive = true;
                    _tabBufferCache[TabMusic] = null;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                    return true;
                }
                else
                {
                    float ease = (float)(_musicSleepExpandP * _musicSleepExpandP * (3.0 - 2.0 * _musicSleepExpandP));
                    float curX = 417f + (84f - 417f) * ease;
                    float curY = 105f + (38f - 105f) * ease;
                    float curW = 30f + (362f - 30f) * ease;
                    float curH = 30f + (100f - 30f) * ease;
                    if (mx < curX || mx > curX + curW || my < curY || my > curY + curH)
                    {
                        _musicSleepPickerOpen = false;
                        _tabBufferCache[TabMusic] = null;
                        _needExpandedUpdate = true;
                    }
                }
            }

            int standardBtn = HitTestMediaButton(mx, my);
            if (standardBtn != BtnNone)
            {
                _clickedButton = standardBtn;
                _clickAnimTimer = 1.0;
                UpdateExpandedMask();

                switch (standardBtn)
                {
                    case BtnAudioDevice:
                        _audioPickerOpen = !_audioPickerOpen;
                        if (_audioPickerOpen) _musicSleepPickerOpen = false;
                        _tabBufferCache[TabMusic] = null;
                        _needExpandedUpdate = true;
                        return true;

                    case BtnSleepTimer:
                        _musicSleepPickerOpen = !_musicSleepPickerOpen;
                        if (_musicSleepPickerOpen) _audioPickerOpen = false;
                        _tabBufferCache[TabMusic] = null;
                        _needExpandedUpdate = true;
                        return true;

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

                    case BtnMediaSessionNext:
                        if (_sysMedia.SessionCount > 1)
                        {
                            await _sysMedia.SelectNextSessionAsync();
                            _tabBufferCache[TabMusic] = null;
                            _needExpandedUpdate = true;
                        }
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

            float slot1X = 16f;
            float slot1Y = 44f;
            float slot1H = 44f;
            float curW = (float)_chronoTimerAnimWidth;

            if (_chronoTimerRunning)
            {
                // Clicking the running timer / cancel button cancels the timer
                if (my >= slot1Y && my <= slot1Y + slot1H && mx >= slot1X && mx <= slot1X + curW)
                {
                    CancelChronoTimer();
                    return true;
                }
            }
            else
            {
                // While expanded, clicking any duration option starts the timer
                if (my >= slot1Y && my <= slot1Y + slot1H && curW > 120f)
                {
                    float b1X = slot1X + 86f;
                    float btnW = 44f;
                    float plusW = 30f;
                    float gap = 6f;

                    float b2X = b1X + btnW + gap;
                    float b3X = b2X + btnW + gap;
                    float b4X = b3X + btnW + gap;
                    float b5X = b4X + btnW + gap;

                    if (mx >= b1X && mx < b1X + btnW)
                    {
                        StartChronoTimer(5);
                        return true;
                    }
                    else if (mx >= b2X && mx < b2X + btnW)
                    {
                        StartChronoTimer(10);
                        return true;
                    }
                    else if (mx >= b3X && mx < b3X + btnW)
                    {
                        StartChronoTimer(15);
                        return true;
                    }
                    else if (mx >= b4X && mx < b4X + btnW)
                    {
                        StartChronoTimer(30);
                        return true;
                    }
                    else if (mx >= b5X && mx <= b5X + plusW)
                    {
                        StartChronoTimer(45);
                        return true;
                    }
                }
            }

            // ----------------------------------------------------
            // Manual Timer Clicks
            // ----------------------------------------------------
            float curManX = slot1X + curW + 12f;
            float curManW = 444f - curManX;
            float curManY = 44f;
            float curManH = 44f;

            if (_manualTimerRunning)
            {
                // Clicking running manual capsule cancels it
                if (my >= curManY && my <= curManY + curManH && mx >= curManX && mx <= curManX + curManW)
                {
                    CancelManualTimer();
                    return true;
                }
            }
            else
            {
                if (curManW <= 60f)
                {
                    if (my >= curManY && my <= curManY + curManH && mx >= curManX && mx <= curManX + curManW)
                    {
                        // Clicked the shrunk manual timer icon: restore manual timer setting
                        _chronoTimerHovered = false;
                        _tabBufferCache[TabChrono] = null;
                        _needExpandedUpdate = true;
                        _renderSignal.Set();
                        return true;
                    }
                }
                else if (curManW > 150f && my >= curManY && my <= curManY + curManH && mx >= curManX && mx <= curManX + curManW)
                {
                    float colCenter = curManX + curManW * 0.46f;
                    float colSpacing = 38f;
                    float colHX = colCenter - colSpacing;
                    float colMX = colCenter;
                    float colSX = colCenter + colSpacing;

                    float btnW = 72f;
                    float btnH = 26f;
                    float btnX = curManX + curManW - btnW - 12f;
                    float btnY = curManY + (curManH - btnH) * 0.5f;

                    if (my >= curManY && my <= curManY + 18f)
                    {
                        // Up arrows (▲)
                        if (mx >= colHX - 16f && mx <= colHX + 16f)
                        {
                            AdjustManualTime(1, 0, 0);
                            return true;
                        }
                        else if (mx >= colMX - 16f && mx <= colMX + 16f)
                        {
                            AdjustManualTime(0, 1, 0);
                            return true;
                        }
                        else if (mx >= colSX - 16f && mx <= colSX + 16f)
                        {
                            AdjustManualTime(0, 0, 1);
                            return true;
                        }
                    }
                    else if (my >= curManY + 26f && my <= curManY + curManH)
                    {
                        // Down arrows (▼)
                        if (mx >= colHX - 16f && mx <= colHX + 16f)
                        {
                            AdjustManualTime(-1, 0, 0);
                            return true;
                        }
                        else if (mx >= colMX - 16f && mx <= colMX + 16f)
                        {
                            AdjustManualTime(0, -1, 0);
                            return true;
                        }
                        else if (mx >= colSX - 16f && mx <= colSX + 16f)
                        {
                            AdjustManualTime(0, 0, -1);
                            return true;
                        }
                    }

                    if (mx >= btnX && mx <= btnX + btnW && my >= btnY && my <= btnY + btnH)
                    {
                        // ▶ START button clicked
                        StartManualTimerFromInput();
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private async Task StartOrResumeHomePlayerAsync(PlayerItem player)
    {
        if (!PlayerService.IsSpotify(player))
        {
            PlayerService.LaunchPlayer(player);
            return;
        }

        // A live Spotify session can be resumed directly without waking its UI.
        if (await _sysMedia.ResumeSpotifyAsync(TimeSpan.Zero)) return;

        bool spotifyWasRunning = PlayerService.IsSpotifyRunning();
        if (!spotifyWasRunning && !PlayerService.LaunchSpotifyInBackground(player)) return;

        // A fresh Spotify process needs a moment to register its Windows media
        // session. Existing background instances get a shorter grace period.
        TimeSpan sessionWait = spotifyWasRunning ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(12);
        await _sysMedia.ResumeSpotifyAsync(sessionWait);
    }

    private void AdjustManualTime(int dH, int dM, int dS)
    {
        if (dH != 0)
        {
            _manualHours = (_manualHours + dH + 100) % 100;
            _animHourDelta = dH;
            _animHourTimer = 0.22;
        }
        if (dM != 0)
        {
            _manualMinutes = (_manualMinutes + dM + 60) % 60;
            _animMinuteDelta = dM;
            _animMinuteTimer = 0.22;
        }
        if (dS != 0)
        {
            _manualSeconds = (_manualSeconds + dS + 60) % 60;
            _animSecondDelta = dS;
            _animSecondTimer = 0.22;
        }
        _tabBufferCache[TabChrono] = null;
        _tabBufferCache[TabHome] = null;
        _lastHomeTimerRemainingSec = -1;
        _needExpandedUpdate = true;
        _renderSignal.Set();
    }

    private void StartManualTimerFromInput()
    {
        int totalSec = _manualHours * 3600 + _manualMinutes * 60 + _manualSeconds;
        if (totalSec <= 0)
        {
            totalSec = DebugChronoTimerInSeconds ? 10 : 60;
            _manualMinutes = 1;
        }
        StartManualTimer(totalSec);
    }

    private void StartManualTimer(int totalSeconds)
    {
        _manualTimerTotalSeconds = totalSeconds;
        _manualTimerTargetUtc = DateTime.UtcNow.AddSeconds(totalSeconds);
        _manualTimerRunning = true;
        _manualRunningHovered = false;
        _manualRunningHoverP = 0.0;
        _manualMorphTimer = 0.36; // 360ms fluid spring morph
        _lastManualRemainingSec = -1;

        _tabBufferCache[TabChrono] = null;
        _tabBufferCache[TabHome] = null;
        _lastHomeTimerRemainingSec = -1;
        _needExpandedUpdate = true;
        _renderSignal.Set();
    }

    private void CancelManualTimer()
    {
        _manualTimerRunning = false;
        _manualTimerTargetUtc = DateTime.MinValue;
        _manualRunningHovered = false;
        _manualRunningHoverP = 0.0;
        _manualMorphTimer = 0.25;
        _lastManualRemainingSec = -1;

        _tabBufferCache[TabChrono] = null;
        _tabBufferCache[TabHome] = null;
        _lastHomeTimerRemainingSec = -1;
        _needExpandedUpdate = true;
        _renderSignal.Set();
    }

    private void StartChronoTimer(int minutes)
    {
        _chronoTimerDurationMinutes = minutes;
        _chronoTimerTargetUtc = DebugChronoTimerInSeconds ? DateTime.UtcNow.AddSeconds(minutes) : DateTime.UtcNow.AddMinutes(minutes);
        _chronoTimerTotalSeconds = DebugChronoTimerInSeconds ? minutes : minutes * 60;
        _chronoTimerRunning = true;
        _chronoTimerHovered = false;
        _chronoRunningHovered = false;
        _chronoRunningHoverP = 0.0;
        _hoveredChronoBtn = ChronoBtnNone;

        // Trigger physical collapse and re-expand animation
        _chronoMorphTimer = 0.36; // 360ms fluid spring morph
        _chronoMorphFromW = (float)_chronoTimerAnimWidth;

        _tabBufferCache[TabChrono] = null;
        _tabBufferCache[TabHome] = null;
        _lastHomeTimerRemainingSec = -1;
        _needExpandedUpdate = true;
        _renderSignal.Set();
    }

    private void CancelChronoTimer()
    {
        _chronoTimerRunning = false;
        _chronoTimerTargetUtc = DateTime.MinValue;
        _chronoTimerDurationMinutes = 0;
        _chronoTimerHovered = false;
        _chronoRunningHovered = false;
        _chronoRunningHoverP = 0.0;
        _hoveredChronoBtn = ChronoBtnNone;
        _lastChronoRemainingSec = -1;

        // Smoothly collapse back to 44px idle icon
        _chronoMorphTimer = 0.25;
        _chronoMorphFromW = (float)_chronoTimerAnimWidth;

        _tabBufferCache[TabChrono] = null;
        _tabBufferCache[TabHome] = null;
        _lastHomeTimerRemainingSec = -1;
        _needExpandedUpdate = true;
        _renderSignal.Set();
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
        lock (_expandedRenderLock)
        {
            var track = _currentTrack;
            var cover = GetCurrentCoverArt(track);
            var (colors, w, h) = PrecomputeExpandedContent(
                _activeTab,
                _prevTab,
                _tabTransitionP,
                _tabIndicatorPos,
                _tabIndicatorVel,
                track,
                cover,
                _trackProgressSeconds,
                _isPlaying,
                _isShuffle,
                _vinylRotationAngle,
                _eqBarHeights,
                _hoveredButton,
                _clickedButton,
                _clickAnimTimer,
                _sysMedia.SessionCount);

            lock (_expandedLock)
            {
                _expandedColors = colors;
                _expandedWidth = w;
                _expandedHeight = h;
            }
        }
    }

    private static readonly PrivateFontCollection _privateFonts = new();
    private static FontFamily? _eternaloFamily = null;
    private static FontFamily? _sfProFamily = null;
    private static bool _customFontsLoaded = false;
    private static readonly object _fontLock = new();

    private static void EnsureCustomFontsLoaded()
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
                    @"C:\Users\abhin\Workspace\liquid glass\Fonts"
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

    private static Font GetPremiumFont(float sizeInPoints, FontStyle style)
    {
        EnsureCustomFontsLoaded();

        lock (_fontLock)
        {
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

            string[] fontCandidates = new[]
            {
                "SF Pro Display",
                "SF Pro Text",
                "SF Pro",
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
    }

    private static Font GetEternaloFont(float sizeInPoints, FontStyle style = FontStyle.Regular)
    {
        EnsureCustomFontsLoaded();

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

            return GetPremiumFont(sizeInPoints, style);
        }
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
        Action drawGlyph,
        float alpha = 1.0f)
    {
        if (alpha <= 0.01f) return;
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
        using (var brushShadow = new SolidBrush(Color.FromArgb((int)(50 * alpha), 0, 0, 0)))
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
                    Color.FromArgb((int)(160 * alpha), 10, 15, 25),
                    Color.FromArgb((int)(195 * alpha), 5, 8, 15),
                    90f);
                g.FillPath(brushBody, bodyPath);
            }
            else
            {
                // Distinct frosted diffusion glass fill (translucent frosted density, completely without outline)
                using var brushBody = new LinearGradientBrush(
                    new RectangleF(x, y, size, size),
                    Color.FromArgb((int)(50 * alpha), 255, 255, 255),
                    Color.FromArgb((int)(18 * alpha), 255, 255, 255),
                    90f);
                g.FillPath(brushBody, bodyPath);
            }
        }

        // 3. Button Glyph
        drawGlyph();

        g.Restore(state);
    }

    private static void DrawPlayPauseGlyph(Graphics g, float cx, float cy, float radius, bool isPlaying, float alpha = 1.0f)
    {
        if (alpha <= 0.01f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brushGlyph = new SolidBrush(Color.FromArgb((int)(255 * alpha), 255, 255, 255));
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

    private static void DrawTrackSkipGlyph(Graphics g, float cx, float cy, float size, bool isNext, float alpha = 1.0f)
    {
        if (alpha <= 0.01f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb((int)(240 * alpha), 255, 255, 255));

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

    private static void DrawAudioOutputGlyph(Graphics g, float cx, float cy, float size, float alpha = 1.0f, bool isDark = false)
    {
        if (alpha <= 0.01f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int a = (int)(235 * alpha);
        Color color = isDark ? Color.FromArgb(a, 25, 28, 35) : Color.FromArgb(a, 255, 255, 255);
        using var pen = new Pen(color, 1.5f * (size / 13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);

        float s = size * 0.5f;
        PointF[] speakerPts = new[]
        {
            new PointF(cx - s * 0.65f, cy - s * 0.35f),
            new PointF(cx - s * 0.20f, cy - s * 0.35f),
            new PointF(cx + s * 0.22f, cy - s * 0.72f),
            new PointF(cx + s * 0.22f, cy + s * 0.72f),
            new PointF(cx - s * 0.20f, cy + s * 0.35f),
            new PointF(cx - s * 0.65f, cy + s * 0.35f)
        };
        g.FillPolygon(brush, speakerPts);

        // Sound waves
        float r1 = s * 0.58f;
        g.DrawArc(pen, cx + s * 0.05f - r1, cy - r1, r1 * 2f, r1 * 2f, -38f, 76f);

        float r2 = s * 0.95f;
        g.DrawArc(pen, cx + s * 0.05f - r2, cy - r2, r2 * 2f, r2 * 2f, -38f, 76f);
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

    private static void DrawTopTabBar(
        Graphics g,
        float superScale,
        int targetW,
        double tabIndicatorPos,
        double tabIndicatorVel)
    {
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

        // Active Tab Sliding Indicator Pill (Fluid Liquid Glass Capsule with Inertia Stretch)
        float stretch = (float)Math.Clamp(Math.Abs(tabIndicatorVel) * 0.8 * superScale, 0.0, 5.0 * superScale);
        float activeX = tabStartX + (float)tabIndicatorPos * tabItemW - stretch * 0.5f;
        using (var pathActive = new GraphicsPath())
        {
            float ax = activeX;
            float ay = tabBarCy - tabHeight * 0.5f;
            float aw = tabItemW + stretch;
            float ah = tabHeight;
            float ar = ah * 0.5f;

            pathActive.AddArc(ax, ay, ar * 2, ar * 2, 180, 90);
            pathActive.AddArc(ax + aw - ar * 2, ay, ar * 2, ar * 2, 270, 90);
            pathActive.AddArc(ax + aw - ar * 2, ay + ah - ar * 2, ar * 2, ar * 2, 0, 90);
            pathActive.AddArc(ax, ay + ah - ar * 2, ar * 2, ar * 2, 90, 90);
            pathActive.CloseFigure();

            // Refined calm liquid glass fill (consistent elegant translucency, no brightness flares)
            using var brushActive = new SolidBrush(Color.FromArgb(42, 255, 255, 255));
            g.FillPath(brushActive, pathActive);

            // Refined subtle glass rim border
            using var penActive = new Pen(Color.FromArgb(90, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penActive, pathActive);
        }

        // 4 Compact Icon-Only Tab Labels (⌂, ♫, ☀, ⏱) with Smooth Proximity Illuminance
        string[] tabIcons = new[] { "⌂", "♫", "☀", "⏱" };
        using var fontIconTab = GetPremiumFont(9.0f * superScale, FontStyle.Bold);
        for (int t = 0; t < 4; t++)
        {
            float tx = tabStartX + t * tabItemW;
            var strSize = g.MeasureString(tabIcons[t], fontIconTab, PointF.Empty, StringFormat.GenericTypographic);
            float labelX = tx + (tabItemW - strSize.Width) * 0.5f;
            float labelY = tabBarCy - strSize.Height * 0.5f;

            float dist = Math.Abs(t - (float)tabIndicatorPos);
            float activeWeight = Math.Clamp(1.0f - dist, 0.0f, 1.0f);
            int iconAlpha = (int)Math.Round(130f + 100f * activeWeight);

            Color tabColor = Color.FromArgb(iconAlpha, 255, 255, 255);
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
    }

    private static void DownsampleTopBarToBuffer(Bitmap topBarBmp, uint[] destBuffer, int targetW, int rows)
    {
        int superW = targetW * 4;
        var data = topBarBmp.LockBits(new Rectangle(0, 0, superW, rows * 4), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* scan = (byte*)data.Scan0;
            for (int y = 0; y < rows; y++)
            {
                int destRow = y * targetW;
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
                        destBuffer[destRow + x] = 0;
                    }
                    else
                    {
                        int avgR = Math.Min(255, sumR >> 4);
                        int avgG = Math.Min(255, sumG >> 4);
                        int avgB = Math.Min(255, sumB >> 4);
                        destBuffer[destRow + x] = ((uint)avgA << 24) | ((uint)avgR << 16) | ((uint)avgG << 8) | (uint)avgB;
                    }
                }
            }
        }
        topBarBmp.UnlockBits(data);
    }

    private static void DownsampleToBuffer(Bitmap superBmp, uint[] destBuffer, int startY, int endY, int targetW, int scale = 4)
    {
        int superW = targetW * scale;
        int shift = scale == 2 ? 2 : 4;
        int stride = superW * 4;
        var data = superBmp.LockBits(new Rectangle(0, startY * scale, superW, (endY - startY) * scale), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* scan = (byte*)data.Scan0;
            int height = endY - startY;
            for (int y = 0; y < height; y++)
            {
                int destY = startY + y;
                int destRow = destY * targetW;
                int syBase = y * scale * stride;
                for (int x = 0; x < targetW; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    int sxBase = x * scale * 4;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        byte* pRow = scan + syBase + dy * stride + sxBase;
                        for (int dx = 0; dx < scale; dx++)
                        {
                            byte b = pRow[0];
                            byte gVal = pRow[1];
                            byte r = pRow[2];
                            byte a = pRow[3];
                            pRow += 4;

                            int trueA = (a > 0) ? a : Math.Max(r, Math.Max(gVal, b));
                            sumB += (b * trueA) >> 8;
                            sumG += (gVal * trueA) >> 8;
                            sumR += (r * trueA) >> 8;
                            sumA += trueA;
                        }
                    }

                    int avgA = sumA >> shift;
                    if (avgA == 0)
                    {
                        destBuffer[destRow + x] = 0;
                    }
                    else
                    {
                        int avgR = Math.Min(255, sumR >> shift);
                        int avgG = Math.Min(255, sumG >> shift);
                        int avgB = Math.Min(255, sumB >> shift);
                        destBuffer[destRow + x] = ((uint)avgA << 24) | ((uint)avgR << 16) | ((uint)avgG << 8) | (uint)avgB;
                    }
                }
            }
        }
        superBmp.UnlockBits(data);
    }

    internal static void RenderFullTabBuffer(
        uint[] destBuffer,
        int tabIndex,
        TrackInfo track,
        Bitmap? coverBmp,
        double progressSeconds,
        bool isPlaying,
        bool isShuffle,
        double rotationAngle,
        float[]? eqBarHeights,
        int hoveredButton,
        int clickedButton,
        double clickAnimProgress,
        int mediaSessionCount)
    {
        lock (_expandedRenderLock)
        {
            const float superScale = 4.0f;
            int targetW = 460;
            int targetH = 150;
            int superW = (int)(targetW * superScale);
            int superH = (int)(targetH * superScale);

            if (_tabPrecomputeBmp == null || _tabPrecomputeBmp.Width != superW || _tabPrecomputeBmp.Height != superH)
            {
                _tabPrecomputeBmp?.Dispose();
                _tabPrecomputeBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
            }

            using (var g = Graphics.FromImage(_tabPrecomputeBmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                DrawTopTabBar(g, superScale, targetW, tabIndex, 0.0);
                DrawSingleTabContent(g, tabIndex, superScale, targetW, track, coverBmp, progressSeconds, isPlaying, isShuffle, rotationAngle, eqBarHeights, hoveredButton, clickedButton, clickAnimProgress, mediaSessionCount);
            }

            DownsampleToBuffer(_tabPrecomputeBmp, destBuffer, 0, targetH, targetW);
        }
    }

    private static unsafe void CompositeSubpixelSlideTransition(
        uint[] prevSnapshot,
        uint[] currSnapshot,
        uint[] destBuffer,
        int activeTab,
        int prevTab,
        double transitionP,
        int targetW,
        int targetH,
        int contentStartY)
    {
        double t = Math.Clamp(transitionP, 0.0, 1.0);
        float dir = (activeTab >= prevTab) ? 1.0f : -1.0f;

        // Outgoing tab: fades out quickly (0.0 to 0.40) with subtle directional drift
        float pOut = Math.Clamp((float)(t / 0.40), 0.0f, 1.0f);
        float easeOut = pOut * pOut;
        float wOut = 1.0f - easeOut;
        float prevOffset = -dir * (pOut * 10.0f);

        // Incoming tab: fades in (0.22 to 1.0) with cubic ease-out deceleration drift
        float pIn = Math.Clamp((float)((t - 0.22) / 0.78), 0.0f, 1.0f);
        float easeIn = (float)(1.0 - Math.Pow(1.0 - pIn, 3.0));
        float wIn = easeIn;
        float currOffset = dir * ((1.0f - easeIn) * 14.0f);

        // Normalize weights so (wOut + wIn) is strictly <= 1.0 at all times (guarantees zero brightness accumulation)
        float totalW = wOut + wIn;
        if (totalW > 1.0f)
        {
            wOut /= totalW;
            wIn /= totalW;
        }

        int weightOut = (int)(wOut * 256f);
        int weightIn = (int)(wIn * 256f);

        // Precompute horizontal offset floor and fractional bilinear weights
        float offP = -prevOffset;
        int x0p = (int)Math.Floor(offP);
        float fxP = offP - x0p;
        int wP0 = (int)((1.0f - fxP) * 256f);
        int wP1 = 256 - wP0;

        float offC = -currOffset;
        int x0c = (int)Math.Floor(offC);
        float fxC = offC - x0c;
        int wC0 = (int)((1.0f - fxC) * 256f);
        int wC1 = 256 - wC0;

        fixed (uint* pPrev = prevSnapshot, pCurr = currSnapshot, pDest = destBuffer)
        {
            for (int y = contentStartY; y < targetH; y++)
            {
                int rowOffset = y * targetW;
                uint* rowPrev = pPrev + rowOffset;
                uint* rowCurr = pCurr + rowOffset;
                uint* rowDest = pDest + rowOffset;

                for (int x = 0; x < targetW; x++)
                {
                    int a0 = 0, r0 = 0, g0 = 0, b0 = 0;
                    if (weightOut > 0)
                    {
                        int xp = x + x0p;
                        uint p0 = (xp >= 0 && xp < targetW) ? rowPrev[xp] : 0;
                        uint p1 = (xp + 1 >= 0 && xp + 1 < targetW) ? rowPrev[xp + 1] : 0;

                        if ((p0 | p1) != 0)
                        {
                            int ap0 = (int)(p0 >> 24);
                            int ap1 = (int)(p1 >> 24);
                            a0 = (ap0 * wP0 + ap1 * wP1) >> 8;

                            int rp0 = (int)((p0 >> 16) & 0xFF);
                            int rp1 = (int)((p1 >> 16) & 0xFF);
                            r0 = (rp0 * wP0 + rp1 * wP1) >> 8;

                            int gp0 = (int)((p0 >> 8) & 0xFF);
                            int gp1 = (int)((p1 >> 8) & 0xFF);
                            g0 = (gp0 * wP0 + gp1 * wP1) >> 8;

                            int bp0 = (int)(p0 & 0xFF);
                            int bp1 = (int)(p1 & 0xFF);
                            b0 = (bp0 * wP0 + bp1 * wP1) >> 8;
                        }
                    }

                    int a1 = 0, r1 = 0, g1 = 0, b1 = 0;
                    if (weightIn > 0)
                    {
                        int xc = x + x0c;
                        uint c0 = (xc >= 0 && xc < targetW) ? rowCurr[xc] : 0;
                        uint c1 = (xc + 1 >= 0 && xc + 1 < targetW) ? rowCurr[xc + 1] : 0;

                        if ((c0 | c1) != 0)
                        {
                            int ac0 = (int)(c0 >> 24);
                            int ac1 = (int)(c1 >> 24);
                            a1 = (ac0 * wC0 + ac1 * wC1) >> 8;

                            int rc0 = (int)((c0 >> 16) & 0xFF);
                            int rc1 = (int)((c1 >> 16) & 0xFF);
                            r1 = (rc0 * wC0 + rc1 * wC1) >> 8;

                            int gc0 = (int)((c0 >> 8) & 0xFF);
                            int gc1 = (int)((c1 >> 8) & 0xFF);
                            g1 = (gc0 * wC0 + gc1 * wC1) >> 8;

                            int bc0 = (int)(c0 & 0xFF);
                            int bc1 = (int)(c1 & 0xFF);
                            b1 = (bc0 * wC0 + bc1 * wC1) >> 8;
                        }
                    }

                    // Strict energy-conserving linear cross-fade: guarantees zero brightness flare
                    int finalA = (a0 * weightOut + a1 * weightIn) >> 8;
                    if (finalA == 0)
                    {
                        rowDest[x] = 0;
                        continue;
                    }

                    int finalR = Math.Min(255, (r0 * weightOut + r1 * weightIn) >> 8);
                    int finalG = Math.Min(255, (g0 * weightOut + g1 * weightIn) >> 8);
                    int finalB = Math.Min(255, (b0 * weightOut + b1 * weightIn) >> 8);

                    rowDest[x] = ((uint)finalA << 24) | ((uint)finalR << 16) | ((uint)finalG << 8) | (uint)finalB;
                }
            }
        }
    }

    private static (uint[] colors, int width, int height) PrecomputeExpandedContent(
        int activeTab,
        int prevTab,
        double transitionP,
        double tabIndicatorPos,
        double tabIndicatorVel,
        TrackInfo track,
        Bitmap? coverBmp,
        double progressSeconds,
        bool isPlaying,
        bool isShuffle,
        double rotationAngle,
        float[]? eqBarHeights,
        int hoveredButton,
        int clickedButton,
        double clickAnimProgress,
        int mediaSessionCount)
    {
        bool isMoving = (activeTab == TabMusic) && (
            (Math.Abs(_audioPickerExpandP - (_audioPickerOpen ? 1.0 : 0.0)) > 0.001) ||
            (Math.Abs(_musicSleepExpandP - (_musicSleepPickerOpen ? 1.0 : 0.0)) > 0.001));

        float superScale = isMoving ? 2.0f : 4.0f;
        int scaleInt = (int)superScale;
        int targetW = 460;
        int targetH = 150;
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        if (_expandedBufferA == null || _expandedBufferB == null)
        {
            _expandedBufferA = new uint[targetW * targetH];
            _expandedBufferB = new uint[targetW * targetH];
        }

        uint[] targetBuffer = _expandedFlip ? _expandedBufferA : _expandedBufferB;
        _expandedFlip = !_expandedFlip;

        if (transitionP >= 1.0 || prevTab == activeTab)
        {
            Bitmap bmp;
            if (isMoving)
            {
                if (_reusableFastBmp == null || _reusableFastBmp.Width != superW || _reusableFastBmp.Height != superH)
                {
                    _reusableFastBmp?.Dispose();
                    _reusableFastBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
                }
                bmp = _reusableFastBmp;
            }
            else
            {
                if (_reusableSuperBmp == null || _reusableSuperBmp.Width != superW || _reusableSuperBmp.Height != superH)
                {
                    _reusableSuperBmp?.Dispose();
                    _reusableSuperBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
                }
                bmp = _reusableSuperBmp;
            }

            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                DrawTopTabBar(g, superScale, targetW, tabIndicatorPos, tabIndicatorVel);
                DrawSingleTabContent(g, activeTab, superScale, targetW, track, coverBmp, progressSeconds, isPlaying, isShuffle, rotationAngle, eqBarHeights, hoveredButton, clickedButton, clickAnimProgress, mediaSessionCount);
            }

            DownsampleToBuffer(bmp, targetBuffer, 0, targetH, targetW, scaleInt);

            // Update tab buffer cache for activeTab with the fresh crisp render
            if (!isMoving)
            {
                if (_tabBufferCache[activeTab] == null)
                {
                    _tabBufferCache[activeTab] = new uint[targetW * targetH];
                }
                Array.Copy(targetBuffer, _tabBufferCache[activeTab]!, targetW * targetH);
            }

            return (targetBuffer, targetW, targetH);
        }
        else
        {
            // ULTRA-FAST 0.6ms FLUID SUBPIXEL SLIDING TRANSITION (200FPS+)
            if (_topBarBmp == null || _topBarBmp.Width != superW || _topBarBmp.Height != 104)
            {
                _topBarBmp?.Dispose();
                _topBarBmp = new Bitmap(superW, 104, PixelFormat.Format32bppArgb);
            }

            using (var gTop = Graphics.FromImage(_topBarBmp))
            {
                gTop.Clear(Color.Transparent);
                gTop.SmoothingMode = SmoothingMode.AntiAlias;
                gTop.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                DrawTopTabBar(gTop, superScale, targetW, tabIndicatorPos, tabIndicatorVel);
            }

            // Downsample top bar rows (0..25) directly into _transitionBuffer
            DownsampleTopBarToBuffer(_topBarBmp, _transitionBuffer, targetW, 26);

            // Sub-pixel smooth sliding cross-fade for tab content rows (26..149)
            CompositeSubpixelSlideTransition(_prevContentSnapshot, _currContentSnapshot, _transitionBuffer, activeTab, prevTab, transitionP, targetW, targetH, 26);

            return (_transitionBuffer, targetW, targetH);
        }
    }

    private static void DrawSingleTabContent(
        Graphics g,
        int tabIndex,
        float superScale,
        int targetW,
        TrackInfo track,
        Bitmap? coverBmp,
        double progressSeconds,
        bool isPlaying,
        bool isShuffle,
        double rotationAngle,
        float[]? eqBarHeights,
        int hoveredButton,
        int clickedButton,
        double clickAnimProgress,
        int mediaSessionCount)
    {
        switch (tabIndex)
        {
            case TabHome:
                DrawTabHomeContent(g, superScale, targetW, track, coverBmp, isPlaying, eqBarHeights);
                break;
            case TabMusic:
                DrawTabMusicContent(g, superScale, targetW, track, coverBmp, progressSeconds, isPlaying, isShuffle, rotationAngle, eqBarHeights, hoveredButton, clickedButton, clickAnimProgress, mediaSessionCount);
                break;
            case TabWeather:
                DrawTabWeatherContent(g, superScale, targetW);
                break;
            case TabChrono:
            default:
                DrawTabChronoContent(g, superScale, targetW);
                break;
        }
    }

    private static bool IsPointInHomeTimer(float mx, float my)
    {
        const float cx = 36f;
        const float cy = 111f;
        const float radius = 20f;
        float dx = mx - cx;
        float dy = my - cy;
        if (dx * dx + dy * dy <= radius * radius) return true;
        // Text area hit box beside the bell tile
        return mx >= 56f && mx <= 165f && my >= 95f && my <= 128f;
    }

    private static void DrawHomeActiveTimer(Graphics g, float superScale)
    {
        if (!GetHomeTimerProgress(out int remainingSeconds, out int totalSeconds)) return;

        const float cx = 36f;
        const float cy = 111f;
        const float radius = 18f;
        bool isHovered = _hoveredHomeTimer;
        float progress = Math.Clamp((float)remainingSeconds / Math.Max(1, totalSeconds), 0f, 1f);

        using (var fill = new SolidBrush(isHovered
            ? Color.FromArgb(72, 220, 70, 70)
            : Color.FromArgb(42, 255, 255, 255)))
        {
            g.FillEllipse(fill, (cx - radius) * superScale, (cy - radius) * superScale,
                radius * 2f * superScale, radius * 2f * superScale);
        }
        using (var border = new Pen(isHovered
            ? Color.FromArgb(220, 255, 105, 105)
            : Color.FromArgb(95, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawEllipse(border, (cx - radius) * superScale, (cy - radius) * superScale,
                radius * 2f * superScale, radius * 2f * superScale);
        }
        using (var ringTrack = new Pen(isHovered
            ? Color.FromArgb(110, 255, 130, 130)
            : Color.FromArgb(72, 255, 255, 255), 2.0f * superScale))
        using (var ringProgress = new Pen(isHovered
            ? Color.FromArgb(245, 255, 145, 145)
            : Color.FromArgb(235, 235, 240, 246), 2.0f * superScale))
        {
            float ringRadius = 15.2f * superScale;
            g.DrawEllipse(ringTrack, cx * superScale - ringRadius, cy * superScale - ringRadius,
                ringRadius * 2f, ringRadius * 2f);
            if (progress > 0.001f)
            {
                g.DrawArc(ringProgress, cx * superScale - ringRadius, cy * superScale - ringRadius,
                    ringRadius * 2f, ringRadius * 2f, -90f, 360f * progress);
            }
        }

        float bellScale = superScale * 0.58f;
        var state = g.Save();
        g.TranslateTransform(cx * superScale - 19f * bellScale, cy * superScale - 19f * bellScale);
        DrawRingingBellVector(g, (int)(38f * bellScale), (int)(38f * bellScale), bellScale);
        g.Restore(state);

        // Digital countdown and status label next to bell tile
        float textX = (cx + radius + 8f) * superScale;
        int mins = remainingSeconds / 60;
        int secs = remainingSeconds % 60;
        string timeStr = $"{mins:D2}:{secs:D2}";
        string statusStr = isHovered ? "CANCEL TIMER" : "REMAINING";

        using var fontTime = GetPremiumFont(12.5f * superScale, FontStyle.Bold);
        using var fontStatus = GetPremiumFont(6.2f * superScale, FontStyle.Bold);
        using var brushTime = new SolidBrush(isHovered
            ? Color.FromArgb(255, 255, 140, 140)
            : Color.FromArgb(248, 250, 252, 255));
        using var brushStatus = new SolidBrush(isHovered
            ? Color.FromArgb(240, 255, 110, 110)
            : Color.FromArgb(160, 195, 215, 230));

        g.DrawString(timeStr, fontTime, brushTime, textX, (cy - 12.5f) * superScale, StringFormat.GenericTypographic);
        g.DrawString(statusStr, fontStatus, brushStatus, textX, (cy + 2.5f) * superScale, StringFormat.GenericTypographic);
    }

    private static void DrawTabHomeContent(
        Graphics g,
        float superScale,
        int targetW,
        TrackInfo track,
        Bitmap? coverBmp,
        bool isPlaying,
        float[]? eqBarHeights)
    {
        var now = DateTime.Now;

        // 1. Top-Left: "Welcome" in Eternalo luxury serif font
        float welcomeX = 18f * superScale;
        float welcomeY = 33f * superScale;

        using var fontWelcome = GetEternaloFont(28.0f * superScale, FontStyle.Regular);
        using var fontHomeSub = GetPremiumFont(9.0f * superScale, FontStyle.Regular);
        using var fontCardTitle = GetPremiumFont(8.5f * superScale, FontStyle.Bold);
        using var fontCardSub = GetPremiumFont(7.5f * superScale, FontStyle.Regular);

        // Subtle soft shadow behind "Welcome" for glass depth
        using (var brushShadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            g.DrawString("Welcome", fontWelcome, brushShadow, welcomeX + 1.0f * superScale, welcomeY + 1.5f * superScale, StringFormat.GenericTypographic);
        }
        using (var brushWelcome = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
        {
            g.DrawString("Welcome", fontWelcome, brushWelcome, welcomeX, welcomeY, StringFormat.GenericTypographic);
        }

        // Greeting & Date Line
        string timeGreeting = now.Hour < 12 ? "Good morning" : (now.Hour < 17 ? "Good afternoon" : "Good evening");
        string dateStr = $"{timeGreeting}  •  {now:dddd, MMM d}";
        using (var brushDateShadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        {
            g.DrawString(dateStr, fontHomeSub, brushDateShadow, welcomeX + 0.8f * superScale, welcomeY + 38f * superScale, StringFormat.GenericDefault);
        }
        using (var brushDate = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
        {
            g.DrawString(dateStr, fontHomeSub, brushDate, welcomeX, welcomeY + 37f * superScale, StringFormat.GenericDefault);
        }

        DrawHomeActiveTimer(g, superScale);

        // 2. Right Column Cards: Now Playing & Weather Quick-Glances
        float cardX = 216f * superScale;
        float cardW = (targetW - 16f) * superScale - cardX; // ~228px at 1x
        float cardR = 12f * superScale;

        // ----------------------------------------------------
        // Card 1: Now Playing Quick-Glance & Sleep Timer Widget
        // ----------------------------------------------------
        float c1Y = 36f * superScale;
        float c1H = 43f * superScale;

        bool isSplit = isPlaying || _hasActiveMedia || _sleepTimerActive || _homeSleepPickerOpen || (_homeSleepExpandP > 0.001);

        float sleepP = (float)Math.Clamp(_homeSleepExpandP, 0.0, 1.0);

        if (isSplit)
        {
            // ====================================================
            // FLUID PHYSICAL SLEEP TIMER EXPANSION ANIMATION
            // ====================================================
            // Smooth physical easing (cubic ease in-out / smoothstep)
            float easeP = sleepP * sleepP * (3f - 2f * sleepP);

            float timerW0 = c1H; // 43px square at 1x
            float fullGap = 6f * superScale;
            float mediaW0 = cardW - timerW0 - fullGap; // ~179px at 1x

            // As sleep timer expands to the left:
            // 1) Sleep timer right edge stays pinned at (cardX + cardW).
            //    Its width expands leftwards from 43px (timerW0) to 228px (cardW).
            float curTimerW = timerW0 + (cardW - timerW0) * easeP;
            float curTimerX = (cardX + cardW) - curTimerW;

            // 2) Gap smoothly closes as the timer expands:
            float curGap = fullGap * (1f - easeP);

            // 3) Media piece is pinned at cardX on the left, and shrinks leftwards:
            float curMediaW = Math.Max(0f, curTimerX - curGap - cardX);

            // Piece 1: Left media end (shrinks leftwards and fades off)
            if (curMediaW > 1.5f * superScale)
            {
                float mediaCornerR = Math.Min(cardR, curMediaW * 0.5f);
                using var pathMedia = CreateRoundedRectPath(cardX, c1Y, curMediaW, c1H, mediaCornerR, mediaCornerR, mediaCornerR, mediaCornerR);

                float mediaBgAlpha = 1f - easeP;
                if (mediaBgAlpha > 0.01f)
                {
                    using var brushMedia = new SolidBrush(Color.FromArgb((int)(32 * mediaBgAlpha), 255, 255, 255));
                    g.FillPath(brushMedia, pathMedia);

                    using var penMedia = new Pen(Color.FromArgb((int)(70 * mediaBgAlpha), 255, 255, 255), 1.0f * superScale);
                    g.DrawPath(penMedia, pathMedia);
                }

                // Media contents: fade off cleanly and clipped strictly inside pathMedia
                float mediaAlpha = Math.Clamp(1f - easeP * 1.8f, 0f, 1f);
                if (mediaAlpha > 0.01f)
                {
                    var stateMedia = g.Save();
                    g.SetClip(pathMedia);

                    // Media Piece Thumbnail
                    float thumbX = cardX + 7f * superScale;
                    float thumbY = c1Y + 7f * superScale;
                    float thumbSize = 29f * superScale;
                    float thumbR = 7f * superScale;

                    if (coverBmp != null)
                    {
                        using var pathThumb = CreateRoundedRectPath(thumbX, thumbY, thumbSize, thumbSize, thumbR, thumbR, thumbR, thumbR);
                        var stateThumb = g.Save();
                        g.SetClip(pathThumb);
                        using var ia = new ImageAttributes();
                        ColorMatrix cm = new ColorMatrix();
                        cm.Matrix33 = mediaAlpha;
                        ia.SetColorMatrix(cm);
                        g.DrawImage(coverBmp, new Rectangle((int)thumbX, (int)thumbY, (int)thumbSize, (int)thumbSize), 0, 0, coverBmp.Width, coverBmp.Height, GraphicsUnit.Pixel, ia);
                        g.Restore(stateThumb);
                    }
                    else
                    {
                        using var pathThumbBg = CreateRoundedRectanglePath(thumbX, thumbY, thumbSize, thumbSize, thumbR);
                        using (var brushThumbBg = new SolidBrush(Color.FromArgb((int)(45 * mediaAlpha), 255, 255, 255)))
                        {
                            g.FillPath(brushThumbBg, pathThumbBg);
                        }
                        using (var brushNote = new SolidBrush(Color.FromArgb((int)(200 * mediaAlpha), 255, 255, 255)))
                        {
                            var noteSize = g.MeasureString("♫", fontCardTitle, PointF.Empty, StringFormat.GenericTypographic);
                            g.DrawString("♫", fontCardTitle, brushNote, thumbX + (thumbSize - noteSize.Width) * 0.5f, thumbY + (thumbSize - noteSize.Height) * 0.5f, StringFormat.GenericTypographic);
                        }
                    }

                    // Media Piece Text
                    float text1X = thumbX + thumbSize + 8f * superScale;
                    string musicTitle = string.IsNullOrEmpty(track.Title) || track.Title == "No Media Playing"
                        ? "Audio Idle"
                        : (track.Title.Length > 15 ? track.Title.Substring(0, 13) + "…" : track.Title);

                    string musicSub = isPlaying
                        ? (string.IsNullOrEmpty(track.Artist) ? "Now Playing" : (track.Artist.Length > 16 ? track.Artist.Substring(0, 14) + "…" : track.Artist))
                        : "Tap to open";

                    using (var brushMTitle = new SolidBrush(Color.FromArgb((int)(255 * mediaAlpha), 255, 255, 255)))
                    {
                        g.DrawString(musicTitle, fontCardTitle, brushMTitle, text1X, c1Y + 7f * superScale, StringFormat.GenericDefault);
                    }
                    using (var brushMSub = new SolidBrush(Color.FromArgb((int)(160 * mediaAlpha), 255, 255, 255)))
                    {
                        g.DrawString(musicSub, fontCardSub, brushMSub, text1X, c1Y + 23f * superScale, StringFormat.GenericDefault);
                    }

                    // Media Piece EQ Bars
                    if (isPlaying && eqBarHeights != null && eqBarHeights.Length >= 4)
                    {
                        float eqStartX = cardX + mediaW0 - 24f * superScale;
                        float eqCy = c1Y + c1H * 0.5f;
                        using var brushEq = new SolidBrush(Color.FromArgb((int)(240 * mediaAlpha), 255, 255, 255));
                        for (int b = 0; b < 4; b++)
                        {
                            float barH = Math.Max(2.5f * superScale, eqBarHeights[b] * 12.0f * superScale);
                            float bx = eqStartX + b * 4.2f * superScale;
                            g.FillRectangle(brushEq, bx, eqCy - barH * 0.5f, 2.3f * superScale, barH);
                        }
                    }

                    g.Restore(stateMedia);
                }
            }

            // Piece 2: Sleep timer end (expands leftwards from 43px square to full 228px card)
            float timerCornerR = Math.Min(cardR, curTimerW * 0.5f);
            using var pathTimer = CreateRoundedRectPath(curTimerX, c1Y, curTimerW, c1H, timerCornerR, timerCornerR, timerCornerR, timerCornerR);
            bool isHovMoon = _hoveredHomeSleepBtn == HomeSleepBtnMoon;

            if (_sleepTimerActive)
            {
                // INVERTED COLOURS TO INDICATE SLEEP TIMER ACTIVE
                int invFillA = (int)(32 + (235 - 32) * (1f - easeP));
                using var brushActiveTimer = new SolidBrush(Color.FromArgb(invFillA, 255, 255, 255));
                g.FillPath(brushActiveTimer, pathTimer);
            }
            else
            {
                int fillA = (isHovMoon && easeP < 0.3f) ? 52 : 32;
                using var brushNormalTimer = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255));
                g.FillPath(brushNormalTimer, pathTimer);
            }

            int borderA = _sleepTimerActive ? (int)(75 + (255 - 75) * (1f - easeP)) : ((isHovMoon && easeP < 0.3f) ? 110 : 75);
            float borderThickness = (_sleepTimerActive && easeP < 0.3f) ? 1.2f * superScale : 1.0f * superScale;
            using var penTimer = new Pen(Color.FromArgb(borderA, 255, 255, 255), borderThickness);
            g.DrawPath(penTimer, pathTimer);

            // Moon logo / countdown on hover: anchored at right end, fades out rapidly as timer expands left
            float moonAlpha = Math.Clamp(1f - easeP * 2.8f, 0f, 1f);
            if (moonAlpha > 0.01f)
            {
                var stateMoon = g.Save();
                g.SetClip(pathTimer);

                float moonAnchorX = (cardX + cardW) - timerW0;
                float moonCx = moonAnchorX + timerW0 * 0.5f - 2.5f * superScale;
                float moonCy = c1Y + c1H * 0.5f;

                bool hasTimerValue = _sleepTimerActive && _sleepTimerTargetUtc != DateTime.MinValue && DateTime.UtcNow < _sleepTimerTargetUtc;
                bool showRemainingOnHover = hasTimerValue && isHovMoon && easeP < 0.3f;

                if (showRemainingOnHover)
                {
                    string timeText;
                    if (DebugSleepTimerInSeconds)
                    {
                        int remainingSec = Math.Max(0, (int)Math.Ceiling((_sleepTimerTargetUtc - DateTime.UtcNow).TotalSeconds));
                        timeText = $"{remainingSec}s";
                    }
                    else
                    {
                        int remainingMin = Math.Max(1, (int)Math.Ceiling((_sleepTimerTargetUtc - DateTime.UtcNow).TotalMinutes));
                        timeText = $"{remainingMin}m";
                    }

                    using var fontCountdown = GetPremiumFont(9.5f * superScale, FontStyle.Bold);
                    var textSize = g.MeasureString(timeText, fontCountdown, PointF.Empty, StringFormat.GenericTypographic);
                    float textX = moonAnchorX + (timerW0 - textSize.Width) * 0.5f;
                    float textY = c1Y + (c1H - textSize.Height) * 0.5f;
                    using var brushCountdown = new SolidBrush(Color.FromArgb((int)(240 * moonAlpha), 25, 28, 35));
                    g.DrawString(timeText, fontCountdown, brushCountdown, textX, textY, StringFormat.GenericTypographic);
                }
                else if (_sleepTimerActive)
                {
                    float moonR = 8.8f * superScale;
                    using var brushDarkMoon = new SolidBrush(Color.FromArgb((int)(240 * moonAlpha), 25, 28, 35));
                    DrawMoonWithStars(g, brushDarkMoon, moonCx, moonCy, moonR);
                }
                else
                {
                    float moonR = 8.8f * superScale;
                    using var brushLightMoon = new SolidBrush(Color.FromArgb((int)(225 * moonAlpha), 255, 255, 255));
                    DrawMoonWithStars(g, brushLightMoon, moonCx, moonCy, moonR);
                }

                g.Restore(stateMoon);
            }

            // --- EXPANDED TEMPLATE BUTTONS: Fades in and revealed as timer expands to the left ---
            float templatesAlpha = Math.Clamp((easeP - 0.20f) / 0.80f, 0f, 1f);
            if (templatesAlpha > 0.01f)
            {
                var stateTemplates = g.Save();
                g.SetClip(pathTimer);

                float btnH = 29f * superScale;
                float btnY = c1Y + (c1H - btnH) * 0.5f;
                float btnR = 8f * superScale;
                float gap = 5f * superScale;
                float bW = 44f * superScale;
                float cancelW = 58f * superScale;
                float padX = (cardW - (bW * 3f + cancelW + gap * 3f)) * 0.5f;

                using var fontTimerBtn = GetPremiumFont(7.8f * superScale, FontStyle.Bold);
                using var fontCancelBtn = GetPremiumFont(7.2f * superScale, FontStyle.Bold);

                string lbl15 = DebugSleepTimerInSeconds ? "15s" : "15m";
                string lbl30 = DebugSleepTimerInSeconds ? "30s" : "30m";
                string lbl45 = DebugSleepTimerInSeconds ? "45s" : "45m";

                // Button 1: 15
                float b1X = cardX + padX;
                DrawSleepOptionPill(g, superScale, b1X, btnY, bW, btnH, btnR, lbl15, fontTimerBtn,
                    isActive: _sleepTimerActive && _sleepTimerDurationMinutes == 15,
                    isHovered: _hoveredHomeSleepBtn == HomeSleepBtn15m,
                    isCancel: false,
                    alphaMul: templatesAlpha);

                // Button 2: 30
                float b2X = b1X + bW + gap;
                DrawSleepOptionPill(g, superScale, b2X, btnY, bW, btnH, btnR, lbl30, fontTimerBtn,
                    isActive: _sleepTimerActive && _sleepTimerDurationMinutes == 30,
                    isHovered: _hoveredHomeSleepBtn == HomeSleepBtn30m,
                    isCancel: false,
                    alphaMul: templatesAlpha);

                // Button 3: 45
                float b3X = b2X + bW + gap;
                DrawSleepOptionPill(g, superScale, b3X, btnY, bW, btnH, btnR, lbl45, fontTimerBtn,
                    isActive: _sleepTimerActive && _sleepTimerDurationMinutes == 45,
                    isHovered: _hoveredHomeSleepBtn == HomeSleepBtn45m,
                    isCancel: false,
                    alphaMul: templatesAlpha);

                // Button 4: Cancel
                float b4X = b3X + bW + gap;
                DrawSleepOptionPill(g, superScale, b4X, btnY, cancelW, btnH, btnR, "Cancel", fontCancelBtn,
                    isActive: false,
                    isHovered: _hoveredHomeSleepBtn == HomeSleepBtnCancel,
                    isCancel: true,
                    timerActive: _sleepTimerActive,
                    alphaMul: templatesAlpha);

                g.Restore(stateTemplates);
            }
        }
        else
        {
            // Open Player Card when audio is idle
            using var pathC1 = CreateRoundedRectPath(cardX, c1Y, cardW, c1H, cardR, cardR, cardR, cardR);
            using var brushC1 = new SolidBrush(Color.FromArgb(32, 255, 255, 255));
            g.FillPath(brushC1, pathC1);
            using var penC1 = new Pen(Color.FromArgb(70, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penC1, pathC1);

            // Left Section: "Open Player" header & subtitle
            float textStartX = cardX + 13f * superScale;
            using var fontOpenTitle = GetPremiumFont(9.5f * superScale, FontStyle.Bold);
            using var fontOpenSub = GetPremiumFont(7.2f * superScale, FontStyle.Regular);

            using (var brushMTitle = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
            {
                g.DrawString("Open Player", fontOpenTitle, brushMTitle, textStartX, c1Y + 8f * superScale, StringFormat.GenericDefault);
            }
            using (var brushMSub = new SolidBrush(Color.FromArgb(145, 255, 255, 255)))
            {
                g.DrawString("Quick launch", fontOpenSub, brushMSub, textStartX, c1Y + 23.5f * superScale, StringFormat.GenericDefault);
            }

            // Right Section: Player Quick-Launcher Icon Buttons (sized and spaced for 3 players)
            var players = PlayerService.GetPlayers();
            int count = Math.Min(3, players.Count);
            if (count > 0)
            {
                float btnSize = 28f * superScale;
                float btnR = 7f * superScale;
                float btnY = c1Y + (c1H - btnSize) * 0.5f;
                float gap = 6f * superScale;
                float rightMargin = 10f * superScale;
                float startX = cardX + cardW - rightMargin - (count * btnSize + (count - 1) * gap);

                for (int i = 0; i < count; i++)
                {
                    float bx = startX + i * (btnSize + gap);
                    bool isHov = (_hoveredHomePlayerIndex == i);
                    DrawPlayerIconButton(g, superScale, bx, btnY, btnSize, btnR, players[i], isHov);
                }
            }
        }

        // ----------------------------------------------------
        // ----------------------------------------------------
        // Card 2: Abstract Geometric Weather Card (Bottom-Right)
        // ----------------------------------------------------
        float c2Y = 87f * superScale;
        float c2H = 43f * superScale;

        using (var pathC2 = CreateRoundedRectPath(cardX, c2Y, cardW, c2H, cardR, cardR, cardR, cardR))
        {
            // Base Liquid Glass Fill
            int cardFillA = _hoveredHomeWeather ? 50 : 34;
            using var brushC2 = new SolidBrush(Color.FromArgb(cardFillA, 255, 255, 255));
            g.FillPath(brushC2, pathC2);

            // Fine Liquid Glass Border
            int cardBorderA = _hoveredHomeWeather ? 115 : 72;
            using var penC2 = new Pen(Color.FromArgb(cardBorderA, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penC2, pathC2);

            var stateC2 = g.Save();
            g.SetClip(pathC2);

            DrawHomeWeatherCard(g, superScale, cardX, c2Y, cardW, c2H, pathC2, CurrentWeatherCondition);

            g.Restore(stateC2);
        }
    }

    private static void DrawBauhausHorizons(Graphics g, float superScale, float rightEdge, float groundY, Color c1, Color c2, Color c3)
    {
        // Back Arc
        float h1R = 26f * superScale;
        float h1Cx = rightEdge - 52f * superScale;
        using (var brushH1 = new SolidBrush(c1))
        {
            g.FillPie(brushH1, h1Cx - h1R, groundY - h1R, h1R * 2f, h1R * 2f, 180, 180);
        }
        using (var penH1 = new Pen(Color.FromArgb(100, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawArc(penH1, h1Cx - h1R, groundY - h1R, h1R * 2f, h1R * 2f, 180, 180);
        }

        // Mid Arc
        float h2R = 24f * superScale;
        float h2Cx = rightEdge - 22f * superScale;
        using (var brushH2 = new SolidBrush(c2))
        {
            g.FillPie(brushH2, h2Cx - h2R, groundY - h2R, h2R * 2f, h2R * 2f, 180, 180);
        }
        using (var penH2 = new Pen(Color.FromArgb(110, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawArc(penH2, h2Cx - h2R, groundY - h2R, h2R * 2f, h2R * 2f, 180, 180);
        }

        // Foreground Arc
        float h3R = 18f * superScale;
        float h3Cx = rightEdge - 36f * superScale;
        using (var brushH3 = new SolidBrush(c3))
        {
            g.FillPie(brushH3, h3Cx - h3R, groundY - h3R, h3R * 2f, h3R * 2f, 180, 180);
        }
        using (var penH3 = new Pen(Color.FromArgb(150, 255, 255, 255), 1.2f * superScale))
        {
            g.DrawArc(penH3, h3Cx - h3R, groundY - h3R, h3R * 2f, h3R * 2f, 180, 180);
        }
    }

    private static void DrawBauhausCloud(
        Graphics g,
        float superScale,
        float cx,
        float cy,
        float width,
        float height,
        Color colorTop,
        Color colorBottom,
        Color rimColor,
        float rimWidth = 1.0f,
        bool showInnerVolume = true)
    {
        // 1. Flat aerodynamic base pill
        float baseH = height * 0.46f;
        float baseY = cy + height * 0.5f - baseH;
        float baseW = width * 0.88f;
        float baseX = cx - baseW * 0.5f;
        float baseR = baseH * 0.5f;

        // 2. Billowing puffy lobes
        // Main central dome (highest crest)
        float mainR = height * 0.44f;
        float mainX = cx + width * 0.04f;
        float mainY = cy - height * 0.06f;

        // Left billowing lobe
        float leftR = height * 0.36f;
        float leftX = cx - width * 0.22f;
        float leftY = cy + height * 0.04f;

        // Right billowing lobe
        float rightR = height * 0.32f;
        float rightX = cx + width * 0.26f;
        float rightY = cy + height * 0.08f;

        // Far left shoulder puff
        float farLeftR = height * 0.25f;
        float farLeftX = cx - width * 0.34f;
        float farLeftY = cy + height * 0.15f;

        // 3. Unified Compound Silhouette Path (Single Solid Gradient Fill, No Internal Borders)
        using (var pathCloud = new GraphicsPath())
        {
            using (var pathBase = CreateRoundedRectPath(baseX, baseY, baseW, baseH, baseR, baseR, baseR, baseR))
            {
                pathCloud.AddPath(pathBase, false);
            }
            pathCloud.AddEllipse(farLeftX - farLeftR, farLeftY - farLeftR, farLeftR * 2f, farLeftR * 2f);
            pathCloud.AddEllipse(leftX - leftR, leftY - leftR, leftR * 2f, leftR * 2f);
            pathCloud.AddEllipse(mainX - mainR, mainY - mainR, mainR * 2f, mainR * 2f);
            pathCloud.AddEllipse(rightX - rightR, rightY - rightR, rightR * 2f, rightR * 2f);

            using (var brushGrad = new LinearGradientBrush(
                new PointF(cx, cy - height * 0.55f),
                new PointF(cx, cy + height * 0.5f),
                colorTop,
                colorBottom))
            {
                g.FillPath(brushGrad, pathCloud);
            }
        }

        // 4. Volumetric Inner Highlights (Organic 3D billow depth)
        if (showInnerVolume)
        {
            int alphaMain = Math.Clamp((int)(colorTop.A * 0.38f), 15, 255);
            Color innerMain = Color.FromArgb(alphaMain, Math.Min(255, colorTop.R + 32), Math.Min(255, colorTop.G + 32), Math.Min(255, colorTop.B + 32));
            using (var brushInner = new SolidBrush(innerMain))
            {
                g.FillEllipse(brushInner, mainX - mainR * 0.65f, mainY - mainR * 0.65f, mainR * 1.3f, mainR * 1.15f);
            }

            int alphaLeft = Math.Clamp((int)(colorTop.A * 0.22f), 10, 255);
            Color innerLeft = Color.FromArgb(alphaLeft, Math.Min(255, colorTop.R + 20), Math.Min(255, colorTop.G + 20), Math.Min(255, colorTop.B + 20));
            using (var brushInnerLeft = new SolidBrush(innerLeft))
            {
                g.FillEllipse(brushInnerLeft, leftX - leftR * 0.60f, leftY - leftR * 0.55f, leftR * 1.2f, leftR * 1.05f);
            }
        }

        // 5. Specular Upper Crest Rims (Sunlit/Skylit edges, no internal lines)
        using (var penRim = new Pen(rimColor, rimWidth * superScale))
        {
            penRim.StartCap = LineCap.Round;
            penRim.EndCap = LineCap.Round;

            // Main central dome crest
            g.DrawArc(penRim, mainX - mainR, mainY - mainR, mainR * 2f, mainR * 2f, 195, 145);

            // Left lobe crest
            g.DrawArc(penRim, leftX - leftR, leftY - leftR, leftR * 2f, leftR * 2f, 180, 115);

            // Right lobe crest
            g.DrawArc(penRim, rightX - rightR, rightY - rightR, rightR * 2f, rightR * 2f, 230, 100);

            // Far left shoulder arc
            g.DrawArc(penRim, farLeftX - farLeftR, farLeftY - farLeftR, farLeftR * 2f, farLeftR * 2f, 165, 85);
        }

        // Delicate base underside line (ground boundary)
        using (var penBase = new Pen(Color.FromArgb((int)(rimColor.A * 0.40f), rimColor), 0.8f * superScale))
        {
            g.DrawLine(penBase, baseX + baseR, baseY + baseH, baseX + baseW - baseR, baseY + baseH);
        }
    }

    private static void DrawBauhausStar(Graphics g, float superScale, float cx, float cy, float radius)
    {
        PointF[] starPts = {
            new PointF(cx, cy - radius),
            new PointF(cx + radius * 0.28f, cy - radius * 0.28f),
            new PointF(cx + radius, cy),
            new PointF(cx + radius * 0.28f, cy + radius * 0.28f),
            new PointF(cx, cy + radius),
            new PointF(cx - radius * 0.28f, cy + radius * 0.28f),
            new PointF(cx - radius, cy),
            new PointF(cx - radius * 0.28f, cy - radius * 0.28f)
        };
        using var brushStar = new SolidBrush(Color.FromArgb(255, 255, 245, 200));
        g.FillPolygon(brushStar, starPts);
    }

private static void DrawHomeBauhausWeather(Graphics g, float superScale, float cardX, float c2Y, float cardW, float c2H, float cardR, GraphicsPath pathC2, int condition)
    {
        var now = DateTime.Now;
        WeatherModel wModel = IsLiveWeatherMode ? LiveWeatherService.Current : LiveWeatherService.GetMockWeather(condition);

        string dayCity = $"{now:ddd} · {wModel.City}".ToUpperInvariant();
        string tempStr = wModel.FormattedTemp;
        string condStr = wModel.ConditionName;
        string subStr = wModel.Subtitle;

        Color skyTop, skyBottom;
        switch (condition)
        {
            case 1: // Sunny / Clear Day
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 75 : 55, 11, 23, 40);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 90 : 70, 18, 52, 75);
                break;
            case 2: // Clear Night
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 8, 10, 22);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 18, 16, 42);
                break;
            case 3: // Partly Cloudy Day
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 75 : 55, 16, 26, 38);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 90 : 70, 26, 42, 60);
                break;
            case 4: // Partly Cloudy Night
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 10, 14, 26);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 18, 22, 42);
                break;
            case 5: // Overcast
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 18, 22, 28);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 28, 34, 42);
                break;
            case 6: // Fog & Mist
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 75 : 55, 16, 28, 28);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 90 : 70, 24, 42, 40);
                break;
            case 7: // Drizzle & Light Rain
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 75 : 55, 16, 24, 34);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 90 : 70, 24, 38, 52);
                break;
            case 8: // Rain & Downpour
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 14, 20, 32);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 22, 36, 52);
                break;
            case 9: // Freezing Rain & Sleet
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 12, 24, 34);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 18, 40, 56);
                break;
            case 10: // Light Snow & Flurries
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 14, 26, 42);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 22, 46, 70);
                break;
            case 11: // Heavy Snow & Blizzard
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 10, 18, 34);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 20, 44, 70);
                break;
            case 12: // Thunderstorm & Lightning
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 14, 18, 28);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 22, 32, 48);
                break;
            case 13: // Severe Hailstorm & Lightning
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 85 : 65, 24, 14, 34);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 100 : 80, 38, 22, 52);
                break;
            case 14: // Golden Sunset / Dusk
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 36, 14, 28);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 62, 24, 40);
                break;
            case 15: // Windy / Gale / Squall
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 80 : 60, 12, 28, 38);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 95 : 75, 20, 46, 60);
                break;
            default:
                skyTop = Color.FromArgb(_hoveredHomeWeather ? 75 : 55, 11, 23, 40);
                skyBottom = Color.FromArgb(_hoveredHomeWeather ? 90 : 70, 18, 52, 75);
                break;
        }

        // 1. Atmosphere Gradient Background
        using (var brushAtmosphere = new LinearGradientBrush(
            new PointF(cardX, c2Y),
            new PointF(cardX + cardW, c2Y + c2H),
            skyTop,
            skyBottom))
        {
            g.FillPath(brushAtmosphere, pathC2);
        }

        // 2. Right-side Bauhaus Abstract Geometric Artwork
        float groundY = c2Y + c2H + 2f * superScale;
        float artCx = cardX + cardW - 36f * superScale;
        float artCy = c2Y + 14f * superScale;

        switch (condition)
        {
            case 1: // Sunny: Sun, Orbit Arc, Tangent Ray, Teal Horizons
            {
                using (var penOrbit = new Pen(Color.FromArgb(85, 255, 255, 255), 1.0f * superScale))
                {
                    g.DrawArc(penOrbit, artCx - 14f * superScale, artCy - 14f * superScale, 28f * superScale, 28f * superScale, 190, 160);
                }

                using (var penTangent = new Pen(Color.FromArgb(70, 255, 255, 255), 1.0f * superScale))
                {
                    g.DrawLine(penTangent, artCx - 20f * superScale, c2Y + c2H + 4f * superScale, artCx + 26f * superScale, c2Y - 4f * superScale);
                }

                using (var brushHalo = new SolidBrush(Color.FromArgb(50, 255, 175, 35)))
                {
                    g.FillEllipse(brushHalo, artCx - 15f * superScale, artCy - 15f * superScale, 30f * superScale, 30f * superScale);
                }

                using (var brushSun = new LinearGradientBrush(
                    new PointF(artCx - 10f * superScale, artCy - 10f * superScale),
                    new PointF(artCx + 10f * superScale, artCy + 10f * superScale),
                    Color.FromArgb(255, 255, 225, 72),
                    Color.FromArgb(255, 248, 138, 24)))
                {
                    g.FillEllipse(brushSun, artCx - 10f * superScale, artCy - 10f * superScale, 20f * superScale, 20f * superScale);
                }

                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY, 
                    Color.FromArgb(235, 21, 72, 89), 
                    Color.FromArgb(245, 34, 119, 140), 
                    Color.FromArgb(255, 61, 174, 189));
                break;
            }

            case 2: // Clear Night: Crescent Moon, Orbit Ring, Diamond Starbursts
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY, 
                    Color.FromArgb(235, 18, 14, 40), 
                    Color.FromArgb(245, 28, 24, 64), 
                    Color.FromArgb(255, 42, 38, 94));

                float moonR = 8.5f * superScale;
                float mCx = artCx + 12f * superScale;
                float mCy = artCy - 1f * superScale;

                using (var pathMoon = new GraphicsPath())
                {
                    pathMoon.AddArc(mCx - moonR, mCy - moonR, moonR * 2f, moonR * 2f, -110, 220);
                    float innerR = moonR * 1.15f;
                    float innerOffset = 4.6f * superScale;
                    pathMoon.AddArc(mCx - innerR + innerOffset, mCy - innerR, innerR * 2f, innerR * 2f, 85, -170);
                    pathMoon.CloseFigure();

                    using var brushMoon = new LinearGradientBrush(
                        new PointF(mCx - moonR, mCy - moonR),
                        new PointF(mCx + moonR, mCy + moonR),
                        Color.FromArgb(255, 255, 242, 178),
                        Color.FromArgb(255, 230, 202, 101));
                    g.FillPath(brushMoon, pathMoon);
                }

                using (var penOrbit = new Pen(Color.FromArgb(70, 255, 255, 255), 1.0f * superScale))
                {
                    float oR = moonR + 6.0f * superScale;
                    g.DrawArc(penOrbit, mCx - oR, mCy - oR, oR * 2f, oR * 2f, 115, 210);
                }

                DrawBauhausStar(g, superScale, artCx - 14f * superScale, artCy - 3f * superScale, 4.5f * superScale);
                DrawBauhausStar(g, superScale, artCx + 13f * superScale, artCy + 10f * superScale, 3.2f * superScale);

                using (var brushStarDot = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                {
                    g.FillEllipse(brushStarDot, artCx - 8f * superScale, artCy + 15f * superScale, 2.0f * superScale, 2.0f * superScale);
                    g.FillEllipse(brushStarDot, artCx + 11f * superScale, artCy - 6f * superScale, 1.8f * superScale, 1.8f * superScale);
                }
                break;
            }

            case 3: // Partly Cloudy Day: Peeking Sun, Overlapping Cloud Disks, Stratum Lines
            {
                float sunR = 8.5f * superScale;
                float sunCx = artCx + 13f * superScale;
                float sunCy = artCy - 4f * superScale;

                using (var brushSun = new LinearGradientBrush(
                    new PointF(sunCx - sunR, sunCy - sunR),
                    new PointF(sunCx + sunR, sunCy + sunR),
                    Color.FromArgb(255, 255, 220, 75),
                    Color.FromArgb(255, 248, 138, 24)))
                {
                    g.FillEllipse(brushSun, sunCx - sunR, sunCy - sunR, sunR * 2f, sunR * 2f);
                }

                using (var penOrbit = new Pen(Color.FromArgb(75, 255, 255, 255), 1.0f * superScale))
                {
                    g.DrawArc(penOrbit, sunCx - 12f * superScale, sunCy - 12f * superScale, 24f * superScale, 24f * superScale, 160, 160);
                }

                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 24, 48, 58),
                    Color.FromArgb(245, 36, 70, 84),
                    Color.FromArgb(255, 54, 100, 116));

                DrawBauhausCloud(g, superScale,
                    cx: artCx - 2f * superScale,
                    cy: artCy + 5f * superScale,
                    width: 44f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(245, 96, 122, 154),
                    colorBottom: Color.FromArgb(255, 62, 80, 105),
                    rimColor: Color.FromArgb(170, 255, 235, 180),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                using (var penStratum = new Pen(Color.FromArgb(65, 255, 255, 255), 1.0f * superScale))
                using (var brushNode = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                {
                    float l1Y = c2Y + 28f * superScale;
                    g.DrawLine(penStratum, cardX + cardW - 68f * superScale, l1Y, cardX + cardW - 12f * superScale, l1Y);
                    g.FillEllipse(brushNode, cardX + cardW - 54f * superScale, l1Y - 1.5f * superScale, 3f * superScale, 3f * superScale);
                }
                break;
            }

            case 4: // Partly Cloudy Night: Peeking Moon, Nocturnal Clouds, Starlight
            {
                float moonR = 8.0f * superScale;
                float mCx = artCx + 13f * superScale;
                float mCy = artCy - 4f * superScale;

                using (var pathMoon = new GraphicsPath())
                {
                    pathMoon.AddArc(mCx - moonR, mCy - moonR, moonR * 2f, moonR * 2f, -110, 220);
                    float innerR = moonR * 1.15f;
                    float innerOffset = 4.4f * superScale;
                    pathMoon.AddArc(mCx - innerR + innerOffset, mCy - innerR, innerR * 2f, innerR * 2f, 85, -170);
                    pathMoon.CloseFigure();

                    using var brushMoon = new SolidBrush(Color.FromArgb(255, 255, 240, 180));
                    g.FillPath(brushMoon, pathMoon);
                }

                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 16, 18, 38),
                    Color.FromArgb(245, 24, 28, 54),
                    Color.FromArgb(255, 36, 42, 78));

                DrawBauhausCloud(g, superScale,
                    cx: artCx - 2f * superScale,
                    cy: artCy + 5f * superScale,
                    width: 44f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(240, 42, 48, 80),
                    colorBottom: Color.FromArgb(255, 24, 28, 52),
                    rimColor: Color.FromArgb(130, 210, 230, 255),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                DrawBauhausStar(g, superScale, artCx - 14f * superScale, artCy - 4f * superScale, 3.8f * superScale);
                break;
            }

            case 5: // Overcast: Layered Architectural Lead Strata & Overcast Disks
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 32, 38, 48),
                    Color.FromArgb(245, 44, 52, 64),
                    Color.FromArgb(255, 58, 68, 82));

                // Background cloud bank
                DrawBauhausCloud(g, superScale,
                    cx: artCx + 6f * superScale,
                    cy: artCy - 1f * superScale,
                    width: 46f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(235, 52, 60, 72),
                    colorBottom: Color.FromArgb(245, 36, 42, 52),
                    rimColor: Color.FromArgb(70, 255, 255, 255),
                    rimWidth: 0.9f,
                    showInnerVolume: false);

                // Foreground dense overcast cloud
                DrawBauhausCloud(g, superScale,
                    cx: artCx - 4f * superScale,
                    cy: artCy + 6f * superScale,
                    width: 44f * superScale,
                    height: 20f * superScale,
                    colorTop: Color.FromArgb(250, 78, 90, 106),
                    colorBottom: Color.FromArgb(255, 48, 56, 68),
                    rimColor: Color.FromArgb(130, 255, 255, 255),
                    rimWidth: 1.1f,
                    showInnerVolume: true);

                using (var penStratum = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f * superScale))
                using (var brushNode = new SolidBrush(Color.FromArgb(180, 255, 255, 255)))
                {
                    float l1Y = c2Y + 22f * superScale;
                    g.DrawLine(penStratum, cardX + cardW - 65f * superScale, l1Y, cardX + cardW - 10f * superScale, l1Y);
                    g.FillEllipse(brushNode, cardX + cardW - 48f * superScale, l1Y - 1.5f * superScale, 3f * superScale, 3f * superScale);

                    float l2Y = c2Y + 30f * superScale;
                    g.DrawLine(penStratum, cardX + cardW - 55f * superScale, l2Y, cardX + cardW - 16f * superScale, l2Y);
                }
                break;
            }

            case 6: // Fog & Mist: Slotted Translucent Scanline Bands, Phantom Faded Circles
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 26, 46, 44),
                    Color.FromArgb(245, 36, 62, 58),
                    Color.FromArgb(255, 50, 84, 78));

                using (var brushPhantom = new SolidBrush(Color.FromArgb(40, 200, 240, 235)))
                {
                    g.FillEllipse(brushPhantom, artCx - 6f * superScale, artCy - 6f * superScale, 22f * superScale, 22f * superScale);
                }

                using (var penScan = new Pen(Color.FromArgb(90, 220, 255, 250), 1.4f * superScale))
                {
                    for (int b = 0; b < 4; b++)
                    {
                        float sy = c2Y + 12f * superScale + b * 6.5f * superScale;
                        float sx1 = cardX + cardW - 62f * superScale + (b % 2) * 8f * superScale;
                        float sx2 = cardX + cardW - 10f * superScale - ((b + 1) % 2) * 6f * superScale;
                        g.DrawLine(penScan, sx1, sy, sx2, sy);
                    }
                }
                break;
            }

            case 7: // Drizzle: Fine Delicate 45-deg Micro-Dash Grid, Soft Clouds
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 22, 38, 52),
                    Color.FromArgb(245, 32, 54, 72),
                    Color.FromArgb(255, 46, 74, 98));

                DrawBauhausCloud(g, superScale,
                    cx: artCx,
                    cy: artCy,
                    width: 44f * superScale,
                    height: 20f * superScale,
                    colorTop: Color.FromArgb(245, 64, 94, 122),
                    colorBottom: Color.FromArgb(255, 40, 62, 84),
                    rimColor: Color.FromArgb(120, 180, 230, 255),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                using (var penDrizzle = new Pen(Color.FromArgb(190, 80, 210, 255), 1.0f * superScale))
                {
                    float dx = 4f * superScale;
                    float dy = 6f * superScale;
                    for (int d = 0; d < 5; d++)
                    {
                        float lx = cardX + cardW - 55f * superScale + d * 9f * superScale;
                        float ly = c2Y + 22f * superScale + (d % 2) * 4f * superScale;
                        g.DrawLine(penDrizzle, lx, ly, lx - dx, ly + dy);
                    }
                }
                break;
            }

            case 8: // Rain & Downpour: Petrol Clouds, Steep Diagonal Rain Streaks, Ripple Arc
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 18, 28, 38),
                    Color.FromArgb(245, 27, 42, 56),
                    Color.FromArgb(255, 38, 62, 82));

                DrawBauhausCloud(g, superScale,
                    cx: artCx,
                    cy: artCy - 1f * superScale,
                    width: 46f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(245, 52, 74, 102),
                    colorBottom: Color.FromArgb(255, 32, 46, 68),
                    rimColor: Color.FromArgb(110, 160, 225, 255),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                using (var penRainCyan = new Pen(Color.FromArgb(220, 64, 196, 255), 1.3f * superScale))
                using (var penRainWhite = new Pen(Color.FromArgb(170, 255, 255, 255), 1.1f * superScale))
                {
                    float dx = 6f * superScale;
                    float dy = 13f * superScale;
                    for (int line = 0; line < 5; line++)
                    {
                        float lx = cardX + cardW - 56f * superScale + line * 10f * superScale;
                        float ly = c2Y + 20f * superScale + (line % 2) * 3f * superScale;
                        var penR = (line % 2 == 0) ? penRainCyan : penRainWhite;
                        g.DrawLine(penR, lx, ly, lx - dx, ly + dy);
                    }
                }
                break;
            }

            case 9: // Freezing Rain & Sleet: Glacier Teal, Diamond Ice Crystals, Shard Lines
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 20, 44, 60),
                    Color.FromArgb(245, 28, 62, 82),
                    Color.FromArgb(255, 42, 88, 114));

                DrawBauhausCloud(g, superScale,
                    cx: artCx,
                    cy: artCy - 1f * superScale,
                    width: 46f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(245, 48, 88, 118),
                    colorBottom: Color.FromArgb(255, 28, 56, 80),
                    rimColor: Color.FromArgb(150, 180, 240, 255),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                using (var penSleet = new Pen(Color.FromArgb(220, 140, 230, 255), 1.2f * superScale))
                using (var brushDiamond = new SolidBrush(Color.FromArgb(240, 200, 245, 255)))
                {
                    float dx = 5f * superScale;
                    float dy = 11f * superScale;
                    for (int s = 0; s < 4; s++)
                    {
                        float lx = cardX + cardW - 52f * superScale + s * 11f * superScale;
                        float ly = c2Y + 20f * superScale + (s % 2) * 4f * superScale;
                        g.DrawLine(penSleet, lx, ly, lx - dx, ly + dy);
                        // Ice crystal diamond at tip
                        float tipX = lx - dx;
                        float tipY = ly + dy;
                        PointF[] dPts = {
                            new PointF(tipX, tipY - 2.2f * superScale),
                            new PointF(tipX + 1.8f * superScale, tipY),
                            new PointF(tipX, tipY + 2.2f * superScale),
                            new PointF(tipX - 1.8f * superScale, tipY)
                        };
                        g.FillPolygon(brushDiamond, dPts);
                    }
                }
                break;
            }

            case 10: // Light Snow: Pastel Azure, 4-Arm Snowflake, Snow Particles
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY, 
                    Color.FromArgb(235, 24, 50, 74), 
                    Color.FromArgb(245, 34, 70, 102), 
                    Color.FromArgb(255, 48, 96, 138));

                float sR = 7.5f * superScale;
                using (var brushNode = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                using (var penSpoke = new Pen(Color.FromArgb(240, 255, 255, 255), 1.2f * superScale))
                {
                    g.FillEllipse(brushNode, artCx - 1.8f * superScale, artCy - 1.8f * superScale, 3.6f * superScale, 3.6f * superScale);
                    // 4 perpendicular spokes
                    g.DrawLine(penSpoke, artCx - sR, artCy, artCx + sR, artCy);
                    g.DrawLine(penSpoke, artCx, artCy - sR, artCx, artCy + sR);

                    // Crossbar terminals
                    float cLen = 2.5f * superScale;
                    g.DrawLine(penSpoke, artCx - sR, artCy - cLen, artCx - sR, artCy + cLen);
                    g.DrawLine(penSpoke, artCx + sR, artCy - cLen, artCx + sR, artCy + cLen);
                    g.DrawLine(penSpoke, artCx - cLen, artCy - sR, artCx + cLen, artCy - sR);
                    g.DrawLine(penSpoke, artCx - cLen, artCy + sR, artCx + cLen, artCy + sR);
                }

                using (var brushSnow = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                {
                    g.FillEllipse(brushSnow, artCx - 16f * superScale, artCy - 4f * superScale, 2.5f * superScale, 2.5f * superScale);
                    g.FillEllipse(brushSnow, artCx + 14f * superScale, artCy + 8f * superScale, 3.0f * superScale, 3.0f * superScale);
                    g.FillEllipse(brushSnow, artCx - 8f * superScale, artCy + 12f * superScale, 2.0f * superScale, 2.0f * superScale);
                }
                break;
            }

            case 11: // Heavy Snow & Blizzard: Arctic Cobalt, 6-Arm Snowflake, Wind-blown Drifts
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY, 
                    Color.FromArgb(235, 26, 56, 88), 
                    Color.FromArgb(245, 38, 82, 124), 
                    Color.FromArgb(255, 70, 136, 184));

                float spokeLen = 9.0f * superScale;
                using (var brushNode = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                using (var penSpoke = new Pen(Color.FromArgb(240, 255, 255, 255), 1.1f * superScale))
                {
                    g.FillEllipse(brushNode, artCx - 1.8f * superScale, artCy - 1.8f * superScale, 3.6f * superScale, 3.6f * superScale);
                    for (int a = 0; a < 6; a++)
                    {
                        double rad = a * Math.PI / 3.0;
                        float cos = (float)Math.Cos(rad);
                        float sin = (float)Math.Sin(rad);
                        float ex = artCx + spokeLen * cos;
                        float ey = artCy + spokeLen * sin;
                        g.DrawLine(penSpoke, artCx, artCy, ex, ey);
                        g.FillEllipse(brushNode, ex - 1.1f * superScale, ey - 1.1f * superScale, 2.2f * superScale, 2.2f * superScale);

                        float bx = artCx + spokeLen * 0.55f * cos;
                        float by = artCy + spokeLen * 0.55f * sin;
                        float bLen = 2.8f * superScale;
                        g.DrawLine(penSpoke, bx, by, bx + bLen * (float)Math.Cos(rad + Math.PI / 4.0), by + bLen * (float)Math.Sin(rad + Math.PI / 4.0));
                        g.DrawLine(penSpoke, bx, by, bx + bLen * (float)Math.Cos(rad - Math.PI / 4.0), by + bLen * (float)Math.Sin(rad - Math.PI / 4.0));
                    }
                }

                // Blizzard flurry wind streaks
                using (var penFlurry = new Pen(Color.FromArgb(170, 240, 255, 255), 1.0f * superScale))
                {
                    g.DrawLine(penFlurry, artCx - 24f * superScale, artCy - 8f * superScale, artCx - 6f * superScale, artCy - 2f * superScale);
                    g.DrawLine(penFlurry, artCx - 18f * superScale, artCy + 14f * superScale, artCx + 2f * superScale, artCy + 19f * superScale);
                }

                using (var brushSnow = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                {
                    g.FillEllipse(brushSnow, artCx - 14f * superScale, artCy + 4f * superScale, 2.4f * superScale, 2.4f * superScale);
                    g.FillEllipse(brushSnow, artCx + 16f * superScale, artCy - 6f * superScale, 2.0f * superScale, 2.0f * superScale);
                }
                break;
            }

            case 12: // Thunderstorm: Storm Clouds, Diagonal Rain, Electric Gold Lightning Bolt
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 18, 28, 38),
                    Color.FromArgb(245, 27, 42, 56),
                    Color.FromArgb(255, 38, 62, 82));

                DrawBauhausCloud(g, superScale,
                    cx: artCx,
                    cy: artCy - 2f * superScale,
                    width: 46f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(250, 48, 62, 84),
                    colorBottom: Color.FromArgb(255, 26, 34, 50),
                    rimColor: Color.FromArgb(130, 255, 230, 140),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                using (var penRain = new Pen(Color.FromArgb(180, 64, 196, 255), 1.1f * superScale))
                {
                    g.DrawLine(penRain, cardX + cardW - 52f * superScale, c2Y + 22f * superScale, cardX + cardW - 57f * superScale, c2Y + 34f * superScale);
                    g.DrawLine(penRain, cardX + cardW - 24f * superScale, c2Y + 24f * superScale, cardX + cardW - 29f * superScale, c2Y + 36f * superScale);
                }

                // Electric Gold Constructivist Lightning Bolt
                using (var brushBolt = new SolidBrush(Color.FromArgb(255, 255, 215, 20)))
                using (var penBolt = new Pen(Color.FromArgb(255, 255, 255, 220), 1.0f * superScale))
                {
                    float bx = artCx + 1f * superScale;
                    float by = artCy - 2f * superScale;
                    PointF[] boltPts = {
                        new PointF(bx, by),
                        new PointF(bx - 4.5f * superScale, by + 11f * superScale),
                        new PointF(bx - 0.5f * superScale, by + 11f * superScale),
                        new PointF(bx - 6.5f * superScale, by + 23f * superScale),
                        new PointF(bx + 2f * superScale, by + 12f * superScale),
                        new PointF(bx - 1.5f * superScale, by + 12f * superScale)
                    };
                    g.FillPolygon(brushBolt, boltPts);
                    g.DrawPolygon(penBolt, boltPts);
                }
                break;
            }

            case 13: // Severe Hailstorm: Violent Purple Horizon, Angular Lightning, Faceted Hail
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY,
                    Color.FromArgb(235, 36, 20, 48),
                    Color.FromArgb(245, 52, 28, 68),
                    Color.FromArgb(255, 74, 40, 96));

                DrawBauhausCloud(g, superScale,
                    cx: artCx,
                    cy: artCy - 2f * superScale,
                    width: 46f * superScale,
                    height: 21f * superScale,
                    colorTop: Color.FromArgb(250, 68, 42, 88),
                    colorBottom: Color.FromArgb(255, 38, 22, 54),
                    rimColor: Color.FromArgb(130, 220, 180, 255),
                    rimWidth: 1.0f,
                    showInnerVolume: true);

                // Angular multi-segment lightning
                using (var penBolt = new Pen(Color.FromArgb(255, 120, 240, 255), 1.3f * superScale))
                {
                    PointF[] boltLine = {
                        new PointF(artCx + 8f * superScale, artCy - 5f * superScale),
                        new PointF(artCx + 3f * superScale, artCy + 6f * superScale),
                        new PointF(artCx + 6f * superScale, artCy + 7f * superScale),
                        new PointF(artCx - 1f * superScale, artCy + 22f * superScale)
                    };
                    g.DrawLines(penBolt, boltLine);
                }

                // Geometric Hail Hexagons
                using (var brushHail = new SolidBrush(Color.FromArgb(230, 220, 245, 255)))
                using (var penHail = new Pen(Color.FromArgb(255, 255, 255, 255), 1.0f * superScale))
                {
                    float[] hx = { artCx - 16f * superScale, artCx - 6f * superScale, artCx + 14f * superScale };
                    float[] hy = { artCy + 8f * superScale, artCy + 18f * superScale, artCy + 12f * superScale };
                    float[] hr = { 2.6f * superScale, 3.2f * superScale, 2.8f * superScale };

                    for (int k = 0; k < 3; k++)
                    {
                        PointF[] hex = new PointF[6];
                        for (int a = 0; a < 6; a++)
                        {
                            double rad = a * Math.PI / 3.0;
                            hex[a] = new PointF(hx[k] + hr[k] * (float)Math.Cos(rad), hy[k] + hr[k] * (float)Math.Sin(rad));
                        }
                        g.FillPolygon(brushHail, hex);
                        g.DrawPolygon(penHail, hex);
                    }
                }
                break;
            }

            case 14: // Golden Sunset: Sinking Sun with Constructivist Slits, Dusk Ray
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY, 
                    Color.FromArgb(235, 48, 21, 43), 
                    Color.FromArgb(245, 84, 32, 50), 
                    Color.FromArgb(255, 140, 58, 56));

                float sunR = 14f * superScale;
                float sunCx = artCx + 6f * superScale;
                float sunCy = artCy + 6f * superScale;

                using (var brushSun = new LinearGradientBrush(
                    new PointF(sunCx - sunR, sunCy - sunR),
                    new PointF(sunCx + sunR, sunCy + sunR),
                    Color.FromArgb(255, 255, 183, 77),
                    Color.FromArgb(255, 255, 87, 34)))
                {
                    g.FillEllipse(brushSun, sunCx - sunR, sunCy - sunR, sunR * 2f, sunR * 2f);
                }

                // Constructivist Horizontal Slit Blinds
                using (var penSlit = new Pen(Color.FromArgb(220, 36, 14, 28), 1.3f * superScale))
                {
                    for (float slitY = sunCy - sunR + 3.5f * superScale; slitY < sunCy + sunR; slitY += 3.8f * superScale)
                    {
                        g.DrawLine(penSlit, sunCx - sunR - 1f, slitY, sunCx + sunR + 1f, slitY);
                    }
                }

                using (var penTangent = new Pen(Color.FromArgb(90, 255, 180, 100), 1.0f * superScale))
                {
                    g.DrawLine(penTangent, artCx - 22f * superScale, c2Y + c2H + 4f * superScale, artCx + 22f * superScale, c2Y - 4f * superScale);
                }
                break;
            }

            case 15: // Windy / Gale: Sweeping Bauhaus Aerodynamic Streamline Vectors
            {
                DrawBauhausHorizons(g, superScale, cardX + cardW, groundY, 
                    Color.FromArgb(235, 20, 48, 62), 
                    Color.FromArgb(245, 30, 70, 88), 
                    Color.FromArgb(255, 44, 98, 122));

                using (var penWind = new Pen(Color.FromArgb(210, 80, 220, 235), 1.4f * superScale))
                using (var brushNode = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                {
                    penWind.StartCap = LineCap.Round;
                    penWind.EndCap = LineCap.Round;

                    // Streamline 1
                    g.DrawArc(penWind, artCx - 24f * superScale, artCy - 10f * superScale, 36f * superScale, 18f * superScale, 180, 150);
                    g.FillEllipse(brushNode, artCx + 8f * superScale, artCy - 6f * superScale, 3f * superScale, 3f * superScale);

                    // Streamline 2
                    g.DrawArc(penWind, artCx - 18f * superScale, artCy + 2f * superScale, 38f * superScale, 16f * superScale, 190, 140);
                    g.FillEllipse(brushNode, artCx + 16f * superScale, artCy + 6f * superScale, 2.5f * superScale, 2.5f * superScale);

                    // Streamline 3
                    g.DrawArc(penWind, artCx - 28f * superScale, artCy + 12f * superScale, 32f * superScale, 14f * superScale, 180, 130);
                }

                // Arrowhead on top streamline
                using (var brushArrow = new SolidBrush(Color.FromArgb(220, 80, 220, 235)))
                {
                    PointF[] arrow = {
                        new PointF(artCx + 11f * superScale, artCy - 6f * superScale),
                        new PointF(artCx + 7f * superScale, artCy - 9f * superScale),
                        new PointF(artCx + 7f * superScale, artCy - 3f * superScale)
                    };
                    g.FillPolygon(brushArrow, arrow);
                }
                break;
            }
        }

        // 3. Swiss Typographic Block (Left)
        float textLeft = cardX + 11f * superScale;
        using (var fontDay = GetPremiumFont(6.4f * superScale, FontStyle.Bold))
        using (var brushDay = new SolidBrush(Color.FromArgb(175, 210, 235, 255)))
        {
            g.DrawString(dayCity, fontDay, brushDay, textLeft, c2Y + 5.5f * superScale, StringFormat.GenericTypographic);
        }

        float textStartX = textLeft;
        using (var fontTemp = GetPremiumFont(16.5f * superScale, FontStyle.Bold))
        {
            var tSize = g.MeasureString(tempStr, fontTemp, PointF.Empty, StringFormat.GenericTypographic);
            using var brushT = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
            g.DrawString(tempStr, fontTemp, brushT, textLeft, c2Y + 14.0f * superScale, StringFormat.GenericTypographic);
            textStartX += tSize.Width + 7.5f * superScale;
        }

        using (var fontCond = GetPremiumFont(7.8f * superScale, FontStyle.Bold))
        using (var fontSub = GetPremiumFont(6.4f * superScale, FontStyle.Regular))
        {
            float condY = c2Y + 14.5f * superScale;
            using var brushCond = new SolidBrush(Color.FromArgb(250, 255, 255, 255));
            g.DrawString(condStr, fontCond, brushCond, textStartX, condY, StringFormat.GenericTypographic);

            float subY = c2Y + 25.5f * superScale;
            using var brushSub = new SolidBrush(Color.FromArgb(170, 210, 235, 255));
            g.DrawString(subStr, fontSub, brushSub, textStartX, subY, StringFormat.GenericTypographic);
        }

        if (_hoveredHomeWeather)
        {
            using var brushArrow = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            using var fontArrow = GetPremiumFont(10.0f * superScale, FontStyle.Bold);
            g.DrawString("›", fontArrow, brushArrow, cardX + cardW - 11f * superScale, c2Y + c2H * 0.5f - 7f * superScale, StringFormat.GenericDefault);
        }
    }

    private static GraphicsPath CreateRoundedRectPath(float x, float y, float w, float h, float rtl, float rtr, float rbr, float rbl)
    {
        var path = new GraphicsPath();
        if (rtl > 0.5f) path.AddArc(x, y, rtl * 2, rtl * 2, 180, 90);
        else path.AddLine(x, y, x, y);

        if (rtr > 0.5f) path.AddArc(x + w - rtr * 2, y, rtr * 2, rtr * 2, 270, 90);
        else path.AddLine(x + w, y, x + w, y);

        if (rbr > 0.5f) path.AddArc(x + w - rbr * 2, y + h - rbr * 2, rbr * 2, rbr * 2, 0, 90);
        else path.AddLine(x + w, y + h, x + w, y + h);

        if (rbl > 0.5f) path.AddArc(x, y + h - rbl * 2, rbl * 2, rbl * 2, 90, 90);
        else path.AddLine(x, y + h, x, y + h);

        path.CloseFigure();
        return path;
    }

    private static void DrawSleepOptionPill(
        Graphics g,
        float superScale,
        float x, float y, float w, float h, float r,
        string label,
        Font font,
        bool isActive,
        bool isHovered,
        bool isCancel,
        bool timerActive = false,
        float alphaMul = 1.0f)
    {
        if (alphaMul <= 0.001f) return;
        alphaMul = Math.Clamp(alphaMul, 0.0f, 1.0f);

        using var path = CreateRoundedRectPath(x, y, w, h, r, r, r, r);

        if (isActive)
        {
            using var brushActive = new SolidBrush(Color.FromArgb((int)(235 * alphaMul), 255, 255, 255));
            g.FillPath(brushActive, path);
            using var penActive = new Pen(Color.FromArgb((int)(255 * alphaMul), 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penActive, path);

            var strSize = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic);
            using var brushText = new SolidBrush(Color.FromArgb((int)(240 * alphaMul), 25, 28, 35));
            g.DrawString(label, font, brushText, x + (w - strSize.Width) * 0.5f, y + (h - strSize.Height) * 0.5f, StringFormat.GenericTypographic);
        }
        else if (isCancel)
        {
            int fillA = isHovered ? (timerActive ? 75 : 60) : (timerActive ? 40 : 28);
            Color fillC = timerActive 
                ? Color.FromArgb((int)(fillA * alphaMul), 255, 90, 90) 
                : Color.FromArgb((int)(fillA * alphaMul), 255, 255, 255);
            using var brushCancel = new SolidBrush(fillC);
            g.FillPath(brushCancel, path);

            int borderA = isHovered ? (timerActive ? 140 : 110) : (timerActive ? 80 : 60);
            Color borderC = timerActive 
                ? Color.FromArgb((int)(borderA * alphaMul), 255, 120, 120) 
                : Color.FromArgb((int)(borderA * alphaMul), 255, 255, 255);
            using var penCancel = new Pen(borderC, 1.0f * superScale);
            g.DrawPath(penCancel, path);

            var strSize = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic);
            Color textC = timerActive 
                ? Color.FromArgb((int)(255 * alphaMul), 255, 215, 215) 
                : Color.FromArgb((int)(210 * alphaMul), 255, 255, 255);
            using var brushText = new SolidBrush(textC);
            g.DrawString(label, font, brushText, x + (w - strSize.Width) * 0.5f, y + (h - strSize.Height) * 0.5f, StringFormat.GenericTypographic);
        }
        else
        {
            int fillA = isHovered ? 65 : 34;
            using var brushPill = new SolidBrush(Color.FromArgb((int)(fillA * alphaMul), 255, 255, 255));
            g.FillPath(brushPill, path);

            int borderA = isHovered ? 120 : 65;
            using var penPill = new Pen(Color.FromArgb((int)(borderA * alphaMul), 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penPill, path);

            var strSize = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic);
            using var brushText = new SolidBrush(Color.FromArgb((int)(235 * alphaMul), 255, 255, 255));
            g.DrawString(label, font, brushText, x + (w - strSize.Width) * 0.5f, y + (h - strSize.Height) * 0.5f, StringFormat.GenericTypographic);
        }
    }

    private static void DrawMoonWithStars(Graphics g, Brush brush, float cx, float cy, float r)
    {
        // Outer circle
        using var pathOuter = new GraphicsPath();
        pathOuter.AddEllipse(cx - r, cy - r, r * 2f, r * 2f);

        // Inner cutout circle shifted to the right to carve crescent
        float inR = r * 0.88f;
        float inX = cx + r * 0.42f;
        float inY = cy - r * 0.08f;
        using var pathInner = new GraphicsPath();
        pathInner.AddEllipse(inX - inR, inY - inR, inR * 2f, inR * 2f);

        using var reg = new Region(pathOuter);
        reg.Exclude(pathInner);
        g.FillRegion(brush, reg);

        // 4-Point Sparkle Star 1 (upper-right inside crescent cove)
        float s1X = cx + r * 0.72f;
        float s1Y = cy - r * 0.50f;
        DrawSparkleStar(g, brush, s1X, s1Y, r * 0.32f);

        // 4-Point Sparkle Star 2 (lower-right companion)
        float s2X = cx + r * 1.06f;
        float s2Y = cy + r * 0.26f;
        DrawSparkleStar(g, brush, s2X, s2Y, r * 0.20f);
    }

    private static void DrawSparkleStar(Graphics g, Brush brush, float cx, float cy, float r)
    {
        float ir = r * 0.22f;
        PointF[] pts = new[]
        {
            new PointF(cx, cy - r),
            new PointF(cx + ir, cy - ir),
            new PointF(cx + r, cy),
            new PointF(cx + ir, cy + ir),
            new PointF(cx, cy + r),
            new PointF(cx - ir, cy + ir),
            new PointF(cx - r, cy),
            new PointF(cx - ir, cy - ir)
        };
        g.FillPolygon(brush, pts);
    }

    private static void DrawPlayerIconButton(
        Graphics g,
        float superScale,
        float btnX,
        float btnY,
        float btnSize,
        float btnR,
        PlayerItem player,
        bool isHovered)
    {
        using var pathBtn = CreateRoundedRectanglePath(btnX, btnY, btnSize, btnSize, btnR);

        // Frosted glass button tile
        int bgAlpha = isHovered ? 68 : 36;
        using (var brushBg = new SolidBrush(Color.FromArgb(bgAlpha, 255, 255, 255)))
        {
            g.FillPath(brushBg, pathBtn);
        }

        int rimAlpha = isHovered ? 160 : 70;
        float rimThick = isHovered ? 1.2f * superScale : 1.0f * superScale;
        using (var penRim = new Pen(Color.FromArgb(rimAlpha, 255, 255, 255), rimThick))
        {
            g.DrawPath(penRim, pathBtn);
        }

        float cx = btnX + btnSize * 0.5f;
        float cy = btnY + btnSize * 0.5f;
        string iconType = (player.Icon ?? "").Trim().ToLowerInvariant();
        Bitmap? nativeIcon = PlayerService.GetPlayerIconBitmap(player, 128);

        if (nativeIcon != null)
        {
            const float nativeIconSize = 20f;
            float iconSize = nativeIconSize * superScale;
            float iconX = cx - iconSize * 0.5f;
            float iconY = cy - iconSize * 0.5f;
            var stateIcon = g.Save();
            g.SetClip(pathBtn);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(nativeIcon, new Rectangle((int)iconX, (int)iconY, (int)iconSize, (int)iconSize),
                0, 0, nativeIcon.Width, nativeIcon.Height, GraphicsUnit.Pixel);
            g.Restore(stateIcon);
        }
        else if (iconType == "spotify")
        {
            DrawSpotifyIcon(g, superScale, cx, cy, isHovered);
        }
        else if (iconType == "ytmusic" || iconType == "youtube" || iconType == "youtubemusic")
        {
            DrawYouTubeMusicIcon(g, superScale, cx, cy, isHovered);
        }
        else if (iconType == "applemusic" || iconType == "apple")
        {
            DrawAppleMusicIcon(g, superScale, cx, cy, isHovered);
        }
        else
        {
            DrawGenericMusicIcon(g, superScale, cx, cy, isHovered);
        }
    }

    private static void DrawSpotifyIcon(Graphics g, float superScale, float cx, float cy, bool isHovered)
    {
        float r = 9.0f * superScale;
        Color greenCol = isHovered ? Color.FromArgb(34, 215, 98) : Color.FromArgb(29, 185, 84);
        using (var brushGreen = new SolidBrush(greenCol))
        {
            g.FillEllipse(brushGreen, cx - r, cy - r, r * 2f, r * 2f);
        }

        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(-16f); // Authentic Spotify branding rotation

        Color waveCol = Color.FromArgb(20, 24, 28);
        float arcCenterY = 2.4f * superScale;

        // Top arc (longest)
        float r1 = 6.2f * superScale;
        using (var pen1 = new Pen(waveCol, 1.55f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen1, -r1, arcCenterY - r1, r1 * 2f, r1 * 2f, 216f, 108f);
        }

        // Middle arc
        float r2 = 4.6f * superScale;
        using (var pen2 = new Pen(waveCol, 1.35f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen2, -r2, arcCenterY - r2, r2 * 2f, r2 * 2f, 220f, 100f);
        }

        // Bottom arc (shortest)
        float r3 = 3.1f * superScale;
        using (var pen3 = new Pen(waveCol, 1.15f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen3, -r3, arcCenterY - r3, r3 * 2f, r3 * 2f, 224f, 92f);
        }

        g.Restore(state);
    }

    private static void DrawYouTubeMusicIcon(Graphics g, float superScale, float cx, float cy, bool isHovered)
    {
        float r = 9.0f * superScale;
        Color redCol = isHovered ? Color.FromArgb(255, 45, 75) : Color.FromArgb(255, 0, 0);
        using (var brushRed = new SolidBrush(redCol))
        {
            g.FillEllipse(brushRed, cx - r, cy - r, r * 2f, r * 2f);
        }

        // Concentric white ring
        float ringR = 5.2f * superScale;
        using var penRing = new Pen(Color.White, 1.25f * superScale);
        g.DrawEllipse(penRing, cx - ringR, cy - ringR, ringR * 2f, ringR * 2f);

        // Right-pointing play triangle in center
        PointF[] tri = new PointF[]
        {
            new PointF(cx - 1.8f * superScale, cy - 2.8f * superScale),
            new PointF(cx + 2.8f * superScale, cy),
            new PointF(cx - 1.8f * superScale, cy + 2.8f * superScale)
        };
        using var brushWhite = new SolidBrush(Color.White);
        g.FillPolygon(brushWhite, tri);
    }

    private static void DrawAppleMusicIcon(Graphics g, float superScale, float cx, float cy, bool isHovered)
    {
        float r = 9.0f * superScale;
        Color pinkCol = isHovered ? Color.FromArgb(255, 75, 95) : Color.FromArgb(252, 60, 68);
        using (var brushPink = new SolidBrush(pinkCol))
        {
            g.FillEllipse(brushPink, cx - r, cy - r, r * 2f, r * 2f);
        }

        using var fontNote = GetPremiumFont(9.0f * superScale, FontStyle.Bold);
        var noteSize = g.MeasureString("♫", fontNote, PointF.Empty, StringFormat.GenericTypographic);
        using var brushWhite = new SolidBrush(Color.White);
        g.DrawString("♫", fontNote, brushWhite, cx - noteSize.Width * 0.5f, cy - noteSize.Height * 0.5f, StringFormat.GenericTypographic);
    }

    private static void DrawGenericMusicIcon(Graphics g, float superScale, float cx, float cy, bool isHovered)
    {
        float r = 9.0f * superScale;
        int fillA = isHovered ? 110 : 80;
        using (var brushBg = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255)))
        {
            g.FillEllipse(brushBg, cx - r, cy - r, r * 2f, r * 2f);
        }
        using (var penRim = new Pen(Color.FromArgb(180, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawEllipse(penRim, cx - r, cy - r, r * 2f, r * 2f);
        }

        using var fontNote = GetPremiumFont(8.5f * superScale, FontStyle.Bold);
        var noteSize = g.MeasureString("♫", fontNote, PointF.Empty, StringFormat.GenericTypographic);
        using var brushWhite = new SolidBrush(Color.White);
        g.DrawString("♫", fontNote, brushWhite, cx - noteSize.Width * 0.5f, cy - noteSize.Height * 0.5f, StringFormat.GenericTypographic);
    }

    private static void DrawTabMusicContent(
        Graphics g,
        float superScale,
        int targetW,
        TrackInfo track,
        Bitmap? coverBmp,
        double progressSeconds,
        bool isPlaying,
        bool isShuffle,
        double rotationAngle,
        float[]? eqBarHeights,
        int hoveredButton,
        int clickedButton,
        double clickAnimProgress,
        int mediaSessionCount)
    {
        using var fontTitle = GetPremiumFont(14.0f * superScale, FontStyle.Bold);
        using var fontArtist = GetPremiumFont(9.5f * superScale, FontStyle.Regular);
        using var fontTime = GetPremiumFont(7.5f * superScale, FontStyle.Bold);

        float artX = 16f * superScale;
        float artY = 46f * superScale;
        float artSize = 54f * superScale;
        float artRadius = 12f * superScale;
        DrawRoundedSquareCover(g, artX, artY, artSize, artRadius, coverBmp, track.CoverAccentColor, rotationAngle, isPlaying, eqBarHeights);

        float textStartX = artX + artSize + 16f * superScale;
        float rightEdge = (targetW - 16f) * superScale;
        float barW = rightEdge - textStartX;
        float textRightEdge = (mediaSessionCount > 1 ? 414f : 444f) * superScale;

        float ctrlY = 120f * superScale;

        // Fluid expansion ease progress
        float audioP = (float)Math.Clamp(_audioPickerExpandP, 0.0, 1.0);
        float audioEase = audioP * audioP * (3f - 2f * audioP);

        float sleepP = (float)Math.Clamp(_musicSleepExpandP, 0.0, 1.0);
        float sleepEase = sleepP * sleepP * (3f - 2f * sleepP);

        float expandEase = Math.Max(audioEase, sleepEase);
        float restingAlpha = Math.Clamp(1f - expandEase * 2.2f, 0f, 1f);

        // -------------------------------------------------------------
        // RESTING TRACK INFO & TRANSPORT CONTROLS (FADES OUT AS CARD EXPANDS)
        // -------------------------------------------------------------
        if (restingAlpha > 0.01f)
        {
            using (var brushTitle = new SolidBrush(Color.FromArgb((int)(255 * restingAlpha), 255, 255, 255)))
            {
                using var titleFormat = new StringFormat(StringFormat.GenericDefault)
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(track.Title, fontTitle, brushTitle,
                    new RectangleF(textStartX, 44f * superScale, textRightEdge - textStartX, 18f * superScale), titleFormat);
            }

            using (var brushArtist = new SolidBrush(Color.FromArgb((int)(195 * restingAlpha), 255, 255, 255)))
            {
                using var artistFormat = new StringFormat(StringFormat.GenericDefault)
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(track.Artist, fontArtist, brushArtist,
                    new RectangleF(textStartX, 63f * superScale, textRightEdge - textStartX, 14f * superScale), artistFormat);
            }

            float barY = 86f * superScale;
            float barH = 2.5f * superScale;
            double progressRatio = (track.DurationSeconds > 0)
                ? Math.Clamp(progressSeconds / track.DurationSeconds, 0.0, 1.0)
                : 0.0;

            using (var penRail = new Pen(Color.FromArgb((int)(50 * restingAlpha), 255, 255, 255), barH) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(penRail, textStartX + barH * 0.5f, barY, rightEdge - barH * 0.5f, barY);
            }

            float fillEnd = textStartX + (float)(progressRatio * barW);
            if (fillEnd > textStartX + barH)
            {
                using var penFill = new Pen(Color.FromArgb((int)(250 * restingAlpha), 255, 255, 255), barH) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(penFill, textStartX + barH * 0.5f, barY, fillEnd, barY);
            }

            float beadR = 3.5f * superScale;
            using (var brushBead = new SolidBrush(Color.FromArgb((int)(255 * restingAlpha), 255, 255, 255)))
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
            using (var brushTime = new SolidBrush(Color.FromArgb((int)(165 * restingAlpha), 255, 255, 255)))
            {
                g.DrawString(elStr, fontTime, brushTime, textStartX, timeLabelY, StringFormat.GenericDefault);
                var remSize = g.MeasureString(remStr, fontTime, PointF.Empty, StringFormat.GenericDefault);
                g.DrawString(remStr, fontTime, brushTime, rightEdge - remSize.Width, timeLabelY, StringFormat.GenericDefault);
            }

            // Session selector sits above the progress rail. It only
            // appears when Windows exposes more than one controllable media session.
            if (mediaSessionCount > 1)
            {
                bool isSessionHovered = hoveredButton == BtnMediaSessionNext;
                DrawProjectedButtonContainer(g, 432f * superScale, 54f * superScale, 11f * superScale, 5.5f * superScale,
                    isSessionHovered, clickedButton == BtnMediaSessionNext, clickAnimProgress, superScale,
                    () =>
                    {
                        using var penArrow = new Pen(Color.FromArgb((int)(220 * restingAlpha), 255, 255, 255), 1.3f * superScale)
                        {
                            StartCap = LineCap.Round,
                            EndCap = LineCap.Round,
                            LineJoin = LineJoin.Round
                        };
                        float ax = 432f * superScale;
                        float ay = 54f * superScale;
                        g.DrawLine(penArrow, ax - 3.5f * superScale, ay - 3.0f * superScale, ax + 2.5f * superScale, ay);
                        g.DrawLine(penArrow, ax + 2.5f * superScale, ay, ax - 3.5f * superScale, ay + 3.0f * superScale);
                    }, restingAlpha);
            }

            // Left resting Audio Device button
            DrawProjectedButtonContainer(g, 98f * superScale, ctrlY, 15f * superScale, 7f * superScale,
                hoveredButton == BtnAudioDevice, clickedButton == BtnAudioDevice, clickAnimProgress, superScale,
                () => DrawAudioOutputGlyph(g, 98f * superScale, ctrlY, 13f * superScale, restingAlpha), restingAlpha);

            // Center transport controls stay minimal at rest. Their glass body
            // appears only on hover or during the click bounce.
            bool prevElevated = hoveredButton == BtnPrev || clickedButton == BtnPrev;
            if (prevElevated)
            {
                DrawProjectedButtonContainer(g, 217f * superScale, ctrlY, 17f * superScale, 8.5f * superScale,
                    hoveredButton == BtnPrev, clickedButton == BtnPrev, clickAnimProgress, superScale,
                    () => DrawTrackSkipGlyph(g, 217f * superScale, ctrlY, 13f * superScale, isNext: false, restingAlpha), restingAlpha);
            }
            else
            {
                DrawTrackSkipGlyph(g, 217f * superScale, ctrlY, 13f * superScale, isNext: false, restingAlpha);
            }

            bool playElevated = hoveredButton == BtnPlayPause || clickedButton == BtnPlayPause;
            if (playElevated)
            {
                DrawProjectedButtonContainer(g, 265f * superScale, ctrlY, 21f * superScale, 11f * superScale,
                    hoveredButton == BtnPlayPause, clickedButton == BtnPlayPause, clickAnimProgress, superScale,
                    () => DrawPlayPauseGlyph(g, 265f * superScale, ctrlY, 18f * superScale, isPlaying, restingAlpha), restingAlpha);
            }
            else
            {
                DrawPlayPauseGlyph(g, 265f * superScale, ctrlY, 18f * superScale, isPlaying, restingAlpha);
            }

            bool nextElevated = hoveredButton == BtnNext || clickedButton == BtnNext;
            if (nextElevated)
            {
                DrawProjectedButtonContainer(g, 313f * superScale, ctrlY, 17f * superScale, 8.5f * superScale,
                    hoveredButton == BtnNext, clickedButton == BtnNext, clickAnimProgress, superScale,
                    () => DrawTrackSkipGlyph(g, 313f * superScale, ctrlY, 13f * superScale, isNext: true, restingAlpha), restingAlpha);
            }
            else
            {
                DrawTrackSkipGlyph(g, 313f * superScale, ctrlY, 13f * superScale, isNext: true, restingAlpha);
            }

            // Right resting Sleep Timer button
            bool isHoveredSleep = hoveredButton == BtnSleepTimer;
            bool hasTimerValue = _sleepTimerActive && _sleepTimerTargetUtc != DateTime.MinValue && DateTime.UtcNow < _sleepTimerTargetUtc;
            bool showRemainingOnHover = hasTimerValue && isHoveredSleep;

            if (_sleepTimerActive)
            {
                float halfSize = 15f * superScale;
                float cornerRadius = 7f * superScale;
                float x = 432f * superScale - halfSize;
                float y = ctrlY - halfSize;
                float size = halfSize * 2f;

                using (var shadowPath = CreateRoundedRectanglePath(x, y + 2f * superScale, size, size, cornerRadius))
                using (var brushShadow = new SolidBrush(Color.FromArgb((int)(50 * restingAlpha), 0, 0, 0)))
                {
                    g.FillPath(brushShadow, shadowPath);
                }

                using (var bodyPath = CreateRoundedRectanglePath(x, y, size, size, cornerRadius))
                using (var brushBody = new SolidBrush(Color.FromArgb((int)(235 * restingAlpha), 255, 255, 255)))
                {
                    g.FillPath(brushBody, bodyPath);
                }

                if (showRemainingOnHover)
                {
                    string timeText;
                    if (DebugSleepTimerInSeconds)
                    {
                        int remainingSec = Math.Max(0, (int)Math.Ceiling((_sleepTimerTargetUtc - DateTime.UtcNow).TotalSeconds));
                        timeText = $"{remainingSec}s";
                    }
                    else
                    {
                        int remainingMin = Math.Max(1, (int)Math.Ceiling((_sleepTimerTargetUtc - DateTime.UtcNow).TotalMinutes));
                        timeText = $"{remainingMin}m";
                    }
                    using var fontCountdown = GetPremiumFont(8.5f * superScale, FontStyle.Bold);
                    var sz = g.MeasureString(timeText, fontCountdown, PointF.Empty, StringFormat.GenericTypographic);
                    using var brushDarkText = new SolidBrush(Color.FromArgb((int)(240 * restingAlpha), 25, 28, 35));
                    g.DrawString(timeText, fontCountdown, brushDarkText, 432f * superScale - sz.Width * 0.5f, ctrlY - sz.Height * 0.5f, StringFormat.GenericTypographic);
                }
                else
                {
                    using var brushDarkMoon = new SolidBrush(Color.FromArgb((int)(240 * restingAlpha), 25, 28, 35));
                    DrawMoonWithStars(g, brushDarkMoon, 432f * superScale, ctrlY, 7.5f * superScale);
                }
            }
            else
            {
                DrawProjectedButtonContainer(g, 432f * superScale, ctrlY, 15f * superScale, 7f * superScale,
                    isHoveredSleep, clickedButton == BtnSleepTimer, clickAnimProgress, superScale,
                    () =>
                    {
                        using var brushLightMoon = new SolidBrush(Color.FromArgb((int)(225 * restingAlpha), 255, 255, 255));
                        DrawMoonWithStars(g, brushLightMoon, 432f * superScale, ctrlY, 7.5f * superScale);
                    }, restingAlpha);
            }
        }

        // -------------------------------------------------------------
        // EXPANDED LIQUID GLASS PANEL (EXPANDS TO FILL RED-BOX BOUNDS)
        // -------------------------------------------------------------
        if (expandEase > 0.005f)
        {
            float curX, curY, curW, curH, curR;
            bool isAudioMode = audioP >= sleepP;

            if (isAudioMode)
            {
                // Expands from Audio Button on left (98, 120, size 30x30)
                float oX = 83f, oY = 105f, oW = 30f, oH = 30f, oR = 15f;
                float tX = 84f, tY = 38f, tW = 362f, tH = 100f, tR = 14f;
                curX = oX + (tX - oX) * audioEase;
                curY = oY + (tY - oY) * audioEase;
                curW = oW + (tW - oW) * audioEase;
                curH = oH + (tH - oH) * audioEase;
                curR = oR + (tR - oR) * audioEase;
            }
            else
            {
                // Expands from Sleep Timer Button on right (432, 120, size 30x30)
                float oX = 417f, oY = 105f, oW = 30f, oH = 30f, oR = 15f;
                float tX = 84f, tY = 38f, tW = 362f, tH = 100f, tR = 14f;
                curX = oX + (tX - oX) * sleepEase;
                curY = oY + (tY - oY) * sleepEase;
                curW = oW + (tW - oW) * sleepEase;
                curH = oH + (tH - oH) * sleepEase;
                curR = oR + (tR - oR) * sleepEase;
            }

            // 1. Soft physical contact drop shadow (elevates the glass card above the pill floor)
            using (var shadowPath = CreateRoundedRectanglePath(curX * superScale, (curY + 3.0f) * superScale, curW * superScale, curH * superScale, curR * superScale))
            using (var brushShadow = new SolidBrush(Color.FromArgb((int)(75 * Math.Min(1f, expandEase * 1.5f)), 0, 0, 0)))
            {
                g.FillPath(brushShadow, shadowPath);
            }

            // 2. Liquid Glass Card Body (Ultra-transparent crystal obsidian, NO milky white wash!)
            using var bodyPath = CreateRoundedRectanglePath(curX * superScale, curY * superScale, curW * superScale, curH * superScale, curR * superScale);

            // 2a. Deep translucent obsidian glass base (subtle dark tint for contrast, 100% free of frosted white haze)
            using (var brushBacking = new SolidBrush(Color.FromArgb((int)(20 * expandEase), 6, 8, 14)))
            {
                g.FillPath(brushBacking, bodyPath);
            }

            // 2b. Specular glossy surface sheen (subtle curved reflection across top 28% only)
            using (var brushGloss = new LinearGradientBrush(
                new RectangleF(curX * superScale, curY * superScale, curW * superScale, 28f * superScale),
                Color.FromArgb((int)(28 * expandEase), 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                90f))
            {
                var stateGloss = g.Save();
                g.SetClip(bodyPath);
                g.FillRectangle(brushGloss, curX * superScale, curY * superScale, curW * superScale, 28f * superScale);
                g.Restore(stateGloss);
            }

            // 2c. Specular Dual Rim Border (Top: sharp jewel-like reflection; Bottom: ambient glass edge)
            using (var brushRim = new LinearGradientBrush(
                new RectangleF(curX * superScale, curY * superScale, curW * superScale, curH * superScale),
                Color.FromArgb((int)(140 * expandEase), 255, 255, 255),
                Color.FromArgb((int)(28 * expandEase), 255, 255, 255),
                90f))
            using (var penRim = new Pen(brushRim, 1.0f * superScale))
            {
                g.DrawPath(penRim, bodyPath);
            }

            // 2d. Top Meniscus Crest Line (Liquid glass surface tension highlight)
            using (var penMeniscus = new Pen(Color.FromArgb((int)(110 * expandEase), 255, 255, 255), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(penMeniscus, (curX + curR * 0.75f) * superScale, (curY + 0.5f) * superScale, (curX + curW - curR * 0.75f) * superScale, (curY + 0.5f) * superScale);
            }

            // 3. Expanded Panel Content (Fades in as card expands)
            float contentAlpha = Math.Clamp((expandEase - 0.20f) / 0.80f, 0f, 1f);
            if (contentAlpha > 0.01f)
            {
                var stateContent = g.Save();
                g.SetClip(bodyPath);

                using var fontHeader = GetPremiumFont(9.5f * superScale, FontStyle.Bold);
                using var fontSub = GetPremiumFont(7.0f * superScale, FontStyle.Regular);
                using var brushTitle = new SolidBrush(Color.FromArgb((int)(250 * contentAlpha), 255, 255, 255));
                using var brushSub = new SolidBrush(Color.FromArgb((int)(160 * contentAlpha), 255, 255, 255));
                using var penDivider = new Pen(Color.FromArgb((int)(30 * contentAlpha), 255, 255, 255), 1.0f * superScale);

                if (isAudioMode)
                {
                    // === AUDIO OUTPUT PICKER CONTENT ===
                    DrawAudioOutputGlyph(g, 102f * superScale, 52f * superScale, 11f * superScale, contentAlpha);
                    var szAudioHdr = g.MeasureString("Audio Output", fontHeader, PointF.Empty, StringFormat.GenericTypographic);
                    g.DrawString("Audio Output", fontHeader, brushTitle, 116f * superScale, 45f * superScale, StringFormat.GenericTypographic);
                    g.DrawString("Select playback device", fontSub, brushSub, (116f + szAudioHdr.Width / superScale + 10f) * superScale, 47.5f * superScale, StringFormat.GenericTypographic);

                    // Close Button [✕]
                    float cX = 416f, cY = 43f, cW = 22f, cH = 22f, cR = 11f;
                    bool isCloseHov = _hoveredAudioBtn == AudioBtnClose;
                    using (var pathClose = CreateRoundedRectanglePath(cX * superScale, cY * superScale, cW * superScale, cH * superScale, cR * superScale))
                    {
                        using var brushClose = new SolidBrush(Color.FromArgb((int)((isCloseHov ? 60 : 25) * contentAlpha), 255, 255, 255));
                        g.FillPath(brushClose, pathClose);
                        using var penClose = new Pen(Color.FromArgb((int)((isCloseHov ? 100 : 50) * contentAlpha), 255, 255, 255), 1.0f * superScale);
                        g.DrawPath(penClose, pathClose);
                    }
                    using (var penX = new Pen(Color.FromArgb((int)(240 * contentAlpha), 255, 255, 255), 1.3f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        float xm = (cX + cW * 0.5f) * superScale;
                        float ym = (cY + cH * 0.5f) * superScale;
                        float d = 3.8f * superScale;
                        g.DrawLine(penX, xm - d, ym - d, xm + d, ym + d);
                        g.DrawLine(penX, xm + d, ym - d, xm - d, ym + d);
                    }

                    // Hairline Divider
                    g.DrawLine(penDivider, 96f * superScale, 64f * superScale, 434f * superScale, 64f * superScale);

                    // Device Cards Row
                    var devices = AudioDeviceManager.GetDevices();
                    var shown = devices.Count > 0 ? devices.Take(3).ToList() : new List<AudioDeviceInfo>
                    {
                        new AudioDeviceInfo { Name = "Speakers", ShortName = "Speakers", IsDefault = true },
                        new AudioDeviceInfo { Name = "Headphones", ShortName = "Headphones", IsDefault = false }
                    };

                    int count = Math.Min(3, shown.Count);
                    float gap = 8f;
                    float cardW = (338f - (count - 1) * gap) / count;
                    float cardH = 58f;
                    float cardY = 70f;
                    float cardR = 9f;

                    using var fontDevName = GetPremiumFont(8.5f * superScale, FontStyle.Bold);
                    using var fontDevSub = GetPremiumFont(6.8f * superScale, FontStyle.Regular);

                    for (int i = 0; i < count; i++)
                    {
                        float cardX = 96f + i * (cardW + gap);
                        bool isDevHovered = _hoveredAudioBtn == AudioBtnChip0 + i;
                        bool isDefault = shown[i].IsDefault;

                        using var pathCard = CreateRoundedRectanglePath(cardX * superScale, cardY * superScale, cardW * superScale, cardH * superScale, cardR * superScale);
                        if (isDefault)
                        {
                            // Active / Connected Device: Luminous liquid pearl glass tile with dark obsidian typography
                            using var brushActiveShadow = new SolidBrush(Color.FromArgb((int)(40 * contentAlpha), 0, 0, 0));
                            using var pathActiveShadow = CreateRoundedRectanglePath(cardX * superScale, (cardY + 1.5f) * superScale, cardW * superScale, cardH * superScale, cardR * superScale);
                            g.FillPath(brushActiveShadow, pathActiveShadow);

                            using var brushActive = new SolidBrush(Color.FromArgb((int)(238 * contentAlpha), 255, 255, 255));
                            g.FillPath(brushActive, pathCard);
                            using var penActive = new Pen(Color.FromArgb((int)(255 * contentAlpha), 255, 255, 255), 1.0f * superScale);
                            g.DrawPath(penActive, pathCard);

                            // Dark speaker glyph
                            DrawAudioOutputGlyph(g, (cardX + 17f) * superScale, (cardY + 29f) * superScale, 11f * superScale, contentAlpha, isDark: true);

                            using var brushDarkText = new SolidBrush(Color.FromArgb((int)(245 * contentAlpha), 18, 22, 28));
                            g.DrawString(shown[i].ShortName, fontDevName, brushDarkText, (cardX + 32f) * superScale, (cardY + 14f) * superScale, StringFormat.GenericTypographic);

                            // Badge: "✓ Active"
                            using var brushBadge = new SolidBrush(Color.FromArgb((int)(35 * contentAlpha), 0, 0, 0));
                            using var pathBadge = CreateRoundedRectanglePath((cardX + 32f) * superScale, (cardY + 34f) * superScale, 48f * superScale, 15f * superScale, 4f * superScale);
                            g.FillPath(brushBadge, pathBadge);
                            using var brushBadgeText = new SolidBrush(Color.FromArgb((int)(225 * contentAlpha), 18, 22, 28));
                            g.DrawString("✓ Active", fontDevSub, brushBadgeText, (cardX + 35f) * superScale, (cardY + 36f) * superScale, StringFormat.GenericTypographic);
                        }
                        else
                        {
                            // Inactive Device: Polished liquid glass tile (crystal depth, specular rim, no milky wash)
                            using var brushNorm = new SolidBrush(Color.FromArgb((int)((isDevHovered ? 28 : 12) * contentAlpha), 255, 255, 255));
                            g.FillPath(brushNorm, pathCard);

                            // Specular edge gradient pen
                            using var brushTileRim = new LinearGradientBrush(
                                new RectangleF(cardX * superScale, cardY * superScale, cardW * superScale, cardH * superScale),
                                Color.FromArgb((int)((isDevHovered ? 130 : 65) * contentAlpha), 255, 255, 255),
                                Color.FromArgb((int)((isDevHovered ? 45 : 18) * contentAlpha), 255, 255, 255),
                                90f);
                            using var penNorm = new Pen(brushTileRim, 1.0f * superScale);
                            g.DrawPath(penNorm, pathCard);

                            // Top edge hairline glint
                            using var penTileGlint = new Pen(Color.FromArgb((int)((isDevHovered ? 90 : 35) * contentAlpha), 255, 255, 255), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(penTileGlint, (cardX + cardR * 0.7f) * superScale, (cardY + 0.5f) * superScale, (cardX + cardW - cardR * 0.7f) * superScale, (cardY + 0.5f) * superScale);

                            // Light speaker glyph
                            DrawAudioOutputGlyph(g, (cardX + 17f) * superScale, (cardY + 29f) * superScale, 11f * superScale, contentAlpha, isDark: false);

                            using var brushLightText = new SolidBrush(Color.FromArgb((int)(240 * contentAlpha), 255, 255, 255));
                            g.DrawString(shown[i].ShortName, fontDevName, brushLightText, (cardX + 32f) * superScale, (cardY + 14f) * superScale, StringFormat.GenericTypographic);

                            using var brushSubText = new SolidBrush(Color.FromArgb((int)(160 * contentAlpha), 255, 255, 255));
                            g.DrawString("Tap to connect", fontDevSub, brushSubText, (cardX + 32f) * superScale, (cardY + 35f) * superScale, StringFormat.GenericTypographic);
                        }
                    }
                }
                else
                {
                    // === SLEEP TIMER PICKER CONTENT ===
                    using var brushLightMoon = new SolidBrush(Color.FromArgb((int)(240 * contentAlpha), 255, 255, 255));
                    DrawMoonWithStars(g, brushLightMoon, 102f * superScale, 52f * superScale, 7.5f * superScale);

                    g.DrawString("Sleep Timer", fontHeader, brushTitle, 116f * superScale, 45f * superScale, StringFormat.GenericTypographic);

                    // Close Button [✕]
                    float cX = 416f, cY = 43f, cW = 22f, cH = 22f, cR = 11f;
                    bool isCloseHov = _hoveredMusicSleepBtn == MusicSleepBtnClose;
                    using (var pathClose = CreateRoundedRectanglePath(cX * superScale, cY * superScale, cW * superScale, cH * superScale, cR * superScale))
                    {
                        using var brushClose = new SolidBrush(Color.FromArgb((int)((isCloseHov ? 75 : 35) * contentAlpha), 255, 255, 255));
                        g.FillPath(brushClose, pathClose);
                        using var penClose = new Pen(Color.FromArgb((int)(70 * contentAlpha), 255, 255, 255), 1.0f * superScale);
                        g.DrawPath(penClose, pathClose);
                    }
                    using (var penX = new Pen(Color.FromArgb((int)(240 * contentAlpha), 255, 255, 255), 1.3f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        float xm = (cX + cW * 0.5f) * superScale;
                        float ym = (cY + cH * 0.5f) * superScale;
                        float d = 3.8f * superScale;
                        g.DrawLine(penX, xm - d, ym - d, xm + d, ym + d);
                        g.DrawLine(penX, xm + d, ym - d, xm - d, ym + d);
                    }

                    // Hairline Divider
                    g.DrawLine(penDivider, 96f * superScale, 64f * superScale, 434f * superScale, 64f * superScale);

                    // Body Area
                    if (_sleepTimerActive && _sleepTimerTargetUtc > DateTime.UtcNow)
                    {
                        // === ACTIVE COUNTDOWN DISPLAY ===
                        TimeSpan rem = _sleepTimerTargetUtc - DateTime.UtcNow;
                        string clockText;
                        if (DebugSleepTimerInSeconds)
                        {
                            clockText = $"{(int)Math.Max(0, rem.TotalSeconds)}s";
                        }
                        else
                        {
                            int remMin = Math.Max(0, (int)rem.TotalMinutes);
                            int remSec = Math.Max(0, rem.Seconds);
                            clockText = $"{remMin}:{remSec:D2}";
                        }

                        // Left card: Digital countdown clock (Liquid glass crystal container)
                        float cLeftX = 96f, cLeftY = 70f, cLeftW = 154f, cLeftH = 58f, cLeftR = 9f;
                        using (var pathLeft = CreateRoundedRectanglePath(cLeftX * superScale, cLeftY * superScale, cLeftW * superScale, cLeftH * superScale, cLeftR * superScale))
                        {
                            using var brushLeft = new SolidBrush(Color.FromArgb((int)(16 * contentAlpha), 255, 255, 255));
                            g.FillPath(brushLeft, pathLeft);
                            using var brushClockRim = new LinearGradientBrush(
                                new RectangleF(cLeftX * superScale, cLeftY * superScale, cLeftW * superScale, cLeftH * superScale),
                                Color.FromArgb((int)(80 * contentAlpha), 255, 255, 255),
                                Color.FromArgb((int)(22 * contentAlpha), 255, 255, 255),
                                90f);
                            using var penLeft = new Pen(brushClockRim, 1.0f * superScale);
                            g.DrawPath(penLeft, pathLeft);

                            using var penClockGlint = new Pen(Color.FromArgb((int)(55 * contentAlpha), 255, 255, 255), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(penClockGlint, (cLeftX + cLeftR * 0.7f) * superScale, (cLeftY + 0.5f) * superScale, (cLeftX + cLeftW - cLeftR * 0.7f) * superScale, (cLeftY + 0.5f) * superScale);
                        }

                        using var fontClock = GetPremiumFont(17.5f * superScale, FontStyle.Bold);
                        var szClock = g.MeasureString(clockText, fontClock, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString(clockText, fontClock, brushTitle, (cLeftX + 16f) * superScale, (cLeftY + 8f) * superScale, StringFormat.GenericTypographic);

                        using var fontClockLabel = GetPremiumFont(6.5f * superScale, FontStyle.Regular);
                        using var brushClockLabel = new SolidBrush(Color.FromArgb((int)(160 * contentAlpha), 255, 255, 255));
                        g.DrawString("REMAINING TIME", fontClockLabel, brushClockLabel, (cLeftX + 16f) * superScale, (cLeftY + 36f) * superScale, StringFormat.GenericTypographic);

                        // Mini progress rail
                        double totalSec = DebugSleepTimerInSeconds ? _sleepTimerDurationMinutes : (_sleepTimerDurationMinutes * 60.0);
                        double elapsedRatio = totalSec > 0 ? Math.Clamp(1.0 - (rem.TotalSeconds / totalSec), 0.0, 1.0) : 0.0;
                        float pRailX = cLeftX + 16f, pRailY = cLeftY + 47f, pRailW = cLeftW - 32f;
                        using (var penPRail = new Pen(Color.FromArgb((int)(40 * contentAlpha), 255, 255, 255), 2.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            g.DrawLine(penPRail, pRailX * superScale, pRailY * superScale, (pRailX + pRailW) * superScale, pRailY * superScale);
                        }
                        if (elapsedRatio > 0.01)
                        {
                            using var penPFill = new Pen(Color.FromArgb((int)(220 * contentAlpha), 255, 255, 255), 2.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(penPFill, pRailX * superScale, pRailY * superScale, (pRailX + (float)(pRailW * elapsedRatio)) * superScale, pRailY * superScale);
                        }

                        // Right buttons: [+5m] at [260, 314], [+15m] at [320, 374], [✕ Stop] at [380, 434]
                        float bH = 58f, bR = 9f;

                        // [+5m]
                        bool isAdd5Hov = _hoveredMusicSleepBtn == MusicSleepBtnAdd5m;
                        using (var pathAdd5 = CreateRoundedRectanglePath(260f * superScale, 70f * superScale, 54f * superScale, bH * superScale, bR * superScale))
                        {
                            using var brushAdd5 = new SolidBrush(Color.FromArgb((int)((isAdd5Hov ? 28 : 12) * contentAlpha), 255, 255, 255));
                            g.FillPath(brushAdd5, pathAdd5);
                            using var brushAdd5Rim = new LinearGradientBrush(
                                new RectangleF(260f * superScale, 70f * superScale, 54f * superScale, bH * superScale),
                                Color.FromArgb((int)((isAdd5Hov ? 130 : 65) * contentAlpha), 255, 255, 255),
                                Color.FromArgb((int)((isAdd5Hov ? 45 : 18) * contentAlpha), 255, 255, 255),
                                90f);
                            using var penAdd5 = new Pen(brushAdd5Rim, 1.0f * superScale);
                            g.DrawPath(penAdd5, pathAdd5);

                            using var penGlint = new Pen(Color.FromArgb((int)((isAdd5Hov ? 90 : 35) * contentAlpha), 255, 255, 255), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(penGlint, (260f + bR * 0.7f) * superScale, 70.5f * superScale, (260f + 54f - bR * 0.7f) * superScale, 70.5f * superScale);
                        }
                        using var fontAdjNum = GetPremiumFont(11.5f * superScale, FontStyle.Bold);
                        using var fontAdjSub = GetPremiumFont(6.2f * superScale, FontStyle.Regular);
                        string lblAdd5 = DebugSleepTimerInSeconds ? "+5s" : "+5m";
                        var szA5 = g.MeasureString(lblAdd5, fontAdjNum, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString(lblAdd5, fontAdjNum, brushTitle, (260f + (54f - szA5.Width / superScale) * 0.5f) * superScale, 80f * superScale, StringFormat.GenericTypographic);
                        var szA5Sub = g.MeasureString("ADD", fontAdjSub, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString("ADD", fontAdjSub, brushClockLabel, (260f + (54f - szA5Sub.Width / superScale) * 0.5f) * superScale, 104f * superScale, StringFormat.GenericTypographic);

                        // [+15m]
                        bool isAdd15Hov = _hoveredMusicSleepBtn == MusicSleepBtn30m;
                        using (var pathAdd15 = CreateRoundedRectanglePath(320f * superScale, 70f * superScale, 54f * superScale, bH * superScale, bR * superScale))
                        {
                            using var brushAdd15 = new SolidBrush(Color.FromArgb((int)((isAdd15Hov ? 28 : 12) * contentAlpha), 255, 255, 255));
                            g.FillPath(brushAdd15, pathAdd15);
                            using var brushAdd15Rim = new LinearGradientBrush(
                                new RectangleF(320f * superScale, 70f * superScale, 54f * superScale, bH * superScale),
                                Color.FromArgb((int)((isAdd15Hov ? 130 : 65) * contentAlpha), 255, 255, 255),
                                Color.FromArgb((int)((isAdd15Hov ? 45 : 18) * contentAlpha), 255, 255, 255),
                                90f);
                            using var penAdd15 = new Pen(brushAdd15Rim, 1.0f * superScale);
                            g.DrawPath(penAdd15, pathAdd15);

                            using var penGlint = new Pen(Color.FromArgb((int)((isAdd15Hov ? 90 : 35) * contentAlpha), 255, 255, 255), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(penGlint, (320f + bR * 0.7f) * superScale, 70.5f * superScale, (320f + 54f - bR * 0.7f) * superScale, 70.5f * superScale);
                        }
                        string lblAdd15 = DebugSleepTimerInSeconds ? "+15s" : "+15m";
                        var szA15 = g.MeasureString(lblAdd15, fontAdjNum, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString(lblAdd15, fontAdjNum, brushTitle, (320f + (54f - szA15.Width / superScale) * 0.5f) * superScale, 80f * superScale, StringFormat.GenericTypographic);
                        var szA15Sub = g.MeasureString("EXTEND", fontAdjSub, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString("EXTEND", fontAdjSub, brushClockLabel, (320f + (54f - szA15Sub.Width / superScale) * 0.5f) * superScale, 104f * superScale, StringFormat.GenericTypographic);

                        // [✕ Stop] (Translucent Ruby Glass Tile)
                        bool isStopBodyHov = _hoveredMusicSleepBtn == MusicSleepBtnCancel;
                        using (var pathStopBody = CreateRoundedRectanglePath(380f * superScale, 70f * superScale, 54f * superScale, bH * superScale, bR * superScale))
                        {
                            using var brushStopBody = new SolidBrush(Color.FromArgb((int)((isStopBodyHov ? 55 : 24) * contentAlpha), 255, 50, 50));
                            g.FillPath(brushStopBody, pathStopBody);
                            using var brushStopRim = new LinearGradientBrush(
                                new RectangleF(380f * superScale, 70f * superScale, 54f * superScale, bH * superScale),
                                Color.FromArgb((int)((isStopBodyHov ? 140 : 75) * contentAlpha), 255, 90, 90),
                                Color.FromArgb((int)((isStopBodyHov ? 60 : 30) * contentAlpha), 200, 40, 40),
                                90f);
                            using var penStopBody = new Pen(brushStopRim, 1.0f * superScale);
                            g.DrawPath(penStopBody, pathStopBody);

                            using var penGlint = new Pen(Color.FromArgb((int)((isStopBodyHov ? 110 : 50) * contentAlpha), 255, 140, 140), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLine(penGlint, (380f + bR * 0.7f) * superScale, 70.5f * superScale, (380f + 54f - bR * 0.7f) * superScale, 70.5f * superScale);
                        }
                        using var brushStopTextB = new SolidBrush(Color.FromArgb((int)(250 * contentAlpha), 255, 200, 200));
                        var szStop = g.MeasureString("✕", fontAdjNum, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString("✕", fontAdjNum, brushStopTextB, (380f + (54f - szStop.Width / superScale) * 0.5f) * superScale, 80f * superScale, StringFormat.GenericTypographic);
                        var szStopSub = g.MeasureString("STOP", fontAdjSub, PointF.Empty, StringFormat.GenericTypographic);
                        g.DrawString("STOP", fontAdjSub, brushStopTextB, (380f + (54f - szStopSub.Width / superScale) * 0.5f) * superScale, 104f * superScale, StringFormat.GenericTypographic);
                    }
                    else
                    {
                        // === PRESET DURATION SELECTION CARDS (15, 30, 45, 60) ===
                        int[] presets = new int[] { 15, 30, 45, 60 };
                        int[] ids = new int[] { MusicSleepBtn15m, MusicSleepBtn30m, MusicSleepBtn45m, MusicSleepBtn60m };
                        float gap = 8f;
                        float cardW = (338f - 3 * gap) / 4f;
                        float cardH = 58f;
                        float cardY = 70f;
                        float cardR = 9f;

                        using var fontNum = GetPremiumFont(14.5f * superScale, FontStyle.Bold);
                        using var fontUnit = GetPremiumFont(6.5f * superScale, FontStyle.Regular);
                        string unitStr = DebugSleepTimerInSeconds ? "SECONDS" : "MINUTES";

                        for (int j = 0; j < 4; j++)
                        {
                            float cardX = 96f + j * (cardW + gap);
                            bool isHovered = _hoveredMusicSleepBtn == ids[j];
                            bool isCurrentVal = _sleepTimerActive && _sleepTimerDurationMinutes == presets[j];

                            using var pathPreset = CreateRoundedRectanglePath(cardX * superScale, cardY * superScale, cardW * superScale, cardH * superScale, cardR * superScale);
                            if (isCurrentVal)
                            {
                                // Active preset: Luminous liquid pearl tile
                                using var brushActiveShadow = new SolidBrush(Color.FromArgb((int)(40 * contentAlpha), 0, 0, 0));
                                using var pathActiveShadow = CreateRoundedRectanglePath(cardX * superScale, (cardY + 1.5f) * superScale, cardW * superScale, cardH * superScale, cardR * superScale);
                                g.FillPath(brushActiveShadow, pathActiveShadow);

                                using var brushActive = new SolidBrush(Color.FromArgb((int)(238 * contentAlpha), 255, 255, 255));
                                g.FillPath(brushActive, pathPreset);
                                using var penActive = new Pen(Color.FromArgb((int)(255 * contentAlpha), 255, 255, 255), 1.0f * superScale);
                                g.DrawPath(penActive, pathPreset);

                                using var brushDark = new SolidBrush(Color.FromArgb((int)(245 * contentAlpha), 18, 22, 28));
                                string num = presets[j].ToString();
                                var szN = g.MeasureString(num, fontNum, PointF.Empty, StringFormat.GenericTypographic);
                                g.DrawString(num, fontNum, brushDark, (cardX + (cardW - szN.Width / superScale) * 0.5f) * superScale, 77f * superScale, StringFormat.GenericTypographic);

                                var szU = g.MeasureString(unitStr, fontUnit, PointF.Empty, StringFormat.GenericTypographic);
                                g.DrawString(unitStr, fontUnit, brushDark, (cardX + (cardW - szU.Width / superScale) * 0.5f) * superScale, 104f * superScale, StringFormat.GenericTypographic);
                            }
                            else
                            {
                                // Inactive preset: Polished liquid glass tile (crystal depth, specular rim, no milky wash)
                                using var brushCard = new SolidBrush(Color.FromArgb((int)((isHovered ? 28 : 12) * contentAlpha), 255, 255, 255));
                                g.FillPath(brushCard, pathPreset);

                                using var brushPresetRim = new LinearGradientBrush(
                                    new RectangleF(cardX * superScale, cardY * superScale, cardW * superScale, cardH * superScale),
                                    Color.FromArgb((int)((isHovered ? 130 : 65) * contentAlpha), 255, 255, 255),
                                    Color.FromArgb((int)((isHovered ? 45 : 18) * contentAlpha), 255, 255, 255),
                                    90f);
                                using var penCard = new Pen(brushPresetRim, 1.0f * superScale);
                                g.DrawPath(penCard, pathPreset);

                                using var penTileGlint = new Pen(Color.FromArgb((int)((isHovered ? 90 : 35) * contentAlpha), 255, 255, 255), 1.0f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                                g.DrawLine(penTileGlint, (cardX + cardR * 0.7f) * superScale, (cardY + 0.5f) * superScale, (cardX + cardW - cardR * 0.7f) * superScale, (cardY + 0.5f) * superScale);

                                string num = presets[j].ToString();
                                var szN = g.MeasureString(num, fontNum, PointF.Empty, StringFormat.GenericTypographic);
                                g.DrawString(num, fontNum, brushTitle, (cardX + (cardW - szN.Width / superScale) * 0.5f) * superScale, 77f * superScale, StringFormat.GenericTypographic);

                                using var brushUnit = new SolidBrush(Color.FromArgb((int)(160 * contentAlpha), 255, 255, 255));
                                var szU = g.MeasureString(unitStr, fontUnit, PointF.Empty, StringFormat.GenericTypographic);
                                g.DrawString(unitStr, fontUnit, brushUnit, (cardX + (cardW - szU.Width / superScale) * 0.5f) * superScale, 104f * superScale, StringFormat.GenericTypographic);
                            }
                        }
                    }
                }

                g.Restore(stateContent);
            }
        }
    }

    private static void DrawTabWeatherContent(
        Graphics g,
        float superScale,
        int targetW)
    {
        DrawTabWeatherCard(g, superScale, targetW, CurrentWeatherCondition);
    }

    private static Color GetWeatherAccent(int condition) => condition switch
    {
        1 or 3 => Color.FromArgb(238, 190, 112),
        2 or 4 => Color.FromArgb(177, 177, 222),
        6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 15 => Color.FromArgb(145, 190, 212),
        14 => Color.FromArgb(225, 157, 119),
        _ => Color.FromArgb(177, 190, 201)
    };

    private static void DrawWeatherArtwork(Graphics g, float cx, float cy, float size, int condition, Color accent)
    {
        // A softly lit, dimensional weather mark gives the card a visual focal point
        // without competing with the live temperature and forecast.
        float halo = size * 0.48f;
        using (var glow = new PathGradientBrush(new[] {
                   new PointF(cx - halo, cy), new PointF(cx, cy - halo),
                   new PointF(cx + halo, cy), new PointF(cx, cy + halo) }))
        {
            glow.CenterPoint = new PointF(cx, cy);
            glow.CenterColor = Color.FromArgb(85, accent);
            glow.SurroundColors = new[] { Color.FromArgb(0, accent) };
            g.FillEllipse(glow, cx - halo, cy - halo, halo * 2f, halo * 2f);
        }

        // Special Condition 14: Golden Sunset / Dusk (Sinking sun over horizon)
        if (condition == 14)
        {
            float sunDuskR = size * 0.22f;
            float horizonY = cy + size * 0.08f;
            using (var duskBrush = new LinearGradientBrush(
                new RectangleF(cx - sunDuskR, horizonY - sunDuskR, sunDuskR * 2f, sunDuskR * 2f),
                Color.FromArgb(255, 255, 205, 116), Color.FromArgb(255, 235, 110, 68), 90f))
            {
                g.FillPie(duskBrush, cx - sunDuskR, horizonY - sunDuskR, sunDuskR * 2f, sunDuskR * 2f, 180, 180);
            }
            using (var horizonPen = new Pen(Color.FromArgb(235, 255, 180, 120), Math.Max(1.4f, size * 0.032f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(horizonPen, cx - size * 0.35f, horizonY, cx + size * 0.35f, horizonY);
            }
            using (var rayPen = new Pen(Color.FromArgb(160, 255, 200, 140), Math.Max(1.1f, size * 0.024f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(rayPen, cx - size * 0.20f, horizonY + size * 0.12f, cx + size * 0.20f, horizonY + size * 0.12f);
            }
            return;
        }

        // Special Condition 15: Windy / Gale / Squall (Sweeping aerodynamic streamlines)
        if (condition == 15)
        {
            using var penWind = new Pen(Color.FromArgb(225, accent), Math.Max(1.4f, size * 0.032f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(penWind, cx - size * 0.34f, cy - size * 0.14f, cx + size * 0.26f, cy - size * 0.14f);
            g.DrawLine(penWind, cx - size * 0.22f, cy + size * 0.06f, cx + size * 0.36f, cy + size * 0.06f);
            g.DrawLine(penWind, cx - size * 0.30f, cy + size * 0.26f, cx + size * 0.18f, cy + size * 0.26f);
            return;
        }

        bool night = condition is 2 or 4;
        bool sunny = condition is 1 or 3;
        bool storm = condition is 12 or 13;
        float sunR = size * 0.22f;
        float sunX = cx + (sunny || night ? -size * 0.08f : 0f);
        float sunY = cy - size * 0.08f;

        if (night)
        {
            using var moon = new SolidBrush(Color.FromArgb(245, 239, 229, 194));
            g.FillEllipse(moon, sunX - sunR, sunY - sunR, sunR * 2f, sunR * 2f);
            using var cut = new SolidBrush(Color.FromArgb(232, 35, 42, 53));
            g.FillEllipse(cut, sunX - sunR * 0.25f, sunY - sunR * 1.12f, sunR * 1.9f, sunR * 1.9f);
        }
        else if (sunny)
        {
            using var sunGlow = new SolidBrush(Color.FromArgb(48, 255, 205, 116));
            using var sun = new LinearGradientBrush(
                new RectangleF(sunX - sunR, sunY - sunR, sunR * 2f, sunR * 2f),
                Color.FromArgb(255, 255, 232, 166), Color.FromArgb(255, 244, 169, 94), 45f);
            g.FillEllipse(sunGlow, sunX - sunR * 1.55f, sunY - sunR * 1.55f, sunR * 3.1f, sunR * 3.1f);
            g.FillEllipse(sun, sunX - sunR, sunY - sunR, sunR * 2f, sunR * 2f);
        }

        // Only pure Sunny (1) and Clear Night (2) return early without drawing cloud
        if (condition is 1 or 2) return;

        float cloudW = size * 0.78f;
        float cloudH = size * 0.39f;
        float cloudX = cx - cloudW * 0.5f + size * 0.05f;
        float cloudY = cy - cloudH * 0.18f;
        using var cloudPath = new GraphicsPath();
        cloudPath.StartFigure();
        cloudPath.AddBezier(cloudX + cloudW * 0.13f, cloudY + cloudH * 0.78f,
            cloudX - cloudW * 0.02f, cloudY + cloudH * 0.58f,
            cloudX + cloudW * 0.08f, cloudY + cloudH * 0.25f,
            cloudX + cloudW * 0.30f, cloudY + cloudH * 0.28f);
        cloudPath.AddBezier(cloudX + cloudW * 0.30f, cloudY + cloudH * 0.28f,
            cloudX + cloudW * 0.36f, cloudY - cloudH * 0.06f,
            cloudX + cloudW * 0.73f, cloudY + cloudH * 0.02f,
            cloudX + cloudW * 0.76f, cloudY + cloudH * 0.39f);
        cloudPath.AddBezier(cloudX + cloudW * 0.76f, cloudY + cloudH * 0.39f,
            cloudX + cloudW * 1.04f, cloudY + cloudH * 0.39f,
            cloudX + cloudW * 1.02f, cloudY + cloudH * 0.86f,
            cloudX + cloudW * 0.78f, cloudY + cloudH * 0.87f);
        cloudPath.AddLine(cloudX + cloudW * 0.20f, cloudY + cloudH * 0.87f,
                          cloudX + cloudW * 0.78f, cloudY + cloudH * 0.87f);
        cloudPath.CloseFigure();
        using (var cloudFill = new LinearGradientBrush(
                   new PointF(cloudX, cloudY), new PointF(cloudX, cloudY + cloudH),
                   Color.FromArgb(250, 246, 249, 252), Color.FromArgb(225, 177, 197, 215)))
            g.FillPath(cloudFill, cloudPath);
        using (var cloudRim = new Pen(Color.FromArgb(135, 255, 255, 255), Math.Max(1f, size * 0.018f)))
            g.DrawPath(cloudRim, cloudPath);

        // Partly cloudy day (3), partly cloudy night (4), and overcast (5) have no precipitation
        if (condition is 3 or 4 or 5) return;

        // Condition 6: Fog & Mist (Horizontal mist scanlines)
        if (condition == 6)
        {
            using var penFog = new Pen(Color.FromArgb(190, accent), Math.Max(1.2f, size * 0.026f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(penFog, cx - size * 0.28f, cy + size * 0.33f, cx + size * 0.24f, cy + size * 0.33f);
            g.DrawLine(penFog, cx - size * 0.18f, cy + size * 0.44f, cx + size * 0.32f, cy + size * 0.44f);
            g.DrawLine(penFog, cx - size * 0.24f, cy + size * 0.55f, cx + size * 0.14f, cy + size * 0.55f);
            return;
        }

        using var weatherStroke = new Pen(Color.FromArgb(225, accent), Math.Max(1.4f, size * 0.035f))
        { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (storm)
        {
            using var bolt = new SolidBrush(Color.FromArgb(255, 255, 218, 139));
            PointF[] points = { new(cx - size * 0.02f, cy + size * 0.16f),
                new(cx - size * 0.13f, cy + size * 0.40f), new(cx - size * 0.01f, cy + size * 0.37f),
                new(cx - size * 0.08f, cy + size * 0.59f), new(cx + size * 0.13f, cy + size * 0.29f),
                new(cx + size * 0.02f, cy + size * 0.31f), new(cx + size * 0.10f, cy + size * 0.16f) };
            g.FillPolygon(bolt, points);
        }
        else if (condition is 10 or 11)
        {
            using var snow = new Pen(Color.FromArgb(228, 226, 242, 255), Math.Max(1.2f, size * 0.025f));
            foreach (float ox in new[] { -0.15f, 0.12f })
            {
                float sx = cx + size * ox, sy = cy + size * 0.44f, r = size * 0.075f;
                g.DrawLine(snow, sx - r, sy, sx + r, sy); g.DrawLine(snow, sx, sy - r, sx, sy + r);
                g.DrawLine(snow, sx - r * .7f, sy - r * .7f, sx + r * .7f, sy + r * .7f);
                g.DrawLine(snow, sx - r * .7f, sy + r * .7f, sx + r * .7f, sy - r * .7f);
            }
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                float rx = cx + size * (-0.17f + i * 0.16f);
                g.DrawLine(weatherStroke, rx, cy + size * 0.34f, rx - size * 0.055f, cy + size * 0.51f);
            }
        }
    }

    private static void DrawHomeWeatherCard(
        Graphics g, float scale, float x, float y, float width, float height,
        GraphicsPath clip, int condition)
    {
        WeatherModel weather = IsLiveWeatherMode
            ? LiveWeatherService.Current
            : LiveWeatherService.GetMockWeather(condition);
        Color accent = GetWeatherAccent(condition);

        using (var panel = new LinearGradientBrush(
                   new PointF(x, y), new PointF(x + width, y + height),
                   Color.FromArgb(_hoveredHomeWeather ? 76 : 58, 35, 41, 51),
                   Color.FromArgb(_hoveredHomeWeather ? 92 : 70, 17, 22, 30)))
            g.FillPath(panel, clip);

        DrawWeatherArtwork(g, x + width - 27f * scale, y + height * 0.53f, 44f * scale, condition, accent);

        float left = x + 12f * scale;
        using var fontMeta = GetPremiumFont(6.2f * scale, FontStyle.Bold);
        using var fontTemp = GetPremiumFont(18f * scale, FontStyle.Bold);
        using var fontCondition = GetPremiumFont(7.6f * scale, FontStyle.Bold);
        using var fontSummary = GetPremiumFont(6.1f * scale, FontStyle.Regular);
        using var primary = new SolidBrush(Color.FromArgb(248, 246, 247, 249));
        using var secondary = new SolidBrush(Color.FromArgb(174, 205, 212, 222));
        using var accentBrush = new SolidBrush(accent);
        var format = StringFormat.GenericTypographic;

        string location = weather.City.Trim().ToUpperInvariant();
        string meta = $"{DateTime.Now:ddd}  ·  {location}";
        g.DrawString(meta, fontMeta, secondary, left, y + 4.5f * scale, format);

        float tempY = y + 13f * scale;
        g.DrawString(weather.FormattedTemp, fontTemp, primary, left, tempY, format);
        float tempWidth = g.MeasureString(weather.FormattedTemp, fontTemp, PointF.Empty, format).Width;
        float detailX = left + tempWidth + 9f * scale;
        g.DrawString(weather.ConditionName, fontCondition, accentBrush, detailX, y + 15f * scale, format);
        g.DrawString(weather.Subtitle, fontSummary, secondary, detailX, y + 26f * scale, format);

        if (_hoveredHomeWeather)
        {
            using var arrowFont = GetPremiumFont(11f * scale, FontStyle.Regular);
            using var arrowBrush = new SolidBrush(Color.FromArgb(210, 244, 246, 249));
            g.DrawString("›", arrowFont, arrowBrush, x + width - 13f * scale,
                         y + height * 0.5f - 7f * scale, StringFormat.GenericDefault);
        }
    }

    private static void DrawTabWeatherCard(Graphics g, float scale, int targetWidth, int condition)
    {
        WeatherModel weather = IsLiveWeatherMode
            ? LiveWeatherService.Current
            : LiveWeatherService.GetMockWeather(condition);
        Color accent = GetWeatherAccent(condition);

        float x = 22f * scale;
        float y = 34f * scale;
        float width = targetWidth * scale - 44f * scale;
        float height = 104f * scale;
        float radius = 13f * scale;
        using var cardPath = CreateRoundedRectPath(x, y, width, height, radius, radius, radius, radius);
        using (var background = new LinearGradientBrush(
                   new PointF(x, y), new PointF(x + width, y + height),
                   Color.FromArgb(206, 30, 36, 45), Color.FromArgb(194, 19, 24, 32)))
            g.FillPath(background, cardPath);
        DrawWeatherArtwork(g, x + width - 37f * scale, y + 43f * scale, 58f * scale, condition, accent);
        using (var border = new Pen(Color.FromArgb(42, 255, 255, 255), 1f * scale))
            g.DrawPath(border, cardPath);

        float inset = 14f * scale;
        using var format = new StringFormat(StringFormat.GenericTypographic);
        using var locationFont = GetPremiumFont(8f * scale, FontStyle.Bold);
        using var dateFont = GetPremiumFont(7.2f * scale, FontStyle.Regular);
        using var tempFont = GetPremiumFont(31f * scale, FontStyle.Bold);
        using var conditionFont = GetPremiumFont(11f * scale, FontStyle.Bold);
        using var detailFont = GetPremiumFont(8f * scale, FontStyle.Regular);
        using var metricLabelFont = GetPremiumFont(6.7f * scale, FontStyle.Bold);
        using var metricValueFont = GetPremiumFont(9.2f * scale, FontStyle.Bold);
        using var primary = new SolidBrush(Color.FromArgb(250, 247, 248, 250));
        using var secondary = new SolidBrush(Color.FromArgb(164, 201, 208, 219));
        using var accentBrush = new SolidBrush(accent);

        string city = weather.City.Trim().ToUpperInvariant();
        g.FillEllipse(accentBrush, x + inset, y + 11f * scale, 5f * scale, 5f * scale);
        g.DrawString(city, locationFont, primary, x + inset + 10f * scale, y + 9f * scale, format);

        string date = DateTime.Now.ToString("dddd, MMM d", CultureInfo.InvariantCulture).ToUpperInvariant();
        var dateSize = g.MeasureString(date, dateFont, PointF.Empty, format);
        g.DrawString(date, dateFont, secondary,
                     x + width - inset - dateSize.Width, y + 9.5f * scale, format);

        float heroY = y + 21f * scale;
        g.DrawString(weather.FormattedTemp, tempFont, primary, x + inset - 1f * scale, heroY, format);
        float tempWidth = g.MeasureString(weather.FormattedTemp, tempFont, PointF.Empty, format).Width;
        float textX = x + inset + tempWidth + 12f * scale;
        g.DrawString(weather.ConditionName, conditionFont, accentBrush, textX, y + 31f * scale, format);
        g.DrawString(weather.Subtitle, detailFont, secondary, textX, y + 49f * scale, format);

        float metricsTop = y + 73f * scale;
        using (var separator = new Pen(Color.FromArgb(34, 255, 255, 255), 1f * scale))
            g.DrawLine(separator, x + inset, metricsTop - 4f * scale, x + width - inset, metricsTop - 4f * scale);

        string[,] metrics = weather.ChipData;
        float usableWidth = width - inset * 2f;
        float columnWidth = usableWidth / 3f;
        using (var separator = new Pen(Color.FromArgb(25, 255, 255, 255), 1f * scale))
        {
            g.DrawLine(separator, x + inset + columnWidth, metricsTop + 1f * scale,
                       x + inset + columnWidth, y + height - 9f * scale);
            g.DrawLine(separator, x + inset + columnWidth * 2f, metricsTop + 1f * scale,
                       x + inset + columnWidth * 2f, y + height - 9f * scale);
        }

        using var mutedMetric = new SolidBrush(Color.FromArgb(157, 255, 255, 255));
        for (int i = 0; i < 3; i++)
        {
            float metricX = x + inset + i * columnWidth + (i == 0 ? 0 : 10f * scale);
            g.DrawString(metrics[i, 0], metricLabelFont, mutedMetric, metricX, metricsTop, format);
            g.DrawString(metrics[i, 1], metricValueFont, primary, metricX, metricsTop + 11f * scale, format);
        }
    }

private static void DrawTabBauhausWeather(
        Graphics g,
        float superScale,
        int targetW,
        int condition)
    {
        var now = DateTime.Now;
        WeatherModel wModel = IsLiveWeatherMode ? LiveWeatherService.Current : LiveWeatherService.GetMockWeather(condition);

        // Content Area Bounds
        float wX = 22f * superScale;
        float wY = 34f * superScale;
        float totalW = targetW * superScale - 44f * superScale;
        float totalH = 104f * superScale;

        string dayString = now.ToString("dddd, MMM d").ToUpperInvariant();
        string cityString = wModel.City;
        string tempString = wModel.FormattedTemp;
        string condString = wModel.ConditionName;
        string subString = wModel.Subtitle;
        string[,] chipData = wModel.ChipData;

        Color skyTop, skyBottom, tempGlowColor;

        switch (condition)
        {
            case 1: // Sunny / Clear Day
                skyTop = Color.FromArgb(240, 11, 23, 40);
                skyBottom = Color.FromArgb(240, 18, 52, 75);
                tempGlowColor = Color.FromArgb(32, 255, 210, 80);
                break;

            case 2: // Clear Night
                skyTop = Color.FromArgb(240, 8, 10, 22);
                skyBottom = Color.FromArgb(240, 20, 18, 46);
                tempGlowColor = Color.FromArgb(32, 178, 140, 255);
                break;

            case 3: // Partly Cloudy Day
                skyTop = Color.FromArgb(240, 16, 26, 38);
                skyBottom = Color.FromArgb(240, 28, 44, 62);
                tempGlowColor = Color.FromArgb(28, 255, 210, 80);
                break;

            case 4: // Partly Cloudy Night
                skyTop = Color.FromArgb(240, 10, 14, 26);
                skyBottom = Color.FromArgb(240, 18, 24, 46);
                tempGlowColor = Color.FromArgb(28, 160, 180, 255);
                break;

            case 5: // Overcast
                skyTop = Color.FromArgb(240, 18, 22, 28);
                skyBottom = Color.FromArgb(240, 30, 36, 46);
                tempGlowColor = Color.FromArgb(24, 180, 195, 210);
                break;

            case 6: // Fog & Mist
                skyTop = Color.FromArgb(240, 16, 28, 28);
                skyBottom = Color.FromArgb(240, 26, 44, 42);
                tempGlowColor = Color.FromArgb(26, 120, 210, 195);
                break;

            case 7: // Drizzle & Light Rain
                skyTop = Color.FromArgb(240, 16, 24, 34);
                skyBottom = Color.FromArgb(240, 26, 40, 56);
                tempGlowColor = Color.FromArgb(28, 100, 190, 235);
                break;

            case 8: // Rain & Downpour
                skyTop = Color.FromArgb(240, 14, 20, 32);
                skyBottom = Color.FromArgb(240, 24, 38, 56);
                tempGlowColor = Color.FromArgb(32, 64, 196, 255);
                break;

            case 9: // Freezing Rain & Sleet
                skyTop = Color.FromArgb(240, 12, 24, 34);
                skyBottom = Color.FromArgb(240, 20, 42, 60);
                tempGlowColor = Color.FromArgb(30, 140, 230, 255);
                break;

            case 10: // Light Snow & Flurries
                skyTop = Color.FromArgb(240, 14, 26, 42);
                skyBottom = Color.FromArgb(240, 24, 48, 74);
                tempGlowColor = Color.FromArgb(30, 160, 220, 255);
                break;

            case 11: // Heavy Snow & Blizzard
                skyTop = Color.FromArgb(240, 10, 18, 34);
                skyBottom = Color.FromArgb(240, 20, 44, 72);
                tempGlowColor = Color.FromArgb(32, 128, 222, 234);
                break;

            case 12: // Thunderstorm & Lightning
                skyTop = Color.FromArgb(240, 14, 18, 28);
                skyBottom = Color.FromArgb(240, 24, 34, 52);
                tempGlowColor = Color.FromArgb(36, 255, 215, 20);
                break;

            case 13: // Severe Hailstorm & Lightning
                skyTop = Color.FromArgb(240, 24, 14, 34);
                skyBottom = Color.FromArgb(240, 40, 24, 56);
                tempGlowColor = Color.FromArgb(36, 220, 120, 255);
                break;

            case 14: // Golden Sunset / Dusk
                skyTop = Color.FromArgb(240, 38, 14, 30);
                skyBottom = Color.FromArgb(240, 66, 26, 44);
                tempGlowColor = Color.FromArgb(36, 255, 112, 67);
                break;

            case 15: // Windy / Gale / Squall
                skyTop = Color.FromArgb(240, 12, 28, 38);
                skyBottom = Color.FromArgb(240, 22, 48, 64);
                tempGlowColor = Color.FromArgb(32, 80, 220, 235);
                break;

            default:
                skyTop = Color.FromArgb(240, 11, 23, 40);
                skyBottom = Color.FromArgb(240, 18, 52, 75);
                tempGlowColor = Color.FromArgb(32, 255, 210, 80);
                break;
        }

        // ----------------------------------------------------
        // 1. Hero Bauhaus Abstract Geometric Art Canvas (Left)
        // ----------------------------------------------------
        float artW = 114f * superScale;
        float artH = totalH;
        float artR = 14f * superScale;

        using (var pathArt = CreateRoundedRectPath(wX, wY, artW, artH, artR, artR, artR, artR))
        {
            using (var brushArtBg = new LinearGradientBrush(
                new PointF(wX, wY),
                new PointF(wX + artW, wY + artH),
                skyTop,
                skyBottom))
            {
                g.FillPath(brushArtBg, pathArt);
            }

            var stateArt = g.Save();
            g.SetClip(pathArt);

            float groundY = wY + artH + 4f * superScale;

            switch (condition)
            {
                case 1: // Sunny: Center Rising Sun, Warm Aura, Compass Orbit Arcs, 45-deg Ray, Teal Horizons
                {
                    float sunCx = wX + artW * 0.5f;
                    float sunCy = wY + 38f * superScale;
                    float sunR = 18f * superScale;

                    using (var brushHalo = new SolidBrush(Color.FromArgb(48, 255, 175, 35)))
                    {
                        float haloR = sunR + 16f * superScale;
                        g.FillEllipse(brushHalo, sunCx - haloR, sunCy - haloR, haloR * 2f, haloR * 2f);
                    }

                    using (var brushSun = new LinearGradientBrush(
                        new PointF(sunCx - sunR, sunCy - sunR),
                        new PointF(sunCx + sunR, sunCy + sunR),
                        Color.FromArgb(255, 255, 220, 75),
                        Color.FromArgb(255, 248, 138, 24)))
                    {
                        g.FillEllipse(brushSun, sunCx - sunR, sunCy - sunR, sunR * 2f, sunR * 2f);
                    }

                    using (var penOrbit1 = new Pen(Color.FromArgb(90, 255, 255, 255), 1.0f * superScale))
                    {
                        float oR = sunR + 10f * superScale;
                        g.DrawArc(penOrbit1, sunCx - oR, sunCy - oR, oR * 2f, oR * 2f, 190, 160);
                    }

                    using (var penTangent = new Pen(Color.FromArgb(65, 255, 255, 255), 1.0f * superScale))
                    {
                        g.DrawLine(penTangent, wX - 10f * superScale, wY + artH + 10f * superScale, wX + artW + 10f * superScale, wY - 10f * superScale);
                    }

                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 21, 72, 89),
                        Color.FromArgb(245, 34, 119, 140),
                        Color.FromArgb(255, 61, 174, 189));
                    break;
                }

                case 2: // Clear Night: Midnight Violet Horizons, Geometric Crescent Moon, Orbit Ring, Starbursts
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 18, 14, 40),
                        Color.FromArgb(245, 28, 24, 64),
                        Color.FromArgb(255, 42, 38, 94));

                    float mCx = wX + artW * 0.5f - 2f * superScale;
                    float mCy = wY + 36f * superScale;
                    float moonR = 16f * superScale;

                    using (var pathMoon = new GraphicsPath())
                    {
                        pathMoon.AddArc(mCx - moonR, mCy - moonR, moonR * 2f, moonR * 2f, -110, 220);
                        float innerR = moonR * 1.15f;
                        float innerOffset = 8.5f * superScale;
                        pathMoon.AddArc(mCx - innerR + innerOffset, mCy - innerR, innerR * 2f, innerR * 2f, 85, -170);
                        pathMoon.CloseFigure();

                        using var brushMoon = new LinearGradientBrush(
                            new PointF(mCx - moonR, mCy - moonR),
                            new PointF(mCx + moonR, mCy + moonR),
                            Color.FromArgb(255, 255, 242, 178),
                            Color.FromArgb(255, 230, 202, 101));
                        g.FillPath(brushMoon, pathMoon);
                    }

                    using (var penOrbit = new Pen(Color.FromArgb(70, 255, 255, 255), 1.0f * superScale))
                    {
                        float oR = moonR + 11f * superScale;
                        g.DrawArc(penOrbit, mCx - oR, mCy - oR, oR * 2f, oR * 2f, 115, 210);
                    }

                    DrawBauhausStar(g, superScale, wX + 22f * superScale, wY + 24f * superScale, 6.0f * superScale);
                    DrawBauhausStar(g, superScale, wX + artW - 20f * superScale, wY + 38f * superScale, 4.5f * superScale);
                    DrawBauhausStar(g, superScale, wX + 32f * superScale, wY + 62f * superScale, 3.5f * superScale);

                    using (var brushDot = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    {
                        g.FillEllipse(brushDot, wX + 16f * superScale, wY + 48f * superScale, 2.2f * superScale, 2.2f * superScale);
                        g.FillEllipse(brushDot, wX + artW - 26f * superScale, wY + 16f * superScale, 2.0f * superScale, 2.0f * superScale);
                        g.FillEllipse(brushDot, wX + 86f * superScale, wY + 54f * superScale, 2.5f * superScale, 2.5f * superScale);
                    }
                    break;
                }

                case 3: // Partly Cloudy Day: Peeking Sun, Interlocking Bauhaus Cloud Forms, Drafting Lines
                {
                    float sunCx = wX + 74f * superScale;
                    float sunCy = wY + 28f * superScale;
                    float sunR = 15f * superScale;

                    using (var brushSun = new LinearGradientBrush(
                        new PointF(sunCx - sunR, sunCy - sunR),
                        new PointF(sunCx + sunR, sunCy + sunR),
                        Color.FromArgb(255, 255, 220, 75),
                        Color.FromArgb(255, 248, 138, 24)))
                    {
                        g.FillEllipse(brushSun, sunCx - sunR, sunCy - sunR, sunR * 2f, sunR * 2f);
                    }

                    using (var penOrbit = new Pen(Color.FromArgb(75, 255, 255, 255), 1.0f * superScale))
                    {
                        g.DrawArc(penOrbit, sunCx - 22f * superScale, sunCy - 22f * superScale, 44f * superScale, 44f * superScale, 160, 160);
                    }

                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 24, 48, 58),
                        Color.FromArgb(245, 36, 70, 84),
                        Color.FromArgb(255, 54, 100, 116));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 50f * superScale,
                        cy: wY + 48f * superScale,
                        width: 74f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(245, 96, 122, 154),
                        colorBottom: Color.FromArgb(255, 62, 80, 105),
                        rimColor: Color.FromArgb(170, 255, 235, 180),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    using (var penStratum = new Pen(Color.FromArgb(65, 255, 255, 255), 1.0f * superScale))
                    using (var brushNode = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    {
                        float l1Y = wY + 68f * superScale;
                        g.DrawLine(penStratum, wX + 8f * superScale, l1Y, wX + artW - 12f * superScale, l1Y);
                        g.FillEllipse(brushNode, wX + 28f * superScale, l1Y - 1.5f * superScale, 3f * superScale, 3f * superScale);

                        float l2Y = wY + 74f * superScale;
                        g.DrawLine(penStratum, wX + 18f * superScale, l2Y, wX + artW - 24f * superScale, l2Y);
                        g.FillEllipse(brushNode, wX + artW - 36f * superScale, l2Y - 1.5f * superScale, 3f * superScale, 3f * superScale);
                    }
                    break;
                }

                case 4: // Partly Cloudy Night: Peeking Moon, Nocturnal Clouds, Starlight
                {
                    float mCx = wX + 74f * superScale;
                    float mCy = wY + 28f * superScale;
                    float moonR = 14f * superScale;

                    using (var pathMoon = new GraphicsPath())
                    {
                        pathMoon.AddArc(mCx - moonR, mCy - moonR, moonR * 2f, moonR * 2f, -110, 220);
                        float innerR = moonR * 1.15f;
                        float innerOffset = 7.5f * superScale;
                        pathMoon.AddArc(mCx - innerR + innerOffset, mCy - innerR, innerR * 2f, innerR * 2f, 85, -170);
                        pathMoon.CloseFigure();

                        using var brushMoon = new SolidBrush(Color.FromArgb(255, 255, 240, 180));
                        g.FillPath(brushMoon, pathMoon);
                    }

                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 16, 18, 38),
                        Color.FromArgb(245, 24, 28, 54),
                        Color.FromArgb(255, 36, 42, 78));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 50f * superScale,
                        cy: wY + 48f * superScale,
                        width: 74f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(240, 42, 48, 80),
                        colorBottom: Color.FromArgb(255, 24, 28, 52),
                        rimColor: Color.FromArgb(130, 210, 230, 255),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    DrawBauhausStar(g, superScale, wX + 22f * superScale, wY + 24f * superScale, 5.0f * superScale);
                    DrawBauhausStar(g, superScale, wX + 32f * superScale, wY + 64f * superScale, 3.5f * superScale);
                    break;
                }

                case 5: // Overcast: Monochromatic Lead Cloud Strata, Dense Slabs
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 32, 38, 48),
                        Color.FromArgb(245, 44, 52, 64),
                        Color.FromArgb(255, 58, 68, 82));

                    // Background cloud bank
                    DrawBauhausCloud(g, superScale,
                        cx: wX + 68f * superScale,
                        cy: wY + 36f * superScale,
                        width: 78f * superScale,
                        height: 36f * superScale,
                        colorTop: Color.FromArgb(235, 52, 60, 72),
                        colorBottom: Color.FromArgb(245, 36, 42, 52),
                        rimColor: Color.FromArgb(70, 255, 255, 255),
                        rimWidth: 1.0f,
                        showInnerVolume: false);

                    // Foreground dense overcast cloud
                    DrawBauhausCloud(g, superScale,
                        cx: wX + 48f * superScale,
                        cy: wY + 50f * superScale,
                        width: 72f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(250, 78, 90, 106),
                        colorBottom: Color.FromArgb(255, 48, 56, 68),
                        rimColor: Color.FromArgb(130, 255, 255, 255),
                        rimWidth: 1.2f,
                        showInnerVolume: true);

                    using (var penStratum = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f * superScale))
                    using (var brushNode = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
                    {
                        float l1Y = wY + 70f * superScale;
                        g.DrawLine(penStratum, wX + 10f * superScale, l1Y, wX + artW - 14f * superScale, l1Y);
                        g.FillEllipse(brushNode, wX + 32f * superScale, l1Y - 1.5f * superScale, 3f * superScale, 3f * superScale);

                        float l2Y = wY + 78f * superScale;
                        g.DrawLine(penStratum, wX + 22f * superScale, l2Y, wX + artW - 20f * superScale, l2Y);
                    }
                    break;
                }

                case 6: // Fog & Mist: Slotted Translucent Scanline Bands, Diffuse Phantom Circles
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 26, 46, 44),
                        Color.FromArgb(245, 36, 62, 58),
                        Color.FromArgb(255, 50, 84, 78));

                    using (var brushPhantom1 = new SolidBrush(Color.FromArgb(45, 180, 230, 220)))
                    using (var brushPhantom2 = new SolidBrush(Color.FromArgb(35, 160, 220, 210)))
                    {
                        g.FillEllipse(brushPhantom1, wX + 32f * superScale, wY + 24f * superScale, 38f * superScale, 38f * superScale);
                        g.FillEllipse(brushPhantom2, wX + 62f * superScale, wY + 34f * superScale, 30f * superScale, 30f * superScale);
                    }

                    using (var penScan = new Pen(Color.FromArgb(90, 210, 255, 245), 1.6f * superScale))
                    {
                        for (int b = 0; b < 6; b++)
                        {
                            float sy = wY + 22f * superScale + b * 10f * superScale;
                            float sx1 = wX + 12f * superScale + (b % 2) * 12f * superScale;
                            float sx2 = wX + artW - 12f * superScale - ((b + 1) % 2) * 10f * superScale;
                            g.DrawLine(penScan, sx1, sy, sx2, sy);
                        }
                    }
                    break;
                }

                case 7: // Drizzle & Light Rain: Delicate 45-deg Micro-Dash Grid, Ground Ripple
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 22, 38, 52),
                        Color.FromArgb(245, 32, 54, 72),
                        Color.FromArgb(255, 46, 74, 98));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 56f * superScale,
                        cy: wY + 34f * superScale,
                        width: 74f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(245, 64, 94, 122),
                        colorBottom: Color.FromArgb(255, 40, 62, 84),
                        rimColor: Color.FromArgb(120, 180, 230, 255),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    using (var penDrizzle = new Pen(Color.FromArgb(200, 80, 210, 255), 1.1f * superScale))
                    {
                        float dx = 6f * superScale;
                        float dy = 10f * superScale;
                        for (int line = 0; line < 8; line++)
                        {
                            float lx = wX + 16f * superScale + line * 12f * superScale;
                            float ly = wY + 46f * superScale + (line % 2) * 5f * superScale;
                            g.DrawLine(penDrizzle, lx, ly, lx - dx, ly + dy);
                        }
                    }

                    // Concentric ripple ellipse on ground
                    using (var penRipple = new Pen(Color.FromArgb(100, 100, 220, 255), 1.0f * superScale))
                    {
                        g.DrawEllipse(penRipple, wX + 38f * superScale, groundY - 6f * superScale, 36f * superScale, 8f * superScale);
                    }
                    break;
                }

                case 8: // Rain & Downpour: Dark Petrol Horizons, Dense 65-deg Cyan Rain Streaks
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 18, 28, 38),
                        Color.FromArgb(245, 27, 42, 56),
                        Color.FromArgb(255, 38, 62, 82));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 58f * superScale,
                        cy: wY + 32f * superScale,
                        width: 76f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(245, 52, 74, 102),
                        colorBottom: Color.FromArgb(255, 32, 46, 68),
                        rimColor: Color.FromArgb(110, 160, 225, 255),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    using (var penRainCyan = new Pen(Color.FromArgb(220, 64, 196, 255), 1.3f * superScale))
                    using (var penRainWhite = new Pen(Color.FromArgb(170, 255, 255, 255), 1.1f * superScale))
                    {
                        float dx = 10f * superScale;
                        float dy = 24f * superScale;
                        for (int line = 0; line < 7; line++)
                        {
                            float lx = wX + 16f * superScale + line * 14f * superScale;
                            float ly = wY + 48f * superScale + (line % 2) * 6f * superScale;
                            var penR = (line % 2 == 0) ? penRainCyan : penRainWhite;
                            g.DrawLine(penR, lx, ly, lx - dx, ly + dy);
                        }
                    }
                    break;
                }

                case 9: // Freezing Rain & Sleet: Glacier Teal, Diamond Ice Crystals, Shard Lines
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 20, 44, 60),
                        Color.FromArgb(245, 28, 62, 82),
                        Color.FromArgb(255, 42, 88, 114));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 58f * superScale,
                        cy: wY + 32f * superScale,
                        width: 76f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(245, 48, 88, 118),
                        colorBottom: Color.FromArgb(255, 28, 56, 80),
                        rimColor: Color.FromArgb(150, 180, 240, 255),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    using (var penSleet = new Pen(Color.FromArgb(220, 140, 230, 255), 1.2f * superScale))
                    using (var brushDiamond = new SolidBrush(Color.FromArgb(240, 200, 245, 255)))
                    {
                        float dx = 8f * superScale;
                        float dy = 20f * superScale;
                        for (int s = 0; s < 6; s++)
                        {
                            float lx = wX + 18f * superScale + s * 15f * superScale;
                            float ly = wY + 46f * superScale + (s % 2) * 6f * superScale;
                            g.DrawLine(penSleet, lx, ly, lx - dx, ly + dy);
                            float tipX = lx - dx;
                            float tipY = ly + dy;
                            PointF[] dPts = {
                                new PointF(tipX, tipY - 3.2f * superScale),
                                new PointF(tipX + 2.6f * superScale, tipY),
                                new PointF(tipX, tipY + 3.2f * superScale),
                                new PointF(tipX - 2.6f * superScale, tipY)
                            };
                            g.FillPolygon(brushDiamond, dPts);
                        }
                    }
                    break;
                }

                case 10: // Light Snow & Flurries: Pastel Azure, 4-Arm Snowflake, Snow Particles
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 24, 50, 74),
                        Color.FromArgb(245, 34, 70, 102),
                        Color.FromArgb(255, 48, 96, 138));

                    float sCx = wX + artW * 0.5f;
                    float sCy = wY + 36f * superScale;
                    float sR = 14f * superScale;

                    using (var brushNode = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                    using (var penSpoke = new Pen(Color.FromArgb(240, 255, 255, 255), 1.3f * superScale))
                    {
                        g.FillEllipse(brushNode, sCx - 2.6f * superScale, sCy - 2.6f * superScale, 5.2f * superScale, 5.2f * superScale);
                        g.DrawLine(penSpoke, sCx - sR, sCy, sCx + sR, sCy);
                        g.DrawLine(penSpoke, sCx, sCy - sR, sCx, sCy + sR);

                        float cLen = 4.5f * superScale;
                        g.DrawLine(penSpoke, sCx - sR, sCy - cLen, sCx - sR, sCy + cLen);
                        g.DrawLine(penSpoke, sCx + sR, sCy - cLen, sCx + sR, sCy + cLen);
                        g.DrawLine(penSpoke, sCx - cLen, sCy - sR, sCx + cLen, sCy - sR);
                        g.DrawLine(penSpoke, sCx - cLen, sCy + sR, sCx + cLen, sCy + sR);
                    }

                    using (var brushSnow = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    {
                        g.FillEllipse(brushSnow, wX + 16f * superScale, wY + 18f * superScale, 3.2f * superScale, 3.2f * superScale);
                        g.FillEllipse(brushSnow, wX + 92f * superScale, wY + 24f * superScale, 2.6f * superScale, 2.6f * superScale);
                        g.FillEllipse(brushSnow, wX + 24f * superScale, wY + 54f * superScale, 3.8f * superScale, 3.8f * superScale);
                        g.FillEllipse(brushSnow, wX + 88f * superScale, wY + 60f * superScale, 2.8f * superScale, 2.8f * superScale);
                    }
                    break;
                }

                case 11: // Heavy Snow & Blizzard: Nordic Ice Horizons, 6-Arm Snowflake, Blizzard Streaks
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 24, 54, 86),
                        Color.FromArgb(245, 36, 80, 122),
                        Color.FromArgb(255, 68, 134, 182));

                    float sCx = wX + artW * 0.5f;
                    float sCy = wY + 36f * superScale;
                    float spokeLen = 16f * superScale;

                    using (var brushNode = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                    using (var penSpoke = new Pen(Color.FromArgb(245, 255, 255, 255), 1.3f * superScale))
                    {
                        g.FillEllipse(brushNode, sCx - 2.8f * superScale, sCy - 2.8f * superScale, 5.6f * superScale, 5.6f * superScale);
                        for (int a = 0; a < 6; a++)
                        {
                            double rad = a * Math.PI / 3.0;
                            float cos = (float)Math.Cos(rad);
                            float sin = (float)Math.Sin(rad);
                            float ex = sCx + spokeLen * cos;
                            float ey = sCy + spokeLen * sin;
                            g.DrawLine(penSpoke, sCx, sCy, ex, ey);
                            g.FillEllipse(brushNode, ex - 1.8f * superScale, ey - 1.8f * superScale, 3.6f * superScale, 3.6f * superScale);

                            float bx = sCx + spokeLen * 0.55f * cos;
                            float by = sCy + spokeLen * 0.55f * sin;
                            float bLen = 5.0f * superScale;
                            g.DrawLine(penSpoke, bx, by, bx + bLen * (float)Math.Cos(rad + Math.PI / 4.0), by + bLen * (float)Math.Sin(rad + Math.PI / 4.0));
                            g.DrawLine(penSpoke, bx, by, bx + bLen * (float)Math.Cos(rad - Math.PI / 4.0), by + bLen * (float)Math.Sin(rad - Math.PI / 4.0));
                        }
                    }

                    using (var penFlurry = new Pen(Color.FromArgb(180, 240, 255, 255), 1.2f * superScale))
                    {
                        g.DrawLine(penFlurry, wX + 12f * superScale, wY + 20f * superScale, wX + 38f * superScale, wY + 28f * superScale);
                        g.DrawLine(penFlurry, wX + 74f * superScale, wY + 62f * superScale, wX + 104f * superScale, wY + 70f * superScale);
                    }

                    using (var brushSnow = new SolidBrush(Color.FromArgb(225, 255, 255, 255)))
                    {
                        g.FillEllipse(brushSnow, wX + 16f * superScale, wY + 16f * superScale, 3.5f * superScale, 3.5f * superScale);
                        g.FillEllipse(brushSnow, wX + 94f * superScale, wY + 22f * superScale, 2.5f * superScale, 2.5f * superScale);
                        g.FillEllipse(brushSnow, wX + 22f * superScale, wY + 54f * superScale, 4.0f * superScale, 4.0f * superScale);
                        g.FillEllipse(brushSnow, wX + 88f * superScale, wY + 58f * superScale, 3.0f * superScale, 3.0f * superScale);
                    }
                    break;
                }

                case 12: // Thunderstorm: Storm Clouds, Rain Dashes, Electric Gold Lightning Bolt
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 18, 28, 38),
                        Color.FromArgb(245, 27, 42, 56),
                        Color.FromArgb(255, 38, 62, 82));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 58f * superScale,
                        cy: wY + 32f * superScale,
                        width: 76f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(250, 48, 62, 84),
                        colorBottom: Color.FromArgb(255, 26, 34, 50),
                        rimColor: Color.FromArgb(130, 255, 230, 140),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    using (var penRainCyan = new Pen(Color.FromArgb(210, 64, 196, 255), 1.2f * superScale))
                    {
                        float dx = 10f * superScale;
                        float dy = 22f * superScale;
                        for (int line = 0; line < 6; line++)
                        {
                            float lx = wX + 16f * superScale + line * 16f * superScale;
                            float ly = wY + 48f * superScale + (line % 2) * 6f * superScale;
                            g.DrawLine(penRainCyan, lx, ly, lx - dx, ly + dy);
                        }
                    }

                    using (var brushBolt = new SolidBrush(Color.FromArgb(255, 255, 215, 20)))
                    using (var penBolt = new Pen(Color.FromArgb(255, 255, 255, 220), 1.0f * superScale))
                    {
                        float bx = wX + 58f * superScale;
                        float by = wY + 28f * superScale;
                        PointF[] boltPts = {
                            new PointF(bx, by),
                            new PointF(bx - 6f * superScale, by + 16f * superScale),
                            new PointF(bx - 1f * superScale, by + 16f * superScale),
                            new PointF(bx - 9f * superScale, by + 36f * superScale),
                            new PointF(bx + 3f * superScale, by + 19f * superScale),
                            new PointF(bx - 2f * superScale, by + 19f * superScale)
                        };
                        g.FillPolygon(brushBolt, boltPts);
                        g.DrawPolygon(penBolt, boltPts);
                    }
                    break;
                }

                case 13: // Severe Hailstorm: Violent Purple Horizon, Angular Lightning, Faceted Hail Hexagons
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 36, 20, 48),
                        Color.FromArgb(245, 52, 28, 68),
                        Color.FromArgb(255, 74, 40, 96));

                    DrawBauhausCloud(g, superScale,
                        cx: wX + 58f * superScale,
                        cy: wY + 32f * superScale,
                        width: 76f * superScale,
                        height: 34f * superScale,
                        colorTop: Color.FromArgb(250, 68, 42, 88),
                        colorBottom: Color.FromArgb(255, 38, 22, 54),
                        rimColor: Color.FromArgb(130, 220, 180, 255),
                        rimWidth: 1.1f,
                        showInnerVolume: true);

                    using (var penBolt = new Pen(Color.FromArgb(255, 120, 240, 255), 1.5f * superScale))
                    {
                        PointF[] boltLine1 = {
                            new PointF(wX + 46f * superScale, wY + 22f * superScale),
                            new PointF(wX + 38f * superScale, wY + 38f * superScale),
                            new PointF(wX + 44f * superScale, wY + 40f * superScale),
                            new PointF(wX + 32f * superScale, wY + 64f * superScale)
                        };
                        g.DrawLines(penBolt, boltLine1);
                    }

                    using (var brushHail = new SolidBrush(Color.FromArgb(240, 225, 248, 255)))
                    using (var penHail = new Pen(Color.FromArgb(255, 255, 255, 255), 1.0f * superScale))
                    {
                        float[] hx = { wX + 22f * superScale, wX + 64f * superScale, wX + 88f * superScale, wX + 52f * superScale };
                        float[] hy = { wY + 36f * superScale, wY + 26f * superScale, wY + 48f * superScale, wY + 66f * superScale };
                        float[] hr = { 4.0f * superScale, 4.8f * superScale, 4.2f * superScale, 3.6f * superScale };

                        for (int k = 0; k < 4; k++)
                        {
                            PointF[] hex = new PointF[6];
                            for (int a = 0; a < 6; a++)
                            {
                                double rad = a * Math.PI / 3.0;
                                hex[a] = new PointF(hx[k] + hr[k] * (float)Math.Cos(rad), hy[k] + hr[k] * (float)Math.Sin(rad));
                            }
                            g.FillPolygon(brushHail, hex);
                            g.DrawPolygon(penHail, hex);
                        }
                    }
                    break;
                }

                case 14: // Golden Sunset: Sinking Sun with Constructivist Slits, Dusk Ray, Dusky Horizons
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 48, 20, 42),
                        Color.FromArgb(245, 86, 32, 50),
                        Color.FromArgb(255, 142, 58, 56));

                    float sunCx = wX + artW * 0.5f;
                    float sunCy = wY + 42f * superScale;
                    float sunR = 23f * superScale;

                    using (var brushSun = new LinearGradientBrush(
                        new PointF(sunCx - sunR, sunCy - sunR),
                        new PointF(sunCx + sunR, sunCy + sunR),
                        Color.FromArgb(255, 255, 183, 77),
                        Color.FromArgb(255, 255, 87, 34)))
                    {
                        g.FillEllipse(brushSun, sunCx - sunR, sunCy - sunR, sunR * 2f, sunR * 2f);
                    }

                    using (var penSlit = new Pen(Color.FromArgb(220, 38, 14, 30), 1.6f * superScale))
                    {
                        for (float slitY = sunCy - sunR + 5.5f * superScale; slitY < sunCy + sunR; slitY += 6.0f * superScale)
                        {
                            g.DrawLine(penSlit, sunCx - sunR - 2f, slitY, sunCx + sunR + 2f, slitY);
                        }
                    }

                    using (var penTangent = new Pen(Color.FromArgb(85, 255, 180, 100), 1.1f * superScale))
                    {
                        g.DrawLine(penTangent, wX - 8f * superScale, wY + artH + 8f * superScale, wX + artW + 8f * superScale, wY - 8f * superScale);
                    }
                    break;
                }

                case 15: // Windy / Gale / Squall: Sweeping Aerodynamic Streamline Vector Bands
                {
                    DrawTabHorizons(g, superScale, wX, artW, groundY,
                        Color.FromArgb(235, 20, 48, 62),
                        Color.FromArgb(245, 30, 70, 88),
                        Color.FromArgb(255, 44, 98, 122));

                    using (var penWind = new Pen(Color.FromArgb(220, 80, 220, 235), 1.6f * superScale))
                    using (var brushNode = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
                    {
                        penWind.StartCap = LineCap.Round;
                        penWind.EndCap = LineCap.Round;

                        // Streamline 1
                        g.DrawArc(penWind, wX + 10f * superScale, wY + 18f * superScale, 60f * superScale, 28f * superScale, 180, 150);
                        g.FillEllipse(brushNode, wX + 68f * superScale, wY + 24f * superScale, 4f * superScale, 4f * superScale);

                        // Streamline 2
                        g.DrawArc(penWind, wX + 24f * superScale, wY + 38f * superScale, 66f * superScale, 26f * superScale, 190, 140);
                        g.FillEllipse(brushNode, wX + 88f * superScale, wY + 44f * superScale, 3.5f * superScale, 3.5f * superScale);

                        // Streamline 3
                        g.DrawArc(penWind, wX + 14f * superScale, wY + 56f * superScale, 54f * superScale, 22f * superScale, 180, 130);
                    }

                    // Top arrowhead
                    using (var brushArrow = new SolidBrush(Color.FromArgb(230, 80, 220, 235)))
                    {
                        PointF[] arrow = {
                            new PointF(wX + 72f * superScale, wY + 24f * superScale),
                            new PointF(wX + 66f * superScale, wY + 20f * superScale),
                            new PointF(wX + 66f * superScale, wY + 28f * superScale)
                        };
                        g.FillPolygon(brushArrow, arrow);
                    }
                    break;
                }
            }

            g.Restore(stateArt);

            using (var penArtRim = new Pen(Color.FromArgb(75, 255, 255, 255), 1.0f * superScale))
            {
                g.DrawPath(penArtRim, pathArt);
            }
        }

        // ----------------------------------------------------
        // 2. Swiss Typographic Block (Right)
        // ----------------------------------------------------
        float infoX = wX + artW + 16f * superScale;
        float infoW = totalW - artW - 16f * superScale;

        using var sfTypo = new StringFormat(StringFormat.GenericTypographic);

        // Top Row: Day/Date & City
        using (var fontDay = GetPremiumFont(8.0f * superScale, FontStyle.Bold))
        using (var brushDay = new SolidBrush(Color.FromArgb(180, 215, 245, 255)))
        {
            g.DrawString(dayString, fontDay, brushDay, infoX, wY + 2f * superScale, sfTypo);
        }

        using (var fontCity = GetPremiumFont(8.0f * superScale, FontStyle.Bold))
        using (var brushCity = new SolidBrush(Color.FromArgb(220, 240, 255, 255)))
        {
            var cSize = g.MeasureString(cityString, fontCity, PointF.Empty, sfTypo);
            g.DrawString(cityString, fontCity, brushCity, Math.Max(infoX + 100f * superScale, infoX + infoW - cSize.Width), wY + 2f * superScale, sfTypo);
        }

        // Middle Row: Hero Temperature Numerals & Condition Name
        float tempBlockY = wY + 18f * superScale;
        float textStartX = infoX;
        using (var fontTemp = GetPremiumFont(27.0f * superScale, FontStyle.Bold))
        {
            var tSize = g.MeasureString(tempString, fontTemp, PointF.Empty, StringFormat.GenericTypographic);

            using (var brushGlow = new SolidBrush(tempGlowColor))
            {
                g.FillEllipse(brushGlow, infoX - 4f * superScale, tempBlockY - 2f * superScale, tSize.Width + 8f * superScale, tSize.Height + 4f * superScale);
            }

            using var brushT = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
            g.DrawString(tempString, fontTemp, brushT, infoX, tempBlockY, sfTypo);
            textStartX = infoX + tSize.Width + 12f * superScale;
        }

        using (var fontCond = GetPremiumFont(10.5f * superScale, FontStyle.Bold))
        using (var fontSub = GetPremiumFont(8.0f * superScale, FontStyle.Regular))
        {
            using var brushCond = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
            g.DrawString(condString, fontCond, brushCond, textStartX, tempBlockY + 2f * superScale, sfTypo);

            using var brushSub = new SolidBrush(Color.FromArgb(180, 215, 240, 255));
            g.DrawString(subString, fontSub, brushSub, textStartX, tempBlockY + 18f * superScale, sfTypo);
        }

        // ----------------------------------------------------
        // 3. Three Bauhaus Micro-Telemetry Chips (Base)
        // ----------------------------------------------------
        float chipY = wY + 68f * superScale;
        float chipH = 34f * superScale;
        float chipGap = 8f * superScale;
        float chipW = (infoW - chipGap * 2f) / 3f;
        float chipR = 9f * superScale;

        using var fontChipLbl = GetPremiumFont(6.8f * superScale, FontStyle.Bold);
        using var fontChipVal = GetPremiumFont(8.8f * superScale, FontStyle.Bold);
        using var brushChipLbl = new SolidBrush(Color.FromArgb(165, 205, 235, 255));
        using var brushChipVal = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        using var penChipBorder = new Pen(Color.FromArgb(65, 255, 255, 255), 1.0f * superScale);
        using var brushChipBg = new SolidBrush(Color.FromArgb(32, 255, 255, 255));

        for (int i = 0; i < 3; i++)
        {
            float cx = infoX + i * (chipW + chipGap);
            using var pathChip = CreateRoundedRectPath(cx, chipY, chipW, chipH, chipR, chipR, chipR, chipR);

            g.FillPath(brushChipBg, pathChip);
            g.DrawPath(penChipBorder, pathChip);

            float glyphX = cx + 8f * superScale;
            float glyphY = chipY + chipH * 0.5f;

            if (i == 0)
            {
                // Chip 1 Icon
                if (condition is 7 or 8) // Precip Rain Drops
                {
                    using var penDrop = new Pen(Color.FromArgb(220, 64, 196, 255), 1.3f * superScale);
                    g.DrawLine(penDrop, glyphX + 2f * superScale, glyphY - 4f * superScale, glyphX, glyphY + 2f * superScale);
                    g.DrawLine(penDrop, glyphX + 7f * superScale, glyphY - 6f * superScale, glyphX + 5f * superScale, glyphY);
                    g.DrawLine(penDrop, glyphX + 11f * superScale, glyphY - 3f * superScale, glyphX + 9f * superScale, glyphY + 3f * superScale);
                }
                else if (condition is 10 or 11) // Snowflake
                {
                    using var penSnow = new Pen(Color.FromArgb(240, 128, 222, 234), 1.2f * superScale);
                    g.DrawLine(penSnow, glyphX + 2f * superScale, glyphY, glyphX + 10f * superScale, glyphY);
                    g.DrawLine(penSnow, glyphX + 6f * superScale, glyphY - 4f * superScale, glyphX + 6f * superScale, glyphY + 4f * superScale);
                    g.DrawLine(penSnow, glyphX + 3f * superScale, glyphY - 3f * superScale, glyphX + 9f * superScale, glyphY + 3f * superScale);
                }
                else if (condition is 2 or 4) // Crescent Moon
                {
                    using var pathM = new GraphicsPath();
                    pathM.AddArc(glyphX + 2f * superScale, glyphY - 5f * superScale, 9f * superScale, 9f * superScale, -110, 220);
                    pathM.AddArc(glyphX + 5f * superScale, glyphY - 5f * superScale, 10f * superScale, 10f * superScale, 80, -160);
                    pathM.CloseFigure();
                    using var brushM = new SolidBrush(Color.FromArgb(255, 255, 235, 160));
                    g.FillPath(brushM, pathM);
                }
                else if (condition == 12) // Lightning
                {
                    using var brushBolt = new SolidBrush(Color.FromArgb(255, 255, 215, 20));
                    PointF[] bPts = {
                        new PointF(glyphX + 7f * superScale, glyphY - 6f * superScale),
                        new PointF(glyphX + 2f * superScale, glyphY),
                        new PointF(glyphX + 6f * superScale, glyphY),
                        new PointF(glyphX + 1f * superScale, glyphY + 6f * superScale),
                        new PointF(glyphX + 10f * superScale, glyphY - 1f * superScale),
                        new PointF(glyphX + 6f * superScale, glyphY - 1f * superScale)
                    };
                    g.FillPolygon(brushBolt, bPts);
                }
                else if (condition == 13) // Hail Hexagon
                {
                    using var brushHail = new SolidBrush(Color.FromArgb(240, 220, 245, 255));
                    PointF[] hex = new PointF[6];
                    for (int a = 0; a < 6; a++)
                    {
                        double rad = a * Math.PI / 3.0;
                        hex[a] = new PointF(glyphX + 6f * superScale + 4.5f * superScale * (float)Math.Cos(rad), glyphY + 4.5f * superScale * (float)Math.Sin(rad));
                    }
                    g.FillPolygon(brushHail, hex);
                }
                else if (condition == 6) // Fog Scanlines
                {
                    using var penFog = new Pen(Color.FromArgb(200, 180, 240, 230), 1.2f * superScale);
                    g.DrawLine(penFog, glyphX + 1f * superScale, glyphY - 4f * superScale, glyphX + 11f * superScale, glyphY - 4f * superScale);
                    g.DrawLine(penFog, glyphX + 3f * superScale, glyphY, glyphX + 13f * superScale, glyphY);
                    g.DrawLine(penFog, glyphX + 1f * superScale, glyphY + 4f * superScale, glyphX + 11f * superScale, glyphY + 4f * superScale);
                }
                else if (condition == 14) // Sunset Horizon
                {
                    using var penS = new Pen(Color.FromArgb(255, 255, 183, 77), 1.2f * superScale);
                    g.DrawLine(penS, glyphX + 1f * superScale, glyphY + 2f * superScale, glyphX + 11f * superScale, glyphY + 2f * superScale);
                    g.DrawArc(penS, glyphX + 3f * superScale, glyphY - 4f * superScale, 6f * superScale, 6f * superScale, 180, 180);
                }
                else // Wind Vectors
                {
                    using var penWind = new Pen(Color.FromArgb(220, 61, 174, 189), 1.2f * superScale);
                    penWind.StartCap = LineCap.Round;
                    penWind.EndCap = LineCap.Round;
                    g.DrawLine(penWind, glyphX, glyphY - 3f * superScale, glyphX + 11f * superScale, glyphY - 3f * superScale);
                    g.DrawLine(penWind, glyphX + 2f * superScale, glyphY + 2f * superScale, glyphX + 13f * superScale, glyphY + 2f * superScale);
                }
            }
            else if (i == 1)
            {
                // Chip 2 Icon: Level Bars or Wind
                if (condition is 8 or 12 or 13 or 15) // Wind Streamlines
                {
                    using var penWind = new Pen(Color.FromArgb(220, 64, 196, 255), 1.2f * superScale);
                    penWind.StartCap = LineCap.Round;
                    penWind.EndCap = LineCap.Round;
                    g.DrawLine(penWind, glyphX, glyphY - 3f * superScale, glyphX + 11f * superScale, glyphY - 3f * superScale);
                    g.DrawLine(penWind, glyphX + 2f * superScale, glyphY + 2f * superScale, glyphX + 13f * superScale, glyphY + 2f * superScale);
                }
                else // Humidity: 3 Level Bars
                {
                    using var brushBar = new SolidBrush(Color.FromArgb(220, 34, 119, 140));
                    g.FillRectangle(brushBar, glyphX + 1f * superScale, glyphY + 1f * superScale, 2.5f * superScale, 5f * superScale);
                    g.FillRectangle(brushBar, glyphX + 5f * superScale, glyphY - 2f * superScale, 2.5f * superScale, 8f * superScale);
                    g.FillRectangle(brushBar, glyphX + 9f * superScale, glyphY - 5f * superScale, 2.5f * superScale, 11f * superScale);
                }
            }
            else
            {
                // Chip 3 Icon: Radiant Sun, Level Bars, or Custom
                if (condition is 8 or 12 or 13 or 14 or 15) // Humidity or Storm bars
                {
                    using var brushBar = new SolidBrush(Color.FromArgb(220, 64, 196, 255));
                    g.FillRectangle(brushBar, glyphX + 1f * superScale, glyphY - 2f * superScale, 2.5f * superScale, 8f * superScale);
                    g.FillRectangle(brushBar, glyphX + 5f * superScale, glyphY - 4f * superScale, 2.5f * superScale, 10f * superScale);
                    g.FillRectangle(brushBar, glyphX + 9f * superScale, glyphY - 6f * superScale, 2.5f * superScale, 12f * superScale);
                }
                else // Radiant UV Sun Disc
                {
                    using var brushSunDot = new SolidBrush(Color.FromArgb(255, 255, 200, 50));
                    g.FillEllipse(brushSunDot, glyphX + 3f * superScale, glyphY - 3f * superScale, 6f * superScale, 6f * superScale);

                    using var penRay = new Pen(Color.FromArgb(220, 255, 210, 70), 1.0f * superScale);
                    g.DrawLine(penRay, glyphX + 6f * superScale, glyphY - 6f * superScale, glyphX + 6f * superScale, glyphY - 4f * superScale);
                    g.DrawLine(penRay, glyphX + 6f * superScale, glyphY + 4f * superScale, glyphX + 6f * superScale, glyphY + 6f * superScale);
                    g.DrawLine(penRay, glyphX + 1f * superScale, glyphY, glyphX + 3f * superScale, glyphY);
                    g.DrawLine(penRay, glyphX + 9f * superScale, glyphY, glyphX + 11f * superScale, glyphY);
                }
            }

            // Labels and Values
            float textX = cx + 22f * superScale;
            string lbl = chipData[i, 0];
            string val = chipData[i, 1];

            g.DrawString(lbl, fontChipLbl, brushChipLbl, textX, chipY + 4f * superScale, sfTypo);
            g.DrawString(val, fontChipVal, brushChipVal, textX, chipY + 16f * superScale, sfTypo);
        }
    }

    private static void DrawTabHorizons(Graphics g, float superScale, float wX, float artW, float groundY, Color c1, Color c2, Color c3)
    {
        // Back Left Arc
        float h1R = 40f * superScale;
        float h1Cx = wX + 32f * superScale;
        using (var brushH1 = new SolidBrush(c1))
        {
            g.FillPie(brushH1, h1Cx - h1R, groundY - h1R, h1R * 2f, h1R * 2f, 180, 180);
        }
        using (var penH1 = new Pen(Color.FromArgb(100, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawArc(penH1, h1Cx - h1R, groundY - h1R, h1R * 2f, h1R * 2f, 180, 180);
        }

        // Back Right Arc
        float h2R = 44f * superScale;
        float h2Cx = wX + artW - 28f * superScale;
        using (var brushH2 = new SolidBrush(c2))
        {
            g.FillPie(brushH2, h2Cx - h2R, groundY - h2R, h2R * 2f, h2R * 2f, 180, 180);
        }
        using (var penH2 = new Pen(Color.FromArgb(110, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawArc(penH2, h2Cx - h2R, groundY - h2R, h2R * 2f, h2R * 2f, 180, 180);
        }

        // Foreground Center Arc
        float h3R = 34f * superScale;
        float h3Cx = wX + artW * 0.5f;
        using (var brushH3 = new SolidBrush(c3))
        {
            g.FillPie(brushH3, h3Cx - h3R, groundY - h3R, h3R * 2f, h3R * 2f, 180, 180);
        }
        using (var penH3 = new Pen(Color.FromArgb(160, 255, 255, 255), 1.2f * superScale))
        {
            g.DrawArc(penH3, h3Cx - h3R, groundY - h3R, h3R * 2f, h3R * 2f, 180, 180);
        }
    }


    private static void DrawTabChronoContent(
        Graphics g,
        float superScale,
        int targetW)
    {
        // ----------------------------------------------------
        // CHRONO TAB: MODULAR FLUID TIMERS
        // Modular layout reserving space for additional timers
        // ----------------------------------------------------
        float slot1X = 16f * superScale;
        float slot1Y = 44f * superScale;
        float slot1H = 44f * superScale;
        float slot1R = 12f * superScale;

        // Dynamic height calculation: smoothly morphs between 44px (idle/picker) and 34px (active running)
        float curH;
        if (_chronoTimerRunning)
        {
            if (_chronoMorphTimer > 0.0)
            {
                double p = 1.0 - (_chronoMorphTimer / 0.36);
                if (p < 0.40)
                {
                    curH = slot1H;
                }
                else
                {
                    double t2 = (p - 0.40) / 0.60;
                    double easeOut = 1.0 - Math.Pow(1.0 - t2, 3.0);
                    curH = (float)(44.0 + (ChronoActiveHeight - 44.0) * easeOut) * superScale;
                }
            }
            else
            {
                curH = ChronoActiveHeight * superScale;
            }
        }
        else
        {
            if (_chronoMorphTimer > 0.0)
            {
                double p = 1.0 - (_chronoMorphTimer / 0.25);
                double easeOut = 1.0 - Math.Pow(1.0 - p, 3.0);
                curH = (float)(ChronoActiveHeight + (44.0 - ChronoActiveHeight) * easeOut) * superScale;
            }
            else
            {
                curH = slot1H;
            }
        }

        float curQuickW = (float)_chronoTimerAnimWidth * superScale;
        float quickWProgress = (float)Math.Clamp((_chronoTimerAnimWidth - 44.0) / (396.0 - 44.0), 0.0, 1.0);

        // Container capsule path
        using var pathContainer = CreateRoundedRectPath(slot1X, slot1Y, curQuickW, slot1H, slot1R, slot1R, slot1R, slot1R);

        // Liquid glass capsule background
        if (_chronoTimerRunning)
        {
            // Active timer running state: subtle warm active glow / tint
            using var brushRunning = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            g.FillPath(brushRunning, pathContainer);

            // Active border
            using var penRunning = new Pen(Color.FromArgb(95, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penRunning, pathContainer);
        }
        else
        {
            // Idle or picker state
            int fillA = (_chronoTimerHovered && curQuickW < 50f * superScale) ? 50 : 34;
            using var brushIdle = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255));
            g.FillPath(brushIdle, pathContainer);

            int borderA = (_chronoTimerHovered && curQuickW < 50f * superScale) ? 110 : 75;
            using var penIdle = new Pen(Color.FromArgb(borderA, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penIdle, pathContainer);
        }

        // Render contents based on state
        if (_chronoTimerRunning && _chronoMorphTimer <= 0.20)
        {
            // ================================================
            // STATE 3: RUNNING TIMER (MINUTES & SECONDS) OR CANCEL BUTTON ON HOVER
            // ================================================
            var stateRunning = g.Save();
            g.SetClip(pathContainer);

            float cancelAlpha = (float)Math.Clamp(_chronoRunningHoverP, 0.0, 1.0);
            float timeAlpha = 1.0f - cancelAlpha;

            // Compute remaining time
            int remainingSec = Math.Max(0, (int)Math.Ceiling((_chronoTimerTargetUtc - DateTime.UtcNow).TotalSeconds));
            string timeStr = $"{remainingSec / 60:D2}:{remainingSec % 60:D2}";

            // 1. Time display (fades out as Cancel is hovered)
            if (timeAlpha > 0.01f)
            {
                using var fontTime = GetPremiumFont(9.0f * superScale, FontStyle.Bold);
                var strSize = g.MeasureString(timeStr, fontTime, PointF.Empty, StringFormat.GenericTypographic);
                float dotSize = 5f * superScale;
                float dotGap = 4.5f * superScale;
                float totalContentW = dotSize + dotGap + strSize.Width;
                float startX = slot1X + (curQuickW - totalContentW) * 0.5f;
                float iconCy = slot1Y + slot1H * 0.5f;

                // Pulsing ash-white active dot
                using (var brushDotHalo = new SolidBrush(Color.FromArgb((int)(60 * timeAlpha), 255, 255, 255)))
                {
                    g.FillEllipse(brushDotHalo, startX - 1.5f * superScale, iconCy - dotSize * 0.5f - 1.5f * superScale, dotSize + 3f * superScale, dotSize + 3f * superScale);
                }
                using (var brushDot = new SolidBrush(Color.FromArgb((int)(245 * timeAlpha), 255, 255, 255)))
                {
                    g.FillEllipse(brushDot, startX, iconCy - dotSize * 0.5f, dotSize, dotSize);
                }

                float textX = startX + dotSize + dotGap;
                float textY = slot1Y + (slot1H - strSize.Height) * 0.5f;

                using var brushTime = new SolidBrush(Color.FromArgb((int)(250 * timeAlpha), 255, 255, 255));
                g.DrawString(timeStr, fontTime, brushTime, textX, textY, StringFormat.GenericTypographic);
            }

            // 2. Cancel button on hover (reveals as hovered)
            if (cancelAlpha > 0.01f)
            {
                float btnPad = 4f * superScale;
                float cancelX = slot1X + btnPad;
                float cancelY = slot1Y + btnPad;
                float cancelW = curQuickW - btnPad * 2f;
                float cancelH = slot1H - btnPad * 2f;
                float cancelR = 7f * superScale;

                using var pathCancel = CreateRoundedRectPath(cancelX, cancelY, cancelW, cancelH, cancelR, cancelR, cancelR, cancelR);

                int fillA = (int)(75 * cancelAlpha);
                using var brushCancel = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255));
                g.FillPath(brushCancel, pathCancel);

                int borderA = (int)(160 * cancelAlpha);
                using var penCancel = new Pen(Color.FromArgb(borderA, 255, 255, 255), 1.0f * superScale);
                g.DrawPath(penCancel, pathCancel);

                using var fontCancel = GetPremiumFont(7.5f * superScale, FontStyle.Bold);
                string cancelText = "✕ Cancel";
                var cSize = g.MeasureString(cancelText, fontCancel, PointF.Empty, StringFormat.GenericTypographic);
                float cx = cancelX + (cancelW - cSize.Width) * 0.5f;
                float cy = cancelY + (cancelH - cSize.Height) * 0.5f;

                using var brushCText = new SolidBrush(Color.FromArgb((int)(250 * cancelAlpha), 255, 255, 255));
                g.DrawString(cancelText, fontCancel, brushCText, cx, cy, StringFormat.GenericTypographic);
            }

            g.Restore(stateRunning);
        }
        else
        {
            // ================================================
            // STATE 1 & 2: IDLE ICON OR HOVER EXPANDED PICKER
            // ================================================
            // Icon alpha fades off as capsule expands
            float iconAlpha = Math.Clamp(1.0f - quickWProgress * 2.5f, 0.0f, 1.0f);
            if (iconAlpha > 0.01f)
            {
                var stateIcon = g.Save();
                g.SetClip(pathContainer);

                float iconCx = slot1X + 22f * superScale;
                float iconCy = slot1Y + slot1H * 0.5f;
                float iconR = 8.5f * superScale;

                using var brushIcon = new SolidBrush(Color.FromArgb((int)(230 * iconAlpha), 255, 255, 255));
                DrawStopwatchVector(g, brushIcon, iconCx, iconCy, iconR, superScale);

                g.Restore(stateIcon);
            }

            // Picker buttons fade in as capsule expands
            float pickerAlpha = Math.Clamp((quickWProgress - 0.20f) / 0.80f, 0.0f, 1.0f);
            if (pickerAlpha > 0.01f)
            {
                var statePicker = g.Save();
                g.SetClip(pathContainer);

                // "QUICK" label on left
                using (var fontQuickLbl = GetPremiumFont(7.0f * superScale, FontStyle.Bold))
                using (var brushQuickLbl = new SolidBrush(Color.FromArgb((int)(160 * pickerAlpha), 255, 255, 255)))
                {
                    string qLbl = "QUICK";
                    var qSize = g.MeasureString(qLbl, fontQuickLbl, PointF.Empty, StringFormat.GenericTypographic);
                    g.DrawString(qLbl, fontQuickLbl, brushQuickLbl, slot1X + 16f * superScale, slot1Y + (slot1H - qSize.Height) * 0.5f, StringFormat.GenericTypographic);
                }

                float btnH = 26f * superScale;
                float btnY = slot1Y + (slot1H - btnH) * 0.5f;
                float btnR = 8f * superScale;
                float gap = 6f * superScale;

                float btnW = 44f * superScale;
                float plusW = 30f * superScale;

                using var fontBtn = GetPremiumFont(8.0f * superScale, FontStyle.Bold);
                using var fontPlus = GetPremiumFont(11.0f * superScale, FontStyle.Regular);

                string lbl5 = DebugChronoTimerInSeconds ? "5s" : "5m";
                string lbl10 = DebugChronoTimerInSeconds ? "10s" : "10m";
                string lbl15 = DebugChronoTimerInSeconds ? "15s" : "15m";
                string lbl30 = DebugChronoTimerInSeconds ? "30s" : "30m";

                // Button 1: 5m
                float b1X = slot1X + 86f * superScale;
                DrawChronoPill(g, superScale, b1X, btnY, btnW, btnH, btnR, lbl5, fontBtn,
                    isHovered: _hoveredChronoBtn == ChronoBtn5m,
                    alphaMul: pickerAlpha);

                // Button 2: 10m
                float b2X = b1X + btnW + gap;
                DrawChronoPill(g, superScale, b2X, btnY, btnW, btnH, btnR, lbl10, fontBtn,
                    isHovered: _hoveredChronoBtn == ChronoBtn10m,
                    alphaMul: pickerAlpha);

                // Button 3: 15m
                float b3X = b2X + btnW + gap;
                DrawChronoPill(g, superScale, b3X, btnY, btnW, btnH, btnR, lbl15, fontBtn,
                    isHovered: _hoveredChronoBtn == ChronoBtn15m,
                    alphaMul: pickerAlpha);

                // Button 4: 30m
                float b4X = b3X + btnW + gap;
                DrawChronoPill(g, superScale, b4X, btnY, btnW, btnH, btnR, lbl30, fontBtn,
                    isHovered: _hoveredChronoBtn == ChronoBtn30m,
                    alphaMul: pickerAlpha);

                // Button 5: +
                float b5X = b4X + btnW + gap;
                DrawChronoPill(g, superScale, b5X, btnY, plusW, btnH, btnR, "+", fontPlus,
                    isHovered: _hoveredChronoBtn == ChronoBtnPlus,
                    alphaMul: pickerAlpha);

                g.Restore(statePicker);
            }
        }

        // ----------------------------------------------------
        // MANUAL TIMER DYNAMIC PISTON CAPSULE (To the right of Quick Timer)
        // Exactly matches the 44px height and alignment of Quick Timer.
        // Piston mechanics: curQuickW + 12px + curManW = 428px total span (16px to 444px).
        // When Quick Timer expands to 372px, Manual Timer smoothly shrinks
        // to a 44x44px constructivist Hourglass icon.
        // When Quick Timer is idle (44px), Manual Timer expands to 372px setting bar.
        // ----------------------------------------------------
        float curManX = slot1X + curQuickW + 12f * superScale;
        float curManW = 444f * superScale - curManX;
        float curManY = slot1Y;
        float curManH = slot1H;
        float curManR = slot1R;

        float manProgress = (float)Math.Clamp((curManW - 44f * superScale) / (328f * superScale), 0.0, 1.0);
        float settingAlpha = (float)Math.Clamp((manProgress - 0.20) / 0.80, 0.0, 1.0);
        float manIconAlpha = (float)Math.Clamp(1.0 - manProgress * 2.5, 0.0, 1.0);

        // Contact drop shadow beneath the capsule
        using (var pathManShadow = CreateRoundedRectPath(curManX, curManY + 2.5f * superScale, curManW, curManH, curManR, curManR, curManR, curManR))
        using (var brushShadow = new SolidBrush(Color.FromArgb(65, 0, 0, 0)))
        {
            g.FillPath(brushShadow, pathManShadow);
        }

        using var pathManCard = CreateRoundedRectPath(curManX, curManY, curManW, curManH, curManR, curManR, curManR, curManR);

        // Glass background fill
        if (_manualTimerRunning)
        {
            using var brushManBg = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
            g.FillPath(brushManBg, pathManCard);

            using var penManRim = new Pen(Color.FromArgb(95, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penManRim, pathManCard);
        }
        else
        {
            using var brushManBg = new SolidBrush(Color.FromArgb(32, 255, 255, 255));
            g.FillPath(brushManBg, pathManCard);

            using var brushRimGrad = new LinearGradientBrush(
                new PointF(curManX, curManY),
                new PointF(curManX, curManY + curManH),
                Color.FromArgb(130, 255, 255, 255),
                Color.FromArgb(35, 255, 255, 255));
            using var penManRim = new Pen(brushRimGrad, 1.0f * superScale);
            g.DrawPath(penManRim, pathManCard);
        }

        // Top meniscus tension optical line
        using (var penMeniscus = new Pen(Color.FromArgb(90, 255, 255, 255), 1.0f * superScale))
        {
            g.DrawLine(penMeniscus, curManX + curManR, curManY + 1.0f * superScale, curManX + curManW - curManR, curManY + 1.0f * superScale);
        }

        // ----------------------------------------------------
        // ICON STATE: Shrunken 44x44px constructivist Hourglass
        // ----------------------------------------------------
        if (manIconAlpha > 0.01f)
        {
            var stateIcon = g.Save();
            g.SetClip(pathManCard);

            float cx = curManX + curManW * 0.5f;
            float cy = curManY + curManH * 0.5f;
            float r = 9.5f * superScale;

            bool isIconHovered = (_hoveredManualBtn == ManualBtnIcon);
            int iconA = (int)((isIconHovered ? 255 : 190) * manIconAlpha);

            using var brushHourglass = new SolidBrush(Color.FromArgb(iconA, 255, 255, 255));
            DrawHourglassVector(g, brushHourglass, cx, cy, r, superScale);

            g.Restore(stateIcon);
        }

        if (_manualTimerRunning && settingAlpha > 0.01f)
        {
            // ================================================
            // MANUAL RUNNING STATE: Remaining countdown display (HH:MM:SS) or ✕ Cancel on hover
            // ================================================
            var stateRunning = g.Save();
            g.SetClip(pathManCard);

            float cancelAlpha = (float)Math.Clamp(_manualRunningHoverP, 0.0, 1.0) * settingAlpha;
            float timeAlpha = (1.0f - (float)Math.Clamp(_manualRunningHoverP, 0.0, 1.0)) * settingAlpha;

            int remainingSec = Math.Max(0, (int)Math.Ceiling((_manualTimerTargetUtc - DateTime.UtcNow).TotalSeconds));
            int remH = remainingSec / 3600;
            int remM = (remainingSec % 3600) / 60;
            int remS = remainingSec % 60;
            string timeStr = $"{remH:D2}:{remM:D2}:{remS:D2}";

            if (timeAlpha > 0.01f)
            {
                using var fontTime = GetPremiumFont(10.0f * superScale, FontStyle.Bold);
                using var fontSub = GetPremiumFont(6.5f * superScale, FontStyle.Bold);

                // Left: Pulsing ash-white active indicator dot
                float dotSize = 6.0f * superScale;
                float dotX = curManX + 16f * superScale;
                float dotCy = curManY + curManH * 0.5f;

                using (var brushHalo = new SolidBrush(Color.FromArgb((int)(65 * timeAlpha), 255, 255, 255)))
                {
                    g.FillEllipse(brushHalo, dotX - 2.5f * superScale, dotCy - dotSize * 0.5f - 2.5f * superScale, dotSize + 5f * superScale, dotSize + 5f * superScale);
                }
                using (var brushDot = new SolidBrush(Color.FromArgb((int)(250 * timeAlpha), 255, 255, 255)))
                {
                    g.FillEllipse(brushDot, dotX, dotCy - dotSize * 0.5f, dotSize, dotSize);
                }

                // Subtitle label: "MANUAL TIMER"
                float labelX = dotX + dotSize + 10f * superScale;
                string labelStr = "MANUAL TIMER";
                var labelSize = g.MeasureString(labelStr, fontSub, PointF.Empty, StringFormat.GenericTypographic);
                float labelY = curManY + (curManH - labelSize.Height) * 0.5f;
                using (var brushLabel = new SolidBrush(Color.FromArgb((int)(165 * timeAlpha), 255, 255, 255)))
                {
                    g.DrawString(labelStr, fontSub, brushLabel, labelX, labelY, StringFormat.GenericTypographic);
                }

                // Time String (prominent countdown display)
                var strSize = g.MeasureString(timeStr, fontTime, PointF.Empty, StringFormat.GenericTypographic);
                float timeX = curManX + curManW - strSize.Width - 18f * superScale;
                float timeY = curManY + (curManH - strSize.Height) * 0.5f;

                using (var brushTimeShadow = new SolidBrush(Color.FromArgb((int)(70 * timeAlpha), 0, 0, 0)))
                {
                    g.DrawString(timeStr, fontTime, brushTimeShadow, timeX + 1.0f * superScale, timeY + 1.0f * superScale, StringFormat.GenericTypographic);
                }
                using (var brushTime = new SolidBrush(Color.FromArgb((int)(255 * timeAlpha), 255, 255, 255)))
                {
                    g.DrawString(timeStr, fontTime, brushTime, timeX, timeY, StringFormat.GenericTypographic);
                }
            }

            // Cancel Button on Hover
            if (cancelAlpha > 0.01f)
            {
                float btnPad = 4f * superScale;
                float cancelX = curManX + btnPad;
                float cancelY = curManY + btnPad;
                float cancelW = curManW - btnPad * 2f;
                float cancelH = curManH - btnPad * 2f;
                float cancelR = 7f * superScale;

                using var pathCancel = CreateRoundedRectPath(cancelX, cancelY, cancelW, cancelH, cancelR, cancelR, cancelR, cancelR);

                int fillA = (int)(75 * cancelAlpha);
                using var brushCancel = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255));
                g.FillPath(brushCancel, pathCancel);

                int borderA = (int)(160 * cancelAlpha);
                using var penCancel = new Pen(Color.FromArgb(borderA, 255, 255, 255), 1.0f * superScale);
                g.DrawPath(penCancel, pathCancel);

                using var fontCancel = GetPremiumFont(8.0f * superScale, FontStyle.Bold);
                string cancelText = "✕ Cancel Manual Timer";
                var cSize = g.MeasureString(cancelText, fontCancel, PointF.Empty, StringFormat.GenericTypographic);
                float cx = cancelX + (cancelW - cSize.Width) * 0.5f;
                float cy = cancelY + (cancelH - cSize.Height) * 0.5f;

                using var brushCText = new SolidBrush(Color.FromArgb((int)(250 * cancelAlpha), 255, 255, 255));
                g.DrawString(cancelText, fontCancel, brushCText, cx, cy, StringFormat.GenericTypographic);
            }

            g.Restore(stateRunning);
        }
        else if (!_manualTimerRunning && settingAlpha > 0.01f)
        {
            // ================================================
            // MANUAL SETTING STATE: 44px capsule height
            // Left: "MANUAL" header
            // Center: HH : MM : SS columns with compact chevrons
            // Right: ▶ START button
            // ================================================
            var stateSetting = g.Save();
            g.SetClip(pathManCard);

            using var fontHeader = GetPremiumFont(6.8f * superScale, FontStyle.Bold);
            using var fontNum = GetPremiumFont(11.0f * superScale, FontStyle.Bold);
            using var fontSep = GetPremiumFont(11.0f * superScale, FontStyle.Bold);
            using var fontBtn = GetPremiumFont(7.5f * superScale, FontStyle.Bold);

            // 1. Left Header Label
            string title = "MANUAL";
            using (var brushHeader = new SolidBrush(Color.FromArgb((int)(160 * settingAlpha), 255, 255, 255)))
            {
                var tSize = g.MeasureString(title, fontHeader, PointF.Empty, StringFormat.GenericTypographic);
                float tx = curManX + 16f * superScale;
                float ty = curManY + (curManH - tSize.Height) * 0.5f;
                g.DrawString(title, fontHeader, brushHeader, tx, ty, StringFormat.GenericTypographic);
            }

            // 2. Three Columns (HH, MM, SS)
            float colCenter = curManX + curManW * 0.46f;
            float colSpacing = 38f * superScale;
            float colHX = colCenter - colSpacing;
            float colMX = colCenter;
            float colSX = colCenter + colSpacing;

            float arrowUpY = curManY + 3.5f * superScale;
            float numY = curManY + 14.5f * superScale;
            float arrowDownY = curManY + 34.5f * superScale;

            // Draw Column HH
            DrawManualColumn(g, superScale, colHX, arrowUpY, numY, arrowDownY,
                _manualHours, "D2", _animHourTimer, _animHourDelta,
                isUpHovered: _hoveredManualBtn == ManualBtnUpH,
                isDownHovered: _hoveredManualBtn == ManualBtnDownH,
                fontNum: fontNum,
                alphaMul: settingAlpha);

            // Separator 1 (:)
            using (var brushSep = new SolidBrush(Color.FromArgb((int)(150 * settingAlpha), 255, 255, 255)))
            {
                var sSize = g.MeasureString(":", fontSep, PointF.Empty, StringFormat.GenericTypographic);
                float sep1X = (colHX + colMX) * 0.5f - sSize.Width * 0.5f;
                g.DrawString(":", fontSep, brushSep, sep1X, curManY + (curManH - sSize.Height) * 0.5f - 1.0f * superScale, StringFormat.GenericTypographic);
            }

            // Draw Column MM
            DrawManualColumn(g, superScale, colMX, arrowUpY, numY, arrowDownY,
                _manualMinutes, "D2", _animMinuteTimer, _animMinuteDelta,
                isUpHovered: _hoveredManualBtn == ManualBtnUpM,
                isDownHovered: _hoveredManualBtn == ManualBtnDownM,
                fontNum: fontNum,
                alphaMul: settingAlpha);

            // Separator 2 (:)
            using (var brushSep = new SolidBrush(Color.FromArgb((int)(150 * settingAlpha), 255, 255, 255)))
            {
                var sSize = g.MeasureString(":", fontSep, PointF.Empty, StringFormat.GenericTypographic);
                float sep2X = (colMX + colSX) * 0.5f - sSize.Width * 0.5f;
                g.DrawString(":", fontSep, brushSep, sep2X, curManY + (curManH - sSize.Height) * 0.5f - 1.0f * superScale, StringFormat.GenericTypographic);
            }

            // Draw Column SS
            DrawManualColumn(g, superScale, colSX, arrowUpY, numY, arrowDownY,
                _manualSeconds, "D2", _animSecondTimer, _animSecondDelta,
                isUpHovered: _hoveredManualBtn == ManualBtnUpS,
                isDownHovered: _hoveredManualBtn == ManualBtnDownS,
                fontNum: fontNum,
                alphaMul: settingAlpha);

            // 3. ▶ START Button
            float btnW = 72f * superScale;
            float btnH = 26f * superScale;
            float btnX = curManX + curManW - btnW - 12f * superScale;
            float btnY = curManY + (curManH - btnH) * 0.5f;
            float btnR = 8f * superScale;

            using var pathStart = CreateRoundedRectPath(btnX, btnY, btnW, btnH, btnR, btnR, btnR, btnR);
            bool isStartHovered = (_hoveredManualBtn == ManualBtnStart);

            int startFillA = (int)((isStartHovered ? 80 : 42) * settingAlpha);
            using var brushStartBg = new SolidBrush(Color.FromArgb(startFillA, 255, 255, 255));
            g.FillPath(brushStartBg, pathStart);

            int startBorderA = (int)((isStartHovered ? 180 : 95) * settingAlpha);
            using var penStartRim = new Pen(Color.FromArgb(startBorderA, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(penStartRim, pathStart);

            string startTxt = "▶ START";
            var startSize = g.MeasureString(startTxt, fontBtn, PointF.Empty, StringFormat.GenericTypographic);
            float stX = btnX + (btnW - startSize.Width) * 0.5f;
            float stY = btnY + (btnH - startSize.Height) * 0.5f;

            int textA = (int)((isStartHovered ? 255 : 235) * settingAlpha);
            using (var brushStartText = new SolidBrush(Color.FromArgb(textA, 255, 255, 255)))
            {
                g.DrawString(startTxt, fontBtn, brushStartText, stX, stY, StringFormat.GenericTypographic);
            }

            g.Restore(stateSetting);
        }
    }

    private static void DrawStopwatchVector(Graphics g, Brush brush, float cx, float cy, float r, float superScale)
    {
        using var pen = new Pen(brush, 1.4f * superScale);
        g.DrawEllipse(pen, cx - r, cy - r + 1.5f * superScale, r * 2, r * 2);

        float stemTop = cy - r - 1.5f * superScale;
        float stemBottom = cy - r + 1.5f * superScale;
        g.DrawLine(pen, cx, stemTop, cx, stemBottom);
        g.DrawLine(pen, cx - 2.5f * superScale, stemTop, cx + 2.5f * superScale, stemTop);

        float dialCenterY = cy + 1.5f * superScale;
        float needleLen = r * 0.6f;
        float needleAngleRad = -2.2f;
        float nx = cx + needleLen * (float)Math.Cos(needleAngleRad);
        float ny = dialCenterY + needleLen * (float)Math.Sin(needleAngleRad);
        g.DrawLine(pen, cx, dialCenterY, nx, ny);

        g.FillEllipse(brush, cx - 1.2f * superScale, dialCenterY - 1.2f * superScale, 2.4f * superScale, 2.4f * superScale);
    }

    private static void DrawHourglassVector(Graphics g, Brush brush, float cx, float cy, float r, float superScale)
    {
        using var pen = new Pen(brush, 1.3f * superScale);
        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

        float topY = cy - r + 1.0f * superScale;
        float botY = cy + r - 1.0f * superScale;
        float rimHalfW = r * 0.72f;

        // Top and bottom horizontal rims
        g.DrawLine(pen, cx - rimHalfW, topY, cx + rimHalfW, topY);
        g.DrawLine(pen, cx - rimHalfW, botY, cx + rimHalfW, botY);

        // Glass contours (two opposing bezier waist curves)
        using var pathGlass = new GraphicsPath();
        pathGlass.StartFigure();
        // Top-left to waist
        pathGlass.AddBezier(
            new PointF(cx - rimHalfW + 1.5f * superScale, topY),
            new PointF(cx - rimHalfW * 0.5f, cy - r * 0.35f),
            new PointF(cx - 2.0f * superScale, cy - 1.0f * superScale),
            new PointF(cx - 1.2f * superScale, cy));
        // Waist to bottom-left
        pathGlass.AddBezier(
            new PointF(cx - 1.2f * superScale, cy),
            new PointF(cx - 2.0f * superScale, cy + 1.0f * superScale),
            new PointF(cx - rimHalfW * 0.5f, cy + r * 0.35f),
            new PointF(cx - rimHalfW + 1.5f * superScale, botY));
        g.DrawPath(pen, pathGlass);

        using var pathRight = new GraphicsPath();
        pathRight.StartFigure();
        // Top-right to waist
        pathRight.AddBezier(
            new PointF(cx + rimHalfW - 1.5f * superScale, topY),
            new PointF(cx + rimHalfW * 0.5f, cy - r * 0.35f),
            new PointF(cx + 2.0f * superScale, cy - 1.0f * superScale),
            new PointF(cx + 1.2f * superScale, cy));
        // Waist to bottom-right
        pathRight.AddBezier(
            new PointF(cx + 1.2f * superScale, cy),
            new PointF(cx + 2.0f * superScale, cy + 1.0f * superScale),
            new PointF(cx + rimHalfW * 0.5f, cy + r * 0.35f),
            new PointF(cx + rimHalfW - 1.5f * superScale, botY));
        g.DrawPath(pen, pathRight);

        // Internal sand mounds
        using (var brushSand = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
        {
            PointF[] topSand = {
                new PointF(cx - rimHalfW * 0.42f, cy - r * 0.45f),
                new PointF(cx + rimHalfW * 0.42f, cy - r * 0.45f),
                new PointF(cx, cy - 1.5f * superScale)
            };
            g.FillPolygon(brushSand, topSand);

            PointF[] botSand = {
                new PointF(cx - rimHalfW * 0.52f, botY - 1.0f * superScale),
                new PointF(cx + rimHalfW * 0.52f, botY - 1.0f * superScale),
                new PointF(cx, cy + r * 0.32f)
            };
            g.FillPolygon(brushSand, botSand);
        }

        // Falling sand trickle
        using var penTrickle = new Pen(brush, 1.0f * superScale);
        g.DrawLine(penTrickle, cx, cy - 1.0f * superScale, cx, cy + r * 0.32f);
    }

    private static void DrawChronoPill(
        Graphics g,
        float superScale,
        float x, float y, float w, float h, float r,
        string label,
        Font font,
        bool isHovered,
        float alphaMul = 1.0f)
    {
        if (alphaMul <= 0.001f) return;
        alphaMul = Math.Clamp(alphaMul, 0.0f, 1.0f);

        using var path = CreateRoundedRectPath(x, y, w, h, r, r, r, r);

        int fillA = isHovered ? (int)(65 * alphaMul) : (int)(32 * alphaMul);
        using var brushPill = new SolidBrush(Color.FromArgb(fillA, 255, 255, 255));
        g.FillPath(brushPill, path);

        int borderA = isHovered ? (int)(130 * alphaMul) : (int)(65 * alphaMul);
        using var penPill = new Pen(Color.FromArgb(borderA, 255, 255, 255), 1.0f * superScale);
        g.DrawPath(penPill, path);

        var strSize = g.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic);
        int textA = isHovered ? (int)(255 * alphaMul) : (int)(225 * alphaMul);
        using var brushText = new SolidBrush(Color.FromArgb(textA, 255, 255, 255));
        g.DrawString(label, font, brushText, x + (w - strSize.Width) * 0.5f, y + (h - strSize.Height) * 0.5f, StringFormat.GenericTypographic);
    }

    private static void DrawManualColumn(
        Graphics g,
        float superScale,
        float cx,
        float arrowUpY,
        float numY,
        float arrowDownY,
        int val,
        string format,
        double animTimer,
        double animDelta,
        bool isUpHovered,
        bool isDownHovered,
        Font fontNum,
        float alphaMul = 1.0f)
    {
        if (alphaMul <= 0.001f) return;
        alphaMul = Math.Clamp(alphaMul, 0.0f, 1.0f);

        // 1. UP Arrow (▲)
        PointF[] upTri = {
            new PointF(cx, arrowUpY),
            new PointF(cx - 4.5f * superScale, arrowUpY + 5.5f * superScale),
            new PointF(cx + 4.5f * superScale, arrowUpY + 5.5f * superScale)
        };
        int upA = (int)((isUpHovered ? 255 : 140) * alphaMul);
        if (upA > 0)
        {
            using (var brushUp = new SolidBrush(Color.FromArgb(upA, 255, 255, 255)))
            {
                g.FillPolygon(brushUp, upTri);
            }
            if (isUpHovered)
            {
                using var penUpGlow = new Pen(Color.FromArgb((int)(180 * alphaMul), 255, 255, 255), 1.0f * superScale);
                g.DrawPolygon(penUpGlow, upTri);
            }
        }

        // 2. Number Display with Roll & Pop Animation
        float yOffset = 0f;
        float scale = 1.0f;
        float glowA = 0f;
        if (animTimer > 0.0)
        {
            float p = 1.0f - (float)(animTimer / 0.22);
            // Sinusoidal bounce and vertical translation
            yOffset = (float)(animDelta * -4.5 * Math.Sin(p * Math.PI) * superScale);
            scale = 1.0f + 0.14f * (float)Math.Sin(p * Math.PI);
            glowA = (float)Math.Sin(p * Math.PI);
        }

        string valStr = val.ToString(format);
        var size = g.MeasureString(valStr, fontNum, PointF.Empty, StringFormat.GenericTypographic);

        var numState = g.Save();
        g.TranslateTransform(cx, numY + size.Height * 0.5f + yOffset);
        if (scale != 1.0f)
        {
            g.ScaleTransform(scale, scale);
        }

        if (glowA > 0.01f)
        {
            using var brushGlow = new SolidBrush(Color.FromArgb((int)(160 * glowA * alphaMul), 255, 255, 255));
            g.DrawString(valStr, fontNum, brushGlow, -size.Width * 0.5f, -size.Height * 0.5f, StringFormat.GenericTypographic);
        }

        using (var brushNum = new SolidBrush(Color.FromArgb((int)(255 * alphaMul), 255, 255, 255)))
        {
            g.DrawString(valStr, fontNum, brushNum, -size.Width * 0.5f, -size.Height * 0.5f, StringFormat.GenericTypographic);
        }
        g.Restore(numState);

        // 3. DOWN Arrow (▼)
        PointF[] downTri = {
            new PointF(cx, arrowDownY + 5.5f * superScale),
            new PointF(cx - 4.5f * superScale, arrowDownY),
            new PointF(cx + 4.5f * superScale, arrowDownY)
        };
        int downA = (int)((isDownHovered ? 255 : 140) * alphaMul);
        if (downA > 0)
        {
            using (var brushDown = new SolidBrush(Color.FromArgb(downA, 255, 255, 255)))
            {
                g.FillPolygon(brushDown, downTri);
            }
            if (isDownHovered)
            {
                using var penDownGlow = new Pen(Color.FromArgb((int)(180 * alphaMul), 255, 255, 255), 1.0f * superScale);
                g.DrawPolygon(penDownGlow, downTri);
            }
        }
    }

    private static int GetActiveTimerRemainingMinutes()
    {
        DateTime utcNow = DateTime.UtcNow;
        int activeSec = -1;
        if (_chronoTimerRunning && _chronoTimerTargetUtc > utcNow)
        {
            int s = (int)Math.Ceiling((_chronoTimerTargetUtc - utcNow).TotalSeconds);
            if (s > 0) activeSec = s;
        }
        if (_manualTimerRunning && _manualTimerTargetUtc > utcNow)
        {
            int s = (int)Math.Ceiling((_manualTimerTargetUtc - utcNow).TotalSeconds);
            if (s > 0)
            {
                activeSec = (activeSec < 0) ? s : Math.Min(activeSec, s);
            }
        }
        return (activeSec > 0) ? Math.Max(1, (int)Math.Ceiling(activeSec / 60.0)) : 0;
    }

    private static bool IsAnyTimerLive()
    {
        return GetHomeTimerProgress(out _, out _);
    }

    private static bool GetHomeTimerProgress(out int remainingSeconds, out int totalSeconds)
    {
        DateTime utcNow = DateTime.UtcNow;
        DateTime target = DateTime.MinValue;
        int total = 0;

        void Consider(DateTime candidate, int candidateTotal)
        {
            if (candidate <= utcNow || (target != DateTime.MinValue && candidate >= target)) return;
            target = candidate;
            total = candidateTotal;
        }

        if (_chronoTimerRunning) Consider(_chronoTimerTargetUtc, _chronoTimerTotalSeconds);
        if (_manualTimerRunning) Consider(_manualTimerTargetUtc, _manualTimerTotalSeconds);
        if (_sleepTimerActive)
        {
            int sleepTotal = _sleepTimerDurationMinutes > 0
                ? (_sleepTimerDurationMinutes * 60)
                : Math.Max(1, (int)Math.Ceiling((_sleepTimerTargetUtc - utcNow).TotalSeconds));
            Consider(_sleepTimerTargetUtc, sleepTotal);
        }

        if (target == DateTime.MinValue)
        {
            remainingSeconds = 0;
            totalSeconds = 0;
            return false;
        }

        remainingSeconds = Math.Max(0, (int)Math.Ceiling((target - utcNow).TotalSeconds));
        totalSeconds = Math.Max(remainingSeconds, total);
        return remainingSeconds > 0;
    }

    private void CancelAllActiveTimers()
    {
        if (_chronoTimerRunning) CancelChronoTimer();
        if (_manualTimerRunning) CancelManualTimer();

        _sleepTimerActive = false;
        _sleepTimerTargetUtc = DateTime.MinValue;
        _sleepTimerDurationMinutes = 0;
        _hoveredHomeTimer = false;
        _tabBufferCache[TabHome] = null;
        _tabBufferCache[TabMusic] = null;
        _lastHomeTimerRemainingSec = -1;
        _needExpandedUpdate = true;
        UpdateTimeMaskIfNeeded(force: true);
        _renderSignal.Set();
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
                               (Math.Abs(_compactTimeAlpha - _lastRenderedTimeAlpha) > 0.005) ||
                               (_timerAlarmActive && !_timerAlarmDismissed);

        bool stateChanged = (_isPlaying != _lastRenderedIsPlaying);

        bool isEqDecaying = !_isPlaying && (_eqBarHeights[0] > 0.005f || _eqBarHeights[1] > 0.005f || _eqBarHeights[2] > 0.005f || _eqBarHeights[3] > 0.005f);

        int currentTimerMin = GetActiveTimerRemainingMinutes();
        bool timerMinChanged = (currentTimerMin != _lastRenderedTimerMinutes);
        if (timerMinChanged)
        {
            _lastRenderedTimerMinutes = currentTimerMin;
        }

        if (_isPlaying || isTransitioning || isEqDecaying || stateChanged || timerMinChanged || force || now.ToString("h:mm:ss tt") != _lastTimeString)
        {
            if ((_isPlaying || isTransitioning || isEqDecaying) && !force && !stateChanged && !timerMinChanged && nowSec - _lastTimeMaskUpdateTime < 0.016)
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
                GetCompactPillHeight(),
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
        double compactHeight,
        Color trackAccent)
    {
        const float superScale = 4.0f;
        string timeMain = now.ToString("h:mm");
        string timeSec = ":" + now.ToString("ss");
        string timeAmPm = now.ToString("tt");

        int activeTimerMin = GetActiveTimerRemainingMinutes();
        string timerPrefix = activeTimerMin > 0 ? $"{activeTimerMin}m · " : "";

        using var fontMain = GetPremiumFont(14.0f * superScale, FontStyle.Bold);
        using var fontSub = GetPremiumFont(6.5f * superScale, FontStyle.Bold);

        using var bmpMeasure = new Bitmap(1, 1);
        using var gMeasure = Graphics.FromImage(bmpMeasure);
        gMeasure.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var sizeMain = gMeasure.MeasureString(timeMain, fontMain, PointF.Empty, StringFormat.GenericTypographic);
        var sizeSec = gMeasure.MeasureString(timeSec, fontSub, PointF.Empty, StringFormat.GenericTypographic);
        var sizeAmPm = gMeasure.MeasureString(timeAmPm, fontSub, PointF.Empty, StringFormat.GenericTypographic);
        var sizeTimer = string.IsNullOrEmpty(timerPrefix) ? SizeF.Empty : gMeasure.MeasureString(timerPrefix, fontMain, PointF.Empty, StringFormat.GenericTypographic);

        float subW = Math.Max(sizeSec.Width, sizeAmPm.Width);
        float spacingSub = 2.5f * superScale;
        float totalClockWidth = sizeTimer.Width + sizeMain.Width + spacingSub + subW;

        int targetW = Math.Max(40, (int)Math.Round(currentCompactWidth));
        int targetH = (int)Math.Round(compactHeight);
        int superW = (int)(targetW * superScale);
        int superH = (int)(targetH * superScale);

        using var superBmp = new Bitmap(superW, superH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(superBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            if (_timerAlarmActive && !_timerAlarmDismissed)
            {
                DrawRingingBellVector(g, superW, superH, superScale);
            }
            else
            {
                // 1. Left Section: Non-Moving Rounded Square Album Art (Controlled by mediaElementsAlpha)
                if (mediaElementsAlpha > 0.01)
                {
                    float discAlpha = (float)Math.Clamp(mediaElementsAlpha, 0.0, 1.0);
                    float discScale = 0.85f + 0.15f * discAlpha;
                    float artSize = (28.0f + 4.0f * (float)Math.Clamp(1.0 - timeAlpha, 0.0, 1.0)) * superScale * discScale;
                    float artR = 6.0f * superScale * discScale;
                    // Keep a deliberate left inset in the compact pill. The vertical
                    // inset is 8px at full scale; the 10px left inset prevents the
                    // rounded outer shell from making the art feel edge-hugging.
                    float artX = (10.0f + 4.0f * (float)Math.Clamp(1.0 - timeAlpha, 0.0, 1.0)) * superScale;
                    float artY = (superH - artSize) * 0.5f;
                    float discCx = artX + artSize * 0.5f;
                    float discCy = artY + artSize * 0.5f;

                    var state = g.Save();

                    using (var artPath = CreateRoundedRectPath(artX, artY, artSize, artSize, artR, artR, artR, artR))
                    {
                        g.SetClip(artPath);

                        // 1. Static Non-Moving Album Art
                        bool coverDrawn = false;
                        if (coverBmp != null)
                        {
                            try
                            {
                                lock (coverBmp)
                                {
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(coverBmp, artX, artY, artSize, artSize);

                                    // Dynamic specular light gleam on top half of album art
                                    using var brushHighlight = new LinearGradientBrush(
                                        new RectangleF(artX, artY, artSize, artSize * 0.5f),
                                        Color.FromArgb((int)(50 * discAlpha), 255, 255, 255),
                                        Color.FromArgb(0, 255, 255, 255),
                                        90f);
                                    g.FillRectangle(brushHighlight, artX, artY, artSize, artSize * 0.5f);
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
                            using var brushCenter = new SolidBrush(Color.FromArgb((int)(200 * discAlpha), trackAccent));
                            g.FillPath(brushCenter, artPath);

                            using var fontNote = GetPremiumFont(12.0f * superScale, FontStyle.Bold);
                            var noteSize = g.MeasureString("♫", fontNote, PointF.Empty, StringFormat.GenericTypographic);
                            using var brushNote = new SolidBrush(Color.FromArgb((int)(140 * discAlpha), 255, 255, 255));
                            g.DrawString("♫", fontNote, brushNote, discCx - noteSize.Width * 0.5f, discCy - noteSize.Height * 0.5f, StringFormat.GenericTypographic);
                        }

                        g.ResetClip();

                        // 2. Crisp Rounded Square Glass Outer Rim
                        using (var penBorder = new Pen(Color.FromArgb((int)(160 * discAlpha), 255, 255, 255), 1.0f * superScale))
                        {
                            g.DrawPath(penBorder, artPath);
                        }
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
                float curDrawX = textStartX;
                float cy = superH * 0.5f;
                float mainY = cy - sizeMain.Height * 0.5f - 1.25f * superScale;
                float subTopY = cy - sizeSec.Height - 1.25f * superScale;
                float subBotY = cy - 1.0f * superScale;

                if (!string.IsNullOrEmpty(timerPrefix))
                {
                    int dotIdx = timerPrefix.IndexOf('·');
                    if (dotIdx > 0)
                    {
                        string minStr = timerPrefix.Substring(0, dotIdx);
                        string dotStr = timerPrefix.Substring(dotIdx);
                        var sizeMin = g.MeasureString(minStr, fontMain, PointF.Empty, StringFormat.GenericTypographic);

                        using (var brushTimer = new SolidBrush(Color.FromArgb((int)(220 * clockAlpha), 215, 225, 235)))
                        {
                            g.DrawString(minStr, fontMain, brushTimer, curDrawX, mainY, StringFormat.GenericTypographic);
                        }
                        using (var brushDot = new SolidBrush(Color.FromArgb((int)(150 * clockAlpha), 148, 163, 184)))
                        {
                            g.DrawString(dotStr, fontMain, brushDot, curDrawX + sizeMin.Width, mainY, StringFormat.GenericTypographic);
                        }
                    }
                    else
                    {
                        using (var brushTimer = new SolidBrush(Color.FromArgb((int)(220 * clockAlpha), 215, 225, 235)))
                        {
                            g.DrawString(timerPrefix, fontMain, brushTimer, curDrawX, mainY, StringFormat.GenericTypographic);
                        }
                    }
                    curDrawX += sizeTimer.Width;
                }

                using (var brushMain = new SolidBrush(Color.FromArgb((int)(255 * clockAlpha), 255, 255, 255)))
                {
                    g.DrawString(timeMain, fontMain, brushMain, curDrawX, mainY, StringFormat.GenericTypographic);
                }

                float subStartX = curDrawX + sizeMain.Width + spacingSub;
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
                float eqCy = targetH * 0.5f * superScale;
                DrawEqualizerBars(g, eqCx, eqCy, 1.85f * superScale, 11.5f * superScale, eqBarHeights, trackAccent, (float)mediaElementsAlpha);
            }
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

    private static void DrawRingingBellVector(Graphics g, int superW, int superH, float superScale)
    {
        float cx = superW * 0.5f;
        float cy = superH * 0.5f;

        // Constructivist minimal vector bell matching signature Liquid Glass ash-white palette
        // 1. Clapper (protruding slightly below bell rim in subtle ash tone)
        float clapperCy = cy + 6.2f * superScale;
        float clapperR = 2.4f * superScale;
        using (var clapperBrush = new SolidBrush(Color.FromArgb(240, 203, 213, 225))) // #CBD5E1 ash
        {
            g.FillEllipse(clapperBrush, cx - clapperR, clapperCy - clapperR, clapperR * 2, clapperR * 2);
        }
        using (var clapperPen = new Pen(Color.FromArgb(180, 255, 255, 255), 0.8f * superScale))
        {
            g.DrawEllipse(clapperPen, cx - clapperR, clapperCy - clapperR, clapperR * 2, clapperR * 2);
        }

        // 2. Bell Body Path
        using (var bellPath = new GraphicsPath())
        {
            bellPath.StartFigure();
            // Top crown left
            bellPath.AddLine(cx - 3.2f * superScale, cy - 6.5f * superScale, cx - 4.6f * superScale, cy - 2.5f * superScale);
            // Shoulder to flared waist
            bellPath.AddBezier(
                new PointF(cx - 4.6f * superScale, cy - 2.5f * superScale),
                new PointF(cx - 5.2f * superScale, cy + 1.5f * superScale),
                new PointF(cx - 7.5f * superScale, cy + 4.5f * superScale),
                new PointF(cx - 8.8f * superScale, cy + 5.5f * superScale)
            );
            // Flared bottom rim
            bellPath.AddLine(cx - 8.8f * superScale, cy + 5.5f * superScale, cx + 8.8f * superScale, cy + 5.5f * superScale);
            // Right flared waist to shoulder
            bellPath.AddBezier(
                new PointF(cx + 8.8f * superScale, cy + 5.5f * superScale),
                new PointF(cx + 7.5f * superScale, cy + 4.5f * superScale),
                new PointF(cx + 5.2f * superScale, cy + 1.5f * superScale),
                new PointF(cx + 4.6f * superScale, cy - 2.5f * superScale)
            );
            // Right shoulder to top crown right
            bellPath.AddLine(cx + 4.6f * superScale, cy - 2.5f * superScale, cx + 3.2f * superScale, cy - 6.5f * superScale);
            bellPath.CloseFigure();

            // Frosted pure white to ash-silver gradient
            using var bellGrad = new LinearGradientBrush(
                new PointF(cx, cy - 7.0f * superScale),
                new PointF(cx, cy + 6.0f * superScale),
                Color.FromArgb(250, 255, 255, 255),
                Color.FromArgb(220, 203, 213, 225)
            );
            g.FillPath(bellGrad, bellPath);

            // Specular border
            using var bellBorderPen = new Pen(Color.FromArgb(240, 255, 255, 255), 1.0f * superScale);
            g.DrawPath(bellBorderPen, bellPath);
        }

        // 3. Bottom rim pill band
        float rimW = 18.2f * superScale;
        float rimH = 2.2f * superScale;
        float rimX = cx - rimW * 0.5f;
        float rimY = cy + 4.6f * superScale;
        using (var rimBrush = new SolidBrush(Color.FromArgb(245, 241, 245, 249)))
        {
            g.FillRectangle(rimBrush, rimX, rimY, rimW, rimH);
        }
        using (var rimBorder = new Pen(Color.FromArgb(255, 255, 255, 255), 0.8f * superScale))
        {
            g.DrawRectangle(rimBorder, rimX, rimY, rimW, rimH);
        }

        // 4. Top suspension crown loop
        float loopW = 5.2f * superScale;
        float loopH = 4.4f * superScale;
        float loopX = cx - loopW * 0.5f;
        float loopY = cy - 9.8f * superScale;
        using (var loopPen = new Pen(Color.FromArgb(235, 255, 255, 255), 1.4f * superScale))
        {
            g.DrawEllipse(loopPen, loopX, loopY, loopW, loopH);
        }

        // 5. Specular glint on left shoulder
        using (var glintPen = new Pen(Color.FromArgb(210, 255, 255, 255), 1.1f * superScale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(glintPen, cx - 4.2f * superScale, cy - 4.5f * superScale, cx - 5.5f * superScale, cy + 0.5f * superScale);
        }
    }

    public void StepForward()
    {
        _animDirection = 1.0;
        _renderSignal.Set();
    }

    public void CycleWeatherCardStyle()
    {
        if (IsLiveWeatherMode)
        {
            // First press of S enters debug preview mode starting at condition 1
            IsLiveWeatherMode = false;
            CurrentWeatherCondition = 1;
        }
        else
        {
            CurrentWeatherCondition++;
            if (CurrentWeatherCondition > MaxWeatherConditions)
            {
                // After condition 15, return to live weather mode
                IsLiveWeatherMode = true;
                CurrentWeatherCondition = LiveWeatherService.Current.BauhausConditionIndex;
            }
        }

        _tabBufferCache[TabHome] = null;
        _tabBufferCache[TabWeather] = null;
        _needExpandedUpdate = true;
        UpdateExpandedMask();
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
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string rootDir = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
            string parentDir = Path.GetFullPath(Path.Combine(rootDir, @".."));

            // Also export crisp 600x250 transparent pill PNG directly
            try
            {
                using var pillBmp = new Bitmap(SurfaceWidth, SurfaceHeight, PixelFormat.Format32bppArgb);
                var pillData = pillBmp.LockBits(new Rectangle(0, 0, SurfaceWidth, SurfaceHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                unsafe
                {
                    System.Buffer.MemoryCopy((void*)surface.BitsPtr, (void*)pillData.Scan0, SurfaceWidth * SurfaceHeight * 4, SurfaceWidth * SurfaceHeight * 4);
                }
                pillBmp.UnlockBits(pillData);
                pillBmp.Save(Path.Combine(rootDir, "pill_" + filename), ImageFormat.Png);
            }
            catch { }

            using (var g = Graphics.FromImage(bmp))
            {
                try
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }
                catch
                {
                    g.Clear(Color.FromArgb(20, 22, 28));
                }
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

            bmp.Save(Path.Combine(rootDir, filename), ImageFormat.Png);
            bmp.Save(Path.Combine(parentDir, filename), ImageFormat.Png);
        }
        catch (Exception ex)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string rootDir = Path.GetFullPath(Path.Combine(dir, @"..\..\.."));
                File.WriteAllText(Path.Combine(rootDir, "screenshot_error.txt"), ex.ToString());
            }
            catch { }
        }
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
                string targetName = "screenshot.png";
                try
                {
                    string content = File.ReadAllText(triggerPath).Trim();
                    if (!string.IsNullOrEmpty(content)) targetName = content;
                }
                catch { }
                SaveDesktopScreenshotWithPill(targetName);
                try { File.Delete(triggerPath); } catch { }
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
            parameters.ClassStyle |= CsDblClks;
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

        // Ensure the Dynamic Island stays persistently topmost above all open windows.
        // If any window (browser, editor, file explorer) is placed in front of us,
        // immediately assert HWND_TOPMOST without stealing focus or activating.
        IntPtr windowAbove = GetWindow(hwnd, GW_HWNDPREV);
        if (windowAbove != IntPtr.Zero)
        {
            SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            // Do not activate or steal focus from active windows on click
            m.Result = (IntPtr)MaNoActivate;
            return;
        }

        if (m.Msg == WmLButtonDblClk)
        {
            var pt = PointToClient(Cursor.Position);
            if (IsPointInManualTimerArrows(pt))
            {
                m.Result = IntPtr.Zero;
                return;
            }
            CollapseAndDespawnIsland();
            m.Result = IntPtr.Zero;
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
            bool isFastAnimating = (_tabTransitionP < 1.0) ||
                                   (Math.Abs(_tabIndicatorVel) > 0.002) ||
                                   (_clickAnimTimer > 0.0) ||
                                   (_progress > 0.001 && _progress < 0.999) ||
                                   (Math.Abs(_hoverVel) > 0.001) ||
                                   (Math.Abs(_homeSleepExpandP - (_homeSleepPickerOpen ? 1.0 : 0.0)) > 0.001) ||
                                   (Math.Abs(_audioPickerExpandP - (_audioPickerOpen ? 1.0 : 0.0)) > 0.001) ||
                                   (Math.Abs(_musicSleepExpandP - (_musicSleepPickerOpen ? 1.0 : 0.0)) > 0.001) ||
                                   (_chronoMorphTimer > 0.0) ||
                                   (!_chronoTimerRunning && Math.Abs(_chronoTimerAnimWidth - (_chronoTimerHovered ? 372.0 : 44.0)) > 0.5) ||
                                   (_chronoTimerRunning && Math.Abs(_chronoTimerAnimWidth - ChronoActiveWidth) > 0.5) ||
                                   (_chronoTimerRunning && Math.Abs(_chronoRunningHoverP - (_chronoRunningHovered ? 1.0 : 0.0)) > 0.01) ||
                                   (_activeTab == TabChrono && _chronoTimerRunning) ||
                                   _timerAlarmPendingCollapse ||
                                   (_timerAlarmActive && !_timerAlarmDismissed) ||
                                   (_heldManualBtn != ManualBtnNone) ||
                                   (_manualMorphTimer > 0.0) ||
                                   (_animHourTimer > 0.0 || _animMinuteTimer > 0.0 || _animSecondTimer > 0.0) ||
                                   (_manualTimerRunning && Math.Abs(_manualRunningHoverP - (_manualRunningHovered ? 1.0 : 0.0)) > 0.01) ||
                                   (_notchAttachment > 0.001 && _notchAttachment < 0.999) ||
                                   (_activeTab == TabChrono && _manualTimerRunning);
            int sleepTimeout = isFastAnimating ? 0 : (_hoverPos > 0.6 ? 4 : 10);
            if (sleepTimeout > 0)
            {
                _renderSignal.WaitOne(sleepTimeout);
            }
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

            // Press-and-hold continuous repeat for manual timer arrows
            if (_heldManualBtn != ManualBtnNone)
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    _heldManualBtn = ManualBtnNone;
                }
                else
                {
                    DateTime nowHold = DateTime.UtcNow;
                    if (nowHold >= _manualNextRepeatTime)
                    {
                        _manualNextRepeatTime = nowHold.AddMilliseconds(85);
                        ExecuteManualArrowAction(_heldManualBtn);
                    }
                }
            }

            // Track music playback start and track change to trigger the 2-minute time display
            bool justStartedPlaying = !_lastWasPlaying && _isPlaying;
            bool trackChanged = _isPlaying && !string.IsNullOrEmpty(_currentTrack.Title) && _currentTrack.Title != _lastPlayingTrackTitle && _currentTrack.Title != "No Media Playing";
            if (justStartedPlaying || trackChanged)
            {
                _musicTimeDisplayUntil = utcNow.AddMinutes(2);
                _userDismissed = false;
            }
            _lastWasPlaying = _isPlaying;
            if (_isPlaying)
            {
                _lastPlayingTrackTitle = _currentTrack.Title;
            }

            bool isInsideTriggerArea = false;
            bool isEdgeOrNotchHover = false;
            bool cursorInPill = false;
            bool isExpandHover = false;

            if (GetCursorPos(out var cursorPos))
            {
                int mouseSurfaceX = cursorPos.x - rect.Left;
                int mouseSurfaceY = cursorPos.y - rect.Top;

                double summonHalfWidth = Math.Max(85.0, _currentGeometry.HalfWidth);

                if (_progress > 0.10)
                {
                    double px = mouseSurfaceX - _currentGeometry.CenterX;
                    double py = mouseSurfaceY - _currentGeometry.CenterY;
                    double curR = (py < 0) ? _currentGeometry.Radius * (1.0 - _currentGeometry.NotchP) : _currentGeometry.Radius;
                    double straightW = Math.Max(0.0, _currentGeometry.HalfWidth - curR);
                    double straightH = Math.Max(0.0, _currentGeometry.HalfHeight - curR);
                    double qx = Math.Abs(px) - straightW;
                    double qy = Math.Abs(py) - straightH;
                    double outX = Math.Max(0.0, qx);
                    double outY = Math.Max(0.0, qy);
                    double outDist = Math.Sqrt(outX * outX + outY * outY);
                    double insideDist = Math.Min(0.0, Math.Max(qx, qy));
                    double mouseSdf = outDist + insideDist - curR;

                    // Trigger area: physically inside or directly touching the pill body (supports notch attached at Y=0)
                    double sdfTolerance = _hoverPos > 0.3 ? 4.0 : 2.0;
                    double minTriggerY = (_currentGeometry.NotchP > 0.1) ? 0.0 : 14.0;
                    if (mouseSurfaceY >= minTriggerY && mouseSdf <= sdfTolerance)
                    {
                        isInsideTriggerArea = true;
                    }

                    // Top edge / notch summon strip. After an accidental body-trigger
                    // dismissal, only a 4px edge line remains and it requires a steady
                    // 0.5s dwell before the island can be summoned again.
                    bool bodyTriggerSuppressed = utcNow < _bodyTriggerSuppressedUntilUtc;
                    int topTriggerHeight = bodyTriggerSuppressed ? 4 : 16;
                    if (mouseSurfaceY >= 0 && mouseSurfaceY < topTriggerHeight && Math.Abs(mouseSurfaceX - _currentGeometry.CenterX) <= summonHalfWidth)
                    {
                        if (!bodyTriggerSuppressed)
                        {
                            isEdgeOrNotchHover = true;
                        }
                        else
                        {
                            bool steady = _suppressedTopTriggerDwellStartedAtUtc != DateTime.MinValue &&
                                Math.Abs(mouseSurfaceX - _suppressedTopTriggerDwellPoint.X) <= 1 &&
                                Math.Abs(mouseSurfaceY - _suppressedTopTriggerDwellPoint.Y) <= 1;
                            if (!steady)
                            {
                                _suppressedTopTriggerDwellPoint = new Point(mouseSurfaceX, mouseSurfaceY);
                                _suppressedTopTriggerDwellStartedAtUtc = utcNow;
                            }
                            else if ((utcNow - _suppressedTopTriggerDwellStartedAtUtc).TotalSeconds >= 0.5)
                            {
                                isEdgeOrNotchHover = true;
                            }
                        }
                    }
                    else if (bodyTriggerSuppressed)
                    {
                        _suppressedTopTriggerDwellStartedAtUtc = DateTime.MinValue;
                        _suppressedTopTriggerDwellPoint = Point.Empty;
                    }
                }
                else
                {
                    // Despawned / hidden state:
                    // Top edge (Y from 0 to 14) spawns as compact (not expanded)
                    if (mouseSurfaceY >= 0 && mouseSurfaceY < 16 && Math.Abs(mouseSurfaceX - 300) <= 85.0)
                    {
                        isEdgeOrNotchHover = true;
                    }
                    // Trigger area (resting pill body area: Y from 16 to 65) spawns and then expands once landed
                    else if (utcNow >= _bodyTriggerSuppressedUntilUtc &&
                             mouseSurfaceY >= 16 && mouseSurfaceY <= 65 && Math.Abs(mouseSurfaceX - 300) <= 85.0)
                    {
                        isInsideTriggerArea = true;
                        if (!_userDismissed && _progress <= 0.10)
                        {
                            _spawnedFromBodyTrigger = true;
                            _bodyTriggerSpawnedAtUtc = utcNow;
                        }
                    }
                }

                cursorInPill = isInsideTriggerArea || isEdgeOrNotchHover;

                // Alarm Hover Dismissal: hovering over the ringing bell stops it immediately and restores collapsed state
                if (_timerAlarmActive && !_timerAlarmDismissed)
                {
                    if (cursorInPill)
                    {
                        _timerAlarmActive = false;
                        _timerAlarmDismissed = true;
                        _timerAlarmPendingCollapse = false;
                        _userDismissed = false;
                        _hoverPos = 0.0;
                        _hoverVel = 0.0;
                        _unhoverShowTimeUntil = DateTime.UtcNow.AddMinutes(1);
                        _tabBufferCache[TabChrono] = null;
                        UpdateTimeMaskIfNeeded(force: true);
                        _renderSignal.Set();
                    }
                }

                // A dismissed island must not reclaim the pointer when the user moves
                // through the area below it to reach another control. Re-arm only on
                // a deliberate return to the narrow top notch, never the broader body.
                if (_userDismissed)
                {
                    if (!cursorInPill)
                    {
                        _cursorWasInsideNotch = false;
                    }
                    else if (isEdgeOrNotchHover && !_cursorWasInsideNotch)
                    {
                        // Cursor re-entered the top notch from outside: summon the island.
                        _userDismissed = false;
                        _cursorWasInsideNotch = true;
                    }
                }
                else
                {
                    _cursorWasInsideNotch = cursorInPill;
                }

                // Expansion gate:
                // 1. Spawning should NOT be initially expanded: must complete entry drop (_progress >= 0.82)
                // 2. Must be inside trigger area (not at the most edge / notch strip)
                // 3. Not dismissed by user, and not in the process of despawning (_animDirection >= 0.0)
                // 4. Do not expand while alarm is ringing or pending collapse
                isExpandHover = isInsideTriggerArea && !_userDismissed && (_progress >= 0.82) && (_animDirection >= 0.0) && !_timerAlarmActive && !_timerAlarmPendingCollapse;

                // Intelligence: on hover expansion, auto-select Media Tab if active media, Home Tab if no media
                if (!_wasHovered && isExpandHover)
                {
                    SwitchTab(_hasActiveMedia ? TabMusic : TabHome, immediate: true);
                }
                _wasHovered = isExpandHover;

                if (_hoverPos > 0.6 && !_userDismissed)
                {
                    // 4 Tab Switcher Hover: Home [86, 108), Music [108, 130), Weather [130, 152), Chrono [152, 176]
                    if (mouseSurfaceY >= 24 && mouseSurfaceY <= 54)
                    {
                        if (mouseSurfaceX >= 86 && mouseSurfaceX < 108)
                        {
                            SwitchTab(TabHome);
                        }
                        else if (mouseSurfaceX >= 108 && mouseSurfaceX < 130)
                        {
                            SwitchTab(TabMusic);
                        }
                        else if (mouseSurfaceX >= 130 && mouseSurfaceX < 152)
                        {
                            SwitchTab(TabWeather);
                        }
                        else if (mouseSurfaceX >= 152 && mouseSurfaceX <= 176)
                        {
                            SwitchTab(TabChrono);
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
                            _hoveredAudioBtn = hoveredBtn;
                            _hoveredMusicSleepBtn = hoveredBtn;
                            _tabBufferCache[TabMusic] = null;
                            UpdateExpandedMask();
                        }
                    }
                    else if (_hoveredButton != BtnNone)
                    {
                        _hoveredButton = BtnNone;
                        _hoveredAudioBtn = BtnNone;
                        _hoveredMusicSleepBtn = BtnNone;
                        UpdateExpandedMask();
                    }

                    // Home Tab Sleep Timer & Weather Buttons Hover Interaction
                    if (_activeTab == TabHome)
                    {
                        float mx = mouseSurfaceX - 70f;
                        float my = mouseSurfaceY - 26f;
                        int newSleepBtn = HomeSleepBtnNone;
                        bool isSplit = _isPlaying || _hasActiveMedia || _sleepTimerActive || _homeSleepPickerOpen || (_homeSleepExpandP > 0.001);
                        int newPlayerIdx = -1;
                        bool newHoveredHomeTimer = IsAnyTimerLive() && IsPointInHomeTimer(mx, my);

                        if (my >= 36 && my <= 80 && mx >= 216 && mx <= 444)
                        {
                            if (_homeSleepExpandP > 0.4)
                            {
                                if (mx >= 224 && mx < 274) newSleepBtn = HomeSleepBtn15m;
                                else if (mx >= 274 && mx < 323) newSleepBtn = HomeSleepBtn30m;
                                else if (mx >= 323 && mx < 372) newSleepBtn = HomeSleepBtn45m;
                                else if (mx >= 372 && mx <= 438) newSleepBtn = HomeSleepBtnCancel;
                            }
                            else if (isSplit && mx >= 398 && mx <= 444)
                            {
                                newSleepBtn = HomeSleepBtnMoon;
                            }
                            else if (!isSplit)
                            {
                                var players = PlayerService.GetPlayers();
                                int count = Math.Min(3, players.Count);
                                if (count > 0)
                                {
                                    float btnSize = 28f;
                                    float btnY = 36f + (44f - btnSize) * 0.5f;
                                    float gap = 6f;
                                    float rightMargin = 10f;
                                    float cardX = 216f;
                                    float cardW = 228f;
                                    float startX = cardX + cardW - rightMargin - (count * btnSize + (count - 1) * gap);
                                    if (my >= btnY && my <= btnY + btnSize)
                                    {
                                        for (int i = 0; i < count; i++)
                                        {
                                            float bx = startX + i * (btnSize + gap);
                                            if (mx >= bx && mx <= bx + btnSize)
                                            {
                                                newPlayerIdx = i;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (newSleepBtn != _hoveredHomeSleepBtn)
                        {
                            _hoveredHomeSleepBtn = newSleepBtn;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }

                        if (newPlayerIdx != _hoveredHomePlayerIndex)
                        {
                            _hoveredHomePlayerIndex = newPlayerIdx;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }

                        if (newHoveredHomeTimer != _hoveredHomeTimer)
                        {
                            _hoveredHomeTimer = newHoveredHomeTimer;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }

                        bool newHoveredWeather = (my >= 87 && my <= 130 && mx >= 216 && mx <= 444);
                        if (newHoveredWeather != _hoveredHomeWeather)
                        {
                            _hoveredHomeWeather = newHoveredWeather;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }
                    }
                    else
                    {
                        if (_hoveredHomeSleepBtn != HomeSleepBtnNone)
                        {
                            _hoveredHomeSleepBtn = HomeSleepBtnNone;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }
                        if (_hoveredHomeWeather)
                        {
                            _hoveredHomeWeather = false;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }
                        if (_hoveredHomePlayerIndex != -1)
                        {
                            _hoveredHomePlayerIndex = -1;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }
                        if (_hoveredHomeTimer)
                        {
                            _hoveredHomeTimer = false;
                            _tabBufferCache[TabHome] = null;
                            _needExpandedUpdate = true;
                        }
                    }

                    // Chrono Tab Timer Hover Interaction
                    if (_activeTab == TabChrono)
                    {
                        float mx = mouseSurfaceX - 70f;
                        float my = mouseSurfaceY - 26f;
                        int newChronoBtn = ChronoBtnNone;
                        bool newChronoHovered = false;
                        bool newRunningHovered = false;

                        float slot1X = 16f;
                        float slot1Y = 44f;
                        float slot1H = 44f;
                        float curW = (float)_chronoTimerAnimWidth;

                        if (my >= slot1Y && my <= slot1Y + slot1H && mx >= slot1X && mx <= slot1X + curW)
                        {
                            if (_chronoTimerRunning)
                            {
                                newRunningHovered = true;
                                newChronoBtn = ChronoBtnCancel;
                            }
                            else
                            {
                                newChronoHovered = true;
                                if (curW > 120f)
                                {
                                    float b1X = slot1X + 86f;
                                    float btnW = 44f;
                                    float plusW = 30f;
                                    float gap = 6f;
                                    float b2X = b1X + btnW + gap;
                                    float b3X = b2X + btnW + gap;
                                    float b4X = b3X + btnW + gap;
                                    float b5X = b4X + btnW + gap;

                                    if (mx >= b1X && mx < b1X + btnW) newChronoBtn = ChronoBtn5m;
                                    else if (mx >= b2X && mx < b2X + btnW) newChronoBtn = ChronoBtn10m;
                                    else if (mx >= b3X && mx < b3X + btnW) newChronoBtn = ChronoBtn15m;
                                    else if (mx >= b4X && mx < b4X + btnW) newChronoBtn = ChronoBtn30m;
                                    else if (mx >= b5X && mx <= b5X + plusW) newChronoBtn = ChronoBtnPlus;
                                }
                                else
                                {
                                    newChronoBtn = ChronoBtnIcon;
                                }
                            }
                        }

                        // Manual Timer Hover Interaction
                        int newManualBtn = ManualBtnNone;
                        bool newManualRunningHovered = false;

                        float curManX = slot1X + curW + 12f;
                        float curManW = 444f - curManX;
                        float curManY = 44f;
                        float curManH = 44f;

                        if (my >= curManY && my <= curManY + curManH && mx >= curManX && mx <= curManX + curManW)
                        {
                            if (_manualTimerRunning)
                            {
                                newManualRunningHovered = true;
                                newManualBtn = ManualBtnCancel;
                            }
                            else
                            {
                                if (curManW <= 60f)
                                {
                                    newManualBtn = ManualBtnIcon;
                                }
                                else if (curManW > 150f)
                                {
                                    float colCenter = curManX + curManW * 0.46f;
                                    float colSpacing = 38f;
                                    float colHX = colCenter - colSpacing;
                                    float colMX = colCenter;
                                    float colSX = colCenter + colSpacing;

                                    float btnW = 72f;
                                    float btnH = 26f;
                                    float btnX = curManX + curManW - btnW - 12f;
                                    float btnY = curManY + (curManH - btnH) * 0.5f;

                                    if (my >= curManY && my <= curManY + 18f)
                                    {
                                        if (mx >= colHX - 16f && mx <= colHX + 16f) newManualBtn = ManualBtnUpH;
                                        else if (mx >= colMX - 16f && mx <= colMX + 16f) newManualBtn = ManualBtnUpM;
                                        else if (mx >= colSX - 16f && mx <= colSX + 16f) newManualBtn = ManualBtnUpS;
                                    }
                                    else if (my >= curManY + 26f && my <= curManY + curManH)
                                    {
                                        if (mx >= colHX - 16f && mx <= colHX + 16f) newManualBtn = ManualBtnDownH;
                                        else if (mx >= colMX - 16f && mx <= colMX + 16f) newManualBtn = ManualBtnDownM;
                                        else if (mx >= colSX - 16f && mx <= colSX + 16f) newManualBtn = ManualBtnDownS;
                                    }
                                    else if (mx >= btnX && mx <= btnX + btnW && my >= btnY && my <= btnY + btnH)
                                    {
                                        newManualBtn = ManualBtnStart;
                                    }
                                }
                            }
                        }

                        if (newChronoHovered != _chronoTimerHovered || newRunningHovered != _chronoRunningHovered || newChronoBtn != _hoveredChronoBtn ||
                            newManualBtn != _hoveredManualBtn || newManualRunningHovered != _manualRunningHovered)
                        {
                            _chronoTimerHovered = newChronoHovered;
                            _chronoRunningHovered = newRunningHovered;
                            _hoveredChronoBtn = newChronoBtn;
                            _hoveredManualBtn = newManualBtn;
                            _manualRunningHovered = newManualRunningHovered;
                            _tabBufferCache[TabChrono] = null;
                            _needExpandedUpdate = true;
                        }
                    }
                    else if (_chronoTimerHovered || _chronoRunningHovered || _hoveredChronoBtn != ChronoBtnNone || _hoveredManualBtn != ManualBtnNone || _manualRunningHovered)
                    {
                        _chronoTimerHovered = false;
                        _chronoRunningHovered = false;
                        _hoveredChronoBtn = ChronoBtnNone;
                        _hoveredManualBtn = ManualBtnNone;
                        _manualRunningHovered = false;
                        _tabBufferCache[TabChrono] = null;
                        _needExpandedUpdate = true;
                    }
                }
            }

            if (!cursorInPill && (_hoveredButton != BtnNone || _hoveredHomeSleepBtn != HomeSleepBtnNone || _hoveredHomeTimer || _hoveredHomeWeather || _hoveredHomePlayerIndex != -1 || _chronoTimerHovered || _chronoRunningHovered || _hoveredChronoBtn != ChronoBtnNone || _hoveredManualBtn != ManualBtnNone || _manualRunningHovered || _hoveredAudioBtn != BtnNone || _hoveredMusicSleepBtn != BtnNone))
            {
                _hoveredButton = BtnNone;
                _hoveredAudioBtn = BtnNone;
                _hoveredMusicSleepBtn = BtnNone;
                _hoveredHomeSleepBtn = HomeSleepBtnNone;
                _hoveredHomeTimer = false;
                _hoveredHomeWeather = false;
                _hoveredHomePlayerIndex = -1;
                _chronoTimerHovered = false;
                _chronoRunningHovered = false;
                _hoveredChronoBtn = ChronoBtnNone;
                _hoveredManualBtn = ManualBtnNone;
                _manualRunningHovered = false;
                _tabBufferCache[TabHome] = null;
                _tabBufferCache[TabMusic] = null;
                _tabBufferCache[TabChrono] = null;
                UpdateExpandedMask();
            }

            if (_wasCursorInPill && !cursorInPill && !_userDismissed)
            {
                // Unhovered: time displays for 1 minute
                _unhoverShowTimeUntil = utcNow.AddMinutes(1);
            }
            _wasCursorInPill = cursorInPill;

            // Spawning Intelligence:
            // 1. O'clocks: spawns and displays time for 3 minutes (e.g. HH:00:00 to HH:02:59)
            // 2. Music active: spawns
            // 3. Unhovered: stays spawned showing time for 1 minute
            // 4. Hovered: stays spawned
            // 5. Active Countdown Timer or Alarm: stays spawned (NEVER despawns due to inactivity)
            // Otherwise: despawns and hides
            bool isOClock = (now.Minute < 3);
            bool isUnhoverActive = (utcNow < _unhoverShowTimeUntil);
            bool isSpawnHover = cursorInPill && !_userDismissed;
            bool isTimerRunning = (_chronoTimerRunning && _chronoTimerTargetUtc > utcNow) ||
                                  (_manualTimerRunning && _manualTimerTargetUtc > utcNow);
            bool isAlarmActive = (_timerAlarmActive && !_timerAlarmDismissed) || _timerAlarmPendingCollapse;
            bool shouldBeSpawned = !_userDismissed && (isSpawnHover || _hasActiveMedia || isOClock || isUnhoverActive || isTimerRunning || isAlarmActive);

            if (shouldBeSpawned)
            {
                if (_animDirection < 0.0 || (_animDirection == 0.0 && _progress < 1.0))
                {
                    _animDirection = 1.0;
                }
            }
            else
            {
                if (_progress > 0.0 && (_animDirection > 0.0 || _animDirection == 0.0))
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
                shouldShowTime = isMusicTimeActive || isUnhoverActive || isOClock || isTimerRunning;
            }

            double targetTimeAlpha = shouldShowTime ? 1.0 : 0.0;
            _compactTimeAlpha += (targetTimeAlpha - _compactTimeAlpha) * Math.Min(1.0, 8.0 * dt);

            if (_timerAlarmActive && !_timerAlarmDismissed)
            {
                // Alarm active: shrink resting pill into a compact circular bubble (44px diameter)
                double targetWidth = CompactPillHeight;
                _currentCompactWidth += (targetWidth - _currentCompactWidth) * Math.Min(1.0, 16.0 * dt);
            }
            else if (_hasActiveMedia)
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

                bool hasRunningTimer = (_chronoTimerRunning && _chronoTimerTargetUtc > utcNow) ||
                                       (_manualTimerRunning && _manualTimerTargetUtc > utcNow);
                double targetWidth = hasRunningTimer ? 186.0 : CompactPausedWidth;
                _currentCompactWidth += (targetWidth - _currentCompactWidth) * Math.Min(1.0, 10.0 * dt);
            }

            // Attached Notch Dynamics:
            // When music is playing, the island is unexpanded, and time is hidden (displaying only artwork & visualizer),
            // smoothly dock flush against the top screen bezel as an attached physical notch.
            // When hovered or when time is showing, detach and return to default floating geometry.
            bool isMusicOnlyResting = _hasActiveMedia && (_compactTimeAlpha < 0.15) && (_mediaElementsAlpha > 0.4) &&
                                      (_hoverPos < 0.05) && (_progress > 0.82) && !_timerAlarmActive && !_userDismissed;
            double targetNotch = isMusicOnlyResting ? 1.0 : 0.0;
            _notchAttachment += (targetNotch - _notchAttachment) * Math.Min(1.0, 10.0 * dt);
            if (Math.Abs(_notchAttachment - targetNotch) < 0.001) _notchAttachment = targetNotch;

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

            double target = isExpandHover ? 1.0 : 0.0;
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
            UpdateTabTransitionPhysics(dt);
            UpdateChronoPhysics(dt, utcNow);

            // Smoothly animate sleep timer picker expand / collapse progress
            double targetSleepP = _homeSleepPickerOpen ? 1.0 : 0.0;
            if (Math.Abs(_homeSleepExpandP - targetSleepP) > 0.001)
            {
                _homeSleepExpandP += (targetSleepP - _homeSleepExpandP) * Math.Min(1.0, 8.5 * dt);
                if (Math.Abs(_homeSleepExpandP - targetSleepP) < 0.001)
                {
                    _homeSleepExpandP = targetSleepP;
                }
                _tabBufferCache[TabHome] = null;
                _needExpandedUpdate = true;
            }

            // Smoothly animate audio output picker expand / collapse progress
            double targetAudioP = _audioPickerOpen ? 1.0 : 0.0;
            if (Math.Abs(_audioPickerExpandP - targetAudioP) > 0.0005)
            {
                _audioPickerExpandP += (targetAudioP - _audioPickerExpandP) * Math.Min(1.0, 16.5 * dt);
                if (Math.Abs(_audioPickerExpandP - targetAudioP) < 0.0005)
                {
                    _audioPickerExpandP = targetAudioP;
                }
                _tabBufferCache[TabMusic] = null;
                _needExpandedUpdate = true;
            }

            // Smoothly animate music sleep timer picker expand / collapse progress
            double targetMusicSleepP = _musicSleepPickerOpen ? 1.0 : 0.0;
            if (Math.Abs(_musicSleepExpandP - targetMusicSleepP) > 0.0005)
            {
                _musicSleepExpandP += (targetMusicSleepP - _musicSleepExpandP) * Math.Min(1.0, 16.5 * dt);
                if (Math.Abs(_musicSleepExpandP - targetMusicSleepP) < 0.0005)
                {
                    _musicSleepExpandP = targetMusicSleepP;
                }
                _tabBufferCache[TabMusic] = null;
                _needExpandedUpdate = true;
            }

            // Live countdown ticker when Music Sleep Timer panel is open
            if (_activeTab == TabMusic && _musicSleepPickerOpen && _sleepTimerActive && _sleepTimerTargetUtc != DateTime.MinValue)
            {
                int curVal = (int)Math.Max(0, Math.Ceiling((_sleepTimerTargetUtc - utcNow).TotalSeconds));
                if (curVal != _lastMusicHoverCountdownSec)
                {
                    _lastMusicHoverCountdownSec = curVal;
                    _tabBufferCache[TabMusic] = null;
                    _needExpandedUpdate = true;
                }
            }
            // Live hover countdown ticker for Music Sleep Timer resting button
            else if (_activeTab == TabMusic && _hoveredButton == BtnSleepTimer && _sleepTimerActive && _sleepTimerTargetUtc != DateTime.MinValue && !_musicSleepPickerOpen)
            {
                int curVal = DebugSleepTimerInSeconds 
                    ? Math.Max(0, (int)Math.Ceiling((_sleepTimerTargetUtc - utcNow).TotalSeconds))
                    : Math.Max(1, (int)Math.Ceiling((_sleepTimerTargetUtc - utcNow).TotalMinutes));
                if (curVal != _lastMusicHoverCountdownSec)
                {
                    _lastMusicHoverCountdownSec = curVal;
                    _tabBufferCache[TabMusic] = null;
                    _needExpandedUpdate = true;
                }
            }
            else if (_activeTab == TabMusic && _hoveredButton != BtnSleepTimer && !_musicSleepPickerOpen)
            {
                _lastMusicHoverCountdownSec = -1;
            }

            // Live hover countdown ticker: when hovering over the active sleep timer button, invalidate cache when remaining value changes
            if (_activeTab == TabHome && _hoveredHomeSleepBtn == HomeSleepBtnMoon && _sleepTimerActive && _sleepTimerTargetUtc != DateTime.MinValue)
            {
                int curVal = DebugSleepTimerInSeconds 
                    ? Math.Max(0, (int)Math.Ceiling((_sleepTimerTargetUtc - utcNow).TotalSeconds))
                    : Math.Max(1, (int)Math.Ceiling((_sleepTimerTargetUtc - utcNow).TotalMinutes));
                if (curVal != _lastHoverCountdownSec)
                {
                    _lastHoverCountdownSec = curVal;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                }
            }
            else if (_hoveredHomeSleepBtn != HomeSleepBtnMoon)
            {
                _lastHoverCountdownSec = -1;
            }

            // Keep the Home cancel tile's countdown and perimeter ring current.
            if (_activeTab == TabHome && GetHomeTimerProgress(out int homeTimerRemainingSec, out _))
            {
                if (homeTimerRemainingSec != _lastHomeTimerRemainingSec)
                {
                    _lastHomeTimerRemainingSec = homeTimerRemainingSec;
                    _tabBufferCache[TabHome] = null;
                    _needExpandedUpdate = true;
                }
            }
            else if (!IsAnyTimerLive())
            {
                _lastHomeTimerRemainingSec = -1;
            }

            // Sleep timer expiration check: automatically pauses playback when countdown finishes
            if (_sleepTimerActive && _sleepTimerTargetUtc != DateTime.MinValue && utcNow >= _sleepTimerTargetUtc)
            {
                _sleepTimerActive = false;
                _sleepTimerTargetUtc = DateTime.MinValue;
                _sleepTimerDurationMinutes = 0;
                if (_isPlaying)
                {
                    if (_sysMedia.HasActiveSession)
                    {
                        _ = _sysMedia.TogglePlayPauseAsync();
                    }
                    else
                    {
                        _isPlaying = false;
                        _hasActiveMedia = false;
                    }
                }
                _tabBufferCache[TabHome] = null;
                _tabBufferCache[TabMusic] = null;
                _needExpandedUpdate = true;
            }

            double nowSec = _totalStopwatch.Elapsed.TotalSeconds;
            if (_hoverPos > 0.6 && _isPlaying && (nowSec - _lastExpandedMaskUpdateTime >= 0.050))
            {
                _lastExpandedMaskUpdateTime = nowSec;
                UpdateExpandedMask();
            }

            if (nowSec - _lastZOrderCheckTime >= 0.05)
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
            CheckScreenshotTrigger();
        }
        catch (Exception)
        {
            // Prevent unexpected transient GDI+ or OS rendering exceptions from killing the render thread
        }
    }
}

    private double GetCompactPillHeight()
    {
        if (!_hasActiveMedia) return CompactPillHeight;
        double baseH = CompactPillHeight + 8.0 * Math.Clamp(1.0 - _compactTimeAlpha, 0.0, 1.0);
        if (_notchAttachment > 0.001)
        {
            double notchP = _notchAttachment * _notchAttachment * (3.0 - 2.0 * _notchAttachment);
            return baseH + (36.0 - baseH) * notchP;
        }
        return baseH;
    }

    private PillGeometry ComputeGeometry(double spawnP, double hoverP, double compactWidth)
    {
        double targetCenterX = SurfaceWidth * 0.5;

        // Alarm physical vibration physics (high-frequency left-right shake of the liquid glass circle)
        if (_timerAlarmActive && !_timerAlarmDismissed && hoverP < 0.5)
        {
            double elapsed = (DateTime.UtcNow - _timerAlarmStartTime).TotalSeconds;
            if (elapsed < _timerAlarmDuration)
            {
                double decay = Math.Clamp(1.0 - (elapsed / _timerAlarmDuration), 0.2, 1.0);
                double shakeOffset = Math.Sin(elapsed * 24.0 * Math.PI) * 7.5 * decay;
                targetCenterX += shakeOffset;
            }
        }

        double notchP = _notchAttachment * _notchAttachment * (3.0 - 2.0 * _notchAttachment);
        double restingTopY = TopPadding * (1.0 - notchP);

        double restingHalfWidth = (compactWidth * 0.5) + ((DefaultPillWidth * 0.5) - (compactWidth * 0.5)) * hoverP;
        double compactHeight = GetCompactPillHeight();
        double restingHalfHeight = (compactHeight * 0.5) + ((DefaultPillHeight * 0.5) - (compactHeight * 0.5)) * hoverP;

        restingHalfWidth = Math.Max(20.0, restingHalfWidth);
        restingHalfHeight = Math.Max(16.0, restingHalfHeight);
        double restingCenterY = restingTopY + restingHalfHeight;

        double spawnRadius = 14.0;
        double spawnCenterY = -spawnRadius;

        if (spawnP <= 0.0)
        {
            return new PillGeometry(targetCenterX, spawnCenterY, spawnRadius, spawnRadius, spawnRadius, notchP);
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
        double targetRadius = (compactHeight * 0.5) + (38.0 - (compactHeight * 0.5)) * clampedHover;
        double currentRadius = Math.Min(targetRadius, Math.Min(currentHalfWidth, currentHalfHeight));

        return new PillGeometry(targetCenterX, currentCenterY, currentHalfWidth, currentHalfHeight, currentRadius, notchP);
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

            // 2b. Second-pass cascade Gaussian Blur on 300x125 (deep, creamy frosted glass blur)
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

            // 3. Apple-Grade Liquid Glass Physical Optics Engine
            double straightW = Math.Max(0.0, geom.HalfWidth - geom.Radius);
            double straightH = Math.Max(0.0, geom.HalfHeight - geom.Radius);
            double bevelWidth = Math.Min(16.0, geom.Radius * 0.72);
            double invBevelWidth = 1.0 / Math.Max(1.0, bevelWidth);

            double spawnExpAlpha = Math.Clamp((_progress - 0.50) / 0.50, 0.0, 1.0);
            double hoverExpLinear = Math.Clamp((_hoverPos - 0.62) / 0.38, 0.0, 1.0);
            double hoverExpHermite = hoverExpLinear * hoverExpLinear * (3.0 - 2.0 * hoverExpLinear);
            double expAlpha = EaseOutCubic(spawnExpAlpha) * hoverExpHermite;
            double musicTabBlend = (_activeTab == TabMusic) ? _tabTransitionP : ((_prevTab == TabMusic) ? (1.0 - _tabTransitionP) : 0.0);
            if (_tabTransitionP >= 1.0) musicTabBlend = (_activeTab == TabMusic) ? 1.0 : 0.0;
            bool isMusicTabActive = (musicTabBlend > 0.01) && (expAlpha > 0.01);

            // Virtual illumination vectors (normalized half-vectors with view ray V = (0, 0, 1))
            // Primary key light from top-left (elevation ~65°)
            const double H1x = -0.175;
            const double H1y = -0.496;
            const double H1z = 0.8505;
            const double InvOneMinusH1z = 1.0 / (1.0 - H1z);

            // Ambient fill light bounce from bottom edge
            const double H2x = 0.06;
            const double H2y = 0.54;
            const double H2z = 0.84;
            const double InvOneMinusH2z = 1.0 / (1.0 - H2z);

            double panelExpandP = Math.Max(_audioPickerExpandP, _musicSleepExpandP);
            bool hasExpandedMusicPanel = isMusicTabActive && (panelExpandP > 0.01);
            double pcx = 0, pcy = 0, strW = 0, strH = 0, panelR = 0, panelEase = 0;
            if (hasExpandedMusicPanel)
            {
                panelEase = panelExpandP * panelExpandP * (3.0 - 2.0 * panelExpandP);
                double pX, pY, pW, pH;
                if (_audioPickerExpandP >= _musicSleepExpandP)
                {
                    double oX = 70.0 + 83.0, oY = 26.0 + 105.0, oW = 30.0, oH = 30.0, oR = 15.0;
                    double tX = 70.0 + 84.0, tY = 26.0 + 38.0, tW = 362.0, tH = 100.0, tR = 14.0;
                    pX = oX + (tX - oX) * panelEase;
                    pY = oY + (tY - oY) * panelEase;
                    pW = oW + (tW - oW) * panelEase;
                    pH = oH + (tH - oH) * panelEase;
                    panelR = oR + (tR - oR) * panelEase;
                }
                else
                {
                    double oX = 70.0 + 417.0, oY = 26.0 + 105.0, oW = 30.0, oH = 30.0, oR = 15.0;
                    double tX = 70.0 + 84.0, tY = 26.0 + 38.0, tW = 362.0, tH = 100.0, tR = 14.0;
                    pX = oX + (tX - oX) * panelEase;
                    pY = oY + (tY - oY) * panelEase;
                    pW = oW + (tW - oW) * panelEase;
                    pH = oH + (tH - oH) * panelEase;
                    panelR = oR + (tR - oR) * panelEase;
                }
                pcx = pX + pW * 0.5;
                pcy = pY + pH * 0.5;
                strW = Math.Max(0.0, pW * 0.5 - panelR);
                strH = Math.Max(0.0, pH * 0.5 - panelR);
            }

            double notchP = geom.NotchP;
            double topR = geom.Radius * (1.0 - notchP);
            double botR = geom.Radius;
            double straightWTop = Math.Max(0.0, geom.HalfWidth - topR);
            double straightWBot = Math.Max(0.0, geom.HalfWidth - botR);
            double straightHTop = Math.Max(0.0, geom.HalfHeight - topR);
            double straightHBot = Math.Max(0.0, geom.HalfHeight - botR);

            for (int y = 0; y < SurfaceHeight; y++)
            {
                int rowIdx = y * SurfaceWidth;
                double py = y - geom.CenterY;
                double absPy = Math.Abs(py);
                bool isTop = py < 0.0;
                double curR = isTop ? topR : botR;
                double curStrW = isTop ? straightWTop : straightWBot;
                double curStrH = isTop ? straightHTop : straightHBot;
                double qy = absPy - curStrH;

                for (int x = 0; x < SurfaceWidth; x++)
                {
                    int idx = rowIdx + x;
                    double px = x - geom.CenterX;
                    double absPx = Math.Abs(px);
                    double qx = absPx - curStrW;

                    double outsideX = Math.Max(0.0, qx);
                    double outsideY = Math.Max(0.0, qy);
                    double outsideDist = Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
                    double insideDist = Math.Min(0.0, Math.Max(qx, qy));
                    double sdf = outsideDist + insideDist - curR;

                    // Subpixel Hermite anti-aliased edge alpha (C1-smooth transition)
                    double edgeFactor = Math.Clamp((-sdf + 0.75) * (1.0 / 1.5), 0.0, 1.0);
                    double alphaVal = edgeFactor * edgeFactor * (3.0 - 2.0 * edgeFactor);
                    byte a = (byte)Math.Round(alphaVal * 255.0);

                    // Dual-component physical elevation shadow (contact + ambient penumbra)
                    byte shadowA = 0;
                    if (sdf >= -1.0 && sdf <= 20.0)
                    {
                        double spy = py - 3.0;
                        double absSpy = Math.Abs(spy);
                        bool isSpyTop = spy < 0.0;
                        double sCurR = isSpyTop ? topR : botR;
                        double sCurStrW = isSpyTop ? straightWTop : straightWBot;
                        double sCurStrH = isSpyTop ? straightHTop : straightHBot;
                        double sqy = absSpy - sCurStrH;
                        double sOutX = Math.Max(0.0, absPx - sCurStrW);
                        double sOutY = Math.Max(0.0, sqy);
                        double sOutDist = Math.Sqrt(sOutX * sOutX + sOutY * sOutY);
                        double sInDist = Math.Min(0.0, Math.Max(absPx - sCurStrW, sqy));
                        double shadowSdf = sOutDist + sInDist - sCurR;

                        // Contact shadow (hugs edge within 5px)
                        double cNorm = Math.Clamp((shadowSdf + 1.0) * (1.0 / 6.0), 0.0, 1.0);
                        double cFalloff = (1.0 - cNorm) * (1.0 - cNorm);
                        double cAlpha = cFalloff * 20.0;

                        // Ambient depth penumbra (soft falloff up to 15px)
                        double pNorm = Math.Clamp(shadowSdf * (1.0 / 15.0), 0.0, 1.0);
                        double pFalloff = (1.0 - pNorm) * (1.0 - pNorm);
                        double pAlpha = pFalloff * 12.0;

                        shadowA = (byte)Math.Clamp(Math.Round(Math.Max(cAlpha, pAlpha)), 0.0, 255.0);
                    }

                    if (a == 0)
                    {
                        pDst[idx] = (shadowA > 0) ? ((uint)shadowA << 24) : 0;
                        continue;
                    }

                    // Smooth, continuous C1 squircle dome model (Zero-crease, zero-triangle physical optics)
                    double hw = Math.Max(1.0, geom.HalfWidth);
                    double hh = Math.Max(1.0, geom.HalfHeight);
                    double ux = px / hw;
                    double uy = py / hh;
                    double ux2 = ux * ux;
                    double uy2 = uy * uy;
                    double ux4 = ux2 * ux2;
                    double uy4 = uy2 * uy2;

                    // Apple squircle radial coordinate rNorm in [0, 1] with perfectly smooth contours
                    double rNorm = Math.Clamp(Math.Pow(ux4 + uy4, 0.25), 0.0, 1.0);
                    double r2 = rNorm * rNorm;
                    double r4 = r2 * r2;

                    // Outward surface gradient
                    double vx = (ux2 * ux) / hw;
                    double vy = (uy2 * uy) / hh;
                    double vLen = Math.Sqrt(vx * vx + vy * vy);
                    double gx = 0.0, gy = 0.0;
                    if (vLen > 1e-7)
                    {
                        gx = vx / vLen;
                        gy = vy / vLen;
                    }

                    // Physical surface slope sinTheta: zero at center, smoothly steepening to 0.90 at outer edge
                    double slopeFactor = 0.30 * r2 + 0.70 * r4;
                    double sinTheta = 0.90 * slopeFactor;
                    double nz = Math.Sqrt(Math.Max(0.01, 1.0 - sinTheta * sinTheta));
                    double nx = gx * sinTheta;
                    double ny = gy * sinTheta;

                    // Continuous intensity factor: non-zero baseline in center (0.28), smooth peak at edges (1.0)
                    double k = 0.28 + 0.72 * slopeFactor;

                    // Symmetrical physical refraction displacement: vanishes at center, smoothly peaks at edges
                    double refractMag = 16.0;
                    double magShiftX = px * (0.040 + 0.030 * slopeFactor);
                    double magShiftY = py * (0.040 + 0.030 * slopeFactor);

                    double baseSx = Math.Clamp(x - nx * refractMag - magShiftX, 0.0, SurfaceWidth - 2.0);
                    double baseSy = Math.Clamp(y - ny * refractMag - magShiftY, 0.0, SurfaceHeight - 2.0);

                    // Chromatic Micro-Dispersion: sharp prism fringe at edges (1.8px), zero at center
                    double disp = 1.8 * slopeFactor;

                    int rawR, rawG, rawB;
                    if (disp > 0.04)
                    {
                        // Chromatic dispersion
                        double sxR = Math.Clamp(baseSx + gx * disp, 0.0, SurfaceWidth - 2.0);
                        double syR = Math.Clamp(baseSy + gy * disp, 0.0, SurfaceHeight - 2.0);
                        double sxB = Math.Clamp(baseSx - gx * disp, 0.0, SurfaceWidth - 2.0);
                        double syB = Math.Clamp(baseSy - gy * disp, 0.0, SurfaceHeight - 2.0);

                        int ixR = (int)sxR, iyR = (int)syR;
                        double fxR = sxR - ixR, fyR = syR - iyR;
                        int w00R = (int)((1.0 - fxR) * (1.0 - fyR) * 256.0);
                        int w10R = (int)(fxR * (1.0 - fyR) * 256.0);
                        int w01R = (int)((1.0 - fxR) * fyR * 256.0);
                        int w11R = Math.Max(0, 256 - (w00R + w10R + w01R));
                        int r0R = iyR * SurfaceWidth * 4, r1R = (iyR + 1) * SurfaceWidth * 4;
                        rawR = (pRaw[r0R + (ixR << 2) + 2] * w00R + pRaw[r0R + ((ixR + 1) << 2) + 2] * w10R +
                                pRaw[r1R + (ixR << 2) + 2] * w01R + pRaw[r1R + ((ixR + 1) << 2) + 2] * w11R) >> 8;

                        int ixG = (int)baseSx, iyG = (int)baseSy;
                        double fxG = baseSx - ixG, fyG = baseSy - iyG;
                        int w00G = (int)((1.0 - fxG) * (1.0 - fyG) * 256.0);
                        int w10G = (int)(fxG * (1.0 - fyG) * 256.0);
                        int w01G = (int)((1.0 - fxG) * fyG * 256.0);
                        int w11G = Math.Max(0, 256 - (w00G + w10G + w01G));
                        int r0G = iyG * SurfaceWidth * 4, r1G = (iyG + 1) * SurfaceWidth * 4;
                        rawG = (pRaw[r0G + (ixG << 2) + 1] * w00G + pRaw[r0G + ((ixG + 1) << 2) + 1] * w10G +
                                pRaw[r1G + (ixG << 2) + 1] * w01G + pRaw[r1G + ((ixG + 1) << 2) + 1] * w11G) >> 8;

                        int ixB = (int)sxB, iyB = (int)syB;
                        double fxB = sxB - ixB, fyB = syB - iyB;
                        int w00B = (int)((1.0 - fxB) * (1.0 - fyB) * 256.0);
                        int w10B = (int)(fxB * (1.0 - fyB) * 256.0);
                        int w01B = (int)((1.0 - fxB) * fyB * 256.0);
                        int w11B = Math.Max(0, 256 - (w00B + w10B + w01B));
                        int r0B = iyB * SurfaceWidth * 4, r1B = (iyB + 1) * SurfaceWidth * 4;
                        rawB = (pRaw[r0B + (ixB << 2)] * w00B + pRaw[r0B + ((ixB + 1) << 2)] * w10B +
                                pRaw[r1B + (ixB << 2)] * w01B + pRaw[r1B + ((ixB + 1) << 2)] * w11B) >> 8;
                    }
                    else
                    {
                        // Direct bilinear sample from full-res captured background
                        int ix = (int)baseSx, iy = (int)baseSy;
                        double fx = baseSx - ix, fy = baseSy - iy;
                        int w00 = (int)((1.0 - fx) * (1.0 - fy) * 256.0);
                        int w10 = (int)(fx * (1.0 - fy) * 256.0);
                        int w01 = (int)((1.0 - fx) * fy * 256.0);
                        int w11 = Math.Max(0, 256 - (w00 + w10 + w01));

                        int r0 = iy * SurfaceWidth * 4;
                        int r1 = (iy + 1) * SurfaceWidth * 4;
                        int o00 = r0 + (ix << 2);
                        int o10 = r0 + ((ix + 1) << 2);
                        int o01 = r1 + (ix << 2);
                        int o11 = r1 + ((ix + 1) << 2);

                        rawB = (pRaw[o00] * w00 + pRaw[o10] * w10 + pRaw[o01] * w01 + pRaw[o11] * w11) >> 8;
                        rawG = (pRaw[o00 + 1] * w00 + pRaw[o10 + 1] * w10 + pRaw[o01 + 1] * w01 + pRaw[o11 + 1] * w11) >> 8;
                        rawR = (pRaw[o00 + 2] * w00 + pRaw[o10 + 2] * w10 + pRaw[o01 + 2] * w01 + pRaw[o11 + 2] * w11) >> 8;
                    }

                    // Velvet Refracted Blur sample from half-res blurred buffers
                    double hx = Math.Clamp(baseSx * 0.5, 0.0, HalfWidth - 2.0);
                    double hy = Math.Clamp(baseSy * 0.5, 0.0, HalfHeight - 2.0);
                    int bix = (int)hx, biy = (int)hy;
                    double bfx = hx - bix, bfy = hy - biy;
                    int bw00 = (int)((1.0 - bfx) * (1.0 - bfy) * 256.0);
                    int bw10 = (int)(bfx * (1.0 - bfy) * 256.0);
                    int bw01 = (int)((1.0 - bfx) * bfy * 256.0);
                    int bw11 = Math.Max(0, 256 - (bw00 + bw10 + bw01));
                    int bo00 = (biy * HalfWidth + bix) * 4;
                    int bo10 = (biy * HalfWidth + bix + 1) * 4;
                    int bo01 = ((biy + 1) * HalfWidth + bix) * 4;
                    int bo11 = ((biy + 1) * HalfWidth + bix + 1) * 4;

                    int bStd = (pBlurred[bo00] * bw00 + pBlurred[bo10] * bw10 + pBlurred[bo01] * bw01 + pBlurred[bo11] * bw11) >> 8;
                    int gStd = (pBlurred[bo00 + 1] * bw00 + pBlurred[bo10 + 1] * bw10 + pBlurred[bo01 + 1] * bw01 + pBlurred[bo11 + 1] * bw11) >> 8;
                    int rStd = (pBlurred[bo00 + 2] * bw00 + pBlurred[bo10 + 2] * bw10 + pBlurred[bo01 + 2] * bw01 + pBlurred[bo11 + 2] * bw11) >> 8;

                    int bHvy = (pHeavyBlurred[bo00] * bw00 + pHeavyBlurred[bo10] * bw10 + pHeavyBlurred[bo01] * bw01 + pHeavyBlurred[bo11] * bw11) >> 8;
                    int gHvy = (pHeavyBlurred[bo00 + 1] * bw00 + pHeavyBlurred[bo10 + 1] * bw10 + pHeavyBlurred[bo01 + 1] * bw01 + pHeavyBlurred[bo11 + 1] * bw11) >> 8;
                    int rHvy = (pHeavyBlurred[bo00 + 2] * bw00 + pHeavyBlurred[bo10 + 2] * bw10 + pHeavyBlurred[bo01 + 2] * bw01 + pHeavyBlurred[bo11 + 2] * bw11) >> 8;

                    // Deep creamy cascade blur: 75% heavy in center, 100% heavy at edge
                    double heavyBlend = 0.75 + 0.25 * k;
                    int blurB = (int)(bStd * (1.0 - heavyBlend) + bHvy * heavyBlend);
                    int blurG = (int)(gStd * (1.0 - heavyBlend) + gHvy * heavyBlend);
                    int blurR = (int)(rStd * (1.0 - heavyBlend) + rHvy * heavyBlend);

                    // Distinct blur under resting media buttons, and specular rim crest for expanded liquid glass panel
                    double panelEdgeCrest = 0.0;
                    double btnBlurFactor = 0.0;
                    if (hasExpandedMusicPanel)
                    {
                        double pqx = Math.Abs(x - pcx) - strW;
                        double pqy = Math.Abs(y - pcy) - strH;
                        double pOutDist = Math.Sqrt(Math.Max(0.0, pqx) * Math.Max(0.0, pqx) + Math.Max(0.0, pqy) * Math.Max(0.0, pqy));
                        double pInDist = Math.Min(0.0, Math.Max(pqx, pqy));
                        double panelSdf = pOutDist + pInDist - panelR;
                        if (panelSdf >= -3.5 && panelSdf <= 1.5)
                        {
                            panelEdgeCrest = Math.Clamp(1.0 - Math.Abs(panelSdf + 0.8) / 2.2, 0.0, 1.0) * panelEase * expAlpha * musicTabBlend;
                        }
                    }
                    else if (isMusicTabActive && y >= 124 && y <= 168)
                    {
                        float bcx = 0, bhs = 0, br = 0;
                        if (x >= 152 && x <= 184) { bcx = 168f; bhs = 15f; br = 7f; }
                        else if (x >= 486 && x <= 518) { bcx = 502f; bhs = 15f; br = 7f; }

                        if (bhs > 0)
                        {
                            double bqx = Math.Abs(x - bcx) - (bhs - br);
                            double bqy = Math.Abs(y - 146.0) - (bhs - br);
                            double oDist = Math.Sqrt(Math.Max(0.0, bqx) * Math.Max(0.0, bqx) + Math.Max(0.0, bqy) * Math.Max(0.0, bqy));
                            double iDist = Math.Min(0.0, Math.Max(bqx, bqy));
                            double btnSdf = oDist + iDist - br;

                            if (btnSdf <= 1.0)
                            {
                                btnBlurFactor = Math.Clamp(-btnSdf + 0.5, 0.0, 1.0) * expAlpha * musicTabBlend;
                            }
                        }
                    }

                    // Optical diffusion mix: creamy backdrop blur with higher intensity at edges (86%), decreasing to 70% in center
                    double diffusionMix = 0.64 + 0.22 * k;
                    if (btnBlurFactor > 0.01)
                    {
                        diffusionMix = Math.Max(diffusionMix, 0.82 + btnBlurFactor * 0.16);
                    }

                    int trR = (int)(rawR * (1.0 - diffusionMix) + blurR * diffusionMix);
                    int trG = (int)(rawG * (1.0 - diffusionMix) + blurG * diffusionMix);
                    int trB = (int)(rawB * (1.0 - diffusionMix) + blurB * diffusionMix);

                    // Liquid glass elegant smoked transmission: 78% transmission + refined obsidian body
                    int rGlass = (trR * 200 + 40 * 56) >> 8;
                    int gGlass = (trG * 200 + 44 * 56) >> 8;
                    int bGlass = (trB * 200 + 56 * 56) >> 8;

                    // Specular Highlights & 3D Glass Illumination: sharpest at edges, tapering smoothly inward
                    double specKey = 0.0;
                    double specFill = 0.0;

                    // Key light highlight along upper surface (subtle gleaming crystal reflection)
                    double ndoth1 = Math.Max(0.0, nx * H1x + ny * H1y + nz * H1z);
                    if (ndoth1 > H1z)
                    {
                        double tilt1 = (ndoth1 - H1z) * InvOneMinusH1z;
                        double t2 = tilt1 * tilt1;
                        specKey = (t2 * t2) * (18.0 + 32.0 * k);
                    }

                    // Ambient fill bounce along bottom surface
                    double ndoth2 = Math.Max(0.0, nx * H2x + ny * H2y + nz * H2z);
                    if (ndoth2 > H2z)
                    {
                        double tilt2 = (ndoth2 - H2z) * InvOneMinusH2z;
                        specFill = (tilt2 * tilt2) * (6.0 + 12.0 * k);
                    }

                    // Fresnel grazing rim sheen
                    double oneMinusNz = 1.0 - nz;
                    double fresnelGlow = oneMinusNz * oneMinusNz * (10.0 + 24.0 * k);

                    // Gentle, tasteful upper meniscus edge light (subtle light presence)
                    double topLight = 0.0;
                    if (uy < 0.0 && rNorm > 0.65)
                    {
                        double topNorm = (-uy);
                        double edgeCrest = Math.Pow((rNorm - 0.65) / 0.35, 2.0);
                        topLight = topNorm * edgeCrest * 22.0;
                    }

                    // Liquid glass specular rim crest for expanded music card
                    if (panelEdgeCrest > 0.005)
                    {
                        specKey += panelEdgeCrest * 38.0;
                    }

                    // Composite liquid glass optics: balanced and tasteful
                    int finR = Math.Clamp(rGlass + (int)specKey + (int)specFill + (int)fresnelGlow + (int)topLight, 0, 255);
                    int finG = Math.Clamp(gGlass + (int)specKey + (int)specFill + (int)fresnelGlow + (int)topLight, 0, 255);
                    int finB = Math.Clamp(bGlass + (int)(specKey * 1.04) + (int)specFill + (int)(fresnelGlow * 1.08) + (int)(topLight * 1.04), 0, 255);

                    // Composite over contact/ambient shadow
                    byte finalA = (shadowA > 0 && a < 255)
                        ? (byte)Math.Min(255, a + ((shadowA * (255 - a)) >> 8))
                        : a;

                    uint pR = (uint)((finR * a) / 255);
                    uint pG = (uint)((finG * a) / 255);
                    uint pB = (uint)((finB * a) / 255);
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
                int startX = (int)Math.Round(geom.CenterX - timeW * 0.5);
                int startY = (int)Math.Round(geom.CenterY - timeH * 0.5);

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
                        bool isPpyTop = ppy < 0.0;
                        double pCurR = isPpyTop ? topR : botR;
                        double pCurStrW = isPpyTop ? straightWTop : straightWBot;
                        double pCurStrH = isPpyTop ? straightHTop : straightHBot;
                        double pqx = Math.Abs(ppx) - pCurStrW;
                        double pqy = Math.Abs(ppy) - pCurStrH;
                        double pOutX = Math.Max(0.0, pqx);
                        double pOutY = Math.Max(0.0, pqy);
                        double pOutDist = Math.Sqrt(pOutX * pOutX + pOutY * pOutY);
                        double pInDist = Math.Min(0.0, Math.Max(pqx, pqy));
                        double pSdf = pOutDist + pInDist - pCurR;

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
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running = false;
            _renderSignal.Set();
            _renderThread?.Join(500);

            lock (_expandedRenderLock)
            {
                _reusableSuperBmp?.Dispose();
                _reusableSuperBmp = null;
                _reusableFastBmp?.Dispose();
                _reusableFastBmp = null;
                _tabPrecomputeBmp?.Dispose();
                _tabPrecomputeBmp = null;
                _topBarBmp?.Dispose();
                _topBarBmp = null;
            }

            TimeEndPeriod(1);
            _audioMeter.Dispose();
            _screenCapturer?.Dispose();
            _renderSurface?.Dispose();
            _renderSignal.Dispose();
        }
        base.Dispose(disposing);
    }
}
