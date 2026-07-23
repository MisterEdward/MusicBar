# Taskbar Music 🎵

A tiny Windows music indicator: a Fluent-style pill that shows what's playing
(album art · **Title • Artist** · transport buttons), changes volume by scrolling
while hovered, docks to the taskbar on any monitor, and hides for real fullscreen
apps. When playback ends it collapses into a compact motion-branding mark; after
30 seconds, a geometric vector cat can make one short visit. 🐱

Inspired by the look of FluentFlyout, built from scratch in **C# / WPF (.NET 8)**.

> ⚠️ **Runs on Windows only — but it cross-builds anywhere.** The app itself needs
> Windows 11 (WPF + SMTC + WASAPI). Compiling it, though, does *not*: the whole thing was
> written and built on a Mac. `dotnet` ships the Windows Desktop reference packs, so
> `-p:EnableWindowsTargeting=true` cross-compiles — and even *publishes* the self-contained
> `win-x64` .exe — from macOS or Linux. See [Cross-building](#cross-building-from-macos--linux).

---

## Features

| Requirement | How it's done |
|---|---|
| Hover + **scroll → system volume** | System-wide low-level mouse hook (`WH_MOUSE_LL`) so it works even when the widget isn't focused / on another monitor. A hairline volume bar + % flashes on the pill. |
| Works **without the window active** | The window is `WS_EX_NOACTIVATE` (never steals focus) and the scroll hook is global. |
| **Taskbar-locked, drag onto any monitor** | Detects primary/secondary taskbar windows, every edge, auto-hide, display changes, and mixed-DPI origins. Drag follows the taskbar's long axis: horizontal on Top/Bottom, vertical on Left/Right. |
| **Living background** | The pill's background is an audio-reactive halftone dot-field + sparkles (à la the Gemini prompt box) — palette-tinted, brightest in the centre, faded at the corners, pulsing with the music. Not bars. Real system audio via WASAPI loopback + FFT (drives brightness/size), with a gentle idle shimmer if capture is unavailable. |
| **Smooth scaling** on track change | The pill width animates with a slight overshoot while preserving its nearest screen-side anchor. |
| **Colour from album art + random** | A vibrant dominant colour is extracted from the cover and paired with a fresh random accent hue each track; that palette drives the dot-field + corner glow. |
| **Any media, Apple Music prioritised** | Uses Windows' System Media Transport Controls (SMTC). Session scoring prefers Apple Music / iTunes > other known players > whatever is actually playing. |
| **Fluent / native look** | Rounded translucent surface, Segoe UI Variable text, Segoe Fluent Icons, and live light/dark theme updates. |
| Album art + **◀ ⏯ ▶** on the right, **Title • Artist** centred | See `MainWindow.xaml`. |
| Idle → **motion mark + cat** | Reference-driven, fast shape morphs replace the old rotating cartoon forms. The redesigned vector cat appears once per play→stop transition. Toggleable. |
| **Crash/race hardening** | Versioned SMTC updates, synchronized WASAPI buffers, atomic settings writes, complete event cleanup, and app-wide crash logs. |

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

### Cross-building from macOS / Linux

You do **not** need a Windows machine to compile this — only to run it. The .NET SDK ships
the Windows Desktop reference packs, so add `-p:EnableWindowsTargeting=true`:

```bash
cd src && dotnet build -p:EnableWindowsTargeting=true
```

That includes the XAML markup compiler, so it's a genuine full build, not just a syntax
check. You can even produce the shipping binary from a Mac:

```bash
cd src && dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -p:EnableCompressionInSingleFile=true
```

This project was in fact written and built entirely on macOS; only testing happened on
Windows 11.

---

## Using it

- **Change volume:** hover the pill and scroll the wheel. No click/focus needed.
- **Move it:** click-drag the pill (anywhere except the buttons) to any monitor.
- **Transport:** ◀ previous · ⏯ play/pause · ▶ next (right side).
- **Right-click the pill** for the menu: **Start with Windows** (toggle), toggle the idle
  **cat**, lock/reset its position, or exit.
- The **tray icon** exposes the same safety menu. Left- and right-click both work.

### Start with Windows

Right-click the pill → **Start with Windows**. This writes a per-user `Run` registry entry
(no admin needed) and self-heals the path if you move the .exe. Toggle it off the same way.

## Prebuilt executables

Self-contained x64 and ARM64 executables are published under
[GitHub Releases](https://github.com/MisterEdward/MusicBar/releases). They are
not committed to the repository. Framework-dependent builds are much smaller,
but require the .NET 8 Desktop Runtime.

---

## Project layout

```
src/
├─ App.xaml / .cs            App shell, Fluent styles, icon-button template
├─ MainWindow.xaml / .cs     The pill: layout, width animation, scroll-volume, drag, theme
├─ CatWindow.xaml / .cs      The click-through idle cat overlay + its animations
├─ Services/
│  ├─ MediaService.cs        SMTC wrapper (session picking, art, transport)
│  ├─ AudioVisualizer.cs     synchronized WASAPI capture + FFT
│  ├─ VolumeService.cs       Core Audio master volume (NAudio)
│  ├─ SettingsService.cs     validated atomic JSON settings
│  └─ TrayIconService.cs     tray icon and public-API menu ownership
└─ Interop/
   ├─ NativeMethods.cs       P/Invoke surface
   ├─ ScreenGeometry.cs      pure/tested taskbar, DPI and hit-test maths
   ├─ TaskbarHelper.cs       taskbar window and edge discovery
   ├─ GlobalScrollHook.cs    system-wide wheel hook for hover-volume
   ├─ FullscreenDetector.cs  fullscreen filtering and geometry
   └─ ThemeWatcher.cs        live light/dark updates
tests/
└─ TaskbarMusic.Tests/       cross-platform unit tests for pure logic
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

## Known limitations

- **Running** requires Windows (building doesn't — see [Cross-building](#cross-building-from-macos--linux)).
- SMTC needs Windows 10 1809+ (Windows 11 recommended).
- True acrylic is opt-in (see above).
- Exclusive-fullscreen (not borderless) apps render above every topmost window, so the
  pill is invisible there regardless of the auto-hide logic.
- The app is unsigned, so SmartScreen can warn on a fresh download.
- Windows-only runtime behavior still requires a real Windows smoke test; CI
  verifies compilation, XAML, pure logic, and publishing.

## Tests

```bash
dotnet test tests/TaskbarMusic.Tests/TaskbarMusic.Tests.csproj
dotnet build src/TaskbarMusicPlayer.csproj -c Release -p:EnableWindowsTargeting=true
```

GitHub Actions runs tests and builds on both Ubuntu and Windows. Detailed
`v1.1.0` changes and verification evidence are in [`Sol.md`](Sol.md).

## License

[MIT](LICENSE)
