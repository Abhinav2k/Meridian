using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiquidGlassCircle;

internal sealed class OverlayForm : Form
{
    private const int CircleSize = 280;
    private const int WorkSize = CircleSize / 2; // 140
    private const uint WdaExcludeFromCapture = 0x11;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

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

    private readonly struct RefractionEntry
    {
        public readonly int Off00;
        public readonly int Off10;
        public readonly int Off01;
        public readonly int Off11;
        public readonly int W00;
        public readonly int W10;
        public readonly int W01;
        public readonly int W11;

        public RefractionEntry(int off00, int off10, int off01, int off11, int w00, int w10, int w01, int w11)
        {
            Off00 = off00;
            Off10 = off10;
            Off01 = off01;
            Off11 = off11;
            W00 = w00;
            W10 = w10;
            W01 = w01;
            W11 = w11;
        }
    }

    private static readonly RefractionEntry[] RefractionMap = PrecomputeRefractionMap();

    private readonly System.Windows.Forms.Timer _renderTimer = new() { Interval = 16 };
    private FastScreenCapturer? _screenCapturer;
    private Bitmap? _frontTexture;
    private Bitmap? _backTexture;
    private readonly byte[] _downscaledBuffer = new byte[WorkSize * WorkSize * 4];
    private readonly byte[] _blurHBuffer = new byte[WorkSize * WorkSize * 4];
    private readonly byte[] _blurredBuffer = new byte[WorkSize * WorkSize * 4];
    private readonly uint[] _refractedBuffer = new uint[WorkSize * WorkSize];
    private readonly object _textureLock = new();

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
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

        // Center initially on primary working area
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(
            workingArea.Left + (workingArea.Width - CircleSize) / 2,
            workingArea.Top + (workingArea.Height - CircleSize) / 2);

