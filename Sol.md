# MusicBar v1.1.1 — registrul complet al remedierii

**Data:** 24 iulie 2026

**Bază:** `v1.0.0` / commit `5f27cb0`
**Scop:** eliminarea bug-urilor cunoscute din audit, întărirea zonelor concurente,
introducerea testelor și refacerea vizuală a pisicii și indicatorului idle.

Acest document descrie toate schimbările făcute până la `v1.1.1`. Raportul
`report.md` rămâne documentul istoric pentru `v1.0.0`.

## 0. Incidentul de packaging v1.1.0

Release-ul `v1.1.0` a fost retras imediat ca prerelease, iar `v1.0.0` a redevenit
Latest. Executabilele atașate erau publicate cu `PublishSingleFile=true`, dar
fără `IncludeNativeLibrariesForSelfExtract=true`. SDK-ul lăsa separat cinci
DLL-uri native WPF (`wpfgfx_cor3`, `PresentationNative_cor3`, `D3DCompiler_47_cor3`,
`PenImc_cor3` și `vcruntime140_cor3`), iar release-ul conținea doar EXE-ul.
Aplicația se oprea înainte de `App.Main`, deci nici `crash.log` nu putea fi scris.

În `v1.1.1`, librăriile native sunt incluse în bundle și extrase automat de
host-ul .NET. CI copiază numai EXE-ul într-un director izolat, îl pornește pe
Windows, așteaptă 12 secunde și cere atât proces viu, cât și o fereastră vizibilă.
Astfel testul reproduce exact forma livrată prin GitHub Releases.

Prima rulare a acestui test corect a expus încă un crash real: WPF trimitea
`LocationChanged` în timpul `Show()`, înainte ca visual tree-ul să fie conectat
la un `PresentationSource`, iar `UpdateAnchor` apela `PointToScreen`. Acum
`OnWindowMoved` ignoră mesajele până la finalizarea plasării, iar
`UpdateAnchor` folosește dreptunghiul fizic al HWND-ului prin `GetWindowRect`.
Nu mai depinde de un Visual conectat în traseul de startup.

Proba nu folosește `Process.MainWindowHandle`: acea proprietate poate ignora
ferestrele WPF de tip tool window și a produs un fals negativ. În schimb, CI
enumeră HWND-urile vizibile prin `EnumWindows`, le filtrează după PID și eșuează
dacă aplicația loghează orice excepție în primele 12 secunde.

## 1. Rezumatul rezultatului

- Update-urile media nu mai pot afișa o piesă veche peste una nouă.
- Paleta aleatorie rămâne stabilă pe durata aceleiași piese.
- Captura audio nu mai partajează nesincronizat bufferul FFT cu UI-ul.
- Scroll-ul global nu mai reacționează când pill-ul este ascuns și nu mai
  interceptează colțurile transparente sau porțiunea tăiată la marginea unui monitor.
- Taskbar-ul este detectat pe toate cele patru muchii, inclusiv pe monitoare
  secundare și în mod auto-hide.
- Conversiile mixed-DPI sunt raportate la HWND-ul curent, inclusiv pentru origini negative.
- Schimbările de display, DPI și taskbar provoacă repoziționare controlată.
- Detectorul fullscreen ignoră ferestre invizibile, minimizate și DWM-cloaked.
- Meniul tray nu mai folosește reflection într-o metodă privată WinForms.
- Pisica idle are state machine cu anulare și apare o singură dată după play→idle.
- Tema light/dark se actualizează în timpul rulării.
- Setările sunt validate și scrise atomic.
- Wrapper-ele Core Audio sunt eliberate după fiecare operație de volum.
- Shutdown-ul curăță idempotent timer-ele, hook-urile, evenimentele și serviciile.
- Indicatorul idle și pisica au fost redesenate în limbajul vizual al referințelor primite.
- Există 26 de teste automate și CI pe Ubuntu și Windows.
- Build-ul Release este curat: zero warning-uri și zero erori.
- Pachetul single-file izolat pornește efectiv pe runner-ul Windows.

## 2. Media și concurență SMTC

### Problema

