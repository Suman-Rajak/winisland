# WinIsland

A Dynamic Island for Windows: a pill at the top of the screen that expands on hover and shows what's going on.

**Status: Phase 2** — live activities: now playing, volume, brightness, battery, Bluetooth, mic/camera, clipboard history, file shelf. Notifications are next.

## What works

- Transparent, always-on-top pill at the top centre of the primary monitor (sits below the taskbar if it's docked at the top).
- Spring animations between **idle**, **compact** and **expanded**; hover to expand.
- Clicks pass straight through everywhere except the pill, and clicking it never steals focus.
- Now playing for any app that publishes media info (Spotify, Amazon Music, YouTube in Chrome/Edge, Media Player…): artwork, title, progress, play/pause/next/previous.
- Glass look: a slightly see-through tint with a top highlight and bright rim. While music plays, the glass, level bars and progress bar take their colours from the artwork.
- Artwork comes from the playing app; when the app doesn't provide any (Amazon Music doesn't), it's looked up by artist + title in the iTunes catalogue (your country's store, then the US store) and then MusicBrainz / Cover Art Archive. Only results whose artist *and* title match are used; if nothing matches, the island gets a colour generated from the song instead. The lookup sends the artist and title to Apple and MusicBrainz, and can be switched off in Settings.
- Some apps publish odd metadata. Amazon Music never shares artwork or track length, and sometimes reports one value as both title and artist for a whole song — sometimes it's the artist ("Sayfalse"), sometimes the song ("Pooja & Patola"). The island asks the catalogue which it is (when online lookups are allowed); if it's an artist, or it can't tell, it shows "Now playing" rather than passing the artist off as the song.
- Volume and brightness changes show briefly as a level bar, keeping the music's colours if something's playing. Switching speakers (headphones connecting) doesn't pop up the volume; the Bluetooth notice covers that. Brightness works on laptop screens; desktop monitors don't report it. Windows' own volume pop-up still appears too.
- Battery: "Charging" when you plug in, "Fully charged" when it gets to 100%, and warnings at 20% and 10% (once each per discharge). Unplugging is silent; PCs without a battery never show it.
- Bluetooth: paired devices connecting ("OnePlus Nord Buds 3 · 80%", with the battery when the device reports it) and disconnecting, with an icon for headphones, speakers, phones, mice and so on. Short drops and quick reconnects are ignored; mice and keyboards (which nap when idle) only announce reconnecting after a long absence and never announce disconnecting.
- Microphone and camera: while any app uses them, a dot sits in a small bubble beside the island (orange for the mic, green for the camera), and the island stays visible even with "hide when idle". When an app starts using one, the island says which ("Discord · Microphone"); point at the bubble to see it again. Read from Windows' capability consent store, the same place its own privacy indicator looks.
- Tabs: the expanded island has a small tab row: music (or the clock), the clipboard and the file shelf. Every hover starts on the first tab; dragging files onto the island opens the shelf.
- Clipboard history: the last 20 things you copied (text, links, images, files), newest first; click one to copy it again, × to forget it. Kept in memory only (gone when WinIsland quits), and anything password managers mark as private is skipped, using the same markers as Windows' own clipboard history.
- File shelf: drag files or folders onto the island to park them; drag them out later into any folder, email or chat (always as a copy, the originals never move), click to open, × to take off the shelf. The shelf only remembers where files are, in `%LOCALAPPDATA%\WinIsland\shelf.json`, so it survives restarts. Things that aren't files yet (an image dragged out of a browser) are saved into `%LOCALAPPDATA%\WinIsland\Shelf`.
- Hides itself while a fullscreen app or game is in front.
- Tray icon: click for Settings, right-click to hide the island or quit. Launching WinIsland again while it's running also opens Settings.

## Settings

Changes apply immediately and are saved to `%LOCALAPPDATA%\WinIsland\settings.json`.