        // Shape the window to a true circle; clicks outside pass through to the OS
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, CircleSize, CircleSize);
        Region = new Region(path);

        _frontTexture = new Bitmap(CircleSize, CircleSize, PixelFormat.Format32bppArgb);
        _backTexture = new Bitmap(CircleSize, CircleSize, PixelFormat.Format32bppArgb);

        _renderTimer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.08) % (Math.PI * 2);
            RequestCapture();
            Invalidate();
        };

        Shown += (_, _) =>
        {
            _screenCapturer = new FastScreenCapturer(CircleSize, CircleSize);
            SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
            _renderTimer.Start();
            RequestCapture();
        };

        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        MouseClick += (_, e) => { if (e.Button == MouseButtons.Right) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: hidden from taskbar/Alt+Tab
            return parameters;
        }
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
        if (_screenCapturer == null || IsDisposed) return;
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
                    ProcessFrame(screenPos);
                } while (_updatePending && !IsDisposed);
            }
            finally
            {
                _isProcessing = false;
                if (!IsDisposed)
                {
                    try { BeginInvoke(new Action(Invalidate)); }
                    catch (InvalidOperationException) { }
                }
            }
        });
    }

    private unsafe void ProcessFrame(Point screenPos)
    {
        var capturer = _screenCapturer;
        if (capturer == null || !capturer.Capture(screenPos.X, screenPos.Y, CircleSize, CircleSize))
            return;

        byte* pRaw = (byte*)capturer.BitsPtr;
        if (pRaw == null) return;

        // 1. Fast 2x2 box downscale from 280x280 to 140x140
        fixed (byte* pDown = _downscaledBuffer)
        fixed (byte* pBlurH = _blurHBuffer)
        fixed (byte* pBlurred = _blurredBuffer)
        fixed (uint* pRefracted32 = _refractedBuffer)
        {
            for (int y = 0; y < WorkSize; y++)
            {
                int srcY0 = (y * 2) * CircleSize * 4;
                int srcY1 = (y * 2 + 1) * CircleSize * 4;
                int dstY = y * WorkSize * 4;

                for (int x = 0; x < WorkSize; x++)
                {
                    int srcX0 = x * 8;
                    int srcX1 = srcX0 + 4;
                    int dstX = dstY + x * 4;

                    pDown[dstX + 0] = (byte)((pRaw[srcY0 + srcX0 + 0] + pRaw[srcY0 + srcX1 + 0] + pRaw[srcY1 + srcX0 + 0] + pRaw[srcY1 + srcX1 + 0]) >> 2);
                    pDown[dstX + 1] = (byte)((pRaw[srcY0 + srcX0 + 1] + pRaw[srcY0 + srcX1 + 1] + pRaw[srcY1 + srcX0 + 1] + pRaw[srcY1 + srcX1 + 1]) >> 2);
                    pDown[dstX + 2] = (byte)((pRaw[srcY0 + srcX0 + 2] + pRaw[srcY0 + srcX1 + 2] + pRaw[srcY1 + srcX0 + 2] + pRaw[srcY1 + srcX1 + 2]) >> 2);
                    pDown[dstX + 3] = 255;
                }
            }

            // 2. Separable 5-tap Gaussian Blur (Horizontal) [1, 4, 6, 4, 1] / 16
            for (int y = 0; y < WorkSize; y++)
            {
                int rowOffset = y * WorkSize * 4;
                for (int x = 0; x < WorkSize; x++)
                {
                    int xm2 = Math.Max(0, x - 2);
                    int xm1 = Math.Max(0, x - 1);
                    int xp1 = Math.Min(WorkSize - 1, x + 1);
                    int xp2 = Math.Min(WorkSize - 1, x + 2);

                    int offM2 = rowOffset + xm2 * 4;
                    int offM1 = rowOffset + xm1 * 4;
                    int off0 = rowOffset + x * 4;
                    int offP1 = rowOffset + xp1 * 4;
                    int offP2 = rowOffset + xp2 * 4;

                    for (int c = 0; c < 3; c++)
                    {
                        int sum = pDown[offM2 + c] +
                                  (pDown[offM1 + c] << 2) +
                                  pDown[off0 + c] * 6 +
                                  (pDown[offP1 + c] << 2) +
                                  pDown[offP2 + c];
                        pBlurH[off0 + c] = (byte)(sum >> 4);
                    }
                    pBlurH[off0 + 3] = 255;
                }
            }

            // 3. Separable 5-tap Gaussian Blur (Vertical) [1, 4, 6, 4, 1] / 16
            for (int y = 0; y < WorkSize; y++)
            {
                int ym2 = Math.Max(0, y - 2) * WorkSize * 4;
                int ym1 = Math.Max(0, y - 1) * WorkSize * 4;
                int y0 = y * WorkSize * 4;
                int yp1 = Math.Min(WorkSize - 1, y + 1) * WorkSize * 4;
                int yp2 = Math.Min(WorkSize - 1, y + 2) * WorkSize * 4;

                for (int x = 0; x < WorkSize; x++)
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

            // 4. Precomputed Liquid Lens Refraction (zero floating point runtime cost)
            for (int i = 0; i < RefractionMap.Length; i++)
            {
                ref readonly var entry = ref RefractionMap[i];
                uint b = (uint)((pBlurred[entry.Off00 + 0] * entry.W00 + pBlurred[entry.Off10 + 0] * entry.W10 + pBlurred[entry.Off01 + 0] * entry.W01 + pBlurred[entry.Off11 + 0] * entry.W11) >> 8);
                uint g = (uint)((pBlurred[entry.Off00 + 1] * entry.W00 + pBlurred[entry.Off10 + 1] * entry.W10 + pBlurred[entry.Off01 + 1] * entry.W01 + pBlurred[entry.Off11 + 1] * entry.W11) >> 8);
                uint r = (uint)((pBlurred[entry.Off00 + 2] * entry.W00 + pBlurred[entry.Off10 + 2] * entry.W10 + pBlurred[entry.Off01 + 2] * entry.W01 + pBlurred[entry.Off11 + 2] * entry.W11) >> 8);
                pRefracted32[i] = (0xFFu << 24) | (r << 16) | (g << 8) | b;
            }

            // 5. Upscale 140x140 into the 280x280 back buffer bitmap
            if (_backTexture != null)
            {
                var bmpData = _backTexture.LockBits(new Rectangle(0, 0, CircleSize, CircleSize),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                uint* pDst32 = (uint*)bmpData.Scan0;

                for (int y = 0; y < WorkSize; y++)
                {
                    int srcRow = y * WorkSize;
                    int dstRow0 = (y * 2) * CircleSize;
                    int dstRow1 = (y * 2 + 1) * CircleSize;

                    for (int x = 0; x < WorkSize; x++)
                    {
                        uint pixel = pRefracted32[srcRow + x];
                        int dstX = x * 2;
                        pDst32[dstRow0 + dstX] = pixel;
                        pDst32[dstRow0 + dstX + 1] = pixel;
                        pDst32[dstRow1 + dstX] = pixel;
                        pDst32[dstRow1 + dstX + 1] = pixel;
                    }
                }
                _backTexture.UnlockBits(bmpData);

                // Ping-pong swap back texture to front texture
                lock (_textureLock)
                {
                    (_frontTexture, _backTexture) = (_backTexture, _frontTexture);
                }
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var circle = new Rectangle(0, 0, CircleSize, CircleSize);

        using var path = new GraphicsPath();
        path.AddEllipse(circle);
        graphics.SetClip(path);

        lock (_textureLock)
        {
            if (_frontTexture != null)
            {
                graphics.DrawImageUnscaled(_frontTexture, 0, 0);
            }
            else
            {
                using var fallback = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(180, 192, 224, 255),
                    SurroundColors = new[] { Color.FromArgb(85, 86, 122, 210) }
                };
                graphics.FillPath(fallback, path);
            }
        }

        // Apple-style soft liquid blue tint
        using (var tint = new SolidBrush(Color.FromArgb(34, 185, 212, 255)))
        {
            graphics.FillEllipse(tint, circle);
        }

        // Soft animating glint sheen
        var glintX = 80 + (int)(Math.Sin(_phase * 0.18) * 18);
        using (var glint = new LinearGradientBrush(new Rectangle(glintX - 66, 30, 132, CircleSize - 60),
                   Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
        {
            glint.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(0, Color.White), Color.FromArgb(24, Color.White),
                    Color.FromArgb(70, Color.White), Color.FromArgb(24, Color.White),
                    Color.FromArgb(0, Color.White)
                },
                Positions = new[] { 0f, .27f, .5f, .73f, 1f }
            };
            graphics.FillEllipse(glint, new Rectangle(glintX - 66, 30, 132, CircleSize - 60));
        }

        graphics.ResetClip();

        // Crisp white outer rim & subtle inner specular rim
        using var outer = new Pen(Color.FromArgb(195, Color.White), 2.2f);
        graphics.DrawEllipse(outer, 1.1f, 1.1f, CircleSize - 2.2f, CircleSize - 2.2f);

        using var inner = new Pen(Color.FromArgb(40, 255, 255, 255), 1.2f);
        graphics.DrawEllipse(inner, 3.2f, 3.2f, CircleSize - 6.4f, CircleSize - 6.4f);
    }

    private static RefractionEntry[] PrecomputeRefractionMap()
    {
        var map = new RefractionEntry[WorkSize * WorkSize];
        double center = (WorkSize - 1) * 0.5;

        for (int y = 0; y < WorkSize; y++)
        {
            for (int x = 0; x < WorkSize; x++)
            {
                double dx = x - center;
                double dy = y - center;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double normalized = Math.Min(distance / center, 1.0);
                double bend = Math.Pow(normalized, 3.1) * 4.0;
                double wave = Math.Sin(normalized * 18.0 + y * 0.018) * normalized;

                double safeDist = Math.Max(distance, 0.001);
                double sx = center + (dx / safeDist) * (distance - bend) + wave;
                double sy = center + (dy / safeDist) * (distance - bend) + (wave * 0.5);

                int ix = Math.Clamp((int)Math.Floor(sx), 0, WorkSize - 2);
                int iy = Math.Clamp((int)Math.Floor(sy), 0, WorkSize - 2);
                double fx = sx - ix;
                double fy = sy - iy;

                int w00 = (int)Math.Round((1.0 - fx) * (1.0 - fy) * 256.0);
                int w10 = (int)Math.Round(fx * (1.0 - fy) * 256.0);
                int w01 = (int)Math.Round((1.0 - fx) * fy * 256.0);
                int w11 = 256 - (w00 + w10 + w01);

                int off00 = (iy * WorkSize + ix) * 4;
                int off10 = (iy * WorkSize + ix + 1) * 4;
                int off01 = ((iy + 1) * WorkSize + ix) * 4;
                int off11 = ((iy + 1) * WorkSize + ix + 1) * 4;

                map[y * WorkSize + x] = new RefractionEntry(off00, off10, off01, off11, w00, w10, w01, w11);
            }
        }

        return map;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer.Dispose();
            _screenCapturer?.Dispose();
            _frontTexture?.Dispose();
            _backTexture?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class FastScreenCapturer : IDisposable
    {
        private readonly IntPtr _memDC;
        private readonly IntPtr _hBitmap;
        private readonly IntPtr _oldBitmap;
        private readonly IntPtr _pBits;

        public IntPtr BitsPtr => _pBits;

        public FastScreenCapturer(int width, int height)
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