Evenimentele WinRT puteau porni mai multe citiri asincrone. O citire veche putea
termina după cea nouă și suprascrie UI-ul. Accesul la sesiunea urmărită și
abonările la evenimente nu erau serializate. Accentul aleator era recreat la
pause/play, deși documentația îl descria ca fiind per piesă.

### Soluția

- `Reevaluate` serializează schimbarea sesiunii printr-un lock dedicat.
- Fiecare update primește o versiune monotonă.
- `LatestVersionGate` respinge rezultatele vechi înainte de citirea costisitoare,
  după citire și încă o dată în coada Dispatcher.
- Un `SemaphoreSlim` serializează citirile metadata/artwork.
- Comenzile previous/play/next sunt serializate separat și tolerează dispariția sesiunii.
- Handler-ele managerului și sesiunii au acum metode nominale și sunt dezabonate la `Dispose`.
- Cheia paletei este un `record struct` cu source app, titlu, artist și album.
- Cache-ul paletelor este limitat la 64 de piese.
- `DataReader` este eliberat explicit.
- Evenimentul public `Changed` este golit la dispose.

## 3. Captură audio și FFT

### Problema

NAudio scria în aceleași array-uri pe care UI-ul le putea citi. `Start`, `Stop`
și callback-uri vechi puteau modifica simultan `_pos`, FFT-ul și benzile.

### Soluția

- Lifecycle-ul capturii este protejat de un lock.
- Procesarea audio are un lock separat.
- Callback-urile verifică identitatea capturii înainte și după așteptarea lock-ului.
- Benzile sunt calculate într-un buffer de lucru și publicate prin schimb atomic.
- Cititorii primesc un snapshot clonat sub lock.
- `Level` și `Active` sunt publicate prin operații volatile.
- `Stop` așteaptă ieșirea callback-ului înainte de resetarea FFT-ului.
- `RecordingStopped` actualizează starea și loghează excepția NAudio.
- O invalidare neașteptată golește imediat nivelul și benzile.
- Captura încearcă reconectarea de până la cinci ori, cu backoff, atât timp cât
  playback-ul nu a intrat între timp în idle.
- Toate evenimentele NAudio sunt dezabonate înainte de dispose.

## 4. Taskbar, DPI, monitoare și drag

### Problema

Geometria veche deducea taskbar-ul doar din work area. Auto-hide presupunea
mereu muchia de jos. Coordonatele absolute ale altui monitor erau înmulțite cu
scala ferestrei curente. Acest lucru este incorect pentru mixed-DPI și origini negative.

### Soluția

- Sunt enumerate ferestrele `Shell_TrayWnd` și `Shell_SecondaryTrayWnd`.
- Pentru auto-hide, `ABM_GETAUTOHIDEBAREX` cere direct Shell-ului HWND-ul
  asociat fiecărei perechi monitor–muchie; un taskbar parcat în monitorul vecin
  nu mai este atribuit prin `MonitorFromWindow`.
- HWND-ul returnat de AppBar este acceptat numai dacă are clasa taskbar-ului
  Windows; dock-urile auto-hide terțe sunt ignorate.
- Muchia reală este derivată din dreptunghiul ferestrei shell.
- Work area rămâne fallback.
- Grosimea auto-hide folosește DPI-ul ferestrei taskbar și gestionează activation sliver-ul.
- `TaskbarGeometry` acoperă explicit Left, Top, Right și Bottom.
- Taskbar-urile orizontale păstrează poziția trasă.
- Pentru taskbar vertical, pill-ul rămâne orizontal, este ancorat la muchia
  taskbar-ului și păstrează poziția aleasă de utilizator pe axa verticală.
- Drag-ul schimbă automat axa: X pe taskbar Top/Bottom și Y pe Left/Right.
- Clamp-ul vertical folosește imediat înălțimea cerută, nu `ActualHeight` din
  layout-ul precedent.
- Conversia pixel→DIP este relativă la originea fizică și originea WPF a HWND-ului.
- Limitele virtual desktop folosesc metricile Win32 în pixeli, apoi aceeași
  conversie relativă; nu mai combină direct `SystemParameters` cu DPI-ul curent.