| Setting | What it does |
|---|---|
| Start with Windows | Adds WinIsland to your sign-in apps (per-user Run key) |
| Display | Which screen the island sits on |
| Distance from top | Gap above the island; 0 hugs the top edge |
| Hide when idle | Fades the island out when nothing is playing; point at the top of the screen to bring it back |
| Animation speed | Relaxed, Normal or Snappy |
| Glass transparency | How much of what's behind shows through (0–30%) |
| Match the music's colours | Tint the glass from the album art while music plays |
| Volume and brightness | Briefly show level changes on the island |
| Battery and charging | Show plugging in, full charge and low-battery warnings |
| Bluetooth devices | Show devices connecting and disconnecting |
| Microphone and camera in use | Dot beside the island while an app uses them, and which app |
| Clipboard history | The clipboard tab; off also forgets everything in it |
| File shelf | The shelf tab, and dropping files on the island |
| Find missing album art online | iTunes / MusicBrainz lookup for apps that don't share artwork (sends artist + title to them) |

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
| `--demo-level` | Keep the volume indicator up (at 45%) |
| `--demo-battery` | Keep "Charging" up (at 64%) |
| `--demo-bluetooth` | Keep a connected pair of earbuds up (at 80%) |
| `--demo-privacy` | Pretend Discord just started using the microphone |
| `--demo-clipboard` | Add a few made-up clipboard entries |
| `--demo-shelf` | Put a few of the app's own files on the shelf (not saved) |
| `--tab=N` | With `--pin=expanded`, open tab N (0 = first) |

Errors are logged to `%LOCALAPPDATA%\WinIsland\log.txt`.

## Layout

```
src/WinIsland/
  IslandWindow.xaml(.cs)      the island: window, hover/click-through, pill animation, glass; picks the activity to show
  Activities/                 what the island shows; each has a priority, a compact view and/or an expanded view
    IslandActivity.cs         the base class (and how the island chooses between activities)
    MediaActivity.cs          now playing (+ MediaCompactView, MediaExpandedView)
    ClockActivity.cs          expanded fallback when nothing else is going on
    TransientActivity.cs      base for short-lived notices that take over the pill for a moment
    LevelActivity.cs          level indicators: VolumeActivity, BrightnessActivity (+ LevelView)
    BatteryActivity.cs        charging / full / low battery (+ BatteryView, BatteryIcon)
    BluetoothActivity.cs      devices connecting / disconnecting (+ BluetoothView)
    PrivacyActivity.cs        "Discord · Microphone" notice (+ PrivacyView); the dot bubble lives in IslandWindow
    ClipboardActivity.cs      clipboard history tab (+ ClipboardView, ClipboardEntry)
    ShelfActivity.cs          file shelf tab (+ ShelfView, ShelfItem); dropping on the island is handled in IslandWindow
  SettingsWindow.xaml(.cs)    settings UI
  AppSettings.cs              settings model + JSON persistence
  StartupRegistration.cs      "Start with Windows"
  Services/MediaService.cs    Windows media sessions (now playing + controls)
  Services/ArtworkService.cs  artwork decoding, colour palette, generated fallback colours
  Services/MusicCatalog.cs    online lookups: artwork, and which of title/artist a value is (iTunes, MusicBrainz)
  Services/TrackQuery.cs      title/artist clean-up and matching
  Services/ColorMath.cs
  Services/VolumeMonitor.cs   default speakers' volume/mute (Core Audio)
  Services/BrightnessMonitor.cs  built-in screen brightness (WMI)
  Services/BluetoothMonitor.cs   paired Bluetooth devices' connections, type and battery
  Services/PrivacyMonitor.cs     which apps are using the mic/camera (consent store, re-read every 1.5 s)
  Services/ClipboardWatcher.cs   clipboard change listener; reads copies, skips private ones
  Services/FullscreenDetector.cs
  Interop/Native.cs           Win32 / DWM calls
  Interop/CoreAudio.cs        Core Audio COM interfaces
  Interop/Displays.cs         monitor enumeration
  TrayIconController.cs       notification-area icon
```
