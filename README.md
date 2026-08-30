# Liquid Glass Overlay

A high-performance Windows desktop overlay featuring a dynamic morphing liquid glass pill with a live real-time clock, frosted diffusion, meniscus refraction, and interactive mouse hover expansion.

The app is a transparent, borderless desktop overlay: there is no visible host window or frame. Only the dynamic liquid glass shape is rendered, and clicks outside its bounds pass directly through to underlying desktop windows and applications.

### Dynamic Interaction & States

- **Compact Resting State ($190\text{px} \times 44\text{px}$)**:
  - Displays **real-time live clock** (e.g. `09:25 :40 AM`) in razor-sharp $4\times$ supersampled `Segoe UI Variable Display` luxury Swiss typography.
- **Interactive Mouse Hover Weather Dashboard ($500\text{px} \times 180\text{px}$)**:
  - Moving the mouse cursor over the pill triggers a fluid liquid expansion downward into a spacious $500\text{px} \times 180\text{px}$ glass modal.
  - The live time text fades out as an **Apple / visionOS style Weather Dashboard** smoothly blooms into view:
    - **Header**: `📍 Location` & `Date / Day`
    - **Hero Temperature & Condition**: `27° · 🌦️ Light Drizzle` with `H: 28° · L: 25°`
    - **3-Metric Bar**: `💧 80% Humidity`, `💨 7 km/h Wind`, `🌧️ 95% Precip`
  - Automatically fetches real local live weather asynchronously in the background.
  - Moving the mouse away smoothly collapses the modal back to its compact resting state and restores the live clock.

### Hotkeys & Controls

- **Forward Animation (Expand / Bloom to Pill)**:
  - Press **`Right Arrow`**, **`F`**, or **`Down Arrow`**
  - Smoothly drops the liquid droplet downwards and morphs it into the compact clock pill.
- **Backward Animation (Collapse / Retract to Droplet)**:
  - Press **`Left Arrow`**, **`B`**, or **`Up Arrow`**
  - Smoothly dissolves the clock text and retracts the droplet upwards off-screen.
- **Toggle Animation Direction**:
  - Press **`Space`** or **Left-Click** on the pill
  - Reverses animation direction immediately from any mid-point without glitching.
- **Replay from Start**:
  - Press **`R`** to restart the spawn drop from $t = 0$.
- **Snapshot**:
  - Press **`S`** to export a crystal-clear PNG snapshot (`liquid-glass-snapshot.png`).
- **Quit**:
  - Press **`Esc`** or **Right-Click**.

### Run

```powershell
dotnet run
```

Or launch the built executable directly:

```powershell
.\bin\Debug\net10.0-windows\LiquidGlassCircle.exe
```