- Drag-ul folosește delte incrementale; schimbarea scalei la trecerea între monitoare
  nu mai recalculează întreaga distanță de la început.
- Rezistența la seam și clipping-ul folosesc aceeași geometrie relativă.
- Poziția salvată este validată prin dreptunghiul fizic real al HWND-ului.
- `WM_DPICHANGED`, `WM_DISPLAYCHANGE` și `WM_SETTINGCHANGE` declanșează resnap.
- La scoaterea unui monitor, pill-ul revine într-o poziție validă.
- Hwnd hook-ul este eliminat la închiderea ferestrei.

## 5. Scroll global și volum

### Problema

`GetWindowRect` continuă să întoarcă ultimul dreptunghi al unei ferestre ascunse.
Hook-ul putea modifica volumul și înghiți scroll-ul în locul unde fusese pill-ul.
Hitbox-ul era dreptunghiular și ignora clipping-ul și colțurile transparente.
`MMDevice` nu era eliberat după operațiile de volum.

### Soluția

- Hook-ul are un switch `IsEnabled`, oprit imediat la fullscreen hide.
- Sunt verificate `IsVisible`, `WindowState`, `Opacity` și suprafața vizibilă.
- Hit-test-ul urmărește transformarea reală a pill-ului.
- Cursorul trebuie să fie pe monitorul care conține partea vizibilă.
- Hit-test-ul refuză input-ul până când suprafața este conectată la un
  `PresentationSource`; un wheel event sosit în timpul `Show()` nu mai poate
  provoca un `PointToScreen` invalid.
- Colțurile stadium sunt testate prin `RoundedRectHitTest`.
- Fiecare `MMDevice` este eliberat prin `using`.
- Dispariția endpoint-ului audio este tratată fără excepție în UI.

## 6. Fullscreen

- Ferestrele invizibile, minimizate și DWM-cloaked sunt excluse.
- Ferestrele shell și procesele Explorer rămân excluse.
- Monitorul foreground trebuie să fie același cu monitorul pill-ului.
- Acoperirea monitorului este calculată în logica pură `FullscreenGeometry`.
- `WS_CAPTION` rămâne filtrul final pentru diferența dintre maximized și borderless fullscreen.
- Scroll-ul și pisica sunt oprite când pill-ul începe să dispară.

## 7. Tray icon

### Problema

Click-ul stâng invoca prin reflection metoda privată
`NotifyIcon.ShowContextMenu`. Fallback-ul nu acoperea cazul în care reflection-ul
exista, dar invocarea eșua. Handle-ul creat de `Bitmap.GetHicon` nu era eliberat.

### Soluția

- Reflection-ul a fost eliminat complet.
- Un `NativeWindow` top-level ascuns, marcat `WS_EX_TOOLWINDOW`, este owner pentru meniu.
- Fluxul este `SetForegroundWindow` → `ContextMenuStrip.Show` → `WM_NULL`.
- Meniul se poate închide la primul click exterior.
- HICON-ul temporar este distrus după clonarea iconului managed.
- Iconul, meniul și owner window sunt eliberate idempotent.

## 8. Pisica idle

### Comportament

- Nu mai apare la pornirea aplicației.
- Nu apare când opțiunea este activată în timp ce aplicația este deja idle.
- Nu este rearmată după fullscreen.
- Fereastra transparentă a pisicii este totuși reafișată după fullscreen, astfel
  încât următoarea tranziție validă să nu ruleze invizibil.
- Este armată numai la tranziția activ→idle.
- Așteaptă 30 de secunde.
- Face o singură vizită de aproximativ 30 de secunde.
- Revenirea playback-ului anulează așteptarea, intrarea, comportamentul sau ieșirea.
- O tranziție play→idle rapidă, sosită în timp ce vizita veche iese, păstrează
  exact un replacement pending.
- Pisica alege automat partea pill-ului care are loc pe monitorul țintă și își
  inversează direcția de intrare/ieșire.
- Poziția verticală este mutată sub pill când nu există loc deasupra.
- Pill-ul și monitorul sunt transmise către HWND-ul pisicii exclusiv în pixeli
  fizici; plasarea folosește `SetWindowPos`, apoi DPI-ul monitorului țintă.
