using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Meridian;

public static class LogoRenderer
{
    // Meridian Signature Colors
    private static readonly Color ObsidianDark = Color.FromArgb(10, 12, 18);
    private static readonly Color ObsidianDeep = Color.FromArgb(18, 22, 32);
    private static readonly Color CyanGlow = Color.FromArgb(0, 242, 254);
    private static readonly Color ElectricBlue = Color.FromArgb(0, 122, 255);
    private static readonly Color IrisViolet = Color.FromArgb(175, 82, 222);
    private static readonly Color CoralPink = Color.FromArgb(255, 45, 85);

    /// <summary>
    /// Renders the high-fidelity Meridian Dynamic Island logo into any Graphics context.
    /// </summary>
    public static void DrawLogo(Graphics g, float x, float y, float size, bool drawBackground = true)
    {
        var prevSmoothing = g.SmoothingMode;
        var prevInterpolation = g.InterpolationMode;
        var prevPixelOffset = g.PixelOffsetMode;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float cx = x + size * 0.5f;
        float cy = y + size * 0.5f;

        // 1. Squircle / Dark Glass Icon Canvas
        if (drawBackground)
        {
            float cornerRadius = size * 0.224f; // Apple iOS/macOS squircle proportion
            using var bgPath = GetRoundedPath(x, y, size, size, cornerRadius);

            // Deep obsidian space gradient
            using (var bgBrush = new LinearGradientBrush(
                new PointF(x, y),
                new PointF(x + size, y + size),
                ObsidianDark,
                ObsidianDeep))
            {
                g.FillPath(bgBrush, bgPath);
            }

            // Outer delicate glass border
            using (var borderPen = new Pen(Color.FromArgb(42, 255, 255, 255), Math.Max(1f, size * 0.015f)))
            {
                g.DrawPath(borderPen, bgPath);
            }
        }

        // 2. Optical Ambient Core Glow (Celestial Nebula / Iris Bloom)
        float glowRadius = size * 0.42f;
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(cx - glowRadius, cy - glowRadius, glowRadius * 2f, glowRadius * 2f);
            using (var pgb = new PathGradientBrush(glowPath))
            {
                pgb.CenterColor = Color.FromArgb(90, 0, 150, 255);
                pgb.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                g.FillPath(pgb, glowPath);
            }
        }

        // 3. The Meridian Celestial Orbit Rings (Rotated -22 degrees)
        float orbitAngle = -22f;
        float orbitW = size * 0.76f;
        float orbitH = size * 0.28f;
        float orbitThickness = Math.Max(1.5f, size * 0.024f);

        var stateOrbit = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(orbitAngle);

        // A. Back Half of Meridian Orbit (Behind the Island)
        using (var orbitBackPen = new Pen(Color.FromArgb(90, 0, 200, 255), orbitThickness))
        {
            g.DrawArc(orbitBackPen, -orbitW * 0.5f, -orbitH * 0.5f, orbitW, orbitH, 180f, 180f);
        }

        // Concentric secondary delicate celestial latitude line
        float innerOrbitW = orbitW * 0.88f;
        float innerOrbitH = orbitH * 0.85f;
        using (var innerPen = new Pen(Color.FromArgb(40, 180, 120, 255), Math.Max(1f, size * 0.01f)))
        {
            g.DrawArc(innerPen, -innerOrbitW * 0.5f, -innerOrbitH * 0.5f, innerOrbitW, innerOrbitH, 175f, 190f);
        }

        g.Restore(stateOrbit);

        // 4. Dynamic Island Central Floating Capsule
        float pillW = size * 0.48f;
        float pillH = size * 0.22f;
        float pillR = pillH * 0.5f;
        float pillX = cx - pillW * 0.5f;
        float pillY = cy - pillH * 0.5f;

