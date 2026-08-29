using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiquidGlassCircle;

internal sealed class OverlayForm : Form
{
    private const int CircleSize = 280;
    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint WdaNone = 0;
    private const int SwpNoActivate = 0x0010;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private Bitmap? _texture;
    private Point _circleLocation;
    private Point _dragOffset;
    private bool _dragging;
    private bool _capturing;
    private double _phase;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr windowHandle, uint affinity);

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Bounds = SystemInformation.VirtualScreen;

        _circleLocation = new Point(
            SystemInformation.WorkingArea.Left - Bounds.Left + (SystemInformation.WorkingArea.Width - CircleSize) / 2,
            SystemInformation.WorkingArea.Top - Bounds.Top + (SystemInformation.WorkingArea.Height - CircleSize) / 2);
        _timer.Tick += (_, _) => UpdateGlass();
        Shown += (_, _) =>
        {
            // DWM excludes the overlay from desktop capture, so sampling never
            // requires Hide/Show and the glass stays stable while it refreshes.
            SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
            _timer.Start();
            UpdateGlass();
        };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: no taskbar/Alt+Tab entry.
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            var point = PointToClient(Cursor.Position);
            var center = new Point(_circleLocation.X + CircleSize / 2, _circleLocation.Y + CircleSize / 2);
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            if ((dx * dx) + (dy * dy) > Math.Pow(CircleSize / 2, 2))
            {
                message.Result = new IntPtr(HtTransparent);
                return;
            }
        }
        base.WndProc(ref message);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Color.Magenta);

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var circle = new Rectangle(_circleLocation, new Size(CircleSize, CircleSize));
        using var path = new GraphicsPath();
        path.AddEllipse(circle);
        graphics.SetClip(path);
        if (_texture != null) graphics.DrawImage(_texture, circle);
        else
        {
            using var fallback = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(180, 192, 224, 255),
                SurroundColors = new[] { Color.FromArgb(85, 86, 122, 210) }
            };
            graphics.FillPath(fallback, path);
        }
        using (var tint = new SolidBrush(Color.FromArgb(34, 185, 212, 255))) graphics.FillEllipse(tint, circle);

        var glintX = circle.Left + 80 + (int)(Math.Sin(_phase * 0.18) * 18);
        using (var glint = new LinearGradientBrush(new Rectangle(glintX - 66, circle.Top + 30, 132, CircleSize - 60),
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
            graphics.FillEllipse(glint, new Rectangle(glintX - 66, circle.Top + 30, 132, CircleSize - 60));
        }
        graphics.ResetClip();

        using var outer = new Pen(Color.FromArgb(190, Color.White), 2.2f);
        graphics.DrawEllipse(outer, circle);
        // One clean rim keeps the glass shape crisp without turning it into a
        // stack of visible concentric rings.
    }

    private void UpdateGlass()
    {
        _phase = (_phase + 0.08) % (Math.PI * 2);
        var screen = new Point(Bounds.Left + _circleLocation.X, Bounds.Top + _circleLocation.Y);
        if (_capturing) return;
        _capturing = true;
        _ = Task.Run(() => CaptureAndProcess(screen)).ContinueWith(completed =>
        {
            Bitmap? texture = completed is { IsCompletedSuccessfully: true } ? completed.Result : null;
            if (IsDisposed) { texture?.Dispose(); return; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (texture == null) return;
                        _texture?.Dispose();
                        _texture = texture;
                        texture = null;
                        Invalidate();
                    }
                    finally
                    {
                        texture?.Dispose();
                        _capturing = false;
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                texture?.Dispose();
                _capturing = false;
            }
        }, TaskScheduler.Default);
    }

    private static Bitmap? CaptureAndProcess(Point screen)
    {
        try
        {
            using var captured = new Bitmap(CircleSize, CircleSize, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(captured);
            graphics.CopyFromScreen(screen, Point.Empty, captured.Size, CopyPixelOperation.SourceCopy);
            return RefractAndBlur(captured);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap RefractAndBlur(Bitmap source)
    {
        const int workSize = CircleSize / 2;
        var result = new Bitmap(CircleSize, CircleSize, PixelFormat.Format32bppArgb);
        var sourceData = source.LockBits(new Rectangle(0, 0, CircleSize, CircleSize), ImageLockMode.ReadOnly,
                                          PixelFormat.Format32bppArgb);
        var fullStride = Math.Abs(sourceData.Stride);
        var fullInput = new byte[fullStride * CircleSize];
        Marshal.Copy(sourceData.Scan0, fullInput, 0, fullInput.Length);
        source.UnlockBits(sourceData);

        // Work at half resolution. The final upscale is intentional: it gives
        // the material a softer, more convincing frosted-glass diffusion while
        // keeping drag-time rendering responsive.
        var input = new byte[workSize * workSize * 4];
        for (var y = 0; y < workSize; y++)
        for (var x = 0; x < workSize; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var sourceX = x * 2;
            var sourceY = y * 2;
            var sum = fullInput[(sourceY * fullStride) + (sourceX * 4) + channel]
                    + fullInput[(sourceY * fullStride) + ((sourceX + 1) * 4) + channel]
                    + fullInput[((sourceY + 1) * fullStride) + (sourceX * 4) + channel]
                    + fullInput[((sourceY + 1) * fullStride) + ((sourceX + 1) * 4) + channel];
            input[(y * workSize * 4) + (x * 4) + channel] = (byte)(sum / 4);
        }

        // Separable 5-tap Gaussian blur: enough diffusion to read as glass,
        // while keeping the work off the UI thread.
        var horizontal = new byte[input.Length];
        var kernel = new[] { 1, 4, 6, 4, 1 };
        for (var y = 0; y < workSize; y++)
        for (var x = 0; x < workSize; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var total = 0;
            for (var offset = -2; offset <= 2; offset++)
            {
                var sampleX = Math.Clamp(x + offset, 0, workSize - 1);
                total += input[(y * workSize * 4) + (sampleX * 4) + channel] * kernel[offset + 2];
            }
            horizontal[(y * workSize * 4) + (x * 4) + channel] = (byte)(total / 16);
        }
        var blurred = new byte[input.Length];
        for (var y = 0; y < workSize; y++)
        for (var x = 0; x < workSize; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var total = 0;
            for (var offset = -2; offset <= 2; offset++)
            {
                var sampleY = Math.Clamp(y + offset, 0, workSize - 1);
                total += horizontal[(sampleY * workSize * 4) + (x * 4) + channel] * kernel[offset + 2];
            }
            blurred[(y * workSize * 4) + (x * 4) + channel] = (byte)(total / 16);
        }

        var workOutput = new byte[workSize * workSize * 4];
        for (var y = 0; y < workSize; y++)
        for (var x = 0; x < workSize; x++)
        {
            var dx = x - (workSize - 1) * .5; var dy = y - (workSize - 1) * .5;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var normalized = Math.Min(distance / (workSize * .5), 1);
            var bend = Math.Pow(normalized, 3.1) * 4;
            var wave = Math.Sin(normalized * 18 + y * 0.018) * normalized;
            var center = (workSize - 1) * .5;
            var sx = center + dx / Math.Max(distance, .001) * (distance - bend) + wave;
            var sy = center + dy / Math.Max(distance, .001) * (distance - bend) + wave * .5;
            var ix = Math.Clamp((int)Math.Floor(sx), 0, workSize - 2);
            var iy = Math.Clamp((int)Math.Floor(sy), 0, workSize - 2);
            var fx = sx - ix;
            var fy = sy - iy;
            var outputIndex = (y * workSize + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                var a = blurred[(iy * workSize * 4) + (ix * 4) + c];
                var b = blurred[(iy * workSize * 4) + ((ix + 1) * 4) + c];
                var d = blurred[((iy + 1) * workSize * 4) + (ix * 4) + c];
                var e = blurred[((iy + 1) * workSize * 4) + ((ix + 1) * 4) + c];
                workOutput[outputIndex + c] = (byte)(a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + d * (1 - fx) * fy + e * fx * fy);
            }
            workOutput[outputIndex + 3] = 255;
        }
        var resultData = result.LockBits(new Rectangle(0, 0, CircleSize, CircleSize), ImageLockMode.WriteOnly,
                                          PixelFormat.Format32bppArgb);
        var output = new byte[CircleSize * CircleSize * 4];
        for (var y = 0; y < CircleSize; y++)
        for (var x = 0; x < CircleSize; x++)
        {
            var sourceIndex = ((y / 2) * workSize + (x / 2)) * 4;
            var outputIndex = (y * CircleSize + x) * 4;
            output[outputIndex] = workOutput[sourceIndex];
            output[outputIndex + 1] = workOutput[sourceIndex + 1];
            output[outputIndex + 2] = workOutput[sourceIndex + 2];
            output[outputIndex + 3] = 255;
        }
        Marshal.Copy(output, 0, resultData.Scan0, output.Length);
        result.UnlockBits(resultData);
        return result;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var dx = e.X - _circleLocation.X - CircleSize / 2;
        var dy = e.Y - _circleLocation.Y - CircleSize / 2;
        if ((dx * dx) + (dy * dy) > Math.Pow(CircleSize / 2, 2)) return;
        _dragging = true; _dragOffset = new Point(e.X - _circleLocation.X, e.Y - _circleLocation.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _circleLocation = new Point(Math.Clamp(e.X - _dragOffset.X, 0, Width - CircleSize),
                                    Math.Clamp(e.Y - _dragOffset.Y, 0, Height - CircleSize));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragging = false;
    protected override void Dispose(bool disposing) { if (disposing) { _timer.Dispose(); _texture?.Dispose(); } base.Dispose(disposing); }
}