- Pisica este limitată la monitorul pill-ului, inclusiv la origini negative și
  configurații mixed-DPI.

### Vizual

- Ilustrația cartoon cu outline, mustăți și litere `Zzz` a fost eliminată.
- Pisica este acum o marcă vectorială geometrică, fără asset extern.
- Paleta folosește deep teal, cobalt, coral, saffron, teal și burgundy.
- Silueta folosește module organice suprapuse, o singură linie optică și particule abstracte.
- Respirația, coada și hop-ul au fost păstrate, dar aplicate noii geometrii.
- Coada folosește un singur pivot explicit; pivotul normalizat dublu a fost eliminat.

## 9. Indicatorul idle și morph

### Referințe analizate

- Imagine statică: grilă 4×4 cu forme geometrice de brand.
- Video: H.264, 1600×1200, 29.97 fps, durată 5.005 secunde.
- Din video a fost măsurat un hold de aproximativ o secundă și un morph ferm de
  aproximativ 300–450 ms.

### Schimbări

- Rotația continuă și ciclul lent circle→polygon→star au fost eliminate.
- Timeline-ul folosește hold `1.05 s` și morph `0.38 s`.
- Easing-ul este `smootherstep`, fără overshoot cartoon.
- Tranziția are doar o comprimare de 4.5%, inspirată din video.
- Formele noi sunt: triple-lobe, vertical pill, four-leaf, rounded diamond,
  rounded triangle, rounded square, circle și soft drop.
- Toate formele au 96 de puncte aliniate la punctul superior, inclusiv diamantul
  rotit, deci interpolarea nu răsucește conturul.
- Rounded triangle folosește colțuri quadratic Bézier, nu un polygon ascuțit.
- Culorile sunt preluate din limbajul referinței.
- Glow-ul vechi a fost înlocuit cu o umbră discretă de un pixel.
- Brush-urile de tranziție sunt cuantizate și ținute într-un cache.
- Timing-ul a fost extras în `MorphTimeline`, independent de WPF și testabil pe macOS/Linux.

## 10. Temă, setări și lifecycle

### Temă

- `ThemeWatcher` este acum instanță disposable.
- Ascultă `SystemEvents.UserPreferenceChanged`.
- Update-ul este trimis pe Dispatcher.
- Paleta dark poate fi reaplicată după light; nu mai depinde doar de valorile inițiale XAML.
- Shutdown-ul Dispatcher este verificat înainte de enqueue, inclusiv cursa dintre check și enqueue.

### Setări

- Sunt acceptate explicit valorile floating-point sentinel folosite pentru poziție.
- Coordonatele sunt validate ca finite/NaN și limitate ca magnitudine.
- `VolumeStep` este validat între `0.001` și `0.25`, altfel revine la `0.02`.
- Scrierea folosește un fișier temporar unic, `WriteThrough`, flush pe disc și
  înlocuire în același director.
- Fișierele temporare incomplete sunt șterse best-effort.
- Erorile de load/save sunt logate.

### Lifecycle

- `MainWindow.Cleanup` este idempotent.
- Este chemat atât de Exit, cât și de `OnClosed`.
- Oprește timer-ele, render loop-ul, pisica, tray-ul, hook-ul, media, audio și volumul.
- Evenimentul media este dezabonat.
- `App.OnExit` eliberează watcher-ul și handler-ele globale.
- O excepție în timpul startup-ului nu mai este marcată `Handled`; procesul
  eșuează vizibil și CI/logul pot raporta corect problema.

## 11. Teste automate

Proiect nou: `tests/TaskbarMusic.Tests`, target `net8.0`, rulabil pe macOS,
Linux și Windows.

**26 teste:**

