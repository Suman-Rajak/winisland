# Glyde

*Formerly WinIsland. The code still uses that as its working name (namespace, folders, repo).*

A voice island for Windows: a pill at the top of the screen that expands on hover, shows what's going on, and answers to "Hey Glyde".

**Status: Phase 4** — **Glyde** has a brain: what it can't do on the PC (questions, Hindi, emails, WhatsApp messages, copied text) goes to Google Gemini with your own free key. (Phase 3: Glyde, started with a shortcut or "Hey Glyde", runs simple commands on the spot.) (Phase 2's live activities: now playing, volume, brightness, battery, Bluetooth, mic/camera, clipboard history, file shelf, notifications.)

## What works

- Always on top, at the top centre of the primary monitor (below the taskbar if it's docked at the top).
- Glyde has a face: its logo, a glossy violet-to-pink orb with two eyes and a smile, like an emoji. It sits in the empty island, blinking and glancing about now and then; it's the assistant's orb (wide-eyed as it listens, eyes up while it thinks, its mouth moving as it talks, smiling after an answer, a little puzzled when it didn't catch you); and it's at the top of Settings, its eyes following your pointer. The app, tray and Start menu icons are the same face.
- By default the island is a Mac-style notch: pure black, hanging from the top edge with square top corners, rounded bottom ones and little shoulders curving out into the edge, always there. When it opens it drops down as a black panel, like NotchNook. Around it, a bar across the top of the screen, exactly as tall as the notch at rest: the Mac's menu bar. It wears the colour of the app you're in, read off the top of its window (its title bar or tabs), as a phone's status bar takes on the app below it, fading over when you switch apps and following when the app changes colour. It's only a little see-through (mostly the app's colour over Windows' blurred acrylic; solid with transparency effects off), its words and icons go dark on a light colour and light on a dark one, and with no app colour to wear it follows the Windows theme. Windows keeps the bar as it keeps the bottom for the taskbar (it's an app bar), so maximized and snapped windows start below it. Full-screen video and games still get the whole screen; hiding the island from the tray or quitting gives the top back, and Windows takes it back by itself if Glyde is ended. Turned off, the island is the floating glass pill. (Settings → Mac-style notch.)
- At rest, Glyde's face sits at the island's left end and the time at its right (12 or 24 hours, as Windows shows it).
- The bar beside the notch is a Mac's menu bar. On the left: Glyde's menu (About this PC, Windows and Glyde settings, Task Manager, Sleep, Shut down or restart… in Windows' own box, Lock screen); the app you're in, by name, with its menu (hide it, maximize or restore it, hide others, close it); and Go (Home, Desktop, Documents, Downloads, Pictures, Music, Videos, This PC, Recycle Bin). On the right: the apps pinned to your taskbar, a microphone to talk to Glyde, Wi-Fi (its signal; click for Windows' network list), sound (scroll over it to turn it up or down; click for a volume slider and mute), battery (charge, green while charging; click for what it's doing and time left) and the date and time (click for notifications and the calendar). Items get a soft highlight under the pointer, the colours follow the Windows theme, menus take the keyboard only while they're open, and pinned apps make room when space beside the notch runs out. (Settings → Menu bar.)
- Windows wasn't designed round a notch, so with the top given back (or a window dragged up there) there's often an app under the island, a browser's tabs say. Then it steps aside: clicks go through to the app (you can pick or close the tab under it), the island fades as you point past it, and it opens only when the pointer rests on it for a moment, never after a click on what's underneath. Over the desktop it opens on hover straight away. Files dragged onto it still land on the shelf. (Settings → Step aside for apps.)
- Spring animations between **idle**, **compact** and **expanded**; hover to expand. It comes up as a dot that springs out to the pill. What it shows glides in as the pill opens, each view with a small entrance of its own (artwork popping in, rows cascading, a battery filling up, a ringing bell swinging); switching tabs slides the content over and the tab highlight springs across. A glint crosses the glass as the island opens or a notice arrives (which springs the pill out with a little bounce), the glass catches the light where you point, buttons grow a touch under the pointer and sink when pressed, and the artwork slowly swells while music plays. A faint shadow keeps the pill clear of light wallpapers. With Windows' "Animation effects" off (Accessibility → Visual effects), things just appear and nothing loops.
- Clicks pass straight through everywhere except the pill, and clicking it never steals focus.
- Now playing for any app that publishes media info (Spotify, Amazon Music, YouTube in Chrome/Edge, Media Player…): artwork, title, progress, play/pause/next/previous.
- Glass look: a solid tint (a hint of see-through is available in Settings) with a top highlight and bright rim. While music plays, the glass, level bars and progress bar take their colours from the artwork.
- Artwork comes from the playing app; when the app doesn't provide any (Amazon Music doesn't), it's looked up by artist + title in the iTunes catalogue (your country's store, then the US store) and then MusicBrainz / Cover Art Archive. Only results whose artist *and* title match are used; if nothing matches, the island gets a colour generated from the song instead. The lookup sends the artist and title to Apple and MusicBrainz, and can be switched off in Settings.
- Some apps publish odd metadata. Amazon Music never shares artwork or track length, and sometimes reports one value as both title and artist for a whole song — sometimes it's the artist ("Sayfalse"), sometimes the song ("Pooja & Patola"). The island asks the catalogue which it is (when online lookups are allowed); if it's an artist, or it can't tell, it shows "Now playing" rather than passing the artist off as the song.
- Volume and brightness changes show briefly as a level bar, keeping the music's colours if something's playing. Switching speakers (headphones connecting) doesn't pop up the volume; the Bluetooth notice covers that. Brightness works on laptop screens; desktop monitors don't report it. Windows' own volume pop-up still appears too.
- Battery: "Charging" when you plug in, "Fully charged" when it gets to 100%, and warnings at 20% and 10% (once each per discharge). Unplugging is silent; PCs without a battery never show it.
- Bluetooth: paired devices connecting ("OnePlus Nord Buds 3 · 80%", with the battery when the device reports it) and disconnecting, with an icon for headphones, speakers, phones, mice and so on. Short drops and quick reconnects are ignored; mice and keyboards (which nap when idle) only announce reconnecting after a long absence and never announce disconnecting.
- Microphone and camera: while any app uses them, a dot sits in a small bubble beside the island (orange for the mic, green for the camera), and the island stays visible even with "hide when idle". When an app starts using one, the island says which ("Discord · Microphone"); point at the bubble to see it again. Read from Windows' capability consent store, the same place its own privacy indicator looks.
- Tabs: the expanded island has a small tab row: music (or the clock), the clipboard and the file shelf. Every hover starts on the first tab; dragging files onto the island opens the shelf.
- Clipboard history: the last 20 things you copied (text, links, images, files), newest first; click one to copy it again, × to forget it. Kept in memory only (gone when Glyde quits), and anything password managers mark as private is skipped, using the same markers as Windows' own clipboard history.
- File shelf: drag files or folders onto the island to park them; drag them out later into any folder, email or chat (always as a copy, the originals never move), click to open, × to take off the shelf. The shelf only remembers where files are, in `shelf.json` in the data folder (see below), so it survives restarts. Things that aren't files yet (an image dragged out of a browser) are saved into a `Shelf` folder there.
- Notifications (installed version only, see below): other apps' notifications appear as a two-line banner for a few seconds; hover it to read the whole message, with Open and Clear. Quiet while Windows' Do Not Disturb is on. Windows' own banner still shows too (turn it off per app in Windows Settings if you only want the island).
- **Glyde**, the voice assistant: press **Ctrl+Alt+S** (S for Glyde; another shortcut can be picked in Settings) or the mic at the end of the expanded island's tab row, and speak. The pill shows a live waveform while it listens, with your words under it as you say them, and stops by itself when you pause (or press the shortcut again, or tap the pill). What you said stays up while Glyde works out the answer, and above the answer (up to two lines). While Glyde listens, thinks or talks, a fine edge of colour in its orb's colours runs round the island, brightening a little with whoever's speaking (your voice, then Glyde's as it answers); the waveform rolls out from the middle while the microphone starts, your words fade in as you say them and shimmer while it thinks, the orb ripples, and the answer flows in a few words at a time. Things that went wrong ("I didn't catch that") give a little shake. It understands:
  - music: "pause", "play", "next song", "previous", "skip this track"
  - volume: "volume 30", "turn it up", "quieter", "mute", "unmute"
  - brightness: "brightness 50", "brighter", "make the screen dimmer"
  - apps: "open Chrome", "open VS Code", "open settings" (anything in the Start menu; a few sites like YouTube and Gmail open in the browser if there's no app)
  - timers, alarms and reminders: "timer 10 minutes", "alarm at 7 am", "wake me up at 6:30 tomorrow", "remind me at 5 to call Mom", "remind me in 20 minutes to check the oven", "how much time is left", "what are my reminders", "cancel the alarm"; while one rings, "stop" or "snooze". A running timer counts down in the pill, everything set is in a Timers tab (× cancels), and when one is due the island shows it with Stop/Snooze, plays a sound (a reminder is also read out) and rings for up to a minute. They're saved in `timers.json` in the data folder; anything due while Glyde wasn't running rings, marked missed, when it starts.
  - "what time is it", "what's the date", "lock my PC", "what can you do"

  With **Listen for "Hey Glyde"** on (Settings, off by default) you can also just say "Hey Glyde", pause, then the request, or say it all in one go: "Hey Glyde, next song". The microphone then stays open (Windows shows its mic icon in the taskbar; the island's own mic dot shows only while Glyde listens to a request). A loudness check finds where speech starts after a pause, and only the first two seconds of it are checked for the name, by one quick Whisper pass (~0.2 s on the graphics card) that's told to expect "Glyde" and must hear "hey/hi/okay Glyde". "Hey Siri", "zero" and the like don't match, and noise comes out as captions ("static", "music"), but some voices' "Hey Sarah" can; Glyde then just listens and times out. Nothing else is kept, logged or sent. Windows' echo cancellation removes the PC's own sound from the microphone, so music, videos and Glyde's replies don't wake it, and "Hey Glyde, pause" works over loud music. (It also means you can't test it by playing "Hey Glyde" through the PC's speakers.) A Bluetooth headset's microphone is never held open for this (it would switch the headset to muffled phone-call audio); the built-in one is used instead, or none.

  It answers on the island and out loud, except for music commands, where you'd hear it over the song. Anything else goes to **Glyde's brain**, with your own API key (pasted in Settings, where it's kept in Windows' credential locker): **Claude Haiku** (Anthropic's fastest model: paid, a fraction of a cent a question, answering in a second or two every time) or **Google Gemini** (a Flash model: free, no card, from Google AI Studio, but its free tier is often busy). Claude can't hear audio, so with a Gemini key saved too, Gemini still hears your words (when Deepgram doesn't) and your accent for it. Either handles questions ("what's the capital of Peru?"), Hindi or Hinglish requests, and:
  - **emails**: "write a mail to Rahul saying the meeting moved to 4": the draft shows on the island (to, subject, the start of it) and Glyde asks "Shall I open it in Gmail?". Say yes (or haan), no, or a change ("make it shorter", "add that I'll bring snacks"), or use the buttons. Yes opens Gmail's compose window filled in; you press Send.
  - **WhatsApp**: "WhatsApp Mom that I'll be late": the same, opening WhatsApp with the message typed (WhatsApp asks which chat, unless you gave a number).
  - **copied text**: "translate this to Hindi", "summarise this", "make this formal", "reply to this": the copied text is sent along (only when you say "this", "copied" or "selected"), and the result goes straight back on the clipboard (what you copied is still in the clipboard tab).

  **Glyde's voice**: with your own **Azure AI Speech** key (Settings → Natural voice; the free F0 tier covers half a million characters a month, and the Azure sign-up asks for a card to check it's you), it answers in Microsoft's natural voices, in the accent you speak with: an Indian-English voice when you speak with an Indian accent, an American one for an American accent, a British one for British (or always one of them, chosen in Settings), female or male, and a Hindi voice for answers in Hindi. To tell your accent, Gemini hears the first few seconds of each spoken request (so those go to Google even when Deepgram hears the words); the reply waits a moment for it, and keeps the last accent heard. Only what Glyde says goes to Microsoft. Without Azure, or when it can't be reached, Windows' own voice answers, in your accent when Windows has a voice for it (the English (India) speech pack has two).

  **Deepgram** (optional, Settings): with your own Deepgram API key (new accounts get free credit, no card), what you say after the shortcut or "Hey Glyde" streams to Deepgram's Nova-3 model while you say it. Your words show under the waveform as you speak, and Glyde understands you as soon as you stop, with no wait for Gemini or this PC to hear it. With "English and Hindi" chosen it uses Nova-3's mixed-language model. The key is kept in Windows' credential locker. If Deepgram can't be reached (or the key or credit runs out), Gemini or this PC hears the request instead, and the live words come from the small model on this PC again.

  Glyde never sends anything itself. Only these requests go to the brain's maker (Anthropic or Google); on Gemini's free tier Google may use them to improve its products. If the model name Glyde uses stops working, it picks Google's current Flash model by itself. Speech is turned into text on this PC by OpenAI's Whisper ("small" model, via whisper.cpp), on the graphics card through Vulkan when there is one, else the CPU. It picks between English and Hindi by itself (left to choose from every language, Whisper takes short English requests for Spanish or even Sinhala). The model (465 MB) is downloaded once from Hugging Face the first time you press the shortcut, into `Models` in the data folder; nothing you say leaves the PC. The model stays loaded for 10 minutes after use, then is unloaded to free memory. Your words are shown as you speak by quick passes of the small model (about half a second each on the graphics card, every half second or so); that needs the small model (it comes with "Hey Glyde") and a graphics card. They're only for show: what Glyde does is decided from the full transcription afterwards (Gemini's, or the big model's), as before. While Glyde listens the mic dot shows as usual, but without the "who's using the mic" notice.
- Hides itself while a fullscreen app or game is in front.
- Tray icon: click for Settings, right-click to hide the island or quit. Launching Glyde again while it's running also opens Settings.

## Settings

Changes apply immediately and are saved to `settings.json` in the data folder:

- plain build: `%LOCALAPPDATA%\Glyde`
- installed (packaged) build: `%LOCALAPPDATA%\Packages\Glyde_…\LocalState`. Windows quietly redirects a packaged app's writes elsewhere in AppData, so it uses the folder Windows gives the package; `build-package.ps1` copies the plain build's settings and shelf there the first time.

| Setting | What it does |
|---|---|
| Start with Windows | Adds Glyde to your sign-in apps (per-user Run key) |
| Display | Which screen the island sits on |
| Distance from top | Gap above the island; 0 hugs the top edge |
| Mac-style notch | The island as a black notch hanging from the top edge, always there, in a frosted bar as tall as it; windows start below the bar (on by default; distance and "hide when idle" then don't apply) |
| Menu bar | With the notch, a Mac's menu bar beside it: Glyde's menu, the app you're in and Go; pinned apps, Glyde, Wi-Fi, sound, battery and the time (on by default) |
| Hide when idle | Fades the island out when nothing is playing; point at the top of the screen to bring it back |
| Step aside for apps | With an app under the island, clicks go through to it and resting the pointer opens the island |
| Animation speed | Relaxed, Normal or Snappy |
| Glass transparency | A hint of what's behind showing through (0–10%; 0, the default, is solid) |
| Match the music's colours | Tint the glass from the album art while music plays |
| Volume and brightness | Briefly show level changes on the island |
| Battery and charging | Show plugging in, full charge and low-battery warnings |
| Bluetooth devices | Show devices connecting and disconnecting |
| Microphone and camera in use | Dot beside the island while an app uses them, and which app |
| Clipboard history | The clipboard tab; off also forgets everything in it |
| File shelf | The shelf tab, and dropping files on the island |
| Notifications | Other apps' notifications on the island (installed version only) |
| Glyde voice assistant | The shortcut and the island's mic button |
| Keyboard shortcut | Glyde's shortcut: Ctrl+Alt+S, Ctrl+Alt+Space, Ctrl+Shift+Space or Alt+Shift+Space; warns if another app already owns it |
| Glyde's brain | On/off; the model (Claude Haiku or Gemini Flash); your Anthropic and Gemini keys (Save, Test, which times the answer), links to get them |
| Hear requests with Deepgram | On/off, your Deepgram API key (Save, Test), link to get one |
| Natural voice (Microsoft Azure) | On/off, your Azure Speech key and region (Save, Test plays a sample), link to create a Speech resource |
| Accent and voice | Match mine (the accent you speak with), Indian, American or British English; female or male |
| Listen for "Hey Glyde" | Wake phrase instead of (or as well as) the shortcut; keeps the microphone open |
| Find missing album art online | iTunes / MusicBrainz lookup for apps that don't share artwork (sends artist + title to them) |

## Run it

Requires the .NET 10 SDK.

```bash
dotnet build src/WinIsland/WinIsland.csproj -p:Platform=x64
```

Then run `src/WinIsland/bin/x64/Debug/net10.0-windows10.0.26100.0/Glyde.exe`, or open `WinIsland.slnx` in Visual Studio and press F5.

### Installed (packaged) version

Windows only shares notifications with apps installed as a package (MSIX). With **Developer Mode** on (Settings → System → For developers), this builds the `Packaged` configuration and installs it for you, unsigned, straight from the build folder:

```bash
./build-package.ps1
```

Then start Glyde from the Start menu; the first time, Windows asks whether it may read your notifications. Run the script again after changes to update it (if the manifest changed, it reinstalls, keeping the app's data). The installed version keeps its own settings, shelf and log; the first install copies the plain build's across and moves "Start with Windows" to the package's startup task (shown in Task Manager → Startup apps). To uninstall: `Get-AppxPackage Glyde | Remove-AppxPackage`.

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
| `--demo-notification` | Keep a made-up message up |
| `--demo-assistant` | Play Glyde's looks over and over (listening, thinking, answering, a slip-up) with made-up voices; no microphone |
| `--tab=N` | With `--pin=expanded`, open tab N (0 = first) |

Errors are logged to `log.txt` in the data folder (see Settings above).

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
    NotificationActivity.cs   notification banner and expanded message (+ NotificationView, NotificationExpandedView)
    TimerActivity.cs          timer countdown, ringing view, Timers tab (+ TimerView, TimersView)
    AssistantActivity.cs      Glyde: listening, thinking, answering (+ AssistantView with the waveform)
  Motion.cs                   how things move: easing curves, durations (the speed setting), entrances, loops, shimmer
  Aurora.cs                   the edge light round the island while Glyde is busy, and the pill's shadow
  GlassLight.cs               light on the glass: the spot that follows the pointer, the glint as it opens
  Press.cs                    island buttons that grow under the pointer and sink when pressed
  GlydeFace.xaml(.cs)         Glyde's face, alive: blinking, glancing, its looks (listening, thinking…), talking
  MenuBarWindow.xaml(.cs)     the Mac-style menu bar beside the notch: menus, pinned apps, Wi-Fi, sound, battery, time
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
  Services/NotificationMonitor.cs  other apps' notifications (UserNotificationListener; packaged only)
  Services/Assistant.cs          the assistant's name, in one place
  Services/HotkeyListener.cs     Glyde's global keyboard shortcut
  Services/MicrophoneRecorder.cs microphone capture (AudioGraph) into a rolling 20 s buffer, read back as 16 kHz mono
  Services/MicrophonePicker.cs   which microphone to use (never a Bluetooth headset's when there's another)
  Services/SpeechDetector.cs     speech vs. background noise, by loudness
  Services/WakeWordDetector.cs   finds the start of each thing said and has it checked for "Hey Glyde"
  Services/SpeechToText.cs       Whisper: model download, loading (Vulkan GPU, CPU fallback), transcription (English or Hindi), wake-phrase check
  Services/VoiceCommands.cs      understands the spoken commands and carries them out
  Services/SystemActions.cs      volume, brightness, media keys, lock
  Services/AppCatalog.cs         finds installed apps by spoken name and opens them
  Services/Speaker.cs            spoken replies: Azure's natural voice in the reply's accent, else Windows' voice
  Services/AzureVoice.cs         Azure AI Speech text to speech: the voice for each accent, the request, its errors
  Services/Brain.cs              the AI brain's interface and results (answer, email/WhatsApp draft, clipboard text)
  Services/GeminiBrain.cs        Google Gemini over REST: tools for the drafts, plain-English errors, model fallback
  Services/ClaudeBrain.cs        Claude Haiku through Anthropic's SDK: the same tools, lookups answered in the same conversation
  Services/Drafts.cs             opens confirmed drafts in Gmail / WhatsApp; clipboard in and out
  Services/SecretStore.cs        API keys in Windows' credential locker
  Services/TimerService.cs       timers, alarms and reminders: saved, ticking, ringing when due
  Services/SpokenTime.cs         "ten minutes", "5 30 p m", "tomorrow at 7" → durations and times
  Package.appxmanifest        the packaged build's manifest (notification and microphone access, startup task)
  AppPaths.cs                 data folder, and whether this is the installed (packaged) build
build-package.ps1             builds and installs the packaged version (Developer Mode)
tools/make-logo.py            draws the logo (Assets/Glyde.svg, the app icon and the tiles); run it after changing the face
  Services/WindowsBelow.cs    what's under the island: an app's window, or the desktop
  Services/TopStrip.cs        the bar the notch hangs in (an app bar), so windows start below it; wears the app's colour
  Services/AppColors.cs       the colour along the top of the app you're in, read off the screen
  Services/ForegroundApp.cs   the app you're in (for the menu bar and the bar's colour), and hiding, maximizing or closing it
  Services/PinnedApps.cs      the taskbar's pinned apps and their icons
  Services/FullscreenDetector.cs
  Interop/Native.cs           Win32 / DWM calls
  Interop/CoreAudio.cs        Core Audio COM interfaces
  Interop/Displays.cs         monitor enumeration
  Interop/MessageWindow.cs    hidden window for Win32 messages (clipboard, hotkeys, the app bar)
  TrayIconController.cs       notification-area icon
```
