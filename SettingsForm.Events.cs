using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Meridian;

public sealed partial class SettingsForm
{
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left) return;

        int mouseX = (int)Math.Round(e.X / _scale);
        int mouseY = (int)Math.Round(e.Y / _scale);

        // 1. macOS Traffic Lights (x: 18..70, y: 16..28)
        if (mouseY >= 14 && mouseY <= 30)
        {
            // Close: 18..30
            if (mouseX >= 16 && mouseX <= 32)
            {
                Close();
                return;
            }
            // Minimize: 38..50
            if (mouseX >= 36 && mouseX <= 52)
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            // Zoom: 58..70
            if (mouseX >= 56 && mouseX <= 72)
            {
                WindowState = (WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                return;
            }
        }

        // 2. Window Dragging
        // Drag allowed on content header (SidebarWidth..CardWidth, 0..50)
        // or on top sidebar empty area (74..SidebarWidth, 0..42)
        bool isContentHeader = (mouseX >= SidebarWidth && mouseX <= CardWidth && mouseY >= 0 && mouseY <= 50);
        bool isSidebarHeader = (mouseX >= 74 && mouseX <= SidebarWidth && mouseY >= 0 && mouseY <= 42);

        if (isContentHeader || isSidebarHeader)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
            return;
        }

        // 3. Sidebar Search Field (16, 44, 188, 28)
        if (mouseX >= 16 && mouseX <= 204 && mouseY >= 44 && mouseY <= 72)
        {
            // Clear button if active
            if (!string.IsNullOrEmpty(_searchText) && mouseX >= 184 && mouseX <= 202)
            {
                _searchText = "";
                InvalidateAndRender();
                return;
            }

            _activeInputField = "sidebar_search";
            InvalidateAndRender();
            return;
        }

        // 4. Sidebar Navigation Tabs
        int tabStartY = 82;
        int tabH = 38;
        int tabSpacing = 4;
        int tabW = 196;
        int tabX = 12;

        for (int i = 0; i < 5; i++)
        {
            int currentY = tabStartY + i * (tabH + tabSpacing);
            if (mouseX >= tabX && mouseX <= tabX + tabW && mouseY >= currentY && mouseY <= currentY + tabH)
            {
                _activeTab = (SettingsTab)i;
                _activeInputField = "";
                InvalidateAndRender();
                return;
            }
        }

        // 5. Content Area Clicks
        int headerH = 50;
        int contentX = SidebarWidth + 24;
        int contentY = headerH + 14;
        int contentW = CardWidth - SidebarWidth - 48; // 592

        switch (_activeTab)
        {
            case SettingsTab.IslandNotch:
                HandleClickIslandNotch(mouseX, mouseY, contentX, contentY, contentW);
                break;
            case SettingsTab.LiveWeather:
                HandleClickWeather(mouseX, mouseY, contentX, contentY, contentW);
                break;
            case SettingsTab.MediaPlayers:
                HandleClickPlayers(mouseX, mouseY, contentX, contentY, contentW);
                break;
            case SettingsTab.ShortcutsSystem:
                HandleClickShortcuts(mouseX, mouseY, contentX, contentY, contentW);
                break;
            case SettingsTab.AboutKeys:
                HandleClickAbout(mouseX, mouseY, contentX, contentY, contentW);
                break;
        }
    }

    private void HandleClickIslandNotch(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 20;
        int card1H = 64;

        // Toggle Switch: Attached Bezel Notch Mode
        if (x >= cx + cw - 60 && x <= cx + cw - 12 && y >= card1Y + 16 && y <= card1Y + 48)
        {
            AppSettings.Current.MusicNotchEnabled = !AppSettings.Current.MusicNotchEnabled;
            AppSettings.Current.Save();
            ShowToast(AppSettings.Current.MusicNotchEnabled ? "✓ Attached Bezel Notch Enabled" : "✓ Attached Bezel Notch Disabled");
            InvalidateAndRender();
            return;
        }

        // Section 2: Display & Timeout Durations
        int sec2Y = card1Y + card1H + 20;
        int card2Y = sec2Y + 20;

        // Row 1: Music Track Change Window Segmented Control (cx + 16, card2Y + 54, cw - 32, 28)
        int seg1X = cx + 16;
        int seg1Y = card2Y + 54;
        int seg1W = cw - 32;
        int seg1H = 28;

        if (x >= seg1X && x <= seg1X + seg1W && y >= seg1Y && y <= seg1Y + seg1H)
        {
            double[] timerVals = { 0.0, 15.0, 30.0, 60.0, 120.0 };
            float itemW = (seg1W - 4f) / timerVals.Length;
            int idx = (int)((x - seg1X - 2) / itemW);
            if (idx >= 0 && idx < timerVals.Length)
            {
                AppSettings.Current.MusicTimeDisplaySeconds = timerVals[idx];
                AppSettings.Current.Save();
                ShowToast($"✓ Music track display set to {timerVals[idx]}s");
                InvalidateAndRender();
                return;
            }
        }

        // Row 2: Idle Despawn Segmented Control (cx + 16, card2Y + 96 + 54, cw - 32, 28)
        int seg2X = cx + 16;
        int seg2Y = card2Y + 96 + 54;
        int seg2W = cw - 32;
        int seg2H = 28;

        if (x >= seg2X && x <= seg2X + seg2W && y >= seg2Y && y <= seg2Y + seg2H)
        {
            double[] idleVals = { 15.0, 30.0, 60.0, 120.0, 999999.0 };
            float itemW = (seg2W - 4f) / idleVals.Length;
            int idx = (int)((x - seg2X - 2) / itemW);
            if (idx >= 0 && idx < idleVals.Length)
            {
                AppSettings.Current.IdleDespawnSeconds = idleVals[idx];
                AppSettings.Current.Save();
                string label = idleVals[idx] > 1000 ? "Always Visible" : $"{idleVals[idx]}s";
                ShowToast($"✓ Idle timeout set to {label}");
                InvalidateAndRender();
                return;
            }
        }
    }

    private void HandleClickWeather(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 20;
        int card1H = 64;

        // Auto-Location Toggle
        if (x >= cx + cw - 60 && x <= cx + cw - 12 && y >= card1Y + 16 && y <= card1Y + 48)
        {
            _weatherUseAuto = !_weatherUseAuto;
            InvalidateAndRender();
            return;
        }

        int sec2Y = card1Y + card1H + 20;
        int card2Y = sec2Y + 20;

        // Row 1 Text Fields: City (x + 110, card2Y + 14, 210, 28) & Country (x + 410, card2Y + 14, 166, 28)
        if (x >= cx + 110 && x <= cx + 320 && y >= card2Y + 14 && y <= card2Y + 42)
        {
            _activeInputField = "weather_city";
            InvalidateAndRender();
            return;
        }
        if (x >= cx + 410 && x <= cx + 576 && y >= card2Y + 14 && y <= card2Y + 42)
        {
            _activeInputField = "weather_country";
            InvalidateAndRender();
            return;
        }

        // Row 2 Text Fields: Lat (x + 110, card2Y + 58 + 14, 210, 28) & Lon (x + 410, card2Y + 58 + 14, 166, 28)
        int row2Y = card2Y + 58;
        if (x >= cx + 110 && x <= cx + 320 && y >= row2Y + 14 && y <= row2Y + 42)
        {
            _activeInputField = "weather_lat";
            InvalidateAndRender();
            return;
        }
        if (x >= cx + 410 && x <= cx + 576 && y >= row2Y + 14 && y <= row2Y + 42)
        {
            _activeInputField = "weather_lon";
            InvalidateAndRender();
            return;
        }

        // Row 3 Buttons: Refresh Now (x + cw - 240, card2Y + 116 + 14, 104, 28), Save Config (x + cw - 124, card2Y + 116 + 14, 108, 28)
        int row3Y = card2Y + 116;
        if (x >= cx + cw - 240 && x <= cx + cw - 136 && y >= row3Y + 14 && y <= row3Y + 42)
        {
            _weatherStatus = "Updating...";
            InvalidateAndRender();
            Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() =>
                    {
                        _weatherStatus = "Refreshed!";
                        ShowToast("✓ Weather data refreshed!");
                        InvalidateAndRender();
                    });
                }
            });
            return;
        }

        if (x >= cx + cw - 124 && x <= cx + cw - 16 && y >= row3Y + 14 && y <= row3Y + 42)
        {
            double.TryParse(_weatherLat, out double lat);
            double.TryParse(_weatherLon, out double lon);
            LiveWeatherService.SaveLocationConfig(_weatherUseAuto, _weatherCity, _weatherCountry, lat, lon);
            ShowToast("✓ Weather configuration saved!");
            InvalidateAndRender();
            return;
        }

        _activeInputField = "";
        InvalidateAndRender();
    }

    private void HandleClickPlayers(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 20;
        int listRows = Math.Max(1, _players.Count);
        int card1H = listRows * 48;

        // Existing Player Row actions
        for (int i = 0; i < _players.Count; i++)
        {
            int rowY = card1Y + i * 48;
            // Launch: x + cw - 146, rowY + 10, 64, 26
            if (x >= cx + cw - 146 && x <= cx + cw - 82 && y >= rowY + 10 && y <= rowY + 36)
            {
                var p = _players[i];
                PlayerService.LaunchPlayer(p);
                ShowToast($"✓ Launched {p.Name}");
                return;
            }
            // Delete: x + cw - 74, rowY + 10, 60, 26
            if (x >= cx + cw - 74 && x <= cx + cw - 14 && y >= rowY + 10 && y <= rowY + 36)
            {
                string removed = _players[i].Name;
                _players.RemoveAt(i);
                PlayerService.SavePlayers(_players);
                ShowToast($"✓ Deleted {removed}");
                InvalidateAndRender();
                return;
            }
        }

        // Section 2: Add New Launcher
        int sec2Y = card1Y + card1H + 20;
        int card2Y = sec2Y + 20;

        // Row 1: App Name (cx + 100, card2Y + 12, 160, 28)
        if (x >= cx + 100 && x <= cx + 260 && y >= card2Y + 12 && y <= card2Y + 40)
        {
            _activeInputField = "new_player_name";
            InvalidateAndRender();
            return;
        }

        // Row 1: Icon Segmented Control (cx + 320, card2Y + 12, 256, 28)
        int icSegX = cx + 320;
        int icSegY = card2Y + 12;
        int icSegW = 256;
        int icSegH = 28;
        if (x >= icSegX && x <= icSegX + icSegW && y >= icSegY && y <= icSegY + icSegH)
        {
            string[] icVals = { "spotify", "ytmusic", "music" };
            float segW = (icSegW - 4f) / icVals.Length;
            int idx = (int)((x - icSegX - 2) / segW);
            if (idx >= 0 && idx < icVals.Length)
            {
                _newPlayerIcon = icVals[idx];
                InvalidateAndRender();
                return;
            }
        }

        // Row 2: Path / URL (cx + 100, card2Y + 50 + 12 = card2Y + 62, 380, 28)
        int addRow2Y = card2Y + 50;
        if (x >= cx + 100 && x <= cx + 480 && y >= addRow2Y + 12 && y <= addRow2Y + 40)
        {
            _activeInputField = "new_player_url";
            InvalidateAndRender();
            return;
        }

        // Browse Button (cx + 490, addRow2Y + 12, 86, 28)
        if (x >= cx + 490 && x <= cx + 576 && y >= addRow2Y + 12 && y <= addRow2Y + 40)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select Media Player Executable",
                Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                _newPlayerUrl = ofd.FileName;
                if (string.IsNullOrWhiteSpace(_newPlayerName))
                {
                    _newPlayerName = Path.GetFileNameWithoutExtension(ofd.FileName);
                }
                InvalidateAndRender();
            }
            return;
        }

        // Row 3: Submit Button (cx + 16, card2Y + 100 + 11 = card2Y + 111, 170, 30)
        int addRow3Y = card2Y + 100;
        if (x >= cx + 16 && x <= cx + 186 && y >= addRow3Y + 11 && y <= addRow3Y + 41)
        {
            if (string.IsNullOrWhiteSpace(_newPlayerName) || string.IsNullOrWhiteSpace(_newPlayerUrl))
            {
                ShowToast("⚠️ Please enter a player name and path/URL");
                return;
            }

            _players.Add(new PlayerItem { Name = _newPlayerName.Trim(), Url = _newPlayerUrl.Trim(), Icon = _newPlayerIcon });
            PlayerService.SavePlayers(_players);
            _newPlayerName = "";
            _newPlayerUrl = "";
            _activeInputField = "";
            ShowToast("✓ New media launcher added!");
            InvalidateAndRender();
            return;
        }

        _activeInputField = "";
        InvalidateAndRender();
    }

    private void HandleClickShortcuts(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 20;

        // Row 1: Start with Windows Toggle (cx + cw - 56, card1Y + 14, 40, 24)
        if (x >= cx + cw - 60 && x <= cx + cw - 12 && y >= card1Y + 10 && y <= card1Y + 42)
        {
            bool nextState = !AppSettings.IsRunOnStartup();
            AppSettings.SetRunOnStartup(nextState);
            ShowToast(nextState ? "✓ Windows Startup Enabled" : "✓ Windows Startup Disabled");
            InvalidateAndRender();
            return;
        }

        // Row 2: Settings Desktop Shortcut (cx + cw - 124, card1Y + 52 + 12 = card1Y + 64, 108, 28)
        int row2Y = card1Y + 52;
        if (x >= cx + cw - 124 && x <= cx + cw - 16 && y >= row2Y + 12 && y <= row2Y + 40)
        {
            bool ok = AppSettings.CreateDesktopShortcut("Meridian Settings.lnk", "--settings", "Meridian Settings & Control");
            if (ok) ShowToast("⭐ Settings shortcut created on Desktop!");
            else ShowToast("⚠️ Failed to create shortcut");
            InvalidateAndRender();
            return;
        }

        // Row 3: Island Desktop Shortcut (cx + cw - 124, card1Y + 104 + 12 = card1Y + 116, 108, 28)
        int row3Y = card1Y + 104;
        if (x >= cx + cw - 124 && x <= cx + cw - 16 && y >= row3Y + 12 && y <= row3Y + 40)
        {
            bool ok = AppSettings.CreateDesktopShortcut("Meridian Island.lnk", "", "Meridian Dynamic Island");
            if (ok) ShowToast("⭐ Dynamic Island shortcut created on Desktop!");
            else ShowToast("⚠️ Failed to create shortcut");
            InvalidateAndRender();
            return;
        }

        // Section 2: Hardware Backdrop & Glass Translucency
        int card1H = 156;
        int sec2Y = card1Y + card1H + 20;
        int card2Y = sec2Y + 20;

        // Row 1: Backdrop Segmented Control (cx + 16, card2Y + 54, cw - 32, 28)
        int bSegX = cx + 16;
        int bSegY = card2Y + 54;
        int bSegW = cw - 32;
        int bSegH = 28;
        if (x >= bSegX && x <= bSegX + bSegW && y >= bSegY && y <= bSegY + bSegH)
        {
            int[] backdropVals = { 3, 4, 2 }; // Acrylic, Mica Alt, Mica
            float segW = (bSegW - 4f) / backdropVals.Length;
            int idx = (int)((x - bSegX - 2) / segW);
            if (idx >= 0 && idx < backdropVals.Length)
            {
                ApplyBackdrop(backdropVals[idx]);
                return;
            }
        }

        // Row 2: Translucency Segmented Control (cx + 16, card2Y + 96 + 54, cw - 32, 28)
        int tSegX = cx + 16;
        int tSegY = card2Y + 96 + 54;
        int tSegW = cw - 32;
        int tSegH = 28;
        if (x >= tSegX && x <= tSegX + tSegW && y >= tSegY && y <= tSegY + tSegH)
        {
            double[] transVals = { 0.78, 0.85, 0.92, 1.0 };
            float segW = (tSegW - 4f) / transVals.Length;
            int idx = (int)((x - tSegX - 2) / segW);
            if (idx >= 0 && idx < transVals.Length)
            {
                ApplyTransparency(transVals[idx], showToast: true);
                return;
            }
        }
    }

    private void HandleClickAbout(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 20;

        // Restart Island Button (cx + cw - 190, card1Y + 18, 76, 30)
        if (x >= cx + cw - 190 && x <= cx + cw - 114 && y >= card1Y + 18 && y <= card1Y + 48)
        {
            StopIslandProcesses();
            StartIslandProcess();
            ShowToast("✓ Dynamic Island Restarted");
            InvalidateAndRender();
            return;
        }

        // Launch / Stop Island Button (cx + cw - 104, card1Y + 18, 88, 30)
        if (x >= cx + cw - 104 && x <= cx + cw - 16 && y >= card1Y + 18 && y <= card1Y + 48)
        {
            int currentId = Environment.ProcessId;
            var list = Process.GetProcessesByName("Meridian");
            bool runningOther = false;
            foreach (var p in list)
            {
                if (p.Id != currentId)
                {
                    runningOther = true;
                    break;
                }
            }

            if (runningOther)
            {
                StopIslandProcesses();
                ShowToast("✓ Dynamic Island Stopped");
            }
            else
            {
                StartIslandProcess();
                ShowToast("✓ Dynamic Island Launched");
            }
            InvalidateAndRender();
            return;
        }
    }

    private static void StopIslandProcesses()
    {
        int currentId = Environment.ProcessId;
        var procs = Process.GetProcessesByName("Meridian");
        foreach (var p in procs)
        {
            if (p.Id != currentId)
            {
                try { p.Kill(); } catch { }
            }
        }
    }

    private static void StartIslandProcess()
    {
        string? exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true
            });
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int mouseX = (int)Math.Round(e.X / _scale);
        int mouseY = (int)Math.Round(e.Y / _scale);

        // Traffic lights hover (x: 16..72, y: 14..30)
        bool trafficHov = (mouseX >= 16 && mouseX <= 72 && mouseY >= 14 && mouseY <= 30);
        int hoveredLight = -1;
        if (trafficHov)
        {
            if (mouseX >= 16 && mouseX <= 32) hoveredLight = 0;
            else if (mouseX >= 36 && mouseX <= 52) hoveredLight = 1;
            else if (mouseX >= 56 && mouseX <= 72) hoveredLight = 2;
        }

        // Sidebar tabs hover
        int tabStartY = 82;
        int tabH = 38;
        int tabSpacing = 4;
        int tabW = 196;
        int tabX = 12;
        int hovTab = -1;

        for (int i = 0; i < 5; i++)
        {
            int currentY = tabStartY + i * (tabH + tabSpacing);
            if (mouseX >= tabX && mouseX <= tabX + tabW && mouseY >= currentY && mouseY <= currentY + tabH)
            {
                hovTab = i;
                break;
            }
        }

        if (trafficHov != _isTrafficLightsHovered || hoveredLight != _hoveredTrafficLight || hovTab != _hoveredTab)
        {
            _isTrafficLightsHovered = trafficHov;
            _hoveredTrafficLight = hoveredLight;
            _hoveredTab = hovTab;
            InvalidateAndRender();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_isTrafficLightsHovered || _hoveredTrafficLight != -1 || _hoveredTab != -1)
        {
            _isTrafficLightsHovered = false;
            _hoveredTrafficLight = -1;
            _hoveredTab = -1;
            InvalidateAndRender();
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        if (string.IsNullOrEmpty(_activeInputField)) return;

        if (e.KeyChar == (char)Keys.Back)
        {
            ModifyActiveInput(s => s.Length > 0 ? s[..^1] : s);
            e.Handled = true;
            InvalidateAndRender();
            return;
        }

        if (e.KeyChar == (char)Keys.Return || e.KeyChar == (char)Keys.Escape)
        {
            _activeInputField = "";
            e.Handled = true;
            InvalidateAndRender();
            return;
        }

        if (!char.IsControl(e.KeyChar))
        {
            ModifyActiveInput(s => s + e.KeyChar);
            e.Handled = true;
            InvalidateAndRender();
        }
    }

    private void ModifyActiveInput(Func<string, string> modifier)
    {
        switch (_activeInputField)
        {
            case "sidebar_search":
                _searchText = modifier(_searchText);
                break;
            case "weather_city":
                _weatherCity = modifier(_weatherCity);
                break;
            case "weather_country":
                _weatherCountry = modifier(_weatherCountry);
                break;
            case "weather_lat":
                _weatherLat = modifier(_weatherLat);
                break;
            case "weather_lon":
                _weatherLon = modifier(_weatherLon);
                break;
            case "new_player_name":
                _newPlayerName = modifier(_newPlayerName);
                break;
            case "new_player_url":
                _newPlayerUrl = modifier(_newPlayerUrl);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _caretTimer?.Stop();
            _caretTimer?.Dispose();
            _renderTimer?.Stop();
            _renderTimer?.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }
}