- toate cele patru muchii taskbar din work area;
- toate cele patru muchii auto-hide din fereastra shell off-screen;
- fallback DPI pentru activation sliver;
- muchia auto-hide cunoscută la granița dintre două monitoare;
- lipsa unui taskbar signal;
- acoperirea fullscreen cu toleranță;
- hit-test pentru colțurile rotunjite;
- conversii DPI relative cu origini pozitive și negative;
- plasarea fizică a pisicii pe monitor cu origine negativă;
- fallback-ul pisicii sub pill la marginea superioară;
- invalidarea versiunilor SMTC vechi;
- 1.000 de versiuni concurente unice cu un singur winner;
- identitate media fără coliziuni între câmpuri;
- hold-ul și mijlocul morph-ului;
- avansarea și wrap-ul timeline-ului;
- sanitizarea timpului invalid.

## 12. Verificare efectuată

### Automat

```text
dotnet test ... -c Release --warnaserror
26 passed, 0 failed, 0 skipped

dotnet build ... -c Release -warnaserror
0 warnings, 0 errors

dotnet format ... --verify-no-changes
passed

git diff --check
passed

NuGet audit (direct + transitive)
no vulnerability warnings
```

### Publish

Au fost produse două executabile self-contained, single-file și comprimate:

| Artifact | Arhitectură verificată | SHA-256 |
|---|---|---|
| `TaskbarMusic-win-x64.exe` | PE32+ x86-64 GUI | `47d18fc64615b3f21b7577241b9304d20f030e4d31af581eebdd5e4b6885a296` |
| `TaskbarMusic-win-arm64.exe` | PE32+ AArch64 GUI | `003d385fe3f3855cb771b618636f43ef7d8b351fa27e03e7f5a4d0008bb543df` |

În executabil au fost confirmate:

- assembly manifest `1.1.1.0`;
- metadata aplicație `1.1.1`;
- DPI awareness `PerMonitorV2`.
- folderul de publish nu mai conține DLL-uri native obligatorii lângă EXE;
  singurul companion rămas este PDB-ul opțional.

### Verificare vizuală

- Pisica a fost transpusă într-un SVG temporar la rezoluția reală și randată cu librsvg.
- Formele țintă și stările intermediare de morph au fost randate într-un contact sheet temporar.
- Ambele randări au fost inspectate vizual.
- Fișierele temporare nu sunt incluse în repo.

WPF nu poate fi executat pe macOS. Build-ul XAML și designul vectorial au fost
verificate local, iar pornirea pachetului izolat este verificată de CI pe Windows.
Aspectul și interacțiunea pe un taskbar desktop real rămân de confirmat manual.

## 13. CI și release engineering

- Workflow nou `.github/workflows/ci.yml`.
- `actions/checkout@v7` și `actions/setup-dotnet@v6` folosesc runtime-ul Node
  curent și nu mai emit avertismentul deprecării Node 20.
- Test și build pe `ubuntu-latest` și `windows-latest`.
- Formatting gate pe Ubuntu.
- Publish smoke self-contained pentru `win-x64` și `win-arm64`.
- Startup smoke pe Windows pornește o copie izolată a EXE-ului timp de 12 secunde
  și verifică procesul, `crash.log` gol și cel puțin un HWND top-level vizibil
  enumerat pentru PID-ul aplicației.
- Toate gate-urile folosesc warnings-as-errors.
- Native WPF sunt incluse prin `IncludeNativeLibrariesForSelfExtract=true`.
- Versiunea proiectului este `1.1.1`, iar manifestul este `1.1.1.0`.
- Licență MIT adăugată.
- Executabilele sunt publicate prin GitHub Releases, nu în Git.

## 14. Registru complet pe fișiere

