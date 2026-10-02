using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Meridian;

public sealed partial class SettingsForm
{
    // Apple macOS Color Palette
    private static readonly Color MacTextWhite = Color.FromArgb(248, 248, 250);
    private static readonly Color MacTextSecondary = Color.FromArgb(170, 175, 185);
    private static readonly Color MacTextMuted = Color.FromArgb(130, 135, 145);
    private static readonly Color MacSectionHeader = Color.FromArgb(145, 150, 165);

    private static readonly Color MacBlue = Color.FromArgb(0, 122, 255);
    private static readonly Color MacBlueHover = Color.FromArgb(24, 136, 255);
    private static readonly Color MacGreen = Color.FromArgb(52, 199, 89);
    private static readonly Color MacRed = Color.FromArgb(255, 69, 58);

    // Traffic Light Colors
    private static readonly Color TrafficClose = Color.FromArgb(255, 95, 86);
    private static readonly Color TrafficCloseBorder = Color.FromArgb(224, 68, 62);
    private static readonly Color TrafficMin = Color.FromArgb(255, 189, 46);
    private static readonly Color TrafficMinBorder = Color.FromArgb(222, 161, 35);
    private static readonly Color TrafficZoom = Color.FromArgb(39, 201, 63);
    private static readonly Color TrafficZoomBorder = Color.FromArgb(26, 171, 41);

    // Glass & Card Materials
    private static readonly Color MacCardBg = Color.FromArgb(18, 255, 255, 255);
    private static readonly Color MacCardBorder = Color.FromArgb(32, 255, 255, 255);
    private static readonly Color MacRowDivider = Color.FromArgb(16, 255, 255, 255);
    private static readonly Color MacSidebarDivider = Color.FromArgb(22, 255, 255, 255);

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

        // 1. Sidebar (Traffic lights, search, navigation tabs)
        RenderSidebar(g);

        // 2. Content Area (Top header toolbar and tab cards)
        RenderContentArea(g);

        // 3. Toast Message
        RenderToast(g);