        using (var pillPath = GetRoundedPath(pillX, pillY, pillW, pillH, pillR))
        {
            // Island Drop Shadow (Soft depth elevation)
            using (var shadowPath = GetRoundedPath(pillX, pillY + size * 0.025f, pillW, pillH, pillR))
            {
                using var shadowBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillPath(shadowBrush, shadowPath);
            }

            // Island Obsidian Glass Body
            using (var pillBrush = new LinearGradientBrush(
                new PointF(pillX, pillY),
                new PointF(pillX, pillY + pillH),
                Color.FromArgb(250, 14, 16, 22),
                Color.FromArgb(250, 6, 8, 12)))
            {
                g.FillPath(pillBrush, pillPath);
            }

            // Island Outer Glass Refraction Rim
            using (var pillBorderPen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, size * 0.016f)))
            {
                g.DrawPath(pillBorderPen, pillPath);
            }

            // Top Glass Specular Gloss Curve
            float glossH = pillH * 0.45f;
            using (var glossPath = GetRoundedPath(pillX + pillR * 0.2f, pillY + size * 0.01f, pillW - pillR * 0.4f, glossH, glossH * 0.5f))
            {
                using var glossBrush = new LinearGradientBrush(
                    new PointF(pillX, pillY),
                    new PointF(pillX, pillY + glossH),
                    Color.FromArgb(55, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255));
                g.FillPath(glossBrush, glossPath);
            }

            // Island Optical Sensors / Camera Lenses
            float lensSize = pillH * 0.44f;
            float leftLensX = pillX + pillW * 0.28f - lensSize * 0.5f;
            float rightSensorX = pillX + pillW * 0.72f - lensSize * 0.5f;
            float lensY = cy - lensSize * 0.5f;

            // Left Camera Aperture (Glass Reflection & Deep Navy Core)
            using (var lensBrush = new SolidBrush(Color.FromArgb(220, 15, 22, 36)))
            {
                g.FillEllipse(lensBrush, leftLensX, lensY, lensSize, lensSize);
            }
            using (var lensRim = new Pen(Color.FromArgb(90, 80, 140, 240), Math.Max(1f, size * 0.008f)))
            {
                g.DrawEllipse(lensRim, leftLensX, lensY, lensSize, lensSize);
            }
            // Anti-reflection optical dot
            using (var dotBrush = new SolidBrush(Color.FromArgb(200, 0, 200, 255)))
            {
                g.FillEllipse(dotBrush, leftLensX + lensSize * 0.55f, lensY + lensSize * 0.25f, lensSize * 0.28f, lensSize * 0.28f);
            }

            // Right Sensor / Ambient Light Indicator
            using (var sensorBrush = new SolidBrush(Color.FromArgb(200, 12, 18, 28)))
            {
                g.FillEllipse(sensorBrush, rightSensorX, lensY, lensSize * 0.85f, lensSize * 0.85f);
            }
            using (var dotGlow = new SolidBrush(Color.FromArgb(160, 52, 199, 89))) // Apple green indicator spark
            {
                g.FillEllipse(dotGlow, rightSensorX + lensSize * 0.3f, lensY + lensSize * 0.3f, lensSize * 0.25f, lensSize * 0.25f);
            }
        }

        // 5. Front Half of Meridian Celestial Orbit (Sweeps across in front of island)
        var stateOrbitFront = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(orbitAngle);

        // Front glowing gradient arc (Electric Cyan to Neon Blue to Iris Violet)
        using (var orbitFrontPen = new Pen(new LinearGradientBrush(
            new PointF(-orbitW * 0.5f, 0),
            new PointF(orbitW * 0.5f, 0),
            CyanGlow,
            IrisViolet), orbitThickness * 1.15f))
        {
            g.DrawArc(orbitFrontPen, -orbitW * 0.5f, -orbitH * 0.5f, orbitW, orbitH, 0f, 180f);
        }

        g.Restore(stateOrbitFront);

        // 6. Radiant Celestial Meridian Star (Apex beacon on the orbital ring)
        double radAngle = (orbitAngle) * Math.PI / 180.0;
        float starApexX = cx + (float)(Math.Cos(radAngle) * (orbitW * 0.49f));
        float starApexY = cy + (float)(Math.Sin(radAngle) * (orbitW * 0.49f));

        DrawCelestialStar(g, starApexX, starApexY, size * 0.11f);

        g.SmoothingMode = prevSmoothing;
        g.InterpolationMode = prevInterpolation;
        g.PixelOffsetMode = prevPixelOffset;
    }

    private static void DrawCelestialStar(Graphics g, float cx, float cy, float radius)
    {
        // Corona glow
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(cx - radius * 1.5f, cy - radius * 1.5f, radius * 3f, radius * 3f);
            using var pgb = new PathGradientBrush(glowPath);
            pgb.CenterColor = Color.FromArgb(200, 255, 255, 255);
            pgb.SurroundColors = new[] { Color.FromArgb(0, 0, 220, 255) };
            g.FillPath(pgb, glowPath);
        }

        // 4-point Diamond Star Ray
        using (var starPath = new GraphicsPath())
        {
            float rOuter = radius;
            float rInner = radius * 0.22f;

            PointF[] pts =
            {
                new(cx, cy - rOuter),
                new(cx + rInner, cy - rInner),
                new(cx + rOuter, cy),
                new(cx + rInner, cy + rInner),
                new(cx, cy + rOuter),
                new(cx - rInner, cy + rInner),
                new(cx - rOuter, cy),
                new(cx - rInner, cy - rInner)
            };
            starPath.AddPolygon(pts);

            using var brushStar = new SolidBrush(Color.White);
            g.FillPath(brushStar, starPath);
        }
    }

    /// <summary>
    /// Creates a high-resolution Bitmap of the Meridian logo at any dimension.
    /// </summary>
    public static Bitmap CreateBitmap(int size, bool drawBackground = true)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            DrawLogo(g, 0, 0, size, drawBackground);
        }
        return bmp;
    }

    /// <summary>
    /// Generates a valid multi-frame Windows .ico file containing standard PNG-encoded icon frames.
    /// </summary>
    public static void SaveIco(string filePath, int[] sizes)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR Header
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type: 1 = Icon
        bw.Write((ushort)sizes.Length); // Image count

        int offset = 6 + sizes.Length * 16;
        byte[][] pngBuffers = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
        {
            using var bmp = CreateBitmap(sizes[i], drawBackground: true);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngBuffers[i] = ms.ToArray();
        }

        // ICONDIRENTRY entries
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // Width
            bw.Write((byte)(sz >= 256 ? 0 : sz)); // Height
            bw.Write((byte)0); // Color count
            bw.Write((byte)0); // Reserved
            bw.Write((ushort)1); // Color planes
            bw.Write((ushort)32); // Bit count
            bw.Write((uint)pngBuffers[i].Length); // Bytes in resource
            bw.Write((uint)offset); // Offset
            offset += pngBuffers[i].Length;
        }

        // Image Data
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write(pngBuffers[i]);
        }
    }

    /// <summary>
    /// Generates a scalable vector SVG file of the Meridian logo.
    /// </summary>
    public static void SaveSvg(string filePath, int size = 512)
    {
        string svg = $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 {size} {size}"" width=""{size}"" height=""{size}"">
  <defs>
    <!-- Background Obsidian Gradient -->
    <linearGradient id=""obsidianGrad"" x1=""0%"" y1=""0%"" x2=""100%"" y2=""100%"">
      <stop offset=""0%"" stop-color=""#0A0C12"" />
      <stop offset=""100%"" stop-color=""#121620"" />
    </linearGradient>

    <!-- Meridian Orbital Arc Gradient -->
    <linearGradient id=""orbitGrad"" x1=""0%"" y1=""0%"" x2=""100%"" y2=""0%"">
      <stop offset=""0%"" stop-color=""#00F2FE"" />
      <stop offset=""50%"" stop-color=""#007AFF"" />
      <stop offset=""100%"" stop-color=""#AF52DE"" />
    </linearGradient>

    <!-- Island Body Gradient -->
    <linearGradient id=""islandGrad"" x1=""0%"" y1=""0%"" x2=""0%"" y2=""100%"">
      <stop offset=""0%"" stop-color=""#141822"" />
      <stop offset=""100%"" stop-color=""#06080C"" />
    </linearGradient>

    <!-- Core Nebula Glow Filter -->
    <radialGradient id=""nebulaGlow"" cx=""50%"" cy=""50%"" r=""50%"">
      <stop offset=""0%"" stop-color=""#0096FF"" stop-opacity=""0.45"" />
      <stop offset=""60%"" stop-color=""#AF52DE"" stop-opacity=""0.15"" />
      <stop offset=""100%"" stop-color=""#000000"" stop-opacity=""0"" />
    </radialGradient>

    <!-- Island Drop Shadow -->
    <filter id=""shadow"" x=""-10%"" y=""-20%"" width=""120%"" height=""150%"">
      <feDropShadow dx=""0"" dy=""10"" stdDeviation=""12"" flood-color=""#000000"" flood-opacity=""0.7"" />
    </filter>
  </defs>

  <!-- Squircle Canvas Body -->
  <rect x=""4"" y=""4"" width=""{size - 8}"" height=""{size - 8}"" rx=""{size * 0.224}"" fill=""url(#obsidianGrad)"" stroke=""rgba(255, 255, 255, 0.16)"" stroke-width=""3"" />

  <!-- Core Optical Glow -->
  <circle cx=""{size * 0.5}"" cy=""{size * 0.5}"" r=""{size * 0.42}"" fill=""url(#nebulaGlow)"" />

  <!-- Meridian Celestial Orbit (Back Arc) -->
  <g transform=""translate({size * 0.5}, {size * 0.5}) rotate(-22)"">
    <ellipse cx=""0"" cy=""0"" rx=""{size * 0.38}"" ry=""{size * 0.14}"" fill=""none"" stroke=""rgba(0, 200, 255, 0.35)"" stroke-width=""{size * 0.024}"" stroke-dasharray=""{size * 0.6} {size * 0.6}"" stroke-dashoffset=""{size * 0.6}"" />
    <ellipse cx=""0"" cy=""0"" rx=""{size * 0.34}"" ry=""{size * 0.12}"" fill=""none"" stroke=""rgba(175, 82, 222, 0.2)"" stroke-width=""1.5"" />
  </g>

  <!-- Central Dynamic Island Capsule -->
  <rect x=""{size * 0.26}"" y=""{size * 0.39}"" width=""{size * 0.48}"" height=""{size * 0.22}"" rx=""{size * 0.11}"" fill=""url(#islandGrad)"" stroke=""rgba(255, 255, 255, 0.24)"" stroke-width=""2.5"" filter=""url(#shadow)"" />

  <!-- Specular Top Rim Gloss -->
  <path d=""M {size * 0.34} {size * 0.405} Q {size * 0.5} {size * 0.40} {size * 0.66} {size * 0.405}"" fill=""none"" stroke=""rgba(255, 255, 255, 0.4)"" stroke-width=""2"" stroke-linecap=""round"" />

  <!-- Left Camera Aperture -->
  <circle cx=""{size * 0.39}"" cy=""{size * 0.5}"" r=""{size * 0.046}"" fill=""#0F1624"" stroke=""rgba(74, 144, 226, 0.6)"" stroke-width=""1.5"" />
  <circle cx=""{size * 0.402}"" cy=""{size * 0.488}"" r=""{size * 0.015}"" fill=""#00F2FE"" opacity=""0.9"" />

  <!-- Right Sensor Indicator -->
  <circle cx=""{size * 0.61}"" cy=""{size * 0.5}"" r=""{size * 0.04}"" fill=""#0C121C"" />
  <circle cx=""{size * 0.61}"" cy=""{size * 0.5}"" r=""{size * 0.012}"" fill=""#34C759"" opacity=""0.9"" />

  <!-- Meridian Celestial Orbit (Front Glowing Arc) -->
  <g transform=""translate({size * 0.5}, {size * 0.5}) rotate(-22)"">
    <path d=""M -{size * 0.38} 0 A {size * 0.38} {size * 0.14} 0 0 0 {size * 0.38} 0"" fill=""none"" stroke=""url(#orbitGrad)"" stroke-width=""{size * 0.026}"" stroke-linecap=""round"" />
  </g>

  <!-- Celestial Zenith Beacon Star -->
  <g transform=""translate({size * 0.81}, {size * 0.37})"">
    <circle cx=""0"" cy=""0"" r=""{size * 0.04}"" fill=""rgba(0, 242, 254, 0.4)"" />
    <path d=""M 0 -{size * 0.038} Q 0 0 {size * 0.038} 0 Q 0 0 0 {size * 0.038} Q 0 0 -{size * 0.038} 0 Q 0 0 0 -{size * 0.038} Z"" fill=""#FFFFFF"" />
  </g>
</svg>";

        File.WriteAllText(filePath, svg);
    }

    private static GraphicsPath GetRoundedPath(float x, float y, float w, float h, float r)
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
}
