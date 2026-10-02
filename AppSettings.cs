using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Meridian;

public sealed class AppSettingsData
{
    public bool MusicNotchEnabled { get; set; } = true;
    public double MusicTimeDisplaySeconds { get; set; } = 30.0;
    public double IdleDespawnSeconds { get; set; } = 60.0;
    public bool RunOnStartup { get; set; } = false;
    public int WeatherRefreshMinutes { get; set; } = 15;
    public int BackdropType { get; set; } = 3; // 3 = Desktop Acrylic (Translucent), 4 = Mica Alt, 2 = Mica
    public double WindowOpacity { get; set; } = 0.85; // 0.85 = 85% opacity (enhanced see-through glass transparency)

    public void Save() => AppSettings.Save();
}

public static class AppSettings
{
    private static readonly object _lock = new();
    private static AppSettingsData _current = new();
    private static FileSystemWatcher? _watcher;
    private static DateTime _lastWriteTimeUtc = DateTime.MinValue;

    public static event Action? SettingsChanged;

    public static AppSettingsData Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    static AppSettings()
    {
        Load();
        SetupWatcher();
    }

    public static string GetSettingsFilePath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDir, "settings.json");
    }

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path))
                {
                    string curDir = Path.Combine(Environment.CurrentDirectory, "settings.json");
                    if (File.Exists(curDir)) path = curDir;
                }

                if (File.Exists(path))
                {
                    DateTime writeTime = File.GetLastWriteTimeUtc(path);
                    if (writeTime == _lastWriteTimeUtc) return;

                    string json = File.ReadAllText(path);
                    var data = JsonSerializer.Deserialize<AppSettingsData>(json);
                    if (data != null)
                    {
                        _current = data;
                        _lastWriteTimeUtc = writeTime;
                        SettingsChanged?.Invoke();
                    }
                }
                else
                {
                    // Create default file
                    Save();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppSettings] Load failed: {ex.Message}");
            }
        }
    }

    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                string path = GetSettingsFilePath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_current, options);

                // Temporarily disable watcher to prevent double-load
                if (_watcher != null) _watcher.EnableRaisingEvents = false;

                File.WriteAllText(path, json);
                _lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);

                if (_watcher != null) _watcher.EnableRaisingEvents = true;

                SettingsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppSettings] Save failed: {ex.Message}");
            }
        }
    }

    private static void SetupWatcher()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(baseDir)) return;

            _watcher = new FileSystemWatcher(baseDir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (s, e) =>
            {
                // Debounce
                System.Threading.Thread.Sleep(50);
                Load();
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] FileSystemWatcher setup failed: {ex.Message}");
        }
    }

    public static bool CreateDesktopShortcut(string shortcutFileName, string arguments, string description)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!Directory.Exists(desktop)) return false;

            string shortcutPath = Path.Combine(desktop, shortcutFileName);
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            shortcut.Description = description;
            shortcut.IconLocation = $"{exePath},0";
            shortcut.Save();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] Shortcut creation error: {ex.Message}");
            return false;
        }
    }

    public static bool IsDesktopShortcutPresent(string shortcutFileName)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string shortcutPath = Path.Combine(desktop, shortcutFileName);
            return File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRunOnStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("MeridianDynamicIsland") != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetRunOnStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return false;

            string appName = "MeridianDynamicIsland";
            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(appName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(appName, false);
            }

            _current.RunOnStartup = enable;
            Save();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] SetRunOnStartup error: {ex.Message}");
            return false;
        }
    }
}
