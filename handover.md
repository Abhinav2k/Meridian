# Liquid Glass Dynamic Island — Handover

## 1. Project Overview & Architecture
- **Framework**: Windows Forms layered-window Dynamic Island overlay (`net10.0-windows10.0.19041.0`).
- **Core File**: [`OverlayForm.cs`](file:///C:/Users/abhin/Workspace/liquid%20glass/OverlayForm.cs) (~10.9k lines) houses the render loop, GDI+ / direct memory 32-bit ARGB surface blitting, 120 FPS physics springs, SDF geometry shader, tab logic, and Windows media/session hooks.
- **Weather Services**: [`LiveWeatherService.cs`](file:///C:/Users/abhin/Workspace/liquid%20glass/LiveWeatherService.cs) (Open-Meteo real-time API, WMO code mapper, 15 Bauhaus condition models) and [`weather_config.json`](file:///C:/Users/abhin/Workspace/liquid%20glass/weather_config.json).
- **Player Launcher**: [`PlayerConfig.cs`](file:///C:/Users/abhin/Workspace/liquid%20glass/PlayerConfig.cs) and [`player_config.json`](file:///C:/Users/abhin/Workspace/liquid%20glass/player_config.json) (native executable icon extraction via `PrivateExtractIcons` + fallback vectors).
- **Entry Point**: [`Program.cs`](file:///C:/Users/abhin/Workspace/liquid%20glass/Program.cs) with global unhandled exception and thread error handlers logging to `crash.log`.

---

## 2. Non-Negotiable Implementation Constraints
1. **Visual Verification & Screenshots (`GEMINI.md`)**:
   - **No autonomous screenshots**: Do NOT create `take_screenshot.trigger` or capture/view desktop screenshots autonomously. The user directly views the running application on their display.
   - **Confirm before capturing**: Always ask the user if a screenshot is needed, and only capture or inspect when explicitly requested.
2. **Optics & Smoked Glass Values**:
   - Preserves 78% transmission in `ProcessAndPresent` (`tr * 200`, obsidian body constants `40/44/56`). Never increase background opacity or wash out the dark smoked glass.
   - Monochrome obsidian/frosted-glass palette across Home, Music, and Chrono tabs; vivid colors are reserved exclusively for weather condition cards and artwork.
3. **Threading & Performance**:
   - Never perform synchronous network requests or heavy disk I/O on UI or render threads.
   - Dispose all transient GDI+ resources (`Brush`, `Pen`, `Font`, `Bitmap`, `GraphicsPath`) deterministically.
   - Keep lock contention minimal: render loop runs at 120+ FPS via high-resolution timer (`TimeBeginPeriod(1)`).
   - Ensure the window stays topmost via `EnsureSystemZOrder()`, executed periodically without stealing focus (`SWP_NOACTIVATE`).

---

## 3. Implemented Features & Architecture Details

### A. Attached Notch Mode with Curved Concave Fillets
- **Activation**: When music is playing (`_hasActiveMedia`), the island is unexpanded (`_hoverPos < 0.05`), entry drop has finished (`_progress > 0.82`), compact time has faded (`_compactTimeAlpha < 0.15`), and no alarms are ringing (`isMusicOnlyResting`).
- **Docking Physics**:
  - `_notchAttachment` interpolates smoothly ($0.0 \leftrightarrow 1.0$) at 10x/sec.
  - `restingTopY` transitions from `TopPadding` (18px) to `0.0` (flush against top bezel).
  - Compact height morphs to `36.0px` (`GetCompactPillHeight()`).
  - Asymmetric SDF: Bottom corners remain convex rounded ($R=18\text{px}$); top edge flattens with $topR = Radius \times (1 - notchP)$.
- **Concave Fillet Ears (MacBook-style Notch Flare)**:
  - Circular arc fillet of radius $R_f = 14.0\text{px} \times notchP$ at both top corners.
  - At $Y=0$, the curve meets the top screen bezel horizontally ($dy/dx = 0$).
  - At $Y=14\text{px}$, the curve merges vertically ($dx/dy = 0$) into the notch's vertical wall.
  - Calculated in SDF via $earSdf = R_f - \sqrt{u^2 + v^2}$ with $u = \text{absPx} - (W + R_f)$, $v = y_{\text{rel}} - R_f$.
  - Elevation contact shadow (`shadowSdf`) follows the curved fillet.
  - Optical surface gradient $(gx, gy)$ blends into the fillet circular normal for background refraction and edge specular sheen.
- **Hover & Interaction**:
  - Mouse hit target includes the flared ears and starts at $Y=0$ (`minTriggerY = 0.0`).
  - Moving cursor onto the notch smoothly detaches it down to the expanded music player card.
  - Unhovering from notch mode returns directly to the notch without re-displaying the 1-minute clock if the initial music time window has already elapsed.

### B. Bauhaus Abstract Geometric Weather System
- **15 Distinct Weather Cards**:
  1. Sunny Day | 2. Clear Night | 3. Partly Cloudy Day | 4. Partly Cloudy Night
  5. Overcast | 6. Fog / Mist | 7. Drizzle | 8. Moderate Rain
  9. Freezing Rain | 10. Light Snow | 11. Heavy Snow | 12. Thunderstorm
  13. Severe Hail | 14. Golden Sunset / Dusk | 15. Wind / Gale
- **Vector Artwork Rules**:
  - Conditions 3 & 4 draw peeking sun/moon behind the cloud before returning (no rain).
  - Condition 5 draws the cloud alone without rain.
  - Condition 6 draws 3 horizontal mist scanlines.
  - Condition 14 draws sinking semicircle sun, datum horizon line, and dusk ray.
  - Condition 15 draws sweeping aerodynamic streamlines.
- **Debug Weather Hotkey ('S')**:
  - Pressing `S` (handled in both `KeyDown` and `GetAsyncKeyState(0x53)`) cycles conditions `1 -> 2 -> ... -> 15 -> Live Weather` via `CycleWeatherCardStyle()`.

### C. Home Active Timer & Cancel Tile
- **Live Bell Widget**: Appears below greeting/date whenever a Quick, Manual, or Sleep timer is running.
- **Visuals**: Frosted circular tile with ash-white bell vector, active progress perimeter ring, bold 12.5pt `mm:ss` countdown text, and `REMAINING` / `CANCEL TIMER` label (tinted coral red on hover).
- **Hit Testing**: `IsPointInHomeTimer` spans the bell circle and text box ($X \in [56, 165], Y \in [95, 128]$) so clicking cancels all active timers immediately.

### D. Player Launcher
- Installed Spotify targeted at `C:\Users\abhin\AppData\Roaming\Spotify\Spotify.exe`.
- Cached 256x256 extracted icons via `PrivateExtractIcons` from `user32.dll`.

---

## 4. Build, Run, and Diagnostics Runbook

### Clean Build
```powershell
dotnet build -c Release
```
Builds cleanly with **0 Warnings, 0 Errors**.

### Running the Application
Always terminate existing instances before launching to avoid mutex / handle conflicts:
```powershell
Stop-Process -Name LiquidGlassCircle -Force -ErrorAction SilentlyContinue
Start-Process -FilePath ".\bin\Release\net10.0-windows10.0.19041.0\LiquidGlassCircle.exe" -WorkingDirectory ".\bin\Release\net10.0-windows10.0.19041.0"
```
Or run directly via dotnet:
```powershell
dotnet run -c Release --no-build
```

### Process Verification
```powershell
Get-Process LiquidGlassCircle
```

### Hotkeys & Shortcuts
- `Space`: Toggle Play / Pause on active media session (or simulated media).
- `S`: Cycle Bauhaus weather conditions (`1 -> 2 -> ... -> 15 -> Live`).
- `1` / `2` / `3` / `4`: Switch tabs (Home / Music / Weather / Chrono).
- `Right` / `N`: Next track.
- `Left` / `P`: Previous track.
- `Z` / `U`: Shuffle toggle.
- `Escape`: Exit application.
- `Right-Click`: Close application.

---

## 5. Working Tree & Commit Status
- **Git Branch**: `master`
- **Recent Commits**:
  - `e717c32`: Attached notch mode for music, weather artwork fixes, home timer UI, S key restore.
  - `330cff4`: Established screenshot guardrail in `GEMINI.md`.
  - `7728da7`: Added curved concave fillet ears ($R_f=14\text{px}$) to docked notch against screen bezel.
- All code changes are committed and working tree is clean.
