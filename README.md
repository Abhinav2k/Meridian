# Liquid Glass Circle

Small Windows prototype for a draggable, medium-sized liquid-glass circle with
a live desktop sample inside the glass surface.

The app is a transparent, borderless desktop overlay: there is no visible host
window or frame. Only the circle is painted, and clicks outside its bounds pass
through to the desktop. Drag the circle to move it; press `Esc` to quit. The
desktop sample refreshes at roughly 20 frames per second while the optical
highlight continues to animate independently.

```powershell
dotnet run
```

Or launch the built executable directly:

```powershell
.\bin\Debug\net10.0-windows\LiquidGlassCircle.exe
```
