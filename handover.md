# Liquid Glass Dynamic Island — Handover

## Project
- Windows Forms layered-window Dynamic Island overlay; .NET `net10.0-windows10.0.19041.0`.
- Main rendering, interaction, timer, and glass shader code: `OverlayForm.cs`.
- Weather API/config: `LiveWeatherService.cs`, `weather_config.json`.
- Player launcher/config: `PlayerConfig.cs`, `player_config.json`.
- Build: `dotnet build -c Release`.
- Run output: `bin\Release\net10.0-windows10.0.19041.0\LiquidGlassCircle.exe` with that directory as working directory.

## Non-negotiable implementation constraints
- Never make synchronous network calls from UI/render code.
- Never add loose `.cs` files under the workspace: SDK compile wildcards include them.
- Dispose GDI+ `Brush`, `Pen`, `Font`, `Bitmap`, and `GraphicsPath` objects correctly.
- Do not share GDI+ surfaces across UI and render threads without the existing locks.
- Keep non-weather UI monochrome obsidian/frosted-glass/ash-white; reserve vivid colors for weather cards.
- The overlay must remain topmost using `EnsureSystemZOrder()`; it is checked from the render loop.

## Current uncommitted work
### Player quick launcher
- `player_config.json` targets installed Spotify directly:
  `C:\Users\abhin\AppData\Roaming\Spotify\Spotify.exe`.
- `PlayerConfig.cs` launches targets with `Process.Start(... UseShellExecute = true)`.
- It uses `PrivateExtractIcons` from `user32.dll` to extract/caches a native high-resolution executable icon; vector icons remain the fallback.
- **Still required:** update `OverlayForm.DrawPlayerIconButton` to render `PlayerService.GetPlayerIconBitmap(player, size)` with high-quality interpolation before falling back to `DrawSpotifyIcon` / other vector renderers.

### Home live-timer cancel widget
Requested behavior: while Quick, Manual, or sleep timer is live, render below the Home greeting/date:
- circular frosted-glass tile;
- ash-white static bell vector;
- perimeter countdown-progress ring;
- remaining `mm:ss` text and hover label; and
- click anywhere on the tile/text cancels every active timer immediately.

Useful existing timer methods/state:
- `CancelChronoTimer()` near `OverlayForm.cs:2625`
- `CancelManualTimer()` near `OverlayForm.cs:2592`
- sleep timer state near `OverlayForm.cs:909`
- collapsed timer indicator helper `GetActiveTimerRemainingMinutes()` near `OverlayForm.cs:7758`
- Home content date/header near `OverlayForm.cs:3830`
- Home click coordinate conversion near `OverlayForm.cs:2037`

Suggested integration:
1. Add `IsAnyTimerLive`, remaining/total-second helpers, and `TurnOffAllActiveTimers` close to existing timer helpers. Cancellation must clear Home/Chrono cache, force `UpdateTimeMaskIfNeeded(true)`, set `_needExpandedUpdate`, and signal `_renderSignal`.
2. Add the widget renderer near other Home render helpers and invoke it after the date line in `DrawTabHomeContent`.
3. Add a Home hit target in `HandleExpandedClickAsync` after converting to `mx`/`my`; update hover state in the existing expanded hover/render path.
4. Build, terminate any running `LiquidGlassCircle.exe` only when approved, relaunch, and verify the icon plus all timer types.

## Existing behavior worth preserving
- Smoked liquid-glass transmission in `ProcessAndPresent` uses the original 78% blend values (`tr * 200`, body constants `40/44/56`); do not reintroduce the reverted 5% brightness increase.
- Chrono uses a 44px Quick/Manual paired capsule, hold-to-repeat manual arrows, arrow-area double-click suppression, live collapsed timer minutes, and post-expiry shaking bell bubble.
- Timers prevent normal inactivity despawn while active; alarm dismissal restores regular behavior.
- Home weather typography includes extra bottom clearance for the temperature block.

## Working-tree status
`OverlayForm.cs`, `PlayerConfig.cs`, `player_config.json`, project/config/weather files, handover, and multiple visual verification PNGs are uncommitted. Do not discard unrelated pending work.
