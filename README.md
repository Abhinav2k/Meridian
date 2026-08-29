# Liquid Glass Circle
 
Small Windows desktop overlay for a draggable, medium-sized liquid-glass circle with
a live desktop sample inside the glass surface.
 
The app is a transparent, borderless desktop overlay: there is no visible host
window or frame. Only the circle is painted, and clicks outside its bounds pass
through to the desktop.
 
- **Drag**: Left click and drag the circle anywhere on your screen.
- **Snapshot**: Press `S` to save a high-resolution PNG snapshot (`liquid-glass-snapshot.png`).
- **Quit**: Press `Esc` or right-click the circle.
- **Performance**: High-speed, zero-allocation optical pipeline running at 120 FPS with sub-millisecond background capture, full-resolution 280x280 native Gaussian blur, precomputed refraction, and direct GPU DWM compositing.
 
```powershell
dotnet run
```
 
Or launch the built executable directly:
 
```powershell
.\bin\Debug\net10.0-windows\LiquidGlassCircle.exe
```

