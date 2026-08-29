# Liquid Glass Overlay

A high-performance Windows desktop overlay featuring a morphing liquid glass pill with live frosted diffusion, real-time meniscus refraction, and direct GPU DWM compositing.

The app is a transparent, borderless desktop overlay: there is no visible host window or frame. Only the dynamic liquid glass shape is rendered, and clicks outside its bounds pass directly through to underlying desktop windows and applications.

### Hotkeys & Controls

- **Forward Animation (Expand / Bloom to Pill)**:
  - Press **`Right Arrow`**, **`F`**, or **`Down Arrow`**
  - Smoothly expands the liquid droplet downwards and morphs it into the wide $500\text{px} \times 64\text{px}$ curved pill.
- **Backward Animation (Collapse / Retract to Droplet)**:
  - Press **`Left Arrow`**, **`B`**, or **`Up Arrow`**
  - Smoothly shrinks the pill back into a tiny droplet ($R = 14\text{px}$) and retracts it upwards off-screen.
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


