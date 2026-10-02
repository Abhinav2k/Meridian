using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Meridian;

public sealed partial class SettingsForm
{
    private static readonly Color TextWhite = Color.FromArgb(245, 245, 250);
    private static readonly Color TextSecondary = Color.FromArgb(170, 175, 190);
    private static readonly Color TextMuted = Color.FromArgb(120, 125, 140);
    private static readonly Color CardBg = Color.FromArgb(40, 255, 255, 255);
    private static readonly Color CardBorder = Color.FromArgb(60, 255, 255, 255);
    private static readonly Color AccentBlue = Color.FromArgb(45, 140, 255);
    private static readonly Color AccentGreen = Color.FromArgb(50, 215, 120);

    private void RenderUI()
    {
        var g = _reusableUiGraphics;
        if (g == null) return;

        g.ResetTransform();
        if (Math.Abs(_scale - 1.0f) > 0.001f)
        {
            g.ScaleTransform(_scale, _scale);
        }

        g.Clear(Color.FromArgb(0, 0, 0, 0));

        // Header
        RenderHeader(g);

        // Sidebar Navigation
        RenderSidebar(g);

        // Content Area
        RenderContentArea(g);

        // Toast Feedback Message
        RenderToast(g);

        g.Flush();
    }

    private void RenderHeader(Graphics g)
    {
        // Drag Area Header: X = CardX + 20, Y = CardY + 16
        using var fontTitle = UITheme.GetTitleFont(15f, FontStyle.Bold);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(TextWhite);
        using var brushMuted = new SolidBrush(TextMuted);

        // Glowing Orb / App Glyph
        int orbX = CardX + 22;
        int orbY = CardY + 18;
        using (var orbPath = new GraphicsPath())
        {
            orbPath.AddEllipse(orbX, orbY, 22, 22);
            using var lgb = new LinearGradientBrush(new Point(orbX, orbY), new Point(orbX + 22, orbY + 22),
                Color.FromArgb(220, 100, 180, 255), Color.FromArgb(160, 40, 90, 200));
            g.FillPath(lgb, orbPath);
            using var rimPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1.2f);
            g.DrawPath(rimPen, orbPath);
        }

        g.DrawString("MERIDIAN", fontTitle, brushWhite, orbX + 30, orbY - 1);
        g.DrawString("SETTINGS & DESKTOP CONTROL", fontSub, brushMuted, orbX + 31, orbY + 18);

        // Window Control Buttons (Minimize & Close)
        int btnY = CardY + 16;
        int closeX = CardX + CardWidth - 44;
        int minX = CardX + CardWidth - 80;

        // Minimize Button
        using (var minPath = GetRoundedPath(minX, btnY, 28, 28, 7))
        {
            Color minBg = _isMinHovered ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(30, 255, 255, 255);
            using var brushMin = new SolidBrush(minBg);
            g.FillPath(brushMin, minPath);
            using var penMin = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
            g.DrawPath(penMin, minPath);

            using var penLine = new Pen(TextWhite, 2f);
            g.DrawLine(penLine, minX + 8, btnY + 15, minX + 20, btnY + 15);
        }

        // Close Button
        using (var closePath = GetRoundedPath(closeX, btnY, 28, 28, 7))
        {
            Color closeBg = _isCloseHovered ? Color.FromArgb(200, 240, 60, 60) : Color.FromArgb(30, 255, 255, 255);
            using var brushClose = new SolidBrush(closeBg);
            g.FillPath(brushClose, closePath);
            using var penClose = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
            g.DrawPath(penClose, closePath);

            using var penX = new Pen(TextWhite, 1.8f);
            g.DrawLine(penX, closeX + 9, btnY + 9, closeX + 19, btnY + 19);
            g.DrawLine(penX, closeX + 19, btnY + 9, closeX + 9, btnY + 19);
        }

