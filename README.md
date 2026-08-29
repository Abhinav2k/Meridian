# Liquid Glass Circle
 
Small Windows desktop overlay for a draggable, medium-sized liquid-glass circle with
a live desktop sample inside the glass surface.
 
The app is a transparent, borderless desktop overlay: there is no visible host
window or frame. Only the circle is painted, and clicks outside its bounds pass
through to the desktop.
 
- **Drag**: Left click and drag the circle anywhere on your screen.
- **Quit**: Press `Esc` or right-click the circle.
- **Performance**: High-speed, zero-allocation optical pipeline running at 60 FPS with sub-millisecond background capture, separable Gaussian blur, and precomputed refraction.
 
```powershell
dotnet run
```
 
Or launch the built executable directly:
 
```powershell
.\bin\Debug\net10.0-windows\LiquidGlassCircle.exe
```

