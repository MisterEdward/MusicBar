# Taskbar Music 🎵

A tiny Windows 11 floating music indicator — a Fluent-style "pill" that shows what's
playing (album art · **Title • Artist** · transport buttons), lets you **scroll to
change system volume while hovering** (even when it isn't the focused window), can be
**dragged onto any monitor**, and **scales smoothly** ("liquid glass") when the track
changes. When nothing is playing it **collapses to a small dot** and, every so often, a
little **cat slides in from off-screen** to do something silly. 🐱

Inspired by the look of FluentFlyout, built from scratch in **C# / WPF (.NET 8)**.

> ⚠️ **Windows only.** WPF apps can only be *built and run on Windows*. This project was
> authored on macOS, so it hasn't been compiled here — build it on your Windows 11 box
> (see below). If anything doesn't compile or behave, tell me and I'll iterate.

---

## Features

| Requirement | How it's done |
|---|---|
| Hover + **scroll → system volume** | System-wide low-level mouse hook (`WH_MOUSE_LL`) so it works even when the widget isn't focused / on another monitor. A hairline volume bar + % flashes on the pill. |
| Works **without the window active** | The window is `WS_EX_NOACTIVATE` (never steals focus) and the scroll hook is global. |
| **Taskbar-locked, drag onto any monitor** | Drag the pill left/right; its vertical centre stays pinned to the taskbar centre (any taskbar height, any monitor — computed from monitor bounds minus work area). Horizontal position is remembered. |
| **Living background** | The pill's background is an audio-reactive halftone dot-field + sparkles (à la the Gemini prompt box) — palette-tinted, brightest in the centre, faded at the corners, pulsing with the music. Not bars. Real system audio via WASAPI loopback + FFT (drives brightness/size), with a gentle idle shimmer if capture is unavailable. |
| **Smooth scaling** on track change | The pill width animates with a slight overshoot (`BackEase`) and re-centres each frame, plus a tiny "settle" scale pop. |
| **Colour from album art + random** | A vibrant dominant colour is extracted from the cover and paired with a fresh random accent hue each track; that palette drives the dot-field + corner glow. |
| **Any media, Apple Music prioritised** | Uses Windows' System Media Transport Controls (SMTC). Session scoring prefers Apple Music / iTunes > other known players > whatever is actually playing. |
| **Fluent / native look** | Rounded translucent "glass card", Segoe UI Variable text, Segoe Fluent Icons transport glyphs, light/dark following the Windows app theme. |
| Album art + **◀ ⏯ ▶** on the right, **Title • Artist** centred | See `MainWindow.xaml`. |
| Idle → **dot + cat** | Collapses to a dot; a vector cat (no copyright baggage) occasionally slides in to sleep/play. Toggleable. |

---

## Build & run (on Windows 11)

Prerequisites: **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`). WPF is included
with the SDK on Windows — no Visual Studio required (though it works fine too).

```bash
cd src
dotnet run
```

Or build a self-contained single .exe you can drop anywhere:

```bash
cd src
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The .exe lands in `src/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/TaskbarMusic.exe`.
(Use `-r win-arm64` on ARM devices.)

---

## Using it

- **Change volume:** hover the pill and scroll the wheel. No click/focus needed.
- **Move it:** click-drag the pill (anywhere except the buttons) to any monitor.
- **Transport:** ◀ previous · ⏯ play/pause · ▶ next (right side).
- **Right-click the pill** for the menu: **Start with Windows** (toggle), toggle the idle
  **cat**, **reset position**, **exit**. (There's no taskbar button or tray icon by design —
  right-click → Exit is how you quit.)

### Start with Windows

Right-click the pill → **Start with Windows**. This writes a per-user `Run` registry entry
(no admin needed) and self-heals the path if you move the .exe. Toggle it off the same way.

## Prebuilt .exe

A ready-to-run, self-contained `dist/TaskbarMusic.exe` (~75 MB, no .NET install required) is
included — copy it to your Windows 11 PC and double-click. If you'd rather have a tiny (~2 MB)
build, install the **.NET 8 Desktop Runtime** and publish framework-dependent
(`--self-contained false`).

---

## Project layout

```
src/
├─ App.xaml / .cs            App shell, Fluent styles, icon-button template
├─ MainWindow.xaml / .cs     The pill: layout, width animation, scroll-volume, drag, theme
├─ CatWindow.xaml / .cs      The click-through idle cat overlay + its animations
├─ Services/
│  ├─ MediaService.cs        SMTC wrapper (session picking, art, transport)
│  ├─ VolumeService.cs       Core Audio master volume (NAudio)
│  └─ SettingsService.cs     JSON settings in %AppData%\TaskbarMusic
└─ Interop/
   ├─ NativeMethods.cs       P/Invoke surface
   ├─ WindowChromeHelper.cs  no-activate / tool-window / rounded / (optional) acrylic
   ├─ GlobalScrollHook.cs    system-wide wheel hook for hover-volume
   └─ ThemeWatcher.cs        light/dark from the registry
```

---

## Notes & knobs

- **Prioritising a specific player:** edit `PreferredApps` in `Services/MediaService.cs`
  (match on the app's `SourceAppUserModelId`, e.g. add `"spotify"` to bump Spotify).
- **Volume step:** `VolumeStep` in settings (default 2% per notch).
- **Real acrylic blur:** there's an `EnableAcrylic(...)` helper wired up but not called by
  default — with a shadow margin it draws a rectangular halo around the rounded pill. See
  the comment in `MainWindow.OnSourceInitialized` for how to switch to true blur if you want it.
- **Swap the cat for a GIF:** drop a `MediaElement` over the `Canvas` in `CatWindow.xaml`
  and drive it from `PlayBehaviorAsync`. The vector cat is intentionally asset-free.

## Known limitations (untested build — flag anything and I'll fix)

- Building requires Windows; can't be compiled from macOS.
- SMTC needs Windows 10 1809+ (Windows 11 recommended).
- True acrylic is opt-in (see above).
