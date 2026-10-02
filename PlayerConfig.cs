using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Meridian;

public sealed class PlayerItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Icon { get; set; } = "";
}

public static class PlayerService
{
    private static readonly object _lock = new();
    private static List<PlayerItem>? _cachedPlayers;
    private static DateTime _lastLoadedUtc = DateTime.MinValue;
    private static DateTime _lastWriteTimeUtc = DateTime.MinValue;
    private static readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int command);

    private const int SwHide = 0;

    public static event Action? PlayersChanged;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint PrivateExtractIcons(
        string lpszFile,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static IReadOnlyList<PlayerItem> GetPlayers()
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            if (_cachedPlayers != null && (now - _lastLoadedUtc).TotalSeconds < 1.0)
            {
                return _cachedPlayers;
            }

            string? configPath = FindConfigFile();
            if (configPath != null && File.Exists(configPath))
            {
                try
                {
                    DateTime writeTime = File.GetLastWriteTimeUtc(configPath);
                    if (_cachedPlayers != null && writeTime == _lastWriteTimeUtc)
                    {
                        _lastLoadedUtc = now;
                        return _cachedPlayers;
                    }

                    string json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("players", out var playersProp) && playersProp.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<PlayerItem>();
                        foreach (var elem in playersProp.EnumerateArray())
                        {
                            string name = elem.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                            string url = elem.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
                            string icon = elem.TryGetProperty("icon", out var ic) ? (ic.GetString() ?? "") : "";
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                list.Add(new PlayerItem { Name = name, Url = url, Icon = icon });
                            }
                        }
                        if (list.Count > 0)
                        {
                            _cachedPlayers = list;
                            _lastWriteTimeUtc = writeTime;
                            _lastLoadedUtc = now;
                            PlayersChanged?.Invoke();
                            return _cachedPlayers;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlayerConfig] Load error: {ex.Message}");
                }
            }

            // Fallback / Auto-generate default if file does not exist
            if (_cachedPlayers == null)
            {
                _cachedPlayers = GetDefaultPlayers();
                EnsureDefaultConfigFile();
            }

            _lastLoadedUtc = now;
            return _cachedPlayers;
        }
    }

    public static void LaunchPlayer(PlayerItem player)
    {
        if (string.IsNullOrWhiteSpace(player.Url)) return;
        try
        {
            string target = Environment.ExpandEnvironmentVariables(player.Url);
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerConfig] Failed to launch {player.Url}: {ex.Message}");
        }
    }

    public static void SavePlayers(IEnumerable<PlayerItem> players)
    {
        lock (_lock)
        {
            try
            {
                string configPath = FindConfigFile() ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "player_config.json");
                var list = new List<PlayerItem>(players);
                var data = new { players = list };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                _cachedPlayers = list;
                _lastWriteTimeUtc = File.GetLastWriteTimeUtc(configPath);
                _lastLoadedUtc = DateTime.UtcNow;
                PlayersChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlayerConfig] Save error: {ex.Message}");
            }
        }
    }

    public static bool IsSpotify(PlayerItem player) =>
        string.Equals(player.Icon, "spotify", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(player.Name, "spotify", StringComparison.OrdinalIgnoreCase);

    public static bool IsSpotifyRunning()
    {
        try
        {
            return Process.GetProcessesByName("Spotify").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool LaunchSpotifyInBackground(PlayerItem player)
    {
        if (IsSpotifyRunning()) return true;

        try
        {
            string? exePath = ResolveExePath(player);
            if (string.IsNullOrWhiteSpace(exePath)) return false;

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--minimized",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process == null) return false;
            _ = HideProcessWindowsAsync(process.Id);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerConfig] Failed to launch Spotify in the background: {ex.Message}");
            return false;
        }
    }

    private static async Task HideProcessWindowsAsync(int processId)
    {
        // Spotify can create its main window after its process starts. Keep its
        // startup path background-only while it registers its media session.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                    if (windowProcessId == processId)
                    {
                        ShowWindowAsync(hWnd, SwHide);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    public static Bitmap? GetPlayerIconBitmap(PlayerItem player, int size = 128)
    {
        string key = $"{player.Icon}_{player.Url}_{size}";
        lock (_lock)
        {
            if (_iconCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            string? exePath = ResolveExePath(player);
            if (exePath != null && File.Exists(exePath))
            {
                var extracted = ExtractHighResIcon(exePath, size);
                if (extracted != null)
                {
                    _iconCache[key] = extracted;
                    return extracted;
                }
            }
            return null;
        }
    }

    private static string? ResolveExePath(PlayerItem player)
    {
        string target = Environment.ExpandEnvironmentVariables(player.Url ?? "");
        if (!string.IsNullOrWhiteSpace(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            return target;
        }

        if (string.Equals(player.Icon, "spotify", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(player.Name, "spotify", StringComparison.OrdinalIgnoreCase))
        {
            string[] candidates =
            {
                @"C:\Users\abhin\AppData\Roaming\Spotify\Spotify.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Spotify\Spotify.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\Spotify.exe")
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
        }

        return null;
    }

    private static Bitmap? ExtractHighResIcon(string exePath, int size)
    {
        try
        {
            IntPtr[] phicon = new IntPtr[1];
            uint[] piconid = new uint[1];
            uint count = PrivateExtractIcons(exePath, 0, size, size, phicon, piconid, 1, 0);
            if (count > 0 && phicon[0] != IntPtr.Zero)
            {
                try
                {
                    using var ico = Icon.FromHandle(phicon[0]);
                    return new Bitmap(ico.ToBitmap());
                }
                finally
                {
                    DestroyIcon(phicon[0]);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerConfig] Error extracting icon from {exePath}: {ex.Message}");
        }
        return null;
    }

    private static string? FindConfigFile()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string rootDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\.."));
        string[] searchPaths =
        {
            Path.Combine(rootDir, "player_config.json"),
            Path.Combine(baseDir, "player_config.json"),
            Path.Combine(Environment.CurrentDirectory, "player_config.json")
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path)) return path;
        }
        return searchPaths[0];
    }

    private static void EnsureDefaultConfigFile()
    {
        try
        {
            string targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "player_config.json");
            if (!File.Exists(targetPath))
            {
                string spotifyPath = @"C:\Users\abhin\AppData\Roaming\Spotify\Spotify.exe";
                string spotifyUrl = File.Exists(spotifyPath) ? spotifyPath.Replace("\\", "\\\\") : "https://open.spotify.com";

                string json = $$"""
                {
                  "players": [
                    {
                      "name": "Spotify",
                      "url": "{{spotifyUrl}}",
                      "icon": "spotify"
                    },
                    {
                      "name": "YouTube Music",
                      "url": "https://music.youtube.com",
                      "icon": "ytmusic"
                    }
                  ]
                }
                """;
                File.WriteAllText(targetPath, json);
            }
        }
        catch { }
    }

    private static List<PlayerItem> GetDefaultPlayers()
    {
        string spotifyPath = @"C:\Users\abhin\AppData\Roaming\Spotify\Spotify.exe";
        string spotifyUrl = File.Exists(spotifyPath) ? spotifyPath : "https://open.spotify.com";

        return new List<PlayerItem>
        {
            new PlayerItem { Name = "Spotify", Url = spotifyUrl, Icon = "spotify" },
            new PlayerItem { Name = "YouTube Music", Url = "https://music.youtube.com", Icon = "ytmusic" }
        };
    }
}
