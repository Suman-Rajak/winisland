# WinIsland

A Dynamic Island for Windows: a pill at the top of the screen that expands on hover and shows what's going on.

**Status: Phase 1** — island shell + now playing.

## What works

- Transparent, always-on-top pill at the top centre of the primary monitor (sits below the taskbar if it's docked at the top).
- Spring animations between **idle**, **compact** and **expanded**; hover to expand.
- Clicks pass straight through everywhere except the pill, and clicking it never steals focus.
- Now playing for any app that publishes media info (Spotify, YouTube in Chrome/Edge, Media Player…): artwork, title, progress, play/pause/next/previous.
- Hides itself while a fullscreen app or game is in front.
- Tray icon (right-click) to hide the island or quit.

## Run it

Requires the .NET 10 SDK.

```bash
dotnet build src/WinIsland/WinIsland.csproj -p:Platform=x64
```

Then run `src/WinIsland/bin/x64/Debug/net10.0-windows10.0.26100.0/WinIsland.exe`, or open `WinIsland.slnx` in Visual Studio and press F5.

### Developer switches

| Switch | Effect |
|---|---|
| `--pin=idle\|compact\|expanded` | Lock the island in one state (no hover needed) |
| `--demo-media` | Show a fake track instead of the real media session |

Errors are logged to `%LOCALAPPDATA%\WinIsland\log.txt`.

## Layout

```
src/WinIsland/
  IslandWindow.xaml(.cs)      the island: window setup, hover/click-through, animations, content
  Services/MediaService.cs    Windows media sessions (now playing + controls)
  Services/FullscreenDetector.cs
  Interop/Native.cs           Win32 / DWM calls
  TrayIconController.cs       notification-area icon
```
