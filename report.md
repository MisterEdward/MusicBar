# MusicBar — Engineering Report

> **Historical document:** this report describes the `v1.0.0` implementation.
> The completed `v1.1.0` remediation, tests, visual redesign, and verification
> record are documented in [`Sol.md`](Sol.md).

**Purpose:** this document is written for an independent AI reviewer (Fable 5) to audit the
work. It covers what was built, how, every bug encountered and its root cause, what is
*verified* vs *unverified*, and a prioritised backlog.

**Repo:** https://github.com/MisterEdward/MusicBar · **Release:** `v1.0.0`
**Stack:** C# / WPF, .NET 8 (`net8.0-windows10.0.19041.0`), NAudio 2.2.1, WinForms (tray only)
**Size:** ~2,940 lines of source (2,614 C# · 265 XAML · 57 csproj/manifest) across 25 files

---

## 0. Review guidance — read this first

### Critical context about how this was built
The entire project was written and compiled **on macOS**, cross-building with
`dotnet build -p:EnableWindowsTargeting=true`. This is a genuine full build (XAML markup
compiler included) and even produces the shipping self-contained `win-x64` .exe.

**But it was never executed by the author.** WPF cannot run on macOS. Every runtime
behaviour claim in this document was verified either (a) by the human user manually testing
on Windows 11 and reporting back, or (b) not at all. **Assume nothing is runtime-verified
unless §4 says so.** This is the single biggest risk in the codebase and the main reason
this review exists.

### Where I would focus a review
1. **Threading in `Services/AudioVisualizer.cs`** — deliberately lock-free shared state
   written from NAudio's capture thread and read from the UI thread. Justified as
   "cosmetic, tearing is invisible", but `_pos` is non-atomic and indexes a fixed array.
   See §3.19 — I hardened it, but the design is still racy by construction.
2. **`Interop/FullscreenDetector.cs` heuristic** — the maximised-vs-fullscreen distinction
   went through three wrong versions (§3.15–§3.17). The current `WS_CAPTION` test is the
   best of them but is still a heuristic. Adversarial edge cases welcome.
3. **Reflection into a private WinForms API** (`TrayIconService.ShowContextMenuMethod`) —
   works, has a fallback, but is inherently fragile. §3.18.
4. **Re-entrancy discipline in `MainWindow.xaml.cs`** — `_dragging` / `_adjusting` /
   `_placed` guards gate handlers that write `Left`/`Top`/`Height`. One missing guard
   already caused a hard crash (§3.12). I believe every cycle is broken; please try to
   prove otherwise.
5. **Mixed-DPI multi-monitor maths** — `TransformFromDevice.M11/M22` is taken from the
   *window's current* source and applied to *other* monitors' rects. Approximate by design.

### Known blind spots
- **Zero automated tests.** No unit tests, no CI. Nothing prevents regressions.
- No profiling of the per-frame renderer under real load / high refresh rates.
- No verification on: multiple monitors with *different* DPI, vertical/left/right taskbars,
  auto-hide taskbar, Windows 10, ARM64.

---

## 1. What it is

A floating, always-on-top "pill" that docks into the Windows 11 taskbar and shows what's
playing, with an audio-reactive background. No window, no Alt-Tab entry, no taskbar button —
it presents as part of the shell.

```
┌──────────────────────────────────────────────┐
│  [art]  Title • Artist            ◀  ⏯  ▶   │   ← translucent, dot-field background
└──────────────────────────────────────────────┘
                  ↑ vertically centred on the taskbar
```

### Architecture

```
App.xaml.cs              Startup + app-wide crash logging/guards
MainWindow.xaml(.cs)     The pill: layout, placement, drag, anchoring, render loop,
                         fullscreen auto-hide, menus  (~745 lines — the god object)
CatWindow.xaml(.cs)      Separate click-through overlay for the idle cat
Controls/
  DotFieldVisualizer.cs  Audio-reactive halftone dots + sparkles + splash blobs
  MorphIndicator.cs      Idle shape-morphing indicator
Services/
  MediaService.cs        SMTC session tracking, metadata, album art, transport
  AudioVisualizer.cs     WASAPI loopback capture + FFT → frequency bands + level
  VolumeService.cs       Core Audio master volume (NAudio)
  ColorHelper.cs         Dominant-colour extraction from album art, HSV utils
  SettingsService.cs     JSON settings in %AppData%\TaskbarMusic
  StartupService.cs      HKCU Run key (start with Windows)
  TrayIconService.cs     System-tray icon + menu (WinForms NotifyIcon)
Interop/
  NativeMethods.cs       All P/Invoke
  WindowChromeHelper.cs  No-activate/tool-window styles, topmost, DWM attributes
  TaskbarHelper.cs       Taskbar strip geometry per monitor
  FullscreenDetector.cs  "Is a real fullscreen app on our monitor?"
  GlobalScrollHook.cs    WH_MOUSE_LL hook for hover-scroll volume
  ThemeWatcher.cs        Light/dark from registry
```

**`MainWindow.xaml.cs` is a ~745-line god object.** It owns placement, drag, anchoring,
clipping, the render loop, idle state, fullscreen hiding, theming and both menus. This is
the clearest structural weakness and a fair thing to flag in review.

---

## 2. Feature inventory

### 2.1 Media (`Services/MediaService.cs`)
Uses `GlobalSystemMediaTransportControlsSessionManager` (SMTC), so it reads **any** player
that reports to Windows.

- **Session scoring** rather than "current session": Apple Music/iTunes `+1000`, other known
  players `+100`, actually-playing `+50`; highest wins, falls back to `GetCurrentSession()`.
- Metadata: title, artist (falls back to album artist), play state, next/prev availability.
- **Album art**: `IRandomAccessStreamReference` → `DataReader` → bytes → `BitmapImage`,
  `Freeze()`d so it can cross threads.
- WinRT events arrive on thread-pool threads; updates are marshalled to the UI dispatcher.
- Transport: `TrySkipPrevious/TryTogglePlayPause/TrySkipNext`.

### 2.2 Volume by hover-scroll (`Interop/GlobalScrollHook.cs`, `Services/VolumeService.cs`)
A system-wide `WH_MOUSE_LL` hook. On `WM_MOUSEWHEEL`, if the cursor is inside the pill's
window rect, it adjusts master volume via NAudio's `AudioEndpointVolume` and **swallows the
event** so the app underneath doesn't scroll. Works with no focus and on any monitor —
which is the whole point, since the pill never activates. Scrolling up unmutes. A hairline
bar + percentage flashes on the pill.

### 2.3 Window behaviour (`Interop/WindowChromeHelper.cs`)
- `WS_EX_NOACTIVATE` (never steals focus) + `WS_EX_TOOLWINDOW` (hidden from Alt-Tab).
- `Topmost` **plus** a 2-second `SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE)` reassert —
  WPF's `Topmost` alone lost z-order after the extended styles were changed (§3.10).
- DWM: `DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND` and
  `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE` to kill the 1px system border (§3.9). Rounding is
  done by our own clip instead.
- `AllowsTransparency`, `SizeToContent`, background `#01000000` (effectively invisible but
  still hit-testable for dragging).

### 2.4 Taskbar docking (`Interop/TaskbarHelper.cs`)
The taskbar strip is derived as **monitor bounds minus work area**, so it works on any edge,
on secondary monitors, and falls back to a synthetic bottom strip when the taskbar
auto-hides. The pill's height becomes `clamp(taskbarHeight − 8, 34, 60)` and its vertical
centre is pinned to the taskbar's centre — so it visually belongs to the bar rather than
floating above it. Album art size and corner radius are derived from that height.

### 2.5 Positioning, drag, anchoring
- **Manual horizontal-only drag.** `GetCursorPos` deltas → `Window.Left`; `Y` is owned
  exclusively by the taskbar snap. Deliberately *not* `DragMove()` (§3.13).
- **Monitor-seam resistance.** The pill stays fully inside its monitor until the cursor
  overshoots by 85 DIP, then slides across with no jump (offset by the threshold).
- **Culling.** `RootGrid.Clip` is set to the intersection of the window with the monitor
  under the pill's centre, so it can never render half on one screen and half on another.
  Because WPF `Clip` also gates hit-testing, the culled part doesn't eat clicks either.
- **Side anchoring.** The anchor edge (left/right) is chosen by which half of the *monitor*
  the pill sits on. Growing/shrinking keeps that edge fixed, so it expands toward screen
  centre and the idle circle collapses to the side it was parked on.
- **Bounds.** `ClampLeft` keeps it inside the virtual screen; a saved position is rejected on
  startup if it no longer lands on a real monitor (`MONITOR_DEFAULTTONULL`).

### 2.6 Visualiser (`Controls/DotFieldVisualizer.cs`, `Services/AudioVisualizer.cs`)
- **Capture**: `WasapiLoopbackCapture` → Hann window → 1024-point FFT (`NAudio.Dsp`) → 24
  log-spaced bands, fast attack / slow decay, plus an overall `Level`.
- **Render**: a halftone dot grid whose brightness and radius ride `Level`, with a
  smoothstep edge feather so dots dissolve toward the pill's border instead of being cut;
  twinkling diamond sparkles; and **random colour splash blobs** (soft radial gradients that
  fade in/out at random positions, colours from a curated palette).
- **Gating**: blobs are multiplied by the audio level, so with no system audio they vanish
  entirely and the pill returns to its quiet baseline shimmer. This was an explicit product
  requirement — "when nothing plays, keep it as it is".
- **Perf**: frozen-brush cache keyed on quantised ARGB; a frozen `RadialGradientBrush` per
  palette colour with per-blob `PushOpacity`. Render loop capped at ~50 fps.

### 2.7 Colour extraction (`Services/ColorHelper.cs`)
Album art is downscaled to 32×32, and each pixel is weighted by
`saturation × (1 − |luminance − 0.55|)` — favouring vivid, mid-bright pixels and ignoring
greys/blacks/whites. The weighted average is saturation-boosted. That becomes accent #1;
accent #2 is a random vivid hue regenerated per track. The pair drives the dot gradient.

### 2.8 Idle state
When no session exists, the pill collapses to a circle showing `MorphIndicator`: a shape
that morphs circle → triangle → square → pentagon → hexagon → star (point-set radius
interpolation with smoothstep easing, slow rotation, soft glow) while drifting through a
**curated** palette. The palette is hand-picked after the user explicitly rejected
"eggplant purple / hot pink" — random hue generation was replaced with a vetted list.

### 2.9 Idle cat (`CatWindow.xaml.cs`)
A separate click-through (`WS_EX_TRANSPARENT`), never-activating overlay window with a
hand-drawn vector cat (no external assets, no licensing questions). It slides in from
off-screen **once**, ~30 seconds after playback stops, does a sleep or play routine, then
leaves and does not return until the next play→stop transition.

### 2.10 Fullscreen auto-hide (`Interop/FullscreenDetector.cs`)
Polls every 600 ms. A foreground window counts as fullscreen when it: isn't the desktop or a
known shell class; isn't owned by `explorer.exe`; **has no `WS_CAPTION`**; is on the same
monitor as the pill; and its rect covers that monitor. Requires two consecutive positive
polls (~1.2 s) before acting, to squash transients. Hiding animates a scale-down + fade
("liquid glass") and then genuinely `Hide()`s so nothing is blocked.

### 2.11 Tray, startup, settings
- **Tray** (`TrayIconService.cs`): runtime-drawn gradient icon (no asset file). Menu: Start
  with Windows · Lock position · Idle cat · Reset position · Exit. Kept in sync with the
  pill's right-click menu.
- **Startup**: `HKCU\…\Run`, no admin needed, and it rewrites its own path on launch so
  moving the .exe doesn't break autostart.
- **Settings**: JSON at `%AppData%\TaskbarMusic\settings.json` (position, volume step, cat
  on/off, lock).

### 2.12 Crash resilience (`App.xaml.cs`)
`DispatcherUnhandledException` (logs, then `Handled = true` so one bad frame can't kill the
widget), `AppDomain.UnhandledException` (log-only — cannot prevent termination) and
`TaskScheduler.UnobservedTaskException`. Full inner-exception and `AggregateException`
chains are written to `%AppData%\TaskbarMusic\crash.log`, capped at 2 MB with tail trimming.
Background-thread entry points additionally log locally, because those can never reach the
dispatcher handler.

---

## 3. Bug log

Ordered roughly chronologically. Each entry: symptom → root cause → fix. The interesting
ones for review are **3.11, 3.12, 3.15–3.18**.

### 3.1 XAML: mismatched closing tag
`MC3000` — a `Setter` was closed with `</Style>`. Caught by the very first cross-build.
*Lesson: the cross-build was worth setting up immediately; it caught real errors from minute one.*

### 3.2 Missing `using System.IO`
`MemoryStream` unresolved — WPF projects don't get `System.IO` from implicit usings the way
assumed.

### 3.3 Play/pause glyphs silently erased
The Segoe Fluent Icons glyphs ``/`` are invisible characters; string-replace
edits kept dropping them. Fixed by writing explicit `\uXXXX` escapes via a script rather
than literal characters.

### 3.4 Width animation jumped on every track change
`DoubleAnimation(from, to)` used `Pill.Width` as `from`, but with `FillBehavior.HoldEnd` the
*base* property value never updates — it stayed at the XAML value while the display showed
the animated one. So each new animation snapped back to 220 first. **Fix:** use the
`to`-only constructor, which starts from the current *effective* (animated) value and chains
smoothly.

### 3.5 Acrylic halo
Real blur-behind (`SetWindowCompositionAttribute` acrylic) is drawn as a **rectangle** on an
`AllowsTransparency` window, haloing around the rounded pill and its shadow margin.
**Decision:** ship a tuned translucent surface instead and leave acrylic documented as
opt-in. Correctness over a nicer-in-theory effect that couldn't be verified.

### 3.6 Album art had square corners
An `<Image>` inside a `Border` with `CornerRadius` is **not** clipped — `Border` only rounds
its own background/border rendering. **Fix:** make the art the Border's `Background` via an
`ImageBrush`, which *is* clipped to the corner radius.

### 3.7 …then the album art became a circle
The concentric-radius formula `h/2 − 6` produced a radius ≥ half the art size on a small
pill. **Fix:** a modest radius (~26 % of art size) — a rounded square, as originally specified.

### 3.8 "Double border"
The translucent pill fill and the dot-field's glow read as two concentric edges. **Fix:**
make the pill background effectively fully transparent so the visualiser alone defines the
shape.

### 3.9 A visible outer border that wasn't ours
After removing every WPF border, a 1px rounded outline remained. It was **DWM's own border**,
added because we opted into `DWMWA_WINDOW_CORNER_PREFERENCE = ROUND`. **Fix:** `DONOTROUND`
+ `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`; our own clip handles rounding.

### 3.10 Not staying always-on-top
`Topmost="True"` was lost after `SetWindowLong` changed the extended styles. **Fix:**
explicit `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` at startup and on a 2 s timer.

### 3.11 ★ Hard crash when dragging the pill — the worst bug
**Symptom:** grabbing the pill and moving it crashed the app instantly. Survived *two* full
rewrites of the drag mechanism, which is what made it so confusing.

**Root cause:** the gate-check that runs *before* any drag logic:
```csharp
if (IsWithinButton(e.OriginalSource as DependencyObject)) return;   // don't drag on buttons
…
d = VisualTreeHelper.GetParent(d);
```
Clicking the **title/artist text** sets `OriginalSource` to a `Run`. A `Run` is a
`FrameworkContentElement`, **not a `Visual`** — and `VisualTreeHelper.GetParent` throws
`InvalidOperationException: 'Run' object is not a Visual or Visual3D` on it. With no
unhandled-exception handler, that was a hard process kill, deterministically, on the most
natural place to grab the widget.

It survived the rewrites because it sits *upstream* of all drag mechanics — every rewrite
changed code that never got to run.

**Fix (second attempt).** The first fix — switching to `LogicalTreeHelper` — stopped the
crash but **broke the transport buttons**: the logical tree can't walk from a Button's
templated internals back up to the Button, so button clicks were misread as drags. The
correct fix walks **both** trees:
```csharp
if (d is Visual or Visual3D) return VisualTreeHelper.GetParent(d);  // reaches templated Buttons
if (d is FrameworkContentElement fce) return fce.Parent;            // Run → TextBlock, no throw
return LogicalTreeHelper.GetParent(d);
```
*Lesson: the first fix traded one bug for another because it was applied without asking what
else that code path served.*

### 3.12 ★ Layout re-entrancy (the crash's accomplice)
`OnPillResized` is a `SizeChanged` handler — i.e. it runs *inside* the layout pass. It was
calling `SnapToTaskbar()` → `ApplyPillHeight()` → which sets `Height`, sets `CornerRadius`,
calls `Measure()` and starts an animation. Mutating layout from inside layout normally just
wastes work, but under a `DragMove` message storm it stops converging and trips WPF's
layout-recursion limit. **Fix:** `OnPillResized` is now the *only* horizontal authority and
never calls `SnapToTaskbar`; height/position live solely in `SnapToTaskbar`, which is called
at discrete moments (placement, drag-end, unhide) — never per animation frame. This also
removed a per-frame `PointToScreen` + monitor P/Invoke cost.

### 3.13 Cursor stutter while dragging
`DragMove()` runs a modal OS move loop that fires `LocationChanged` continuously; the
handler kept yanking `Top` back to the taskbar centre, fighting the OS. **Fix:** manual drag
— absolute `GetCursorPos` deltas applied to `Left` only, with `_dragging` guards so no other
handler writes position mid-drag.

### 3.14 DPI factor captured once per drag
Crossing into a monitor with different scaling mid-drag used a stale px→DIP factor. **Fix:**
recompute per mouse-move.

### 3.15 Fullscreen detection: maximised windows counted as fullscreen
On a monitor with **no taskbar or an auto-hiding one**, the work area equals the monitor
bounds, so any maximised window covers it and was flagged fullscreen — the pill vanished
whenever a taskbar app was clicked. **Fix (v1):** exclude `SW_SHOWMAXIMIZED`.

### 3.16 …which then broke *real* fullscreen detection
Many apps entering fullscreen **keep** a maximised window state (e.g. a video going
fullscreen from an already-maximised browser). The v1 exclusion silently swallowed exactly
the case the feature exists for — the pill stayed visible over fullscreen video.
**Fix (v2):** stop using `showCmd`. Judge by **window style**: a normal or maximised window
keeps `WS_CAPTION` (a title bar); a real fullscreen app removes it. This separates the two
cases correctly *and* preserves the §3.15 fix.
*Lesson: two symmetric bugs, three iterations. The first two fixes were heuristics chosen
without a model of what actually distinguishes the states.*

### 3.17 Task View flagged as fullscreen
Clicking Task View (or Win+Tab) opens a full-monitor overlay that isn't maximised and stays
foreground until dismissed — so the pill hid and only came back after "one more click".
**Fix:** exclude known shell classes *and*, defensively, **any window owned by
`explorer.exe`** — shell chrome is never a fullscreen app, and this survives Microsoft
renaming window classes between releases.

### 3.18 Tray menu ate the next click
Left-clicking the tray icon called `ContextMenuStrip.Show(Cursor.Position)` directly.
WinForms' own right-click path calls a private `NotifyIcon.ShowContextMenu()` that does
`SetForegroundWindow` first — which is what lets the popup dismiss on the very next click
anywhere. Without it the menu is orphaned and swallows one click. **Fix:** invoke that
private method via reflection, with the manual `Show` retained as a fallback.
*Flagged for review: reflection against a private BCL API is fragile.*

### 3.19 Background-thread crash risks (found in an audit, pre-emptively fixed)
These could never be caught by a UI-thread handler — they'd terminate the process silently:
- **`AudioVisualizer.OnData`** ran on NAudio's capture thread with **no try/catch**, and
  `Start()`/`Stop()` churn could let two overlapping callbacks drive the non-atomic `_pos`
  past the FFT buffer → `IndexOutOfRangeException` on a background thread. Added a
  stale-callback identity check (`ReferenceEquals(sender, _capture)`) and a try/catch+log.
- **`MediaService.Reevaluate`** executed WinRT calls on a thread-pool callback with no
  guard; a session dying mid-enumeration (closing Spotify, a browser tab) throws
  `COMException`. Wrapped; also skips posting to a shutting-down dispatcher.
- **`GetPillScreenRectDip`** called `PointToScreen` *before* null-checking the
  `PresentationSource`. It's captured as a delegate by the cat window and invoked seconds
  later — potentially after the window is gone. Reordered.
- **`ExitApp`** didn't stop the cat or all timers before `Shutdown()`.
- **`GlobalScrollHook.HookProc`** let managed exceptions cross a native callback boundary.
  Wrapped.

### 3.20 WinForms/WPF namespace collision
Enabling `UseWindowsForms` (for the tray) added implicit global usings for
`System.Windows.Forms` and `System.Drawing`, making `Color`, `Brush` and `Application`
ambiguous across the whole project. **Fix:** `<Using Remove="System.Windows.Forms" />` and
`<Using Remove="System.Drawing" />`; the tray file imports them explicitly.

### 3.21 Monitor-crossing resistance didn't resist
The first implementation stuck the pill's **centre** on the monitor seam. But a point on the
seam resolves to the *neighbouring* monitor, so the "current monitor" flipped immediately and
the resistance cancelled itself. **Fix:** keep the pill **fully inside** the monitor while in
the dead zone, so its centre never approaches the seam and monitor detection stays stable.

### 3.22 The cat was annoying
It reappeared every 28–75 s forever. Changed to a single ~30 s visit per play→stop
transition.

### 3.23 README stated something false
It claimed the project "can only be built and run on Windows" and listed "can't be compiled
from macOS" as a limitation — while every build in the project's history was done on macOS.
The confusion was *building* vs *running*. Corrected in `ce7ad4b`, with a documented
cross-build section. **Flagged deliberately: this was a factual error that survived the whole
project because nobody re-read the docs against reality.**

---

## 4. Verification status

| Area | Cross-build | Reviewed | Runtime-tested (by user, on Win11) |
|---|---|---|---|
| Build / publish / single-file | ✅ | ✅ | ✅ |
| Media info, album art, palette | ✅ | ✅ | ✅ |
| Transport buttons | ✅ | ✅ | ✅ (broke once, §3.11, then fixed) |
| Hover-scroll volume | ✅ | ✅ | ✅ |
| Drag, resistance, culling | ✅ | ✅ | ✅ (crashed twice before fix) |
| Taskbar sizing/centring | ✅ | ✅ | ✅ |
| Visualiser + blobs | ✅ | ✅ | ✅ |
| Fullscreen auto-hide | ✅ | ✅ | ✅ (after three iterations) |
| Tray menu | ✅ | ✅ | ✅ |
| Start with Windows | ✅ | ✅ | ⚠️ not explicitly confirmed |
| Lock position | ✅ | ✅ | ⚠️ not explicitly confirmed |
| Idle morph indicator / cat | ✅ | ✅ | ⚠️ partially |
| Crash logging | ✅ | ✅ | ❌ never triggered on purpose |
| Light theme | ✅ | ✅ | ❌ never seen |
| Mixed-DPI monitors | ✅ | ⚠️ | ❌ |
| Vertical / top taskbar | ✅ | ⚠️ | ❌ |
| Windows 10, ARM64 | ✅ | — | ❌ |

**No automated test exists for any row in this table.**

---

## 5. Backlog

### 5.1 Repo hygiene (do first — cheap, high value)
- **`LICENSE`** — the repo currently has none, which legally means "all rights reserved".
  MIT is the obvious pick for something meant to be shared.
- **Screenshot / GIF in the README** — the single highest-impact change for a public repo.
- **CI**: a GitHub Actions workflow building on `windows-latest` *and* cross-building on
  `ubuntu-latest` (proving §3.23's claim), attaching the .exe to tagged releases.

### 5.2 Correctness / robustness
- **Split `MainWindow.xaml.cs`** (~745 lines) into a placement/anchoring service, a drag
  controller, and a view-model. It's the main obstacle to testing anything.
- **Unit tests** for the pure logic that is currently untestable only because it's tangled
  with the window: anchoring maths, `ClampLeft`, resistance, `TaskbarHelper` geometry,
  `ColorHelper` extraction, `FullscreenDetector` decisions (with an injectable window
  provider).
- Replace the reflection in `TrayIconService` (§3.18) with a proper hidden owner window +
  explicit `SetForegroundWindow`.
- Make `AudioVisualizer` explicitly double-buffered instead of lock-free-by-assumption.
- Handle `WM_DISPLAYCHANGE` / `WM_DPICHANGED` explicitly rather than relying on polling.
- Honour vertical (left/right) taskbars in the layout, not just geometry.

### 5.3 Features
- **Seek bar + elapsed/total time**, with scrub support where SMTC allows it.
- **Click the album art → focus the source app.**
- **Per-app volume** (audio session volume) instead of master, so scrolling only affects the
  music.
- **Global hotkeys** for play/pause/next/prev.
- **Settings UI** — everything is a hand-edited JSON file today.
- **Lyrics** line under the title.
- **Visualiser customisation**: style presets, sensitivity, "lock palette" (ignore art).
- **Per-monitor remembered positions**.
- **Auto-update** from GitHub Releases.
- **ARM64 build**, and a framework-dependent build (~2 MB vs 75 MB).
- **Code signing** — the unsigned .exe triggers SmartScreen on every fresh download.
- **Localisation** (RO/EN).
- **Accessibility**: the pill is invisible to screen readers and has no keyboard path at all.

### 5.4 Quality of life
- Tray item to open `crash.log` / the settings folder.
- "Reset everything" (delete settings).
- Optional: hide when the pill's monitor is idle/locked; fade on mouse-over so you can see
  the taskbar underneath; snap to taskbar-icon alignment.
- Reduce the 75 MB download (framework-dependent or trimmed non-WPF shell).

---

## 6. Honest assessment

**What went well.** Setting up the macOS cross-build immediately meant every change was
compiler-verified from the first minute — it caught real errors (§3.1, §3.2, §3.20) that
would otherwise have surfaced as broken downloads. Delegating the hardest bug to independent
review found a root cause (§3.11) that two of my own rewrites had walked straight past.
Pre-emptively auditing background threads (§3.19) closed crashes that no UI handler could
have caught.

**What went badly.**
- **Three iterations on fullscreen detection** (§3.15–§3.17) because I reached for
  heuristics (`showCmd`) before modelling what actually separates "maximised" from
  "fullscreen". The window-style test should have been the first idea, not the third.
- **A fix that caused a new bug** (§3.11): swapping the tree walker without checking what
  else depended on it broke the transport buttons.
- **Shipping a factual error in the docs** (§3.23) and repeating it for the entire project,
  contradicted by my own build commands every single day.
- **Not writing a single test**, which is why every regression was found by the human user
  running the app rather than by me.

**Biggest risk today:** no tests + a god object + runtime behaviour that the author has
never observed. It works, but it is held together by careful reading rather than by
verification.

---

*Written for review. If a claim here can't be traced to code, treat it as unverified.*