        g.Flush();
    }

    private void RenderSidebar(Graphics g)
    {
        // Traffic Lights at top-left
        RenderTrafficLights(g, 18, 16);

        // Search Bar below traffic lights
        RenderSidebarSearch(g, 16, 44, 188, 28);

        // Navigation Tabs
        int tabStartY = 82;
        int tabH = 38;
        int tabSpacing = 4;
        int tabW = 196;
        int tabX = 12;

        (string title, string subtitle, Color gradStart, Color gradEnd, int badgeKind)[] tabDefs =
        {
            ("Island & Notch", "Bezel Docking", Color.FromArgb(0, 122, 255), Color.FromArgb(0, 85, 212), 0),
            ("Live Weather", "Open-Meteo", Color.FromArgb(50, 173, 230), Color.FromArgb(0, 119, 182), 1),
            ("Media Launchers", "External Players", Color.FromArgb(255, 45, 85), Color.FromArgb(214, 24, 60), 2),
            ("Appearance & System", "Backdrop & Keys", Color.FromArgb(255, 149, 0), Color.FromArgb(212, 112, 0), 3),
            ("About Meridian", "Version & Engine", Color.FromArgb(142, 142, 147), Color.FromArgb(99, 99, 102), 4)
        };

        using var fontTab = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontTabBold = UITheme.GetFont(9.5f, FontStyle.Bold);

        for (int i = 0; i < tabDefs.Length; i++)
        {
            int currentY = tabStartY + i * (tabH + tabSpacing);
            bool isSelected = ((int)_activeTab == i);
            bool isHovered = (_hoveredTab == i);

            // Tab background pill
            if (isSelected)
            {
                using var path = GetRoundedPath(tabX, currentY, tabW, tabH, 9);
                using var fillBrush = new SolidBrush(Color.FromArgb(235, 0, 122, 255));
                g.FillPath(fillBrush, path);
                using var borderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
                g.DrawPath(borderPen, path);
            }
            else if (isHovered)
            {
                using var path = GetRoundedPath(tabX, currentY, tabW, tabH, 9);
                using var fillBrush = new SolidBrush(Color.FromArgb(24, 255, 255, 255));
                g.FillPath(fillBrush, path);
            }

            // Squircle Badge
            int badgeX = tabX + 6;
            int badgeY = currentY + 6;
            int badgeSize = 26;
            RenderSquircleBadge(g, badgeX, badgeY, badgeSize, tabDefs[i].gradStart, tabDefs[i].gradEnd, tabDefs[i].badgeKind);

            // Tab Text
            Color txtColor = isSelected ? Color.White : (isHovered ? MacTextWhite : MacTextSecondary);
            using var brushTxt = new SolidBrush(txtColor);

            g.DrawString(tabDefs[i].title, isSelected ? fontTabBold : fontTab, brushTxt, badgeX + badgeSize + 10, currentY + 10);
        }

        // Sidebar / Content Vertical Hairline Divider
        using var penDiv = new Pen(MacSidebarDivider, 1f);
        g.DrawLine(penDiv, SidebarWidth, 0, SidebarWidth, CardHeight);
    }

    private void RenderTrafficLights(Graphics g, int startX, int startY)
    {
        int diameter = 12;
        int spacing = 8;

        (Color fill, Color border, int idx)[] lights =
        {
            (TrafficClose, TrafficCloseBorder, 0),
            (TrafficMin, TrafficMinBorder, 1),
            (TrafficZoom, TrafficZoomBorder, 2)
        };

        for (int i = 0; i < lights.Length; i++)
        {
            int cx = startX + i * (diameter + spacing);
            int cy = startY;

            using var brush = new SolidBrush(lights[i].fill);
            g.FillEllipse(brush, cx, cy, diameter, diameter);

            using var pen = new Pen(lights[i].border, 1f);
            g.DrawEllipse(pen, cx, cy, diameter, diameter);

            // Draw glyph on hover
            if (_isTrafficLightsHovered)
            {
                if (i == 0) // Close: ×
                {
                    using var penX = new Pen(Color.FromArgb(180, 75, 0, 0), 1.2f);
                    g.DrawLine(penX, cx + 3.5f, cy + 3.5f, cx + 8.5f, cy + 8.5f);
                    g.DrawLine(penX, cx + 8.5f, cy + 3.5f, cx + 3.5f, cy + 8.5f);
                }
                else if (i == 1) // Minimize: −
                {
                    using var penMin = new Pen(Color.FromArgb(180, 100, 60, 0), 1.2f);
                    g.DrawLine(penMin, cx + 3f, cy + 6f, cx + 9f, cy + 6f);
                }
                else if (i == 2) // Zoom: +
                {
                    using var penZoom = new Pen(Color.FromArgb(180, 0, 80, 20), 1.2f);
                    g.DrawLine(penZoom, cx + 3f, cy + 6f, cx + 9f, cy + 6f);
                    g.DrawLine(penZoom, cx + 6f, cy + 3f, cx + 6f, cy + 9f);
                }
            }
        }
    }

    private void RenderSidebarSearch(Graphics g, int x, int y, int w, int h)
    {
        bool isFocused = (_activeInputField == "sidebar_search");
        using var path = GetRoundedPath(x, y, w, h, 7);

        using var fillBrush = new SolidBrush(isFocused ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(18, 255, 255, 255));
        g.FillPath(fillBrush, path);

        using var borderPen = new Pen(isFocused ? MacBlue : Color.FromArgb(36, 255, 255, 255), isFocused ? 1.5f : 1f);
        g.DrawPath(borderPen, path);

        // Magnifying glass icon
        using (var iconPen = new Pen(isFocused ? Color.White : MacTextMuted, 1.3f))
        {
            g.DrawEllipse(iconPen, x + 8, y + 8, 8, 8);
            g.DrawLine(iconPen, x + 15, y + 15, x + 19, y + 19);
        }

        // Search text / Placeholder
        using var fontSearch = UITheme.GetFont(9f, FontStyle.Regular);
        int textX = x + 24;
        int textY = y + 6;

        if (string.IsNullOrEmpty(_searchText) && !isFocused)
        {
            using var brushPlaceholder = new SolidBrush(MacTextMuted);
            g.DrawString("Search", fontSearch, brushPlaceholder, textX, textY);
        }
        else
        {
            using var brushText = new SolidBrush(MacTextWhite);
            g.DrawString(_searchText, fontSearch, brushText, textX, textY);

            if (isFocused && _caretVisible)
            {
                var sz = g.MeasureString(_searchText, fontSearch);
                int caretX = textX + (int)sz.Width;
                using var penCaret = new Pen(Color.White, 1.4f);
                g.DrawLine(penCaret, caretX, y + 6, caretX, y + h - 6);
            }

            if (!string.IsNullOrEmpty(_searchText))
            {
                // Clear button (x)
                int clrX = x + w - 18;
                int clrY = y + 8;
                using var clrBrush = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
                g.FillEllipse(clrBrush, clrX, clrY, 12, 12);
                using var penX = new Pen(Color.FromArgb(40, 40, 40), 1.2f);
                g.DrawLine(penX, clrX + 3.5f, clrY + 3.5f, clrX + 8.5f, clrY + 8.5f);
                g.DrawLine(penX, clrX + 8.5f, clrY + 3.5f, clrX + 3.5f, clrY + 8.5f);
            }
        }
    }

    private void RenderSquircleBadge(Graphics g, int x, int y, int size, Color gradStart, Color gradEnd, int badgeKind)
    {
        using var path = GetRoundedPath(x, y, size, size, 6);
        using var lgb = new LinearGradientBrush(new Point(x, y), new Point(x, y + size), gradStart, gradEnd);
        g.FillPath(lgb, path);

        using var penRim = new Pen(Color.FromArgb(60, 255, 255, 255), 0.8f);
        g.DrawPath(penRim, path);

        // Icon glyph inside squircle
        using var brushGlyph = new SolidBrush(Color.White);
        using var penGlyph = new Pen(Color.White, 1.4f);

        switch (badgeKind)
        {
            case 0: // Island capsule
                using (var capsulePath = GetRoundedPath(x + 5, y + 9, 16, 8, 4))
                {
                    g.FillPath(brushGlyph, capsulePath);
                }
                using (var camBrush = new SolidBrush(Color.FromArgb(200, 0, 100, 220)))
                {
                    g.FillEllipse(camBrush, x + 7.5f, y + 11.5f, 3f, 3f);
                }
                break;
            case 1: // Weather sun
                g.FillEllipse(brushGlyph, x + 8, y + 8, 10, 10);
                for (int angle = 0; angle < 360; angle += 45)
                {
                    double rad = angle * Math.PI / 180.0;
                    float rx1 = x + 13 + (float)(Math.Cos(rad) * 6.5);
                    float ry1 = y + 13 + (float)(Math.Sin(rad) * 6.5);
                    float rx2 = x + 13 + (float)(Math.Cos(rad) * 9.0);
                    float ry2 = y + 13 + (float)(Math.Sin(rad) * 9.0);
                    g.DrawLine(penGlyph, rx1, ry1, rx2, ry2);
                }
                break;
            case 2: // Media music note
                g.FillEllipse(brushGlyph, x + 7, y + 14, 5, 4);
                g.FillEllipse(brushGlyph, x + 14, y + 12, 5, 4);
                g.DrawLine(penGlyph, x + 11.5f, y + 16, x + 11.5f, y + 8);
                g.DrawLine(penGlyph, x + 18.5f, y + 14, x + 18.5f, y + 6);
                g.DrawLine(new Pen(Color.White, 2f), x + 11.5f, y + 8, x + 18.5f, y + 6);
                break;
            case 3: // Sliders / Gear
                g.DrawLine(penGlyph, x + 6, y + 9, x + 20, y + 9);
                g.DrawLine(penGlyph, x + 6, y + 17, x + 20, y + 17);
                g.FillEllipse(brushGlyph, x + 9, y + 7, 4, 4);
                g.FillEllipse(brushGlyph, x + 15, y + 15, 4, 4);
                break;
            case 4: // Info 'i'
                g.DrawEllipse(penGlyph, x + 6, y + 6, 14, 14);
                g.FillEllipse(brushGlyph, x + 12, y + 9, 2.2f, 2.2f);
                g.DrawLine(new Pen(Color.White, 1.8f), x + 13, y + 13, x + 13, y + 17);
                break;
        }
    }

    private void RenderContentArea(Graphics g)
    {
        int headerH = 50;
        int contentX = SidebarWidth + 24;
        int contentY = headerH + 14;
        int contentW = CardWidth - SidebarWidth - 48; // 860 - 220 - 48 = 592
        int contentH = CardHeight - headerH - 24;

        // Content Area Top Header / Breadcrumb
        string tabTitle = _activeTab switch
        {
            SettingsTab.IslandNotch => "Island & Notch",
            SettingsTab.LiveWeather => "Live Weather",
            SettingsTab.MediaPlayers => "Media Launchers",
            SettingsTab.ShortcutsSystem => "Appearance & System",
            SettingsTab.AboutKeys => "About Meridian",
            _ => "Settings"
        };

        using var fontTitle = UITheme.GetTitleFont(13f, FontStyle.Bold);
        using var brushTitle = new SolidBrush(MacTextWhite);
        g.DrawString(tabTitle, fontTitle, brushTitle, SidebarWidth + 24, 16);

        // Header separator
        using var penHeaderDiv = new Pen(Color.FromArgb(16, 255, 255, 255), 1f);
        g.DrawLine(penHeaderDiv, SidebarWidth, headerH, CardWidth, headerH);

        // Render Active Tab Content
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
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(MacTextWhite);
        using var brushMuted = new SolidBrush(MacTextMuted);

        // Section 1: Bezel Docking Dynamics
        RenderSectionHeader(g, "BEZEL DOCKING DYNAMICS", x, y);

        int card1Y = y + 20;
        int card1H = 64;
        RenderMacGroupedCard(g, x, card1Y, w, card1H);

        g.DrawString("Attached Bezel Notch Mode", fontBody, brushWhite, x + 16, card1Y + 14);
        g.DrawString("Docks into top bezel with concave fillet ears when media is playing", fontSub, brushMuted, x + 16, card1Y + 36);

        bool notchOn = AppSettings.Current.MusicNotchEnabled;
        int swX = x + w - 56;
        int swY = card1Y + 20;
        RenderMacToggle(g, swX, swY, notchOn);

        // Section 2: Display & Timeout Durations
        int sec2Y = card1Y + card1H + 20;
        RenderSectionHeader(g, "DISPLAY & TIMEOUT DURATIONS", x, sec2Y);

        int card2Y = sec2Y + 20;
        int card2H = 152;
        RenderMacGroupedCard(g, x, card2Y, w, card2H);

        // Row 1: Music Track Change Window
        int row1Y = card2Y;
        g.DrawString("Music Start & Track Change Display Duration", fontBody, brushWhite, x + 16, row1Y + 12);
        g.DrawString("Clock and media details visibility before docking flush into the notch", fontSub, brushMuted, x + 16, row1Y + 32);

        double curSecs = AppSettings.Current.MusicTimeDisplaySeconds;
        (string label, double val)[] timerOptions =
        {
            ("Instant (0s)", 0.0),
            ("15s", 15.0),
            ("30s", 30.0),
            ("60s", 60.0),
            ("2 min", 120.0)
        };

        var segItems1 = new (string label, bool selected)[timerOptions.Length];
        for (int i = 0; i < timerOptions.Length; i++)
        {
            segItems1[i] = (timerOptions[i].label, Math.Abs(curSecs - timerOptions[i].val) < 0.1);
        }
        RenderMacSegmentedControl(g, x + w - 380, row1Y + 18, 364, 28, segItems1);

        // Row Divider
        RenderRowDivider(g, x, card2Y + 76, w);

        // Row 2: Idle Despawn Duration
        int row2Y = card2Y + 76;
        g.DrawString("Idle Despawn & Unhover Collapse Duration", fontBody, brushWhite, x + 16, row2Y + 12);
        g.DrawString("Duration before expanded island returns to the compact floating pill", fontSub, brushMuted, x + 16, row2Y + 32);

        double curIdle = AppSettings.Current.IdleDespawnSeconds;
        (string label, double val)[] idleOptions =
        {
            ("15s", 15.0),
            ("30s", 30.0),
            ("60s", 60.0),
            ("120s", 120.0),
            ("Always", 999999.0)
        };

        var segItems2 = new (string label, bool selected)[idleOptions.Length];
        for (int i = 0; i < idleOptions.Length; i++)
        {
            segItems2[i] = (idleOptions[i].label, Math.Abs(curIdle - idleOptions[i].val) < 0.1);
        }
        RenderMacSegmentedControl(g, x + w - 380, row2Y + 18, 364, 28, segItems2);
    }

    // ----------------------------------------------------
    // TAB 1: LIVE WEATHER
    // ----------------------------------------------------
    private void RenderTabWeather(Graphics g, int x, int y, int w, int h)
    {
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(MacTextWhite);
        using var brushSec = new SolidBrush(MacTextSecondary);
        using var brushMuted = new SolidBrush(MacTextMuted);

        // Section 1: Location Source
        RenderSectionHeader(g, "LOCATION SOURCE", x, y);

        int card1Y = y + 20;
        int card1H = 64;
        RenderMacGroupedCard(g, x, card1Y, w, card1H);

        g.DrawString("Auto-Detect Location (IP Geolocation)", fontBody, brushWhite, x + 16, card1Y + 14);
        g.DrawString("Automatically fetches meteorological forecasts using network public IP", fontSub, brushMuted, x + 16, card1Y + 36);

        RenderMacToggle(g, x + w - 56, card1Y + 20, _weatherUseAuto);

        // Section 2: Manual Coordinates
        int sec2Y = card1Y + card1H + 20;
        RenderSectionHeader(g, "MANUAL COORDINATES & WEATHER METRICS", x, sec2Y);

        int card2Y = sec2Y + 20;
        int card2H = 176;
        RenderMacGroupedCard(g, x, card2Y, w, card2H);

        // Row 1: City & Country
        int row1Y = card2Y;
        g.DrawString("City Name", fontBody, brushWhite, x + 16, row1Y + 18);
        RenderMacTextField(g, "weather_city", _weatherCity, "e.g. San Francisco", x + 110, row1Y + 14, 210, 28);

        g.DrawString("Country", fontBody, brushWhite, x + 340, row1Y + 18);
        RenderMacTextField(g, "weather_country", _weatherCountry, "e.g. US", x + 410, row1Y + 14, 166, 28);

        // Divider 1
        RenderRowDivider(g, x, card2Y + 58, w);

        // Row 2: Lat & Lon
        int row2Y = card2Y + 58;
        g.DrawString("Latitude", fontBody, brushWhite, x + 16, row2Y + 18);
        RenderMacTextField(g, "weather_lat", _weatherLat, "37.7749", x + 110, row2Y + 14, 210, 28);

        g.DrawString("Longitude", fontBody, brushWhite, x + 340, row2Y + 18);
        RenderMacTextField(g, "weather_lon", _weatherLon, "-122.4194", x + 410, row2Y + 14, 166, 28);

        // Divider 2
        RenderRowDivider(g, x, card2Y + 116, w);

        // Row 3: Action Buttons & Status
        int row3Y = card2Y + 116;
        using var brushGreen = new SolidBrush(MacGreen);
        g.FillEllipse(brushGreen, x + 18, row3Y + 24, 8, 8);
        g.DrawString($"Status: {_weatherStatus}", fontSub, brushSec, x + 32, row3Y + 21);

        RenderMacButton(g, "Refresh Now", x + w - 240, row3Y + 14, 104, 28, ButtonStyle.Secondary);
        RenderMacButton(g, "Save Config", x + w - 124, row3Y + 14, 108, 28, ButtonStyle.Primary);
    }

    // ----------------------------------------------------
    // TAB 2: MEDIA PLAYERS
    // ----------------------------------------------------
    private void RenderTabPlayers(Graphics g, int x, int y, int w, int h)
    {
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(MacTextWhite);
        using var brushSec = new SolidBrush(MacTextSecondary);
        using var brushMuted = new SolidBrush(MacTextMuted);

        // Section 1: Configured Launchers
        RenderSectionHeader(g, "CONFIGURED MEDIA LAUNCHERS", x, y);

        int card1Y = y + 20;
        int listRows = Math.Max(1, _players.Count);
        int card1H = listRows * 48;
        RenderMacGroupedCard(g, x, card1Y, w, card1H);

        if (_players.Count == 0)
        {
            g.DrawString("No custom media launchers configured. Add one below.", fontSub, brushMuted, x + 16, card1Y + 16);
        }
        else
        {
            for (int i = 0; i < _players.Count; i++)
            {
                var p = _players[i];
                int rowY = card1Y + i * 48;

                // Squircle badge for launcher
                Color bgGrad = p.Icon.Contains("spotify") ? Color.FromArgb(30, 215, 96) :
                               p.Icon.Contains("yt") ? Color.FromArgb(255, 0, 0) : Color.FromArgb(255, 45, 85);
                RenderSquircleBadge(g, x + 14, rowY + 12, 24, bgGrad, bgGrad, 2);

                g.DrawString(p.Name, fontBody, brushWhite, x + 46, rowY + 14);

                string displayUrl = p.Url.Length > 36 ? p.Url.Substring(0, 33) + "..." : p.Url;
                g.DrawString(displayUrl, fontSub, brushMuted, x + 180, rowY + 16);

                // Row action buttons
                RenderMacButton(g, "Launch", x + w - 146, rowY + 10, 64, 26, ButtonStyle.Secondary);
                RenderMacButton(g, "Delete", x + w - 74, rowY + 10, 60, 26, ButtonStyle.Destructive);

                if (i < _players.Count - 1)
                {
                    RenderRowDivider(g, x, rowY + 48, w);
                }
            }
        }

        // Section 2: Add New Launcher
        int sec2Y = card1Y + card1H + 20;
        RenderSectionHeader(g, "ADD NEW MEDIA LAUNCHER", x, sec2Y);

        int card2Y = sec2Y + 20;
        int card2H = 152;
        RenderMacGroupedCard(g, x, card2Y, w, card2H);

        // Row 1: Name & Icon picker
        int addRow1Y = card2Y;
        g.DrawString("App Name", fontBody, brushWhite, x + 16, addRow1Y + 16);
        RenderMacTextField(g, "new_player_name", _newPlayerName, "e.g. Spotify", x + 100, addRow1Y + 12, 160, 28);

        g.DrawString("Icon", fontBody, brushWhite, x + 280, addRow1Y + 16);
        (string icLabel, string icVal)[] icOptions = { ("Spotify", "spotify"), ("YT Music", "ytmusic"), ("Music", "music") };
        var segIcons = new (string label, bool selected)[icOptions.Length];
        for (int i = 0; i < icOptions.Length; i++)
        {
            segIcons[i] = (icOptions[i].icLabel, _newPlayerIcon.Equals(icOptions[i].icVal, StringComparison.OrdinalIgnoreCase));
        }
        RenderMacSegmentedControl(g, x + 320, addRow1Y + 12, 256, 28, segIcons);

        // Divider 1
        RenderRowDivider(g, x, card2Y + 50, w);

        // Row 2: Path / URL
        int addRow2Y = card2Y + 50;
        g.DrawString("Path / URL", fontBody, brushWhite, x + 16, addRow2Y + 16);
        RenderMacTextField(g, "new_player_url", _newPlayerUrl, "Executable path or web URL", x + 100, addRow2Y + 12, 380, 28);
        RenderMacButton(g, "Browse...", x + 490, addRow2Y + 12, 86, 28, ButtonStyle.Secondary);

        // Divider 2
        RenderRowDivider(g, x, card2Y + 100, w);

        // Row 3: Submit Button
        int addRow3Y = card2Y + 100;
        RenderMacButton(g, "+ Add Media Launcher", x + 16, addRow3Y + 11, 170, 30, ButtonStyle.Primary);
    }

    // ----------------------------------------------------
    // TAB 3: SHORTCUTS & SYSTEM
    // ----------------------------------------------------
    private void RenderTabShortcuts(Graphics g, int x, int y, int w, int h)
    {
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(MacTextWhite);
        using var brushSec = new SolidBrush(MacTextSecondary);
        using var brushMuted = new SolidBrush(MacTextMuted);

        // Section 1: Windows Integration
        RenderSectionHeader(g, "WINDOWS STARTUP & INTEGRATION", x, y);

        int card1Y = y + 20;
        int card1H = 156;
        RenderMacGroupedCard(g, x, card1Y, w, card1H);

        // Row 1: Run on startup
        int row1Y = card1Y;
        g.DrawString("Start Dynamic Island with Windows", fontBody, brushWhite, x + 16, row1Y + 12);
        g.DrawString("Automatically starts the Dynamic Island background service on user login", fontSub, brushMuted, x + 16, row1Y + 30);
        RenderMacToggle(g, x + w - 56, row1Y + 14, AppSettings.IsRunOnStartup());

        RenderRowDivider(g, x, card1Y + 52, w);

        // Row 2: Desktop Settings Shortcut
        int row2Y = card1Y + 52;
        g.DrawString("Desktop Settings Shortcut", fontBody, brushWhite, x + 16, row2Y + 12);
        g.DrawString("Creates 'Meridian Settings.lnk' on your Windows Desktop", fontSub, brushMuted, x + 16, row2Y + 30);

        bool settingsLnk = AppSettings.IsDesktopShortcutPresent("Meridian Settings.lnk");
        if (settingsLnk)
        {
            g.DrawString("✓ Installed", fontSub, new SolidBrush(MacGreen), x + w - 190, row2Y + 16);
        }
        RenderMacButton(g, "Create Shortcut", x + w - 124, row2Y + 12, 108, 28, ButtonStyle.Secondary);

        RenderRowDivider(g, x, card1Y + 104, w);

        // Row 3: Desktop Island Shortcut
        int row3Y = card1Y + 104;
        g.DrawString("Desktop Dynamic Island Shortcut", fontBody, brushWhite, x + 16, row3Y + 12);
        g.DrawString("Creates 'Meridian Island.lnk' on your Windows Desktop", fontSub, brushMuted, x + 16, row3Y + 30);

        bool islandLnk = AppSettings.IsDesktopShortcutPresent("Meridian Island.lnk");
        if (islandLnk)
        {
            g.DrawString("✓ Installed", fontSub, new SolidBrush(MacGreen), x + w - 190, row3Y + 16);
        }
        RenderMacButton(g, "Create Shortcut", x + w - 124, row3Y + 12, 108, 28, ButtonStyle.Secondary);

        // Section 2: Hardware Backdrop & Glass Translucency
        int sec2Y = card1Y + card1H + 20;
        RenderSectionHeader(g, "WINDOW BACKDROP & TRANSLUCENCY", x, sec2Y);

        int card2Y = sec2Y + 20;
        int card2H = 148;
        RenderMacGroupedCard(g, x, card2Y, w, card2H);

        // Row 1: Backdrop Type
        int bRowY = card2Y;
        g.DrawString("Backdrop Material (Windows 11 DWM)", fontBody, brushWhite, x + 16, bRowY + 14);
        g.DrawString("Translucent Acrylic blurs apps & desktop; Mica samples wallpaper", fontSub, brushMuted, x + 16, bRowY + 34);

        int curBackdrop = AppSettings.Current.BackdropType;
        (string bLabel, int bVal)[] backdropOptions =
        {
            ("Acrylic", 3),
            ("Mica Alt", 4),
            ("Mica", 2)
        };
        var segBackdrop = new (string label, bool selected)[backdropOptions.Length];
        for (int i = 0; i < backdropOptions.Length; i++)
        {
            segBackdrop[i] = (backdropOptions[i].bLabel, curBackdrop == backdropOptions[i].bVal);
        }
        RenderMacSegmentedControl(g, x + w - 280, bRowY + 18, 264, 28, segBackdrop);

        RenderRowDivider(g, x, card2Y + 74, w);

        // Row 2: Translucency Opacity
        int tRowY = card2Y + 74;
        g.DrawString("Glass Translucency & Opacity", fontBody, brushWhite, x + 16, tRowY + 14);
        g.DrawString("Adjusts see-through opacity across the window and background blur", fontSub, brushMuted, x + 16, tRowY + 34);

        double curOpacity = AppSettings.Current.WindowOpacity;
        (string tLabel, double tVal)[] translucencyOptions =
        {
            ("High (78%)", 0.78),
            ("Balanced (85%)", 0.85),
            ("Subtle (92%)", 0.92),
            ("Solid (100%)", 1.0)
        };
        var segTrans = new (string label, bool selected)[translucencyOptions.Length];
        for (int i = 0; i < translucencyOptions.Length; i++)
        {
            segTrans[i] = (translucencyOptions[i].tLabel, Math.Abs(curOpacity - translucencyOptions[i].tVal) < 0.035);
        }
        RenderMacSegmentedControl(g, x + w - 380, tRowY + 18, 364, 28, segTrans);
    }

    // ----------------------------------------------------
    // TAB 4: ABOUT & SYSTEM
    // ----------------------------------------------------
    private void RenderTabAbout(Graphics g, int x, int y, int w, int h)
    {
        using var fontBody = UITheme.GetFont(9.5f, FontStyle.Regular);
        using var fontSub = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var fontCode = UITheme.GetFont(8.5f, FontStyle.Bold);
        using var brushWhite = new SolidBrush(MacTextWhite);
        using var brushSec = new SolidBrush(MacTextSecondary);
        using var brushMuted = new SolidBrush(MacTextMuted);

        // Section 1: Process Engine
        RenderSectionHeader(g, "MERIDIAN DYNAMIC ISLAND", x, y);

        int card1Y = y + 20;
        int card1H = 68;
        RenderMacGroupedCard(g, x, card1Y, w, card1H);

        bool isIslandRunning = Process.GetProcessesByName("Meridian").Length > 1;

        // Official Meridian Logo Icon
        LogoRenderer.DrawLogo(g, x + 14, card1Y + 12, 44, drawBackground: true);

        g.DrawString("Meridian Dynamic Island", fontBody, brushWhite, x + 68, card1Y + 16);
        g.DrawString("v2.0 · Per-Monitor V2 High-DPI & Hardware Acrylic", fontSub, brushMuted, x + 68, card1Y + 36);

        // Status pill & control buttons
        string statusText = isIslandRunning ? "🟢 Running" : "⚪ Stopped";
        Color statusColor = isIslandRunning ? MacGreen : Color.FromArgb(200, 180, 80);
        g.DrawString(statusText, fontSub, new SolidBrush(statusColor), x + w - 280, card1Y + 24);

        RenderMacButton(g, "Restart", x + w - 190, card1Y + 18, 76, 30, ButtonStyle.Secondary);
        RenderMacButton(g, isIslandRunning ? "Stop" : "Launch", x + w - 104, card1Y + 18, 88, 30, isIslandRunning ? ButtonStyle.Destructive : ButtonStyle.Primary);

        // Section 2: Keyboard Shortcuts Reference
        int sec2Y = card1Y + card1H + 20;
        RenderSectionHeader(g, "KEYBOARD SHORTCUTS REFERENCE", x, sec2Y);

        int card2Y = sec2Y + 20;
        int card2H = 190;
        RenderMacGroupedCard(g, x, card2Y, w, card2H);

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
            int rowY = card2Y + i * 38;

            // Keycap pill
            using var pillPath = GetRoundedPath(x + 16, rowY + 8, 54, 22, 5);
            using var fillBrush = new SolidBrush(Color.FromArgb(32, 255, 255, 255));
            g.FillPath(fillBrush, pillPath);
            using var penKey = new Pen(Color.FromArgb(55, 255, 255, 255), 1f);
            g.DrawPath(penKey, pillPath);

            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(shortcuts[i].key, fontCode, brushWhite, new RectangleF(x + 16, rowY + 8, 54, 22), sfCenter);

            g.DrawString(shortcuts[i].desc, fontSub, brushSec, x + 84, rowY + 12);

            if (i < shortcuts.Length - 1)
            {
                RenderRowDivider(g, x, rowY + 38, w);
            }
        }
    }

    // ----------------------------------------------------
    // COMMON MAC CONTROLS & UTILITIES
    // ----------------------------------------------------
    private void RenderSectionHeader(Graphics g, string text, int x, int y)
    {
        using var fontHeader = UITheme.GetFont(8f, FontStyle.Bold);
        using var brush = new SolidBrush(MacSectionHeader);
        g.DrawString(text, fontHeader, brush, x + 4, y);
    }

    private void RenderMacGroupedCard(Graphics g, int x, int y, int w, int h)
    {
        using var path = GetRoundedPath(x, y, w, h, 10);
        using var fillBrush = new SolidBrush(MacCardBg);
        g.FillPath(fillBrush, path);
        using var borderPen = new Pen(MacCardBorder, 1f);
        g.DrawPath(borderPen, path);
    }

    private void RenderRowDivider(Graphics g, int cardX, int y, int cardW)
    {
        using var pen = new Pen(MacRowDivider, 1f);
        g.DrawLine(pen, cardX + 16, y, cardX + cardW - 16, y);
    }

    private void RenderMacToggle(Graphics g, int x, int y, bool isChecked)
    {
        int swW = 40;
        int swH = 24;
        using var path = GetRoundedPath(x, y, swW, swH, 12);

        Color trackBg = isChecked ? MacGreen : Color.FromArgb(46, 255, 255, 255);
        using var brush = new SolidBrush(trackBg);
        g.FillPath(brush, path);

        using var pen = new Pen(isChecked ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(60, 255, 255, 255), 1f);
        g.DrawPath(pen, path);

        // Circular Thumb with drop shadow
        int thumbX = isChecked ? (x + swW - 22) : (x + 2);
        int thumbY = y + 2;

        // Drop shadow
        using var shadowBrush = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
        g.FillEllipse(shadowBrush, thumbX, thumbY + 1, 20, 20);

        // White Knob
        using var thumbBrush = new SolidBrush(Color.White);
        g.FillEllipse(thumbBrush, thumbX, thumbY, 20, 20);
    }

    private void RenderMacSegmentedControl(Graphics g, int x, int y, int w, int h, (string label, bool selected)[] items)
    {
        using var containerPath = GetRoundedPath(x, y, w, h, 7);
        using var containerBrush = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
        g.FillPath(containerBrush, containerPath);
        using var borderPen = new Pen(Color.FromArgb(38, 255, 255, 255), 1f);
        g.DrawPath(borderPen, containerPath);

        int count = items.Length;
        if (count == 0) return;

        float segW = (w - 4f) / count;
        using var font = UITheme.GetFont(8.5f, FontStyle.Regular);
        using var fontBold = UITheme.GetFont(8.5f, FontStyle.Bold);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < count; i++)
        {
            float segX = x + 2f + i * segW;
            float segY = y + 2f;
            float segH = h - 4f;

            if (items[i].selected)
            {
                using var activePath = GetRoundedPath(segX, segY, segW, segH, 5);
                using var activeBrush = new SolidBrush(Color.FromArgb(235, 0, 122, 255));
                g.FillPath(activeBrush, activePath);
                using var activeBorder = new Pen(Color.FromArgb(80, 255, 255, 255), 0.8f);
                g.DrawPath(activeBorder, activePath);

                using var brushTxt = new SolidBrush(Color.White);
                g.DrawString(items[i].label, fontBold, brushTxt, new RectangleF(segX, segY, segW, segH), sf);
            }
            else
            {
                using var brushTxt = new SolidBrush(MacTextSecondary);
                g.DrawString(items[i].label, font, brushTxt, new RectangleF(segX, segY, segW, segH), sf);
            }
        }
    }

    private enum ButtonStyle
    {
        Primary,
        Secondary,
        Destructive
    }

    private void RenderMacButton(Graphics g, string text, int x, int y, int w, int h, ButtonStyle style)
    {
        using var path = GetRoundedPath(x, y, w, h, 6);

        Color bg = style switch
        {
            ButtonStyle.Primary => MacBlue,
            ButtonStyle.Destructive => Color.FromArgb(200, 220, 50, 50),
            _ => Color.FromArgb(32, 255, 255, 255)
        };

        using var brush = new SolidBrush(bg);
        g.FillPath(brush, path);

        Color border = style switch
        {
            ButtonStyle.Primary => Color.FromArgb(80, 255, 255, 255),
            ButtonStyle.Destructive => Color.FromArgb(255, 100, 100),
            _ => Color.FromArgb(48, 255, 255, 255)
        };

        using var pen = new Pen(border, 1f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(8.5f, style == ButtonStyle.Primary ? FontStyle.Bold : FontStyle.Regular);
        using var brushTxt = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brushTxt, new RectangleF(x, y, w, h), sf);
    }

    private void RenderMacTextField(Graphics g, string fieldId, string value, string placeholder, int x, int y, int w, int h)
    {
        bool isFocused = (_activeInputField == fieldId);
        using var path = GetRoundedPath(x, y, w, h, 6);

        Color bg = isFocused ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(20, 255, 255, 255);
        using var brush = new SolidBrush(bg);
        g.FillPath(brush, path);

        Color border = isFocused ? MacBlue : Color.FromArgb(38, 255, 255, 255);
        using var pen = new Pen(border, isFocused ? 1.5f : 1f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(9f, FontStyle.Regular);

        if (string.IsNullOrEmpty(value) && !isFocused)
        {
            using var brushPl = new SolidBrush(MacTextMuted);
            g.DrawString(placeholder, font, brushPl, x + 8, y + 6);
        }
        else
        {
            using var brushTxt = new SolidBrush(MacTextWhite);
            g.DrawString(value, font, brushTxt, x + 8, y + 6);

            if (isFocused && _caretVisible)
            {
                var sz = g.MeasureString(value, font);
                int caretX = x + 8 + (int)sz.Width;
                using var penCaret = new Pen(Color.White, 1.4f);
                g.DrawLine(penCaret, caretX, y + 5, caretX, y + h - 5);
            }
        }
    }

    private void RenderToast(Graphics g)
    {
        if (DateTime.UtcNow >= _toastUntil || string.IsNullOrEmpty(_toastMessage)) return;

        int toastW = 340;
        int toastH = 38;
        int toastX = CardWidth - toastW - 20;
        int toastY = CardHeight - toastH - 18;

        using var path = GetRoundedPath(toastX, toastY, toastW, toastH, 10);
        using var bgBrush = new SolidBrush(Color.FromArgb(235, 24, 26, 32));
        g.FillPath(bgBrush, path);
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.2f);
        g.DrawPath(pen, path);

        using var font = UITheme.GetFont(9f, FontStyle.Bold);
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