        // Header separator
        using var penSep = new Pen(Color.FromArgb(20, 255, 255, 255), 1f);
        g.DrawLine(penSep, CardX + 20, CardY + 54, CardX + CardWidth - 20, CardY + 54);
    }

    private void RenderSidebar(Graphics g)
    {
        int sideX = CardX + 20;
        int sideY = CardY + 68;
        int sideW = 186;
        int tabH = 42;
        int tabSpacing = 6;

        string[] tabs =
        {
            "Island & Notch",
            "Live Weather",
            "Media Players",
            "Shortcuts & System",
            "About & Keys"
        };

        using var fontTab = UITheme.GetFont(10f, FontStyle.Regular);
        using var fontTabBold = UITheme.GetFont(10f, FontStyle.Bold);

        for (int i = 0; i < tabs.Length; i++)
        {
            int currentY = sideY + i * (tabH + tabSpacing);
            bool isSelected = ((int)_activeTab == i);
            bool isHovered = (_hoveredTab == i);

            using var path = GetRoundedPath(sideX, currentY, sideW, tabH, 10);

            if (isSelected)
            {
                using var fillBrush = new SolidBrush(Color.FromArgb(50, 75, 140, 245));
                g.FillPath(fillBrush, path);
                using var borderPen = new Pen(Color.FromArgb(100, 100, 180, 255), 1.2f);
                g.DrawPath(borderPen, path);
            }
            else if (isHovered)
            {
                using var fillBrush = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
                g.FillPath(fillBrush, path);
            }

            // Tab Text
            Color txtColor = isSelected ? Color.White : (isHovered ? TextWhite : TextSecondary);
            using var brushTxt = new SolidBrush(txtColor);

            g.DrawString(tabs[i], isSelected ? fontTabBold : fontTab, brushTxt, sideX + 18, currentY + 12);
        }

        // Vertical divider
        using var penDiv = new Pen(Color.FromArgb(20, 255, 255, 255), 1f);
        g.DrawLine(penDiv, sideX + sideW + 14, CardY + 60, sideX + sideW + 14, CardY + CardHeight - 20);
    }

    private void RenderContentArea(Graphics g)
    {
        int contentX = CardX + 236;
        int contentY = CardY + 68;
        int contentW = 564;
        int contentH = CardHeight - 88;

        switch (_activeTab)
        {
            case SettingsTab.IslandNotch:
                RenderTabIslandNotch(g, contentX, contentY, contentW, contentH);
                break;
            case SettingsTab.LiveWeather:
                RenderTabWeather(g, contentX, contentY, contentW, contentH);
                break;
            case SettingsTab.MediaPlayers:
                RenderTabPlayers(g, contentX, contentY, contentW, contentH);
                break;
            case SettingsTab.ShortcutsSystem:
                RenderTabShortcuts(g, contentX, contentY, contentW, contentH);
                break;
            case SettingsTab.AboutKeys:
                RenderTabAbout(g, contentX, contentY, contentW, contentH);
                break;
        }
    }

    // ----------------------------------------------------
    // TAB 0: ISLAND & NOTCH
    // ----------------------------------------------------
    private void RenderTabIslandNotch(Graphics g, int x, int y, int w, int h)
    {
        using var fontHeader = UITheme.GetTitleFont(12f, FontStyle.Bold);
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(TextWhite);
        using var brushSec = new SolidBrush(TextSecondary);
        using var brushMuted = new SolidBrush(TextMuted);

        g.DrawString("Island & Bezel Notch Dynamics", fontHeader, brushWhite, x, y);
        g.DrawString("Control when and how the Dynamic Island docks flush against the screen bezel.", fontSub, brushSec, x, y + 22);

        int cardY = y + 46;

        // Card 1: Screen Bezel Attached Notch
        RenderGlassCard(g, x, cardY, w, 84);
        g.DrawString("Attached Bezel Notch Mode", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("Docks into top bezel with concave fillet ears when media is playing.", fontSub, brushMuted, x + 16, cardY + 38);

        bool notchOn = AppSettings.Current.MusicNotchEnabled;
        RenderToggleSwitch(g, x + w - 74, cardY + 26, notchOn);
        g.DrawString(notchOn ? "ENABLED" : "OFF", fontSub, notchOn ? new SolidBrush(AccentGreen) : brushMuted, x + w - 130, cardY + 32);

        cardY += 96;

        // Card 2: Music Start / Track Change Window
        RenderGlassCard(g, x, cardY, w, 114);
        g.DrawString("Music Start & Track Change Display Duration", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("How long the clock and media details remain visible before docking into the notch.", fontSub, brushMuted, x + 16, cardY + 38);

        double curSecs = AppSettings.Current.MusicTimeDisplaySeconds;
        (string label, double val)[] timerOptions =
        {
            ("Instant (0s)", 0.0),
            ("15 Seconds", 15.0),
            ("30 Seconds", 30.0),
            ("60 Seconds", 60.0),
            ("2 Minutes", 120.0)
        };

        int segW = 98;
        int segH = 32;
        int segSpacing = 8;
        int segStartX = x + 16;
        int segStartY = cardY + 64;

        for (int i = 0; i < timerOptions.Length; i++)
        {
            bool isCur = Math.Abs(curSecs - timerOptions[i].val) < 0.1;
            RenderSegmentButton(g, segStartX + i * (segW + segSpacing), segStartY, segW, segH, timerOptions[i].label, isCur);
        }

        cardY += 126;

        // Card 3: Idle Despawn / Unhover Duration
        RenderGlassCard(g, x, cardY, w, 114);
        g.DrawString("Idle Despawn / Unhover Collapse Duration", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("How long before the expanded island unhovers and returns to the compact pill.", fontSub, brushMuted, x + 16, cardY + 38);

        double curIdle = AppSettings.Current.IdleDespawnSeconds;
        (string label, double val)[] idleOptions =
        {
            ("15 Seconds", 15.0),
            ("30 Seconds", 30.0),
            ("60 Seconds", 60.0),
            ("120 Seconds", 120.0),
            ("Always Visible", 999999.0)
        };

        segStartX = x + 16;
        segStartY = cardY + 64;

        for (int i = 0; i < idleOptions.Length; i++)
        {
            bool isCur = Math.Abs(curIdle - idleOptions[i].val) < 0.1;
            RenderSegmentButton(g, segStartX + i * (segW + segSpacing), segStartY, segW, segH, idleOptions[i].label, isCur);
        }
    }

    // ----------------------------------------------------
    // TAB 1: LIVE WEATHER
    // ----------------------------------------------------
    private void RenderTabWeather(Graphics g, int x, int y, int w, int h)
    {
        using var fontHeader = UITheme.GetTitleFont(12f, FontStyle.Bold);
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(TextWhite);
        using var brushSec = new SolidBrush(TextSecondary);
        using var brushMuted = new SolidBrush(TextMuted);

        g.DrawString("Live Weather Configuration", fontHeader, brushWhite, x, y);
        g.DrawString("Real-time meteorological updates from Open-Meteo with Bauhaus artwork.", fontSub, brushSec, x, y + 22);

        int cardY = y + 46;

        // Card 1: IP Geolocation Toggle
        RenderGlassCard(g, x, cardY, w, 74);
        g.DrawString("Auto-Detect Location (IP Geolocation)", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("Automatically fetches local weather based on public IP address.", fontSub, brushMuted, x + 16, cardY + 38);

        RenderToggleSwitch(g, x + w - 74, cardY + 22, _weatherUseAuto);

        cardY += 86;

        // Card 2: Manual Location Inputs
        RenderGlassCard(g, x, cardY, w, 220);
        g.DrawString("Manual Location Coordinates", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("Used when auto-detection is disabled or for fixed weather cards.", fontSub, brushMuted, x + 16, cardY + 36);

        // Inputs
        int inputY1 = cardY + 62;
        int inputY2 = cardY + 114;
        int inputW = 250;

        g.DrawString("City Name:", fontSub, brushSec, x + 16, inputY1 + 6);
        RenderInputField(g, "weather_city", _weatherCity, x + 90, inputY1, inputW, 30);

        g.DrawString("Country:", fontSub, brushSec, x + 360, inputY1 + 6);
        RenderInputField(g, "weather_country", _weatherCountry, x + 420, inputY1, 100, 30);

        g.DrawString("Latitude:", fontSub, brushSec, x + 16, inputY2 + 6);
        RenderInputField(g, "weather_lat", _weatherLat, x + 90, inputY2, inputW, 30);

        g.DrawString("Longitude:", fontSub, brushSec, x + 360, inputY2 + 6);
        RenderInputField(g, "weather_lon", _weatherLon, x + 420, inputY2, 100, 30);

        // Action Buttons
        int btnY = cardY + 166;
        RenderActionButton(g, "Save Weather Config", x + 16, btnY, 170, 34, AccentBlue);
        RenderActionButton(g, "Refresh Weather Now", x + 196, btnY, 170, 34, Color.FromArgb(60, 255, 255, 255));

        // Status text
        g.DrawString($"Status: {_weatherStatus}", fontSub, new SolidBrush(AccentGreen), x + 380, btnY + 8);
    }

    // ----------------------------------------------------
    // TAB 2: MEDIA PLAYERS
    // ----------------------------------------------------
    private void RenderTabPlayers(Graphics g, int x, int y, int w, int h)
    {
        using var fontHeader = UITheme.GetTitleFont(12f, FontStyle.Bold);
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(TextWhite);
        using var brushSec = new SolidBrush(TextSecondary);
        using var brushMuted = new SolidBrush(TextMuted);

        g.DrawString("Media Launchers & External Players", fontHeader, brushWhite, x, y);
        g.DrawString("Configure media apps launched directly from the Dynamic Island disc icon.", fontSub, brushSec, x, y + 22);

        int cardY = y + 46;

        // Card 1: Configured Players List
        int listH = Math.Min(150, Math.Max(70, _players.Count * 44 + 36));
        RenderGlassCard(g, x, cardY, w, listH);
        g.DrawString("Configured Launchers:", fontBody, brushWhite, x + 16, cardY + 12);

        for (int i = 0; i < _players.Count; i++)
        {
            var p = _players[i];
            int rowY = cardY + 36 + i * 40;

            string iconText = p.Icon.ToLower().Contains("spotify") ? "🟢 Spotify" :
                             p.Icon.ToLower().Contains("yt") ? "🔴 YouTube Music" : "🎵 Music";

            g.DrawString(iconText, fontBody, brushWhite, x + 20, rowY + 6);
            string displayUrl = p.Url.Length > 36 ? p.Url.Substring(0, 33) + "..." : p.Url;
            g.DrawString(displayUrl, fontSub, brushMuted, x + 180, rowY + 8);

            // Row Action Buttons
            RenderActionButton(g, "Launch", x + w - 160, rowY + 2, 68, 28, Color.FromArgb(50, 120, 240));
            RenderActionButton(g, "Delete", x + w - 82, rowY + 2, 64, 28, Color.FromArgb(180, 60, 60));
        }

        cardY += listH + 16;

        // Card 2: Add New Player
        RenderGlassCard(g, x, cardY, w, 160);
        g.DrawString("Add New Media Launcher", fontBody, brushWhite, x + 16, cardY + 14);

        int addY1 = cardY + 42;
        int addY2 = cardY + 82;

        g.DrawString("App Name:", fontSub, brushSec, x + 16, addY1 + 6);
        RenderInputField(g, "new_player_name", _newPlayerName, x + 90, addY1, 140, 30);

        g.DrawString("Icon:", fontSub, brushSec, x + 250, addY1 + 6);
        (string icLabel, string icVal)[] icOptions = { ("Spotify", "spotify"), ("YT Music", "ytmusic"), ("Music", "music") };
        for (int i = 0; i < icOptions.Length; i++)
        {
            bool isSel = _newPlayerIcon.Equals(icOptions[i].icVal, StringComparison.OrdinalIgnoreCase);
            RenderSegmentButton(g, x + 295 + i * 82, addY1, 76, 30, icOptions[i].icLabel, isSel);
        }

        g.DrawString("Path / URL:", fontSub, brushSec, x + 16, addY2 + 6);
        RenderInputField(g, "new_player_url", _newPlayerUrl, x + 90, addY2, 340, 30);
        RenderActionButton(g, "Browse...", x + 440, addY2, 90, 30, Color.FromArgb(60, 255, 255, 255));

        RenderActionButton(g, "+ Add Player", x + 16, cardY + 122, 130, 30, AccentBlue);
    }

    // ----------------------------------------------------
    // TAB 3: SHORTCUTS & SYSTEM
    // ----------------------------------------------------
    private void RenderTabShortcuts(Graphics g, int x, int y, int w, int h)
    {
        using var fontHeader = UITheme.GetTitleFont(12f, FontStyle.Bold);
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(TextWhite);
        using var brushSec = new SolidBrush(TextSecondary);
        using var brushMuted = new SolidBrush(TextMuted);

        g.DrawString("Desktop Shortcuts & Launch Options", fontHeader, brushWhite, x, y);
        g.DrawString("Create Windows Desktop shortcuts to launch Settings and Dynamic Island independently.", fontSub, brushSec, x, y + 22);

        int cardY = y + 46;

        // Card 1: Desktop Settings Shortcut
        RenderGlassCard(g, x, cardY, w, 78);
        g.DrawString("Desktop Settings Shortcut", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("Generates 'Meridian Settings.lnk' on your Windows Desktop.", fontSub, brushMuted, x + 16, cardY + 38);

        bool settingsLnkPresent = AppSettings.IsDesktopShortcutPresent("Meridian Settings.lnk");
        if (settingsLnkPresent)
        {
            g.DrawString("✓ Installed on Desktop", fontSub, new SolidBrush(AccentGreen), x + w - 340, cardY + 30);
        }
        RenderActionButton(g, "⭐ Settings Shortcut", x + w - 176, cardY + 22, 160, 34, AccentBlue);

        cardY += 88;

        // Card 2: Desktop Dynamic Island Shortcut
        RenderGlassCard(g, x, cardY, w, 78);
        g.DrawString("Desktop Dynamic Island Shortcut", fontBody, brushWhite, x + 16, cardY + 16);
        g.DrawString("Generates 'Meridian Island.lnk' on your Windows Desktop.", fontSub, brushMuted, x + 16, cardY + 38);

        bool islandLnkPresent = AppSettings.IsDesktopShortcutPresent("Meridian Island.lnk");
        if (islandLnkPresent)
        {
            g.DrawString("✓ Installed on Desktop", fontSub, new SolidBrush(AccentGreen), x + w - 340, cardY + 30);
        }
        RenderActionButton(g, "⭐ Island Shortcut", x + w - 176, cardY + 22, 160, 34, Color.FromArgb(60, 255, 255, 255));

        cardY += 88;

        // Card 3: Windows Startup
        RenderGlassCard(g, x, cardY, w, 70);
        g.DrawString("Start Dynamic Island with Windows", fontBody, brushWhite, x + 16, cardY + 14);
        g.DrawString("Automatically starts the Dynamic Island in the background when you log in.", fontSub, brushMuted, x + 16, cardY + 36);

        bool isStartup = AppSettings.IsRunOnStartup();
        RenderToggleSwitch(g, x + w - 74, cardY + 22, isStartup);

        cardY += 80;

        // Card 4: Hardware Backdrop & Glass Translucency
        RenderGlassCard(g, x, cardY, w, 172);
        g.DrawString("Window Backdrop Material (Windows 11 DWM)", fontBody, brushWhite, x + 16, cardY + 12);
        g.DrawString("Translucent Acrylic blurs background apps & desktop; Mica samples wallpaper.", fontSub, brushMuted, x + 16, cardY + 30);

        int curBackdrop = AppSettings.Current.BackdropType;
        (string bLabel, int bVal)[] backdropOptions =
        {
            ("Translucent Acrylic", 3),
            ("Mica Alt", 4),
            ("Mica", 2)
        };

        int bBtnW = 160;
        int bBtnH = 30;
        int bSpacing = 10;
        int bStartX = x + 16;
        int bStartY = cardY + 48;

        for (int i = 0; i < backdropOptions.Length; i++)
        {
            bool isCur = (curBackdrop == backdropOptions[i].bVal);
            int optX = bStartX + i * (bBtnW + bSpacing);
            using var optPath = GetRoundedPath(optX, bStartY, bBtnW, bBtnH, 7);
            using var brushOpt = new SolidBrush(isCur ? AccentBlue : Color.FromArgb(25, 255, 255, 255));
            g.FillPath(brushOpt, optPath);
            using var penOpt = new Pen(isCur ? AccentBlue : Color.FromArgb(45, 255, 255, 255), 1f);
            g.DrawPath(penOpt, optPath);

            using var fontOpt = UITheme.GetFont(9f, isCur ? FontStyle.Bold : FontStyle.Regular);
            string displayLabel = isCur ? $"✓ {backdropOptions[i].bLabel}" : backdropOptions[i].bLabel;
            var optSize = g.MeasureString(displayLabel, fontOpt);
            g.DrawString(displayLabel, fontOpt, isCur ? brushWhite : brushSec, optX + (bBtnW - optSize.Width) / 2f, bStartY + (bBtnH - optSize.Height) / 2f);
        }

        // Inner separator
        using var penInner = new Pen(Color.FromArgb(20, 255, 255, 255), 1f);
        g.DrawLine(penInner, x + 16, cardY + 88, x + w - 16, cardY + 88);

        // Section 2: Glass Translucency Level
        g.DrawString("Glass Translucency & Opacity (Layered Alpha)", fontBody, brushWhite, x + 16, cardY + 98);
        g.DrawString("Adjusts see-through transparency across the entire window and background blur.", fontSub, brushMuted, x + 16, cardY + 116);

        double curOpacity = AppSettings.Current.WindowOpacity;
        (string tLabel, double tVal)[] translucencyOptions =
        {
            ("High (78%)", 0.78),
            ("Balanced (85%)", 0.85),
            ("Subtle (92%)", 0.92),
            ("Solid (100%)", 1.0)
        };

        int tBtnW = 120;
        int tBtnH = 30;
        int tSpacing = 8;
        int tStartX = x + 16;
        int tStartY = cardY + 134;

        for (int i = 0; i < translucencyOptions.Length; i++)
        {
            bool isCur = Math.Abs(curOpacity - translucencyOptions[i].tVal) < 0.035;
            int optX = tStartX + i * (tBtnW + tSpacing);
            using var optPath = GetRoundedPath(optX, tStartY, tBtnW, tBtnH, 7);
            using var brushOpt = new SolidBrush(isCur ? AccentBlue : Color.FromArgb(25, 255, 255, 255));
            g.FillPath(brushOpt, optPath);
            using var penOpt = new Pen(isCur ? AccentBlue : Color.FromArgb(45, 255, 255, 255), 1f);
            g.DrawPath(penOpt, optPath);

            using var fontOpt = UITheme.GetFont(9f, isCur ? FontStyle.Bold : FontStyle.Regular);
            string displayLabel = isCur ? $"✓ {translucencyOptions[i].tLabel}" : translucencyOptions[i].tLabel;
            var optSize = g.MeasureString(displayLabel, fontOpt);
            g.DrawString(displayLabel, fontOpt, isCur ? brushWhite : brushSec, optX + (tBtnW - optSize.Width) / 2f, tStartY + (tBtnH - optSize.Height) / 2f);
        }
    }

    // ----------------------------------------------------
    // TAB 4: ABOUT & SYSTEM
    // ----------------------------------------------------
    private void RenderTabAbout(Graphics g, int x, int y, int w, int h)
    {
        using var fontHeader = UITheme.GetTitleFont(12f, FontStyle.Bold);
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var fontCode = UITheme.GetFont(8.5f, FontStyle.Bold);
        using var brushWhite = new SolidBrush(TextWhite);
        using var brushSec = new SolidBrush(TextSecondary);
        using var brushMuted = new SolidBrush(TextMuted);

        g.DrawString("Meridian Dynamic Island · v1.0", fontHeader, brushWhite, x, y);
        g.DrawString(".NET 10 Windows Forms with Unsafe Optical SDF Lens Refraction.", fontSub, brushSec, x, y + 22);

        int cardY = y + 46;

        // Process status
        bool isIslandRunning = Process.GetProcessesByName("Meridian").Length > 1; // 1 is SettingsForm itself
        RenderGlassCard(g, x, cardY, w, 84);
        g.DrawString("Dynamic Island Process Status", fontBody, brushWhite, x + 16, cardY + 16);

        string statusText = isIslandRunning ? "🟢 Active & Running on Screen" : "⚪ Dynamic Island Not Running";
        Color statusColor = isIslandRunning ? AccentGreen : Color.FromArgb(200, 180, 80);
        g.DrawString(statusText, fontSub, new SolidBrush(statusColor), x + 16, cardY + 38);

        RenderActionButton(g, "Restart Island", x + w - 210, cardY + 24, 96, 34, Color.FromArgb(60, 255, 255, 255));
        RenderActionButton(g, isIslandRunning ? "Stop Island" : "Launch Island", x + w - 104, cardY + 24, 92, 34, AccentBlue);

        cardY += 96;

        // Keyboard Shortcuts
        RenderGlassCard(g, x, cardY, w, 190);
        g.DrawString("Keyboard Shortcuts Reference", fontBody, brushWhite, x + 16, cardY + 14);

        (string key, string desc)[] shortcuts =
        {
            ("S", "Toggle Sleep / Ambient Mode"),
            ("W", "Toggle Weather & Music Panels"),
            ("Space", "Play / Pause Active Media"),
            ("N / P", "Next Track / Previous Track"),
            ("Esc", "Dock Island into Bezel Notch / Minimize")
        };

        for (int i = 0; i < shortcuts.Length; i++)
        {
            int rowY = cardY + 40 + i * 28;
            using var pillPath = GetRoundedPath(x + 16, rowY, 56, 22, 6);
            using var fillBrush = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
            g.FillPath(fillBrush, pillPath);
            using var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            g.DrawPath(pen, pillPath);

            g.DrawString(shortcuts[i].key, fontCode, brushWhite, x + 24, rowY + 3);
            g.DrawString(shortcuts[i].desc, fontSub, brushSec, x + 84, rowY + 4);
        }
    }

    // ----------------------------------------------------
    // COMMON UI CONTROLS & UTILITIES
    // ----------------------------------------------------
    private void RenderGlassCard(Graphics g, int x, int y, int w, int h)
    {
        using var path = GetRoundedPath(x, y, w, h, 14);
        using var fillBrush = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
        g.FillPath(fillBrush, path);
        using var borderPen = new Pen(Color.FromArgb(32, 255, 255, 255), 1f);
        g.DrawPath(borderPen, path);
    }

    private void RenderToggleSwitch(Graphics g, int x, int y, bool isChecked)
    {
        int swW = 46;
        int swH = 26;
        using var path = GetRoundedPath(x, y, swW, swH, 13);

        Color trackBg = isChecked ? AccentGreen : Color.FromArgb(50, 255, 255, 255);
        using var brush = new SolidBrush(trackBg);
        g.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
        g.DrawPath(pen, path);

        // Thumb
        int thumbX = isChecked ? (x + swW - 24) : (x + 3);
        int thumbY = y + 3;
        using (var thumbPath = new GraphicsPath())
        {
            thumbPath.AddEllipse(thumbX, thumbY, 20, 20);
            using var thumbBrush = new SolidBrush(Color.White);
            g.FillPath(thumbBrush, thumbPath);
        }
    }

    private void RenderSegmentButton(Graphics g, int x, int y, int w, int h, string text, bool isSelected)
    {
        using var path = GetRoundedPath(x, y, w, h, 8);
        Color bg = isSelected ? AccentBlue : Color.FromArgb(25, 255, 255, 255);
        using var brush = new SolidBrush(bg);
        g.FillPath(brush, path);

        using var pen = new Pen(isSelected ? Color.FromArgb(160, 140, 200, 255) : Color.FromArgb(40, 255, 255, 255), 1f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(8.5f, isSelected ? FontStyle.Bold : FontStyle.Regular);
        using var brushTxt = new SolidBrush(isSelected ? Color.White : TextSecondary);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brushTxt, new RectangleF(x, y, w, h), sf);
    }

    private void RenderActionButton(Graphics g, string text, int x, int y, int w, int h, Color color)
    {
        using var path = GetRoundedPath(x, y, w, h, 8);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(100, 255, 255, 255), 1f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(9f, FontStyle.Bold);
        using var brushTxt = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brushTxt, new RectangleF(x, y, w, h), sf);
    }

    private void RenderInputField(Graphics g, string fieldId, string value, int x, int y, int w, int h)
    {
        bool isFocused = (_activeInputField == fieldId);
        using var path = GetRoundedPath(x, y, w, h, 7);

        Color bg = isFocused ? Color.FromArgb(50, 40, 60, 90) : Color.FromArgb(25, 255, 255, 255);
        using var brush = new SolidBrush(bg);
        g.FillPath(brush, path);

        Color border = isFocused ? AccentBlue : Color.FromArgb(50, 255, 255, 255);
        using var pen = new Pen(border, isFocused ? 1.5f : 1f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(9f, FontStyle.Regular);
        using var brushTxt = new SolidBrush(TextWhite);
        g.DrawString(value, font, brushTxt, x + 8, y + 6);

        if (isFocused && _caretVisible)
        {
            var sz = g.MeasureString(value, font);
            int caretX = x + 8 + (int)sz.Width;
            using var penCaret = new Pen(Color.White, 1.5f);
            g.DrawLine(penCaret, caretX, y + 5, caretX, y + h - 5);
        }
    }

    private void RenderToast(Graphics g)
    {
        if (DateTime.UtcNow >= _toastUntil || string.IsNullOrEmpty(_toastMessage)) return;

        int toastW = 320;
        int toastH = 40;
        int toastX = CardX + CardWidth - toastW - 24;
        int toastY = CardY + CardHeight - toastH - 24;

        using var path = GetRoundedPath(toastX, toastY, toastW, toastH, 10);
        using var bgBrush = new SolidBrush(Color.FromArgb(220, 20, 40, 30));
        g.FillPath(bgBrush, path);
        using var pen = new Pen(AccentGreen, 1.5f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(9.5f, FontStyle.Bold);
        using var brushTxt = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_toastMessage, font, brushTxt, new RectangleF(toastX, toastY, toastW, toastH), sf);
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
