using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiquidGlassCircle;

internal sealed class OverlayForm : Form
{
    private const int CircleSize = 280;
    private const uint WdaExcludeFromCapture = 0x11;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;
    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;
    private const int HtClient = 1;

    private const int WsExLayered = 0x80000;
    private const int WsExToolWindow = 0x80;
    private const uint UlwAlpha = 0x02;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

    private readonly struct RefractionPixel
    {
        public readonly int Off00, Off10, Off01, Off11;
        public readonly int W00, W10, W01, W11;
        public readonly byte Alpha;
        public readonly byte RimLight;
        public readonly byte InnerRim;

        public RefractionPixel(
            int off00, int off10, int off01, int off11,
            int w00, int w10, int w01, int w11,
            byte alpha, byte rimLight, byte innerRim)
        {
            Off00 = off00; Off10 = off10; Off01 = off01; Off11 = off11;
            W00 = w00; W10 = w10; W01 = w01; W11 = w11;
            Alpha = alpha;
            RimLight = rimLight;
            InnerRim = innerRim;
        }
    }

    private static readonly RefractionPixel[] RefractionMap = PrecomputeRefractionMap();

    // 8ms interval target for ~120 FPS high-refresh rate fluidity
    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 8 };
    private FastSurface? _screenCapturer;
    private FastSurface? _renderSurface;
    private readonly byte[] _blurHBuffer = new byte[CircleSize * CircleSize * 4];
    private readonly byte[] _blurredBuffer = new byte[CircleSize * CircleSize * 4];

    private volatile bool _isProcessing;
    private volatile bool _updatePending;
    private double _phase;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(CircleSize, CircleSize);

        // Center initially on primary working area
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(
            workingArea.Left + (workingArea.Width - CircleSize) / 2,
            workingArea.Top + (workingArea.Height - CircleSize) / 2);

        _renderTimer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.06) % (Math.PI * 2);
            RequestCapture();
        };

        Shown += (_, _) =>
        {
            _screenCapturer = new FastSurface(CircleSize, CircleSize);
            _renderSurface = new FastSurface(CircleSize, CircleSize);
            SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
            _renderTimer.Start();
            RequestCapture();
        };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.S) SaveSnapshot();
        };
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Right) Close(); };
    }

    private void SaveSnapshot()
    {
        try
        {
            var surface = _renderSurface;
            if (surface != null && surface.BitsPtr != IntPtr.Zero)
            {
                using var bmp = new Bitmap(CircleSize, CircleSize, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, CircleSize, CircleSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                unsafe
                {
                    Buffer.MemoryCopy((void*)surface.BitsPtr, (void*)data.Scan0, CircleSize * CircleSize * 4, CircleSize * CircleSize * 4);
                }
                bmp.UnlockBits(data);
                bmp.Save("liquid-glass-snapshot.png", ImageFormat.Png);
            }
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered;     // Per-pixel 32-bit ARGB DWM GPU compositing
            parameters.ExStyle |= WsExToolWindow;  // Hidden from taskbar / Alt+Tab
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var pt = PointToClient(Cursor.Position);
            double center = CircleSize * 0.5;
            double dx = pt.X - center;
            double dy = pt.Y - center;
            if ((dx * dx) + (dy * dy) > center * center)
            {
                m.Result = (IntPtr)HtTransparent; // Click-through outside circle
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RequestCapture();
    }

    private void RequestCapture()
    {
        if (_screenCapturer == null || _renderSurface == null || IsDisposed) return;
        _updatePending = true;
        if (_isProcessing) return;

        _isProcessing = true;
        _updatePending = false;
        var screenPos = Location;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                do
                {
                    _updatePending = false;
                    screenPos = Location;
                    ProcessAndPresent(screenPos);
                } while (_updatePending && !IsDisposed);
            }
            finally
            {
                _isProcessing = false;
            }
        });
    }

    private unsafe void ProcessAndPresent(Point screenPos)
    {
        var capturer = _screenCapturer;
        var surface = _renderSurface;
        if (capturer == null || surface == null || IsDisposed) return;

        if (!capturer.Capture(screenPos.X, screenPos.Y, CircleSize, CircleSize))
            return;

        byte* pRaw = (byte*)capturer.BitsPtr;
        uint* pDst = (uint*)surface.BitsPtr;
        if (pRaw == null || pDst == null) return;

        fixed (byte* pBlurH = _blurHBuffer)
        fixed (byte* pBlurred = _blurredBuffer)
        {
            // 1. Frosted Liquid Glass Multi-Pass Separable Gaussian Blur (2 passes of [1, 4, 6, 4, 1] / 16)
            // Pass 1: Horizontal (pRaw -> pBlurH)
            for (int y = 0; y < CircleSize; y++)
            {
                int rowOffset = y * CircleSize * 4;
                for (int x = 0; x < CircleSize; x++)
                {
                    int xm2 = Math.Max(0, x - 2);
                    int xm1 = Math.Max(0, x - 1);
                    int xp1 = Math.Min(CircleSize - 1, x + 1);
                    int xp2 = Math.Min(CircleSize - 1, x + 2);

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

            // Pass 1: Vertical (pBlurH -> pBlurred)
            for (int y = 0; y < CircleSize; y++)
            {
                int ym2 = Math.Max(0, y - 2) * CircleSize * 4;
                int ym1 = Math.Max(0, y - 1) * CircleSize * 4;
                int y0 = y * CircleSize * 4;
                int yp1 = Math.Min(CircleSize - 1, y + 1) * CircleSize * 4;
                int yp2 = Math.Min(CircleSize - 1, y + 2) * CircleSize * 4;

                for (int x = 0; x < CircleSize; x++)
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

            // Pass 2: Horizontal (pBlurred -> pBlurH)
            for (int y = 0; y < CircleSize; y++)
            {
                int rowOffset = y * CircleSize * 4;
                for (int x = 0; x < CircleSize; x++)
                {
                    int xm2 = Math.Max(0, x - 2);
                    int xm1 = Math.Max(0, x - 1);
                    int xp1 = Math.Min(CircleSize - 1, x + 1);
                    int xp2 = Math.Min(CircleSize - 1, x + 2);

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

            // Pass 2: Vertical (pBlurH -> pBlurred)
            for (int y = 0; y < CircleSize; y++)
            {
                int ym2 = Math.Max(0, y - 2) * CircleSize * 4;
                int ym1 = Math.Max(0, y - 1) * CircleSize * 4;
                int y0 = y * CircleSize * 4;
                int yp1 = Math.Min(CircleSize - 1, y + 1) * CircleSize * 4;
                int yp2 = Math.Min(CircleSize - 1, y + 2) * CircleSize * 4;

                for (int x = 0; x < CircleSize; x++)
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

            // 2. High-Clarity Liquid Lens Refraction & Specular Lighting
            int glintCenterX = 90 + (int)(Math.Sin(_phase * 0.18) * 22);

            for (int y = 0; y < CircleSize; y++)
            {
                int rowIdx = y * CircleSize;
                double gdy = (y - 140.0) / 100.0;
                double gdy2 = gdy * gdy;

                for (int x = 0; x < CircleSize; x++)
                {
                    int idx = rowIdx + x;
                    ref readonly var pixel = ref RefractionMap[idx];
                    int a = pixel.Alpha;
                    if (a == 0)
                    {
                        pDst[idx] = 0;
                        continue;
                    }

                    // Bilinear sample with strictly verified non-negative weights
                    int b = (pBlurred[pixel.Off00 + 0] * pixel.W00 + pBlurred[pixel.Off10 + 0] * pixel.W10 + pBlurred[pixel.Off01 + 0] * pixel.W01 + pBlurred[pixel.Off11 + 0] * pixel.W11) >> 8;
                    int g = (pBlurred[pixel.Off00 + 1] * pixel.W00 + pBlurred[pixel.Off10 + 1] * pixel.W10 + pBlurred[pixel.Off01 + 1] * pixel.W01 + pBlurred[pixel.Off11 + 1] * pixel.W11) >> 8;
                    int r = (pBlurred[pixel.Off00 + 2] * pixel.W00 + pBlurred[pixel.Off10 + 2] * pixel.W10 + pBlurred[pixel.Off01 + 2] * pixel.W01 + pBlurred[pixel.Off11 + 2] * pixel.W11) >> 8;

                    // Ensure clean bounds
                    b = Math.Clamp(b, 0, 255);
                    g = Math.Clamp(g, 0, 255);
                    r = Math.Clamp(r, 0, 255);

                    // Apple Liquid Glass Crystal Tint (high 95% transmission + subtle cool glass tone)
                    r = (r * 242 + 200 * 14) >> 8;
                    g = (g * 242 + 225 * 14) >> 8;
                    b = (b * 242 + 255 * 14) >> 8;

                    // Smooth animated glint sheen
                    double gdx = (x - glintCenterX) / 48.0;
                    double gdist2 = gdx * gdx + gdy2;
                    if (gdist2 < 1.0)
                    {
                        int glint = (int)((1.0 - gdist2) * 50.0);
                        r = Math.Min(255, r + glint);
                        g = Math.Min(255, g + glint);
                        b = Math.Min(255, b + glint);
                    }

                    // Specular outer rim & inner highlight
                    int rim = pixel.RimLight + pixel.InnerRim;
                    if (rim > 0)
                    {
                        r = Math.Min(255, r + rim);
                        g = Math.Min(255, g + rim);
                        b = Math.Min(255, b + rim);
                    }

                    // Premultiplied 32-bit ARGB for GPU compositor
                    uint pR = (uint)((r * a) / 255);
                    uint pG = (uint)((g * a) / 255);
                    uint pB = (uint)((b * a) / 255);
                    pDst[idx] = ((uint)a << 24) | (pR << 16) | (pG << 8) | pB;
                }
            }
        }

        // Direct DWM GPU compositing update
        if (!IsDisposed)
        {
            try
            {
                var ptDst = new POINT(screenPos.X, screenPos.Y);
                var size = new SIZE(CircleSize, CircleSize);
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
                        UpdateLayeredWindow(Handle, screenDC, ref ptDst, ref size, surface.MemDC, ref ptSrc, 0, ref blend, UlwAlpha);
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, screenDC);
                    }
                }
            }
            catch (Exception)
            {
                // Ignore any transient window state changes during shutdown
            }
        }
    }

    private static RefractionPixel[] PrecomputeRefractionMap()
    {
        var map = new RefractionPixel[CircleSize * CircleSize];
        double center = (CircleSize - 1) * 0.5;

        for (int y = 0; y < CircleSize; y++)
        {
            for (int x = 0; x < CircleSize; x++)
            {
                double dx = x - center;
                double dy = y - center;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double normalized = Math.Min(distance / center, 1.0);

                // Anti-aliased subpixel alpha falloff (138.5 to 140.0)
                double edgeDist = center - distance;
                double alphaFactor = Math.Clamp(edgeDist + 0.5, 0.0, 1.0);
                byte alpha = (byte)Math.Round(alphaFactor * 255.0);

                if (alpha == 0)
                {
                    map[y * CircleSize + x] = default;
                    continue;
                }

                // High-clarity liquid lens profile:
                // Inner 65% is crystal clear, outer 35% forms smooth liquid meniscus bend
                double bendBase = Math.Pow(normalized, 3.2) * 8.5;
                double wave = Math.Sin(normalized * 12.0 + y * 0.015) * normalized * 0.5;
                double safeDist = Math.Max(distance, 0.001);

                ComputeSample(center, dx, dy, safeDist, distance, bendBase, wave,
                    out int off00, out int off10, out int off01, out int off11,
                    out int w00, out int w10, out int w01, out int w11);

                // Specular outer rim & inner reflection highlight
                double rimExp = Math.Exp(-Math.Pow((distance - 137.2) / 1.5, 2));
                byte rimLight = (byte)Math.Clamp(Math.Round(rimExp * 215.0 * alphaFactor), 0, 255);

                double innerExp = Math.Exp(-Math.Pow((distance - 133.5) / 2.0, 2));
                byte innerRim = (byte)Math.Clamp(Math.Round(innerExp * 45.0 * alphaFactor), 0, 255);

                map[y * CircleSize + x] = new RefractionPixel(
                    off00, off10, off01, off11, w00, w10, w01, w11,
                    alpha, rimLight, innerRim);
            }
        }

        return map;
    }

    private static void ComputeSample(
        double center, double dx, double dy, double safeDist, double distance, double bend, double wave,
        out int off00, out int off10, out int off01, out int off11,
        out int w00, out int w10, out int w01, out int w11)
    {
        double sx = center + (dx / safeDist) * (distance - bend) + wave;
        double sy = center + (dy / safeDist) * (distance - bend) + (wave * 0.5);

        // Strictly clamp continuous coordinates within valid sampling box [0, CircleSize - 2]
        sx = Math.Clamp(sx, 0.0, CircleSize - 2.0);
        sy = Math.Clamp(sy, 0.0, CircleSize - 2.0);

        int ix = (int)Math.Floor(sx);
        int iy = (int)Math.Floor(sy);
        double fx = sx - ix;
        double fy = sy - iy;

        // fx and fy are now GUARANTEED in [0.0, 1.0]
        w00 = (int)Math.Round((1.0 - fx) * (1.0 - fy) * 256.0);
        w10 = (int)Math.Round(fx * (1.0 - fy) * 256.0);
        w01 = (int)Math.Round((1.0 - fx) * fy * 256.0);
        w11 = Math.Max(0, 256 - (w00 + w10 + w01));

        off00 = (iy * CircleSize + ix) * 4;
        off10 = (iy * CircleSize + ix + 1) * 4;
        off01 = ((iy + 1) * CircleSize + ix) * 4;
        off11 = ((iy + 1) * CircleSize + ix + 1) * 4;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer.Dispose();
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
            bmi.bmiHeader.biHeight = -height; // Top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

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
                return BitBlt(_memDC, 0, 0, width, height, screenDC, screenX, screenY, 0x00CC0020 /* SRCCOPY */);
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

