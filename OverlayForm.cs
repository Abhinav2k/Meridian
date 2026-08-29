using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiquidGlassCircle;

internal sealed class OverlayForm : Form
{
    private const int SurfaceWidth = 540;
    private const int SurfaceHeight = 110;
    private const int TargetPillWidth = 500;
    private const int TargetPillHeight = 64;
    private const int TopPadding = 18;
    private const double AnimationDuration = 1.15; // seconds

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

    private FastSurface? _screenCapturer;
    private FastSurface? _renderSurface;
    private readonly byte[] _blurHBuffer = new byte[SurfaceWidth * SurfaceHeight * 4];
    private readonly byte[] _blurredBuffer = new byte[SurfaceWidth * SurfaceHeight * 4];

    private readonly AutoResetEvent _renderSignal = new(false);
    private Thread? _renderThread;
    private volatile bool _running = true;
    private IntPtr _hwnd;
    private double _progress = 0.0;
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
            // 8ms interval (~120 FPS)
            _renderSignal.WaitOne(8);
            if (!_running) break;

            IntPtr hwnd = _hwnd;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
                continue;

            double dt = _frameStopwatch.Elapsed.TotalSeconds;
            _frameStopwatch.Restart();
            dt = Math.Clamp(dt, 0.0, 0.05);

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

            PillGeometry geom = ComputeGeometry(_progress);
            _currentGeometry = geom;