| Fișier | Schimbare |
|---|---|
| `.github/workflows/ci.yml` | CI cross-platform, format și publish smoke. |
| `LICENSE` | Licență MIT. |
| `README.md` | Funcții și limitări actualizate; tray, release, teste și structură corecte. |
| `report.md` | Marcat explicit drept raport istoric v1.0.0 și trimite la acest document. |
| `Sol.md` | Registrul complet v1.1.1, inclusiv incidentul de packaging. |
| `src/App.xaml.cs` | Theme watcher lifecycle și cleanup handler-e globale. |
| `src/CatWindow.xaml` | Redesign vectorial complet al pisicii. |
| `src/CatWindow.xaml.cs` | State machine, cancellation, pending re-arm, timing 30 s și plasare fizică mixed-DPI. |
| `src/Controls/MorphIndicator.cs` | Forme, paletă și morph complet refăcute după referințe. |
| `src/Controls/MorphTimeline.cs` | Timeline pur și testabil pentru hold/morph/wrap. |
| `src/Interop/FullscreenDetector.cs` | Filtre visible/minimized/cloaked și geometrie pură. |
| `src/Interop/GlobalScrollHook.cs` | Disable la hide, hit-test vizibil, monitor și colțuri. |
| `src/Interop/NativeMethods.cs` | API-uri display, DPI, AppBar, taskbar enumeration, visibility și DWM cloak. |
| `src/Interop/ScreenGeometry.cs` | Geometrie pură taskbar/fullscreen/DPI/hit-test și plasarea pisicii. |
| `src/Interop/TaskbarHelper.cs` | Detectarea ferestrelor taskbar și auto-hide per monitor/muchie prin Shell AppBar. |
| `src/Interop/ThemeWatcher.cs` | Watcher live, resurse light/dark și shutdown safety. |
| `src/MainWindow.xaml.cs` | Integrarea tuturor corecțiilor, cat gating, display hooks și cleanup. |
| `src/Services/AudioVisualizer.cs` | Lifecycle și double-buffer sincronizat. |
| `src/Services/LatestVersionGate.cs` | Versiuni SMTC și cheia structurată a piesei. |
| `src/Services/MediaService.cs` | Pipeline serializat/versionat, cache paletă și dispose complet. |
| `src/Services/SettingsService.cs` | Validare, scriere atomică și logare. |
| `src/Services/TrayIconService.cs` | Fără reflection, owner public/Win32 și HICON cleanup. |
| `src/Services/VolumeService.cs` | Dispose pentru endpoint-uri și toleranță la device removal. |
| `src/TaskbarMusicPlayer.csproj` | Versiune, metadata, DPI WinForms aliniat și warning justificat. |
| `src/app.manifest` | Assembly identity `1.1.1.0`. |
| `tests/TaskbarMusic.Tests/TaskbarMusic.Tests.csproj` | Infrastructură xUnit cross-platform. |
| `tests/TaskbarMusic.Tests/LatestVersionGateTests.cs` | Teste versiuni și identitate media. |
| `tests/TaskbarMusic.Tests/MorphTimelineTests.cs` | Teste timing morph. |
| `tests/TaskbarMusic.Tests/ScreenGeometryTests.cs` | Teste taskbar, fullscreen, DPI și hit-test. |

## 15. Ce rămâne imposibil de certificat pe macOS

- Rularea efectivă WPF.
- SMTC cu playere reale și schimbări rapide de sesiune.
- WASAPI cu restart/schimbare de endpoint.
- Click-ul tray și focus behavior real al shell-ului.
- Taskbar vertical/auto-hide pe Windows 10 și Windows 11.
- Monitoare fizice cu DPI diferit.
- Fullscreen în browsere și jocuri concrete.

Acestea nu sunt declarate „verificate”. CI și testele reduc riscul, dar nu
înlocuiesc smoke test-ul Windows.

## 16. Mentenanță după v1.1.1

- Filtrul defensiv `IsExplorerOwned` rulează numai după ce fereastra foreground
  a trecut filtrele ieftine de monitor și acoperire fullscreen. Ferestrele
  obișnuite nu mai provoacă o interogare `ProcessName` la fiecare tick de 600 ms.
- `CatWindow.StopIdleAsync` și câmpul `_visit`, ambele nefolosite, au fost
  eliminate. Vizita rămâne fire-and-forget, cu anulare și tratarea internă a
  tuturor excepțiilor.
- `SemaphoreSlim` din `MediaService` rămâne intenționat nedisposed: codul nu
  accesează `AvailableWaitHandle`, iar dispose în timpul operațiilor asincrone
  ar introduce curse fără un beneficiu material.
- Source-linking-ul testelor pure rămâne intenționat pentru CI cross-platform.
  Extragerea într-un proiect Core este amânată până când suprafața de logică
  independentă justifică refactorul.
