# Liquid Glass Overlay

A high-performance Windows desktop overlay featuring a dynamic morphing liquid glass pill with a live real-time clock, frosted diffusion, meniscus refraction, and interactive mouse hover expansion.

The app is a transparent, borderless desktop overlay: there is no visible host window or frame. Only the dynamic liquid glass shape is rendered, and clicks outside its bounds pass directly through to underlying desktop windows and applications.

### Dynamic Interaction & States

- **Compact Resting State ($190\text{px} \times 44\text{px}$)**:
  - Displays **real-time live clock** (e.g. `9:00:25 AM`) in razor-sharp $4\times$ supersampled `Segoe UI Variable Display` typography.
- **Interactive Mouse Hover Expansion ($680\text{px} \times 64\text{px}$)**:
  - Moving the mouse cursor over the pill triggers a fluid liquid expansion to a wide $680\text{px}$ bar, while the time text smoothly fades away to pure translucent glass.
  - Moving the mouse away smoothly collapses the pill back to its compact resting state and fades the live time clock back in.

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



