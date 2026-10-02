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

        // 1. Header Dragging
        int dragXStart = CardX + 20;
        int dragXEnd = CardX + CardWidth - 90;
        int dragYStart = CardY + 10;
        int dragYEnd = CardY + 54;
        if (mouseX >= dragXStart && mouseX <= dragXEnd && mouseY >= dragYStart && mouseY <= dragYEnd)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
            return;
        }

        // 2. Minimize & Close Buttons
        int btnY = CardY + 16;
        int closeX = CardX + CardWidth - 44;
        int minX = CardX + CardWidth - 80;

        if (mouseX >= minX && mouseX <= minX + 28 && mouseY >= btnY && mouseY <= btnY + 28)
        {
            WindowState = FormWindowState.Minimized;
            return;
        }

        if (mouseX >= closeX && mouseX <= closeX + 28 && mouseY >= btnY && mouseY <= btnY + 28)
        {
            Close();
            return;
        }

        // 3. Sidebar Tabs
        int sideX = CardX + 20;
        int sideY = CardY + 68;
        int sideW = 186;
        int tabH = 42;
        int tabSpacing = 6;

        for (int i = 0; i < 5; i++)
        {
            int currentY = sideY + i * (tabH + tabSpacing);
            if (mouseX >= sideX && mouseX <= sideX + sideW && mouseY >= currentY && mouseY <= currentY + tabH)
            {
                _activeTab = (SettingsTab)i;
                _activeInputField = "";
                InvalidateAndRender();
                return;
            }
        }

        // 4. Content Area Clicks
        int contentX = CardX + 236;
        int contentY = CardY + 68;
        int contentW = 564;

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
        int card1Y = cy + 46;
        // Toggle Switch: Attached Notch
        if (x >= cx + cw - 74 && x <= cx + cw - 28 && y >= card1Y + 26 && y <= card1Y + 52)
        {
            AppSettings.Current.MusicNotchEnabled = !AppSettings.Current.MusicNotchEnabled;
            AppSettings.Current.Save();
            ShowToast(AppSettings.Current.MusicNotchEnabled ? "✓ Attached Bezel Notch Enabled" : "✓ Attached Bezel Notch Disabled");
            InvalidateAndRender();
            return;
        }

        // Music Start / Track Change Segment Buttons
        int card2Y = card1Y + 96;
        int segW = 98;
        int segH = 32;
        int segSpacing = 8;
        int segStartX = cx + 16;
        int segStartY = card2Y + 64;

        double[] timerVals = { 0.0, 15.0, 30.0, 60.0, 120.0 };
        for (int i = 0; i < timerVals.Length; i++)
        {
            int bx = segStartX + i * (segW + segSpacing);
            if (x >= bx && x <= bx + segW && y >= segStartY && y <= segStartY + segH)
            {
                AppSettings.Current.MusicTimeDisplaySeconds = timerVals[i];
                AppSettings.Current.Save();
                ShowToast($"✓ Music track display set to {timerVals[i]}s");
                InvalidateAndRender();
                return;
            }
        }

        // Idle Despawn Segment Buttons
        int card3Y = card2Y + 126;
        segStartX = cx + 16;
        segStartY = card3Y + 64;
        double[] idleVals = { 15.0, 30.0, 60.0, 120.0, 999999.0 };
        for (int i = 0; i < idleVals.Length; i++)
        {
            int bx = segStartX + i * (segW + segSpacing);
            if (x >= bx && x <= bx + segW && y >= segStartY && y <= segStartY + segH)
            {
                AppSettings.Current.IdleDespawnSeconds = idleVals[i];
                AppSettings.Current.Save();
                string label = idleVals[i] > 1000 ? "Always Visible" : $"{idleVals[i]}s";
                ShowToast($"✓ Idle timeout set to {label}");
                InvalidateAndRender();
                return;
            }
        }
    }

    private void HandleClickWeather(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 46;
        // Auto-Location Toggle
        if (x >= cx + cw - 74 && x <= cx + cw - 28 && y >= card1Y + 22 && y <= card1Y + 48)
        {
            _weatherUseAuto = !_weatherUseAuto;
            InvalidateAndRender();
            return;
        }

        int card2Y = card1Y + 86;
        int inputY1 = card2Y + 62;
        int inputY2 = card2Y + 114;

        // Input field selection
        if (x >= cx + 90 && x <= cx + 340 && y >= inputY1 && y <= inputY1 + 30)
        {
            _activeInputField = "weather_city";
            InvalidateAndRender();
            return;
        }
        if (x >= cx + 420 && x <= cx + 520 && y >= inputY1 && y <= inputY1 + 30)
        {
            _activeInputField = "weather_country";
            InvalidateAndRender();
            return;
        }
        if (x >= cx + 90 && x <= cx + 340 && y >= inputY2 && y <= inputY2 + 30)
        {
            _activeInputField = "weather_lat";
            InvalidateAndRender();
            return;
        }
        if (x >= cx + 420 && x <= cx + 520 && y >= inputY2 && y <= inputY2 + 30)
        {
            _activeInputField = "weather_lon";
            InvalidateAndRender();
            return;
        }

        _activeInputField = "";

        // Save Button
        int btnY = card2Y + 166;
        if (x >= cx + 16 && x <= cx + 186 && y >= btnY && y <= btnY + 34)
        {
            double.TryParse(_weatherLat, out double lat);
            double.TryParse(_weatherLon, out double lon);
            LiveWeatherService.SaveLocationConfig(_weatherUseAuto, _weatherCity, _weatherCountry, lat, lon);
            ShowToast("✓ Weather configuration saved!");
            InvalidateAndRender();
            return;
        }

        // Refresh Button
        if (x >= cx + 196 && x <= cx + 366 && y >= btnY && y <= btnY + 34)
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
    }

    private void HandleClickPlayers(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 46;
        int listH = Math.Min(150, Math.Max(70, _players.Count * 44 + 36));

        // Existing Player Row actions
        for (int i = 0; i < _players.Count; i++)
        {
            int rowY = card1Y + 36 + i * 40;
            // Launch
            if (x >= cx + cw - 160 && x <= cx + cw - 92 && y >= rowY + 2 && y <= rowY + 30)
            {
                var p = _players[i];
                PlayerService.LaunchPlayer(p);
                ShowToast($"✓ Launched {p.Name}");
                return;
            }
            // Delete
            if (x >= cx + cw - 82 && x <= cx + cw - 18 && y >= rowY + 2 && y <= rowY + 30)
            {
                string removed = _players[i].Name;
                _players.RemoveAt(i);
                PlayerService.SavePlayers(_players);
                ShowToast($"✓ Deleted {removed}");
                InvalidateAndRender();
                return;
            }
        }

        // Add Player Card
        int card2Y = card1Y + listH + 16;
        int addY1 = card2Y + 42;
        int addY2 = card2Y + 82;

        if (x >= cx + 90 && x <= cx + 230 && y >= addY1 && y <= addY1 + 30)
        {
            _activeInputField = "new_player_name";
            InvalidateAndRender();
            return;
        }

        // Icon Segments
        string[] icVals = { "spotify", "ytmusic", "music" };
        for (int i = 0; i < icVals.Length; i++)
        {
            int bx = cx + 295 + i * 82;
            if (x >= bx && x <= bx + 76 && y >= addY1 && y <= addY1 + 30)
            {
                _newPlayerIcon = icVals[i];
                InvalidateAndRender();
                return;
            }
        }

        if (x >= cx + 90 && x <= cx + 430 && y >= addY2 && y <= addY2 + 30)
        {
            _activeInputField = "new_player_url";
            InvalidateAndRender();
            return;
        }

        // Browse Button
        if (x >= cx + 440 && x <= cx + 530 && y >= addY2 && y <= addY2 + 30)
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

        // Add Player Button
        if (x >= cx + 16 && x <= cx + 146 && y >= card2Y + 122 && y <= card2Y + 152)
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
    }

    private void HandleClickShortcuts(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 46;
        // Settings Desktop Shortcut
        if (x >= cx + cw - 176 && x <= cx + cw - 16 && y >= card1Y + 22 && y <= card1Y + 56)
        {
            bool ok = AppSettings.CreateDesktopShortcut("Meridian Settings.lnk", "--settings", "Meridian Settings & Control");
            if (ok) ShowToast("⭐ Settings shortcut created on Desktop!");
            else ShowToast("⚠️ Failed to create shortcut");
            InvalidateAndRender();
            return;
        }

        // Island Desktop Shortcut
        int card2Y = card1Y + 88;
        if (x >= cx + cw - 176 && x <= cx + cw - 16 && y >= card2Y + 22 && y <= card2Y + 56)
        {
            bool ok = AppSettings.CreateDesktopShortcut("Meridian Island.lnk", "", "Meridian Dynamic Island");
            if (ok) ShowToast("⭐ Dynamic Island shortcut created on Desktop!");
            else ShowToast("⚠️ Failed to create shortcut");
            InvalidateAndRender();
            return;
        }

        // Windows Startup Toggle
        int card3Y = card2Y + 88;
        if (x >= cx + cw - 74 && x <= cx + cw - 28 && y >= card3Y + 22 && y <= card3Y + 48)
        {
            bool nextState = !AppSettings.IsRunOnStartup();
            AppSettings.SetRunOnStartup(nextState);
            ShowToast(nextState ? "✓ Windows Startup Enabled" : "✓ Windows Startup Disabled");
            InvalidateAndRender();
            return;
        }

        // Card 4: Hardware Backdrop & Glass Translucency
        int card4Y = card3Y + 80;
        int bBtnW = 160;
        int bBtnH = 30;
        int bSpacing = 10;
        int bStartX = cx + 16;
        int bStartY = card4Y + 48;

        int[] backdropVals = { 3, 4, 2 };
        for (int i = 0; i < backdropVals.Length; i++)
        {
            int optX = bStartX + i * (bBtnW + bSpacing);
            if (x >= optX && x <= optX + bBtnW && y >= bStartY && y <= bStartY + bBtnH)
            {
                ApplyBackdrop(backdropVals[i]);
                return;
            }
        }

        // Translucency Presets
        int tBtnW = 120;
        int tBtnH = 30;
        int tSpacing = 8;
        int tStartX = cx + 16;
        int tStartY = card4Y + 134;

        double[] transVals = { 0.78, 0.85, 0.92, 1.0 };
        for (int i = 0; i < transVals.Length; i++)
        {
            int optX = tStartX + i * (tBtnW + tSpacing);
            if (x >= optX && x <= optX + tBtnW && y >= tStartY && y <= tStartY + tBtnH)
            {
                ApplyTransparency(transVals[i], showToast: true);
                return;
            }
        }
    }

    private void HandleClickAbout(int x, int y, int cx, int cy, int cw)
    {
        int card1Y = cy + 46;
        // Restart Island
        if (x >= cx + cw - 210 && x <= cx + cw - 114 && y >= card1Y + 24 && y <= card1Y + 58)
        {
            StopIslandProcesses();
            StartIslandProcess();
            ShowToast("✓ Dynamic Island Restarted");
            InvalidateAndRender();
            return;
        }

        // Launch / Stop Island
        if (x >= cx + cw - 104 && x <= cx + cw - 12 && y >= card1Y + 24 && y <= card1Y + 58)
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

        int btnY = CardY + 16;
        int closeX = CardX + CardWidth - 44;
        int minX = CardX + CardWidth - 80;

        bool closeHov = (mouseX >= closeX && mouseX <= closeX + 28 && mouseY >= btnY && mouseY <= btnY + 28);
        bool minHov = (mouseX >= minX && mouseX <= minX + 28 && mouseY >= btnY && mouseY <= btnY + 28);

        // Sidebar tabs hover
        int sideX = CardX + 20;
        int sideY = CardY + 68;
        int sideW = 186;
        int tabH = 42;
        int tabSpacing = 6;
        int hovTab = -1;

        for (int i = 0; i < 5; i++)
        {
            int currentY = sideY + i * (tabH + tabSpacing);
            if (mouseX >= sideX && mouseX <= sideX + sideW && mouseY >= currentY && mouseY <= currentY + tabH)
            {
                hovTab = i;
                break;
            }
        }

        if (closeHov != _isCloseHovered || minHov != _isMinHovered || hovTab != _hoveredTab)
        {
            _isCloseHovered = closeHov;
            _isMinHovered = minHov;
            _hoveredTab = hovTab;
            InvalidateAndRender();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_isCloseHovered || _isMinHovered || _hoveredTab != -1)
        {
            _isCloseHovered = false;
            _isMinHovered = false;
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