            ProcessAndPresent(new Point(rect.Left, rect.Top), geom);
        }
    }

    private static PillGeometry ComputeGeometry(double p)
    {
        double targetCenterX = SurfaceWidth * 0.5;
        double targetCenterY = TopPadding + (TargetPillHeight * 0.5);
        double targetHalfWidth = TargetPillWidth * 0.5;
        double targetHalfHeight = TargetPillHeight * 0.5;

        double spawnRadius = 14.0;
        double spawnCenterY = -spawnRadius;

        if (p <= 0.0)
        {
            return new PillGeometry(targetCenterX, spawnCenterY, spawnRadius, spawnRadius, spawnRadius);
        }

        double dropProgress = Math.Clamp(p / 0.35, 0.0, 1.0);
        double dropEase = EaseInOutCubic(dropProgress);
        double currentCenterY = spawnCenterY + (targetCenterY - spawnCenterY) * dropEase;

        double morphProgress = Math.Clamp((p - 0.20) / 0.80, 0.0, 1.0);
        double morphEase = EaseInOutQuad(morphProgress);

        double currentHalfWidth = spawnRadius + (targetHalfWidth - spawnRadius) * morphEase;
        double currentHalfHeight = spawnRadius + (targetHalfHeight - spawnRadius) * morphEase;

        currentHalfWidth = Math.Max(spawnRadius, currentHalfWidth);
        currentHalfHeight = Math.Max(spawnRadius, currentHalfHeight);
        double currentRadius = Math.Min(currentHalfWidth, currentHalfHeight);

        return new PillGeometry(targetCenterX, currentCenterY, currentHalfWidth, currentHalfHeight, currentRadius);
    }

    private static double EaseInOutCubic(double x)
    {
        return x < 0.5 ? 4.0 * x * x * x : 1.0 - Math.Pow(-2.0 * x + 2.0, 3.0) / 2.0;
    }

    private static double EaseInOutQuad(double x)
    {
        return x < 0.5 ? 2.0 * x * x : 1.0 - Math.Pow(-2.0 * x + 2.0, 2.0) / 2.0;
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

        fixed (byte* pBlurH = _blurHBuffer)
        fixed (byte* pBlurred = _blurredBuffer)
        {
            for (int y = 0; y < SurfaceHeight; y++)
            {
                int rowOffset = y * SurfaceWidth * 4;
                for (int x = 0; x < SurfaceWidth; x++)
                {
                    int xm2 = Math.Max(0, x - 2);
                    int xm1 = Math.Max(0, x - 1);
                    int xp1 = Math.Min(SurfaceWidth - 1, x + 1);
                    int xp2 = Math.Min(SurfaceWidth - 1, x + 2);

                    int offM2 = rowOffset + xm2 * 4;
                    int offM1 = rowOffset + xm1 * 4;
                    int off0 = rowOffset + x * 4;
                    int offP1 = rowOffset + xp1 * 4;
                    int offP2 = rowOffset + xp2 * 4;

                    for (int c = 0; c < 3; c++)
                    {
                        int sum = pRaw[offM2 + c] +
                                  (pRaw[offM1 + c] << 2) +
                                  pRaw[off0 + c] * 6 +
                                  (pRaw[offP1 + c] << 2) +
                                  pRaw[offP2 + c];
                        pBlurH[off0 + c] = (byte)(sum >> 4);
                    }
                    pBlurH[off0 + 3] = 255;
                }
            }

            for (int y = 0; y < SurfaceHeight; y++)
            {
                int ym2 = Math.Max(0, y - 2) * SurfaceWidth * 4;
                int ym1 = Math.Max(0, y - 1) * SurfaceWidth * 4;
                int y0 = y * SurfaceWidth * 4;
                int yp1 = Math.Min(SurfaceHeight - 1, y + 1) * SurfaceWidth * 4;
                int yp2 = Math.Min(SurfaceHeight - 1, y + 2) * SurfaceWidth * 4;

                for (int x = 0; x < SurfaceWidth; x++)
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

            for (int y = 0; y < SurfaceHeight; y++)
            {
                int rowOffset = y * SurfaceWidth * 4;
                for (int x = 0; x < SurfaceWidth; x++)
                {
                    int xm2 = Math.Max(0, x - 2);
                    int xm1 = Math.Max(0, x - 1);
                    int xp1 = Math.Min(SurfaceWidth - 1, x + 1);
                    int xp2 = Math.Min(SurfaceWidth - 1, x + 2);

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

            for (int y = 0; y < SurfaceHeight; y++)
            {
                int ym2 = Math.Max(0, y - 2) * SurfaceWidth * 4;
                int ym1 = Math.Max(0, y - 1) * SurfaceWidth * 4;
                int y0 = y * SurfaceWidth * 4;
                int yp1 = Math.Min(SurfaceHeight - 1, y + 1) * SurfaceWidth * 4;
                int yp2 = Math.Min(SurfaceHeight - 1, y + 2) * SurfaceWidth * 4;

                for (int x = 0; x < SurfaceWidth; x++)
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

                    double alphaVal = Math.Clamp(-sdf + 0.5, 0.0, 1.0);
                    byte a = (byte)Math.Round(alphaVal * 255.0);

                    if (a == 0)
                    {
                        pDst[idx] = 0;
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
                    double u = Math.Clamp(1.0 - (edgeDistance / 14.0), 0.0, 1.0);
                    double bend = Math.Pow(u, 2.5) * 6.5;

                    double sx = Math.Clamp(x - nx * bend, 0.0, SurfaceWidth - 2.0);
                    double sy = Math.Clamp(y - ny * bend, 0.0, SurfaceHeight - 2.0);

                    int ix = (int)Math.Floor(sx);
                    int iy = (int)Math.Floor(sy);
                    double fx = sx - ix;
                    double fy = sy - iy;

                    int w00 = (int)Math.Round((1.0 - fx) * (1.0 - fy) * 256.0);
                    int w10 = (int)Math.Round(fx * (1.0 - fy) * 256.0);
                    int w01 = (int)Math.Round((1.0 - fx) * fy * 256.0);
                    int w11 = Math.Max(0, 256 - (w00 + w10 + w01));

                    int off00 = (iy * SurfaceWidth + ix) * 4;
                    int off10 = (iy * SurfaceWidth + ix + 1) * 4;
                    int off01 = ((iy + 1) * SurfaceWidth + ix) * 4;
                    int off11 = ((iy + 1) * SurfaceWidth + ix + 1) * 4;

                    int b = (pBlurred[off00 + 0] * w00 + pBlurred[off10 + 0] * w10 + pBlurred[off01 + 0] * w01 + pBlurred[off11 + 0] * w11) >> 8;
                    int g = (pBlurred[off00 + 1] * w00 + pBlurred[off10 + 1] * w10 + pBlurred[off01 + 1] * w01 + pBlurred[off11 + 1] * w11) >> 8;
                    int r = (pBlurred[off00 + 2] * w00 + pBlurred[off10 + 2] * w10 + pBlurred[off01 + 2] * w01 + pBlurred[off11 + 2] * w11) >> 8;

                    b = Math.Clamp(b, 0, 255);
                    g = Math.Clamp(g, 0, 255);
                    r = Math.Clamp(r, 0, 255);

                    r = (r * 242 + 200 * 14) >> 8;
                    g = (g * 242 + 225 * 14) >> 8;
                    b = (b * 242 + 255 * 14) >> 8;

                    double topLightFactor = Math.Max(0.0, -py / geom.HalfHeight);
                    double domeArc = Math.Exp(-Math.Pow((sdf + 12.0) / 14.0, 2)) * topLightFactor;
                    int domeLight = (int)(domeArc * 30.0 * alphaVal);

                    double rimExp = Math.Exp(-Math.Pow((sdf + 1.2) / 1.3, 2));
                    int rimLight = (int)(rimExp * 215.0 * alphaVal);

                    double innerExp = Math.Exp(-Math.Pow((sdf + 4.5) / 1.8, 2));
                    int innerRim = (int)(innerExp * 45.0 * alphaVal);

                    int light = domeLight + rimLight + innerRim;
                    if (light > 0)
                    {
                        r = Math.Min(255, r + light);
                        g = Math.Min(255, g + light);
                        b = Math.Min(255, b + light);
                    }

                    uint pR = (uint)((r * a) / 255);
                    uint pG = (uint)((g * a) / 255);
                    uint pB = (uint)((b * a) / 255);
                    pDst[idx] = ((uint)a << 24) | (pR << 16) | (pG << 8) | pB;
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
