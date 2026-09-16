# Part A — startup and shell

**Status: proposed.** Plan only; no application code exists yet. The reader is the agent that will
execute this without re-deciding anything, so every choice below is made here and says why.

`PC_CLIENT_PLAN.md` is the architecture and is settled; this document is § 3 and § 4 of it taken to
the level of file names, project settings, script steps and numbers. `PC_CLIENT_PARTS.md` says what
part A owns: `pc/src/RankMaster2.Pc/App/`, every project and solution file of the client, and the
publish scripts. Parts B–E are not designed here; where they are needed, the frozen seams are used
by name and § 7 argues for the small additions this part cannot do without.

Every number below is marked **measured** (on this Linux build box, and says how), **from source**
(quoted from VLC 3.0.x or .NET), or **estimate**. Nothing in this document has run on Windows.

---

## 0. What this part is for

The owner's complaint is that the app takes too long to start. Two packaging fixes shipped against
that (1.1.2, 1.1.3) and nobody measured anything. Part A makes a launch fast and **proves it with a
number the owner can reproduce by running one command.**

Three causes, all owned here:

1. **The video engine wakes before the first window exists.** `src/RankMaster2.App/MainWindow.xaml.cs`
   line 47 constructs `VlcRuntime` in the window constructor — `Core.Initialize` plus `new LibVLC(...)`,
   synchronously, before first paint, on every launch, for a stills folder that will never play a
   frame. Part D owns the engine; part A owns *when* it wakes (§ 3).
2. **Nothing is published ReadyToRun.** Neither `RankMaster2.App.csproj` nor `publish.ps1` sets
   `PublishReadyToRun`; every launch re-JITs WPF, `System.Text.Json`, LibVLCSharp and the app (§ 4).
3. **320 plugin DLLs, 97 MB, no `plugins.dat`.** Measured here from the restored
   `VideoLAN.LibVLC.Windows 3.0.21` package: 320 files, 97 MB, `codec/` alone 39.8 MB, no
   `plugins.dat`, no `vlc-cache-gen.exe`. And — **from source, and this corrects `PC_CLIENT_PLAN.md`
   § 3.2 and § 12** — libvlc 3.0.x **never writes the cache on an ordinary run.** In
   `src/modules/bank.c` the write flag is set in exactly one place:

   ```c
   if (var_InheritBool(p_this, "plugins-cache"))        mode |= CACHE_READ_FILE;
   if (var_InheritBool(p_this, "plugins-scan"))         mode |= CACHE_SCAN_DIR;
   if (var_InheritBool(p_this, "reset-plugins-cache"))  mode = (mode | CACHE_WRITE_FILE) & ~CACHE_READ_FILE;
   ...
   if (mode & CACHE_WRITE_FILE) CacheSave(obj, path, bank.plugins, bank.size);
   ```

   So the shipped app has `LoadLibrary`'d all 320 DLLs on **every** launch since 1.0, not just the
   first after a publish (unless somebody put a `plugins.dat` there by hand — the kit checks, § 6.3).
   The good news is in `bin/cachegen.c`: `vlc-cache-gen` is nothing but
   `libvlc_new("--quiet", "--reset-plugins-cache")` with `VLC_PLUGIN_PATH` set. Any process that can
   call `libvlc_new` can build the index — including our own exe, on the owner's PC, where the
   generator that does not ship and cannot run on Linux is not needed at all (§ 4.3).

Also from source, two smaller corrections: `--plugin-path` is `add_obsolete_string("plugin-path") /*
since 2.0.0 */` — the old app's `--plugin-path=` argument has been ignored all along and playback
worked because the default plugin directory is `<directory of libvlccore.dll>\plugins`
(`src/win32/dirs.c`, `config_GetLibDir`), which is the same folder. And Avalonia renders through
SkiaSharp, so `libSkiaSharp.dll` is loaded before the first frame no matter what part C does; the
"Skia loads on first decode" line in `PC_CLIENT_PLAN.md` § 4 is not achievable and not needed.

"Startup" is two intervals and the owner may mean either (`PC_CLIENT_PLAN.md` § 3.1): **T1**,
double-click → start screen; **T2**, click Resume → both panes painted. The kit measures both. T1 is
entirely this part's; T2 is mostly parts B, C and E, and this part supplies the clock they mark.

---

## 1. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| UI stack | **Avalonia 11.3.22** (the version already restored and proven to publish win-x64 from this box), `Avalonia.Desktop`, **`Avalonia.Themes.Simple`**, **`Avalonia.Fonts.Inter`** | settled in `PC_CLIENT_PLAN.md` § 4. Simple over Fluent: the compare screen is two images and some text, every control is templated explicitly to `SPEC.md` anyway (the old app did the same in WPF), and Fluent's resource dictionaries are the largest managed object an empty Avalonia window builds. Inter embedded: no system-font enumeration on the start screen; icons are vector paths, not `Segoe MDL2 Assets` glyphs |
| Avalonia 12 | **not adopted** | 12.1.x is on nuget.org today; 11.3.22 is the one that has produced a win-x64 R2R binary from here. A stack change is not a startup fix |
| Target framework | **`net8.0`** (not `net8.0-windows`) | the same binary runs on Linux for headless tests and against the real server; nothing in the shell needs a Windows TFM. No `EnableWindowsTargeting` needed |
| Runtime | **.NET 8**, `TieredCompilation` on, `TieredPGO` on (defaults) | the whole repository is .NET 8 |
| Self-contained or framework-dependent | **self-contained** | "No extra .NET install" has been the rule since 1.0 and the tray/server are self-contained too, so there is no shared runtime on the PC to gain from. Measured here: self-contained folder 215 files / 109 MB, framework-dependent 33 files / 38 MB (§ 4.7). Size is irrelevant for a local folder; the install dependency is not |
| Single file or folder | **folder** | single-file buys nothing once `libvlc\` and three natives must sit beside the exe anyway; the folder maps each R2R image directly, ships a `.pdb`, and can never extract anything (the 1.1.2 lesson). The kit measures fresh-vs-warm for the folder; § 4.7 names the one condition under which single-file is switched on |
| ReadyToRun | **`PublishReadyToRun=true`**, non-composite | producible from Linux (`microsoft.netcore.app.crossgen2.linux-x64` is restored; proven for the WPF app and the Avalonia probe). Composite is a Phase A3 knob, not a default (§ 4.7) |
| Trimming | **off** | no startup return: unreferenced assemblies are never loaded, and removing them from disk does not change a launch. Real risk: Avalonia reflection paths, LibVLCSharp marshalling. Kept *reachable*: `IsAotCompatible=true` turns the trim and AOT analyzers on so violations surface at build time here |
| NativeAOT | **not in v1; design stays compatible** | cross-OS AOT is unsupported, a `windows-latest` GitHub Actions job would be needed, and LibVLCSharp under AOT is unverified. Compatibility means: compiled bindings only, no `{Binding}` by reflection, source-generated JSON, no `Assembly.Load` by string, `IsAotCompatible=true` (§ 4.7) |
| Globalization | **`InvariantGlobalization=true`** | no ICU load (`icu.dll` is several MB and its load is on the critical path); the app formats nothing culture-specific |
| Plugin set | **one checked-in manifest, `pc/libvlc/plugins.keep.txt`**: 27 plugin DLLs (27.0 MB) in Phase A0, of which four are marked provisional and are struck by the harness if VLC never uses them (expected final: 23 files, 22.5 MB); `libvlc.dll` + `libvlccore.dll`; nothing else from the package | § 4.2 — my list and part D's § 5.1 list merged; the one file both parts point at. What the app needs is the five containers, the four codecs, software decode, RV32 frames through `vmem`, no audio, no subtitles |
| The index (`plugins.dat`) | **built on the owner's PC by `RankMaster2.exe --build-vlc-cache`**, run by the install script after the files are in place; validated at every wake by a stamp; rebuilt by the app if missing or stale | § 4.3. The generator is `libvlc_new --reset-plugins-cache`, which our exe can do; building after extraction makes the mtime/size validation exact |
| `--no-plugins-scan` | **not used** | it skips VLC's per-file `stat` validation, which with 24 files costs about nothing, and a stale cache with no validation is a silent wrong-module bug |
| When the engine wakes | **when a folder is opened that the probe says would rank as video** — on a thread-pool thread, started the instant Open/Resume is requested, before the server answers; never at process start; never speculatively on the start screen | § 3. The wake overlaps the `POST /session` round trip; a stills folder never pays; T1 stays a pure number |
| Full screen | Avalonia `WindowState.FullScreen`, `SystemDecorations.None`, `CanResize=false`, `Background=Black`, primary screen | `SPEC.md` § Screens: borderless, over the taskbar. Primary screen because the app is launched from the taskbar and driven by keys; § 13 asks the owner |
| DPI | `app.manifest` declares `PerMonitorV2`; Avalonia scales | the manifest is the reliable route; the runtime call fails once a window exists |
| Esc | **part E detects it and raises `UiRoot.QuitRequested`** (its plan § 2.2: E must stop the select cue and knows when a dialog owns the key); **part A quits**: close the session with a 500 ms cap, then `Environment.Exit(0)` | Esc kills the process (`SPEC.md` § Keys); the process is this part's, the key is E's. § 3.4 |
| Crash | one text file next to the exe, `crash-<yyyyMMdd-HHmmss>.txt` | `PC_CLIENT_PLAN.md` § 2.1; the only debugging channel from the owner's PC |
| Version | **2.0.0** in `pc/Directory.Build.props`, overriding the root's 1.1.4 for the `pc/` tree only | `README.md`: a rewrite is a major. The old app stays 1.1.4 |
| Exe name and location | `RankMaster2.exe` in `C:\Utils\rank-master-2\pc\`, beside `tray\` and `server\` | `PC_CLIENT_PLAN.md` § 2 "next to `tray\`"; `SPEC.md` keeps the assembly name. The old exe at the root is left in place — the kit needs it as the baseline |
| Delivery | zip built here, hosted at `https://bormin.fintebtc.de/rm2/`, installed by **one PowerShell line** that also runs the measurement | the owner's time. Telegram's 50 MB bot limit rules the zip out as an attachment |
| Measurement | an external clock applied identically to the old and the new app, cross-checked by an in-app clock the new app writes on every launch | § 6. Nothing in this project has been measured on the owner's PC; from now on every launch measures itself |

### 1.1 Deliberately absent

- **No splash screen.** A splash is how a slow program hides. The start screen is the first frame.
- **No resident helper, no pre-launch at logon, no "keep warm" service.** The number has to be an
  honest cold process.
- **No Defender exclusions, no process-priority tricks, no registry changes.** The install writes
  one folder and one file under `%LOCALAPPDATA%`.
- **No installer, no MSI, no auto-update.** A zip, a script, a folder.
- **No x86, no ARM64.** `win-x64` only.
- **No speculative engine wake on the start screen**, even when the remembered folder is video. It
  would be invisible to T1 but would put 22 MB of DLL loads and a Defender pass on a cold machine
  under the start screen's paint.
- **No plugin scan at runtime beyond VLC's own `stat` pass.** No custom module loading.
- **No `lua\`, no `hrtfs\`, no `.lib` import libraries** from the VLC package. The old build ships all
  three (the NuGet default include is `libvlc.*;libvlccore.*;hrtfs\**;lua\**;plugins\**`).
- **No audio pipeline in the plugin set** — part D disables audio per media (`:no-audio`, its § 5.2)
  and keeps `--aout=dummy`, whose module is shipped (§ 7.3).
- **No subtitle or OSD pipeline** — `--no-osd` is in D's options; a subtitle track in an `.mkv` logs
  "no suitable decoder" and the video plays (D § 5.1) (§ 7.3).
- **No hardware-decode plugins** (`d3d11va`, `dxva2`, `qsv`, `mft`, `crystalhd`): `--avcodec-hw=none`
  is the decision that plays this owner's files today, and it stays.
- **No `Rename/` project references.** Rename is not one of the five parts and the owner has not yet
  confirmed it (`PC_CLIENT_PLAN.md` § 13.2). The client references `RankMaster2.Core` only; adding
  `Catalog`/`Ranking`/`Actions` is three lines if rename is kept.

---

## 2. What part A owns — the tree

```
pc/
  RankMaster2.Pc.sln                       client, tests, rm2probe, the two kit windows; references src/RankMaster2.Core
  Directory.Build.props                    imports the root one; <Version>2.0.0</Version>
  plans/A-startup-and-shell.md             this file
  libvlc/plugins.keep.txt                  the plugin manifest — the single source of truth (§ 4.2)
  publish.sh                               Linux: publish, verify shape, zip, upload (§ 4.4)
  install.ps1                              Windows: download, extract, swap, build index, measure (§ 4.5)
  kit/                                     the Phase A0 measurement kit (§ 6)
    kit.ps1                                the one-line bootstrapper the owner runs
    Measure-Startup.ps1                    the external clock
    Measure.cmd                            double-click fallback for Measure-Startup.ps1
    EmptyWpf/                              net8.0-windows, UseWPF, one black maximized borderless window
    EmptyAvalonia/                         net8.0, Avalonia 11.3.22 Simple + Inter, one black FullScreen window
    build-kit.sh                           builds every candidate, assembles the zip, uploads
  tools/
    rm2probe/                              net8.0 console; published win-x64 for the kit and linux-x64 for the harness (§ 6.4)
    prune-check.sh                         the docker harness that validates the manifest by playing the corpus (§ 5.3)
  src/RankMaster2.Pc/
    RankMaster2.Pc.csproj                  (§ 4.1)
    App/
      Program.cs                           Main: --build-vlc-cache, StartupClock.Start(), the AppBuilder
      App.axaml, App.axaml.cs              SimpleTheme, Dark variant, Inter; calls Composition
      MainWindow.axaml, MainWindow.axaml.cs the shell: FullScreen, black; Content = part E's UiRoot; subscribes UiRoot.QuitRequested
      Composition.cs                       constructs B, C, D, E; wraps B's link in WakingSessionLink; builds E's UiRoot
      WakingSessionLink.cs                 ISessionLink decorator: open and probe concurrently → engine warm-up → delegate (§ 3.2)
      VideoEngineGate.cs                   once-only warm-up through D's VideoEngine.WarmUpAsync(); index check and rebuild; marks (§ 3.3)
      LibVlcLayout.cs                      NativeDir, PluginsDir, IndexPath, StampPath (§ 7.2)
      LibVlcIndex.cs                       Build(), IsCurrent(), WriteStamp() (§ 4.3)
      StartupClock.cs                      marks and %LOCALAPPDATA%\RankMaster2\pc\startup.log (§ 6.5)
      AppInfo.cs                           copied from src/RankMaster2.App/AppInfo.cs, unchanged; its Version goes to E
      CrashLog.cs                          unhandled exceptions → crash-<stamp>.txt beside the exe, with D's DiagnosticsDump() appended
      AppLifetime.cs                       PrepareQuitAsync() + QuitNow()
      app.manifest                         PerMonitorV2, supportedOS Win10/11, longPathAware
      icon.ico                             copied from src/RankMaster2.App/icon.ico
    Link/  Stills/  Video/  Ui/            parts B, C, D, E; the csproj globs them, nobody edits it
                                           (LastFolderStore lives in Ui/Surface/ — part E's plan § 2.1 claims it and A has no use for it)
  tests/RankMaster2.Pc.Tests/
    RankMaster2.Pc.Tests.csproj            xunit 2.9.2, Avalonia.Headless.XUnit 11.3.22 (§ 8)
    App/  Ui/  Video/                      part A's tests; E and D add theirs here per their plans
  tests/RankMaster2.Pc.Link.Tests/         part B's own test project — compiles Link/**/*.cs directly, no Avalonia (B § 6.1);
  tests/RankMaster2.Pc.Stills.Tests/       part C's own test project (C § 5). A writes both .csproj files exactly as those
                                           plans specify and lists them in the .sln; the parts fill them
```

The shipped folder on the owner's PC:

```
C:\Utils\rank-master-2\pc\
  RankMaster2.exe                          apphost
  RankMaster2.dll, RankMaster2.pdb, Avalonia.*.dll, System.*.dll, coreclr.dll ...   (~215 files, measured for the probe)
  av_libglesv2.dll  libSkiaSharp.dll  libHarfBuzzSharp.dll
  libvlc\win-x64\libvlc.dll
  libvlc\win-x64\libvlccore.dll
  libvlc\win-x64\plugins\<subdir>\*.dll    exactly the files of the manifest, no more
  libvlc\win-x64\plugins\plugins.dat       built on this PC by install.ps1 (§ 4.3)
  libvlc\win-x64\plugins\plugins.stamp     written beside it by the same step
  VERSION.txt                              "2.0.0 <git short sha> <publish UTC>"
```

`C:\Utils\rank-master-2\pc\..\tray\RankMaster2.Tray.exe` is therefore exactly where part B's
`Paths.DefaultTrayExecutable()` looks (`{app dir}\..\tray\`, B § 4); the layout is chosen to make
that default true.

---

## 3. The startup path, instant by instant

### 3.1 T1 — double-click to start screen

```
kernel creates the process                                   ← T0 (Process.StartTime; the external clock starts here too)
apphost → hostfxr → coreclr init → R2R images mapped         (before Main; StartupClock computes this as "pre_main")
Main(args)
  if args contains --build-vlc-cache: LibVlcIndex.Build() → exit code (§ 4.3). Nothing else runs.
  StartupClock.Start()                                        mark "main"
  CrashLog.Install()
  BuildAvaloniaApp():
     AppBuilder.Configure<App>()
       .UseWin32().UseSkia()  if OperatingSystem.IsWindows()  — explicit, no UsePlatformDetect (reflection-free, AOT-clean)
       .UseX11().UseSkia()    otherwise                       — tests and Linux runs
       .WithInterFont()
     .StartWithClassicDesktopLifetime(args)                   mark "avalonia_built" at App.Initialize
App.Initialize:        Styles.Add(new SimpleTheme()); RequestedThemeVariant = Dark
App.OnFrameworkInitializationCompleted:
  lastFolder = LastFolderStore.Load()                         one 1 KB read, synchronous (< 1 ms warm; avoids a Resume-button flicker)
  services   = Composition.Build(lastFolder)                  constructs B/C/D/E objects — constructors only; no I/O, no network, no native load
  shell      = new MainWindow { Content = services.UiRoot }   black, FullScreen, primary screen
  desktop.MainWindow = shell
shell.Opened:          mark "window_opened"
                       TopLevel.GetTopLevel(shell).RequestAnimationFrame(_ => {
                           StartupClock.Mark("first_frame");                    ← T1 ends here (in-app)
                           StartupClock.AssertNoVideoEngine();                  LibVLCSharp assembly must not be loaded (§ 8 test 2)
                           Task.Run(services.Link.ConnectAsync)                 part B: /ping, start tray, pair — never before this line
                       })
```

Rules that make T1 a pure number:

- Nothing native but Avalonia's own (`av_libglesv2`, `libSkiaSharp`, `libHarfBuzzSharp`) loads before
  `first_frame`. `LibVLCSharp.dll` must not even be *loaded* — `Composition` holds part D's engine
  behind `VideoEngineGate`, which touches no D type until `WakeAsync`. If part D's surface factory
  type references LibVLCSharp types in its signature, the JIT will load the assembly when
  `Composition.Build` is compiled; the § 8 test catches that and the fix is on D's side (a factory
  interface without LibVLCSharp types in its shape).
- No network, no disk beyond `last-folder.txt`, no `Process.GetCurrentProcess()` (it is ~1 ms and
  StartupClock reads it *after* `first_frame`).
- Part E's start view is constructed before show but must do no work in its constructor beyond
  building controls; any status text ("starting the server…") arrives later through part B.

### 3.2 T2 — Resume or Open to both panes painted

Part E calls `ISessionLink.OpenAsync(folder)` exactly as the frozen seam says. It does not know that
the link it holds is `WakingSessionLink`, part A's decorator around part B's real link:

```
WakingSessionLink.OpenAsync(folder, ct):
  StartupClock.Mark("open_requested")
  video = probe.ContainsVideo(folder)                         IMediaProbe, C/D → A; synchronous, an extension pass over top-level names (≈ ms)
  StartupClock.Mark(video ? "probe_video" : "probe_still")
  if (video) _ = gate.WakeAsync(ct)                            fire-and-forget on the thread pool; never awaited here
  snapshot = await inner.OpenAsync(folder, ct)                 part B: DELETE then POST, the § 13.3 rules
  if (snapshot.Policy == "video" && !gate.IsAwake) _ = gate.WakeAsync(ct)   the probe was wrong (false negative); wake now, late but correct
  StartupClock.Mark("snapshot")
  return snapshot
```

Every other `ISessionLink` member is a one-line delegate. The decorator is the whole of part A's
control over "when": the engine wakes on the first open of a video folder, overlapping the server
round trip, and a stills folder never wakes it. A false *positive* from the probe (a mixed folder the
server ranks as `still`) wakes the engine needlessly; that costs a background thread some work and
nothing on screen. A false *negative* is caught by the snapshot.

`panes_painted` is marked by part E after the frame in which both panes have pixels has been
rendered (`RequestAnimationFrame`, not source assignment) — that is the only T2 end point that means
what the owner sees. `first_video_frame` is marked by part D. Both use `StartupClock.Mark` (§ 7.2).

### 3.3 The gate

```
VideoEngineGate (App/VideoEngineGate.cs)
  IsAwake: bool
  WakeAsync(ct): Task — idempotent; the first caller starts the work, every caller awaits the same Task
    StartupClock.Mark("vlc_wake_begin")
    if (!LibVlcIndex.IsCurrent())  LibVlcIndex.Build()        § 4.3: missing or stale index → build it now, once, before the engine loads (logs "vlc_index_rebuilt")
    await engine.WakeAsync(ct)                                  part D (§ 7.1 proposal); or, until accepted, the first IVideoSurface creation
    StartupClock.Mark("vlc_wake_end")
```

If the index cannot be built (plugin directory not writable — not the case in `C:\Utils`), the gate
logs `vlc_index_unwritable` and wakes the engine anyway; VLC then scans 24 files, which is slow only
relative to the cached path and is still 13× fewer files than today.

### 3.4 Esc

`MainWindow` handles `KeyDown` for `Key.Escape` with tunnelling priority so it runs before any
control in part E's tree:

```
AppLifetime.PrepareQuitAsync():
  StartupClock.Flush()
  var close = link.CloseAsync(CancellationToken(500 ms))       part B's DELETE /session; 404 is fine; a timeout is fine
  await Task.WhenAny(close, Task.Delay(500))
AppLifetime.QuitNow():  await PrepareQuitAsync(); Environment.Exit(0)
```

Nothing is written; the server writes nothing on close (`SERVER_SPEC.md` § 10.4). The process is
gone within ~0.6 s in the worst case. `PrepareQuitAsync` is separate from `QuitNow` so a test can
prove the 500 ms cap without killing the test host.

---

## 4. Packaging

### 4.1 `pc/src/RankMaster2.Pc/RankMaster2.Pc.csproj`

Written exactly like this; the comments stay in the file.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>RankMaster2</AssemblyName>
    <RootNamespace>RankMaster2.Pc</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>App/icon.ico</ApplicationIcon>
    <ApplicationManifest>App/app.manifest</ApplicationManifest>
    <!-- Avalonia's Win32 backend uses COM (composition, drag/drop). -->
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    <!-- No reflection bindings anywhere: the trim/AOT analyzers must stay clean so NativeAOT remains reachable. -->
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <IsAotCompatible>true</IsAotCompatible>
    <!-- No ICU on the critical path; the app formats nothing culture-specific. -->
    <InvariantGlobalization>true</InvariantGlobalization>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <!-- One version string for the package reference and the native copy target below. -->
    <LibVlcWindowsVersion>3.0.21</LibVlcWindowsVersion>
    <LibVlcManifest>$(MSBuildThisFileDirectory)../../libvlc/plugins.keep.txt</LibVlcManifest>
  </PropertyGroup>

  <!-- Publish shape. Folder, not single-file; ReadyToRun; nothing trimmed; nothing extracted at run time. -->
  <PropertyGroup>
    <PublishReadyToRun>true</PublishReadyToRun>
    <PublishSingleFile>false</PublishSingleFile>
    <PublishTrimmed>false</PublishTrimmed>
    <IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>
    <DebugType>portable</DebugType>
    <Deterministic>true</Deterministic>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.22" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.22" />
    <PackageReference Include="Avalonia.Themes.Simple" Version="11.3.22" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.3.22" />
    <PackageReference Include="Avalonia.Diagnostics" Version="11.3.22" Condition="'$(Configuration)' == 'Debug'" />
    <PackageReference Include="LibVLCSharp" Version="3.10.1" />
    <!-- Restored for its files only. Its build targets would copy all 320 plugins, lua\, hrtfs\ and the
         .lib import libraries; the CopyPrunedLibVlc target below copies the manifest instead. -->
    <PackageReference Include="VideoLAN.LibVLC.Windows" Version="$(LibVlcWindowsVersion)"
                      ExcludeAssets="all" GeneratePathProperty="true" />
    <PackageReference Include="SkiaSharp" Version="3.119.0" />
    <PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="3.119.0" />
    <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="3.119.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../../src/RankMaster2.Core/RankMaster2.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <AvaloniaResource Include="App/icon.ico" />
  </ItemGroup>

  <!-- libvlc: only on a win-x64 publish, only what the manifest names, laid out where LibVLCSharp and
       libvlccore both look: <app>\libvlc\win-x64\ and its plugins\ subtree. -->
  <Target Name="CopyPrunedLibVlc" AfterTargets="Publish" Condition="'$(RuntimeIdentifier)' == 'win-x64'">
    <PropertyGroup>
      <_VlcSrc>$(PkgVideoLAN_LibVLC_Windows)/build/x64/</_VlcSrc>
      <_VlcDst>$(PublishDir)libvlc/win-x64/</_VlcDst>
    </PropertyGroup>
    <ReadLinesFromFile File="$(LibVlcManifest)">
      <Output TaskParameter="Lines" ItemName="_VlcLine" />
    </ReadLinesFromFile>
    <ItemGroup>
      <_VlcKeep Include="@(_VlcLine)" Condition="!$([System.String]::Copy('%(Identity)').StartsWith('#')) And '%(Identity)' != ''" />
      <_VlcMissing Include="@(_VlcKeep)" Condition="!Exists('$(_VlcSrc)%(Identity)')" />
    </ItemGroup>
    <Error Condition="'@(_VlcMissing)' != ''" Text="plugins.keep.txt names files that are not in VideoLAN.LibVLC.Windows $(LibVlcWindowsVersion): @(_VlcMissing)" />
    <Copy SourceFiles="$(_VlcSrc)libvlc.dll;$(_VlcSrc)libvlccore.dll" DestinationFolder="$(_VlcDst)" />
    <Copy SourceFiles="@(_VlcKeep->'$(_VlcSrc)%(Identity)')" DestinationFiles="@(_VlcKeep->'$(_VlcDst)%(Identity)')" />
    <Message Importance="high" Text="libvlc: copied libvlc.dll, libvlccore.dll and @(_VlcKeep->Count()) plugins from the manifest" />
  </Target>

</Project>
```

Notes for the executor:

- `GeneratePathProperty` gives `$(PkgVideoLAN_LibVLC_Windows)` — the package folder,
  `~/.nuget/packages/videolan.libvlc.windows/3.0.21` here. `ExcludeAssets="all"` keeps its
  `.targets` from being imported, so nothing else copies VLC files.
- The RID is never set in the csproj: `dotnet build` and `dotnet test` on Linux run RID-less; the
  publish script passes `-r win-x64 --self-contained`.
- `SkiaSharp.NativeAssets.Linux` is for tests on this box (Avalonia's renderer); it is not copied
  into a win-x64 publish.
- `pc/Directory.Build.props`:

  ```xml
  <Project>
    <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
    <PropertyGroup>
      <Version>2.0.0</Version>
    </PropertyGroup>
  </Project>
  ```

- `App/app.manifest`: `assemblyIdentity` `RankMaster2`; `<dpiAware>true/pm</dpiAware>` and
  `<dpiAwareness>PerMonitorV2</dpiAwareness>` under `windowsSettings`; `<longPathAware>true`;
  `<supportedOS>` for Windows 10/11 (`{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`); `requestedExecutionLevel
  asInvoker`.

### 4.2 The plugin manifest — `pc/libvlc/plugins.keep.txt`

What this app asks of VLC, and nothing else: open a local file; demux `mp4`/`mov`, `mkv`/`webm`,
`avi`; packetize and software-decode AV1, H.264, VP9, MPEG-4 part 2 (`SPEC.md` § Media policy,
`MediaExtensions.Video`, corpus `tests/corpus/media/video/`); convert to RV32 at pane size; hand
frames to `vmem` callbacks; no audio, no subtitles, no window. The file is the list below, one
relative path per line, `#` comments allowed, and it is the **only** place the set is defined —
the csproj copies it, `publish.sh` verifies it, the harness validates it, the kit ships it.

The file is written exactly like this — a line is either a comment starting with `#` or one bare
relative path; nothing trails a path. Sizes are KB in `VideoLAN.LibVLC.Windows 3.0.21`, measured.

```
# Rank Master 2 PC client — the libvlc 3.0.21 plugins this app needs.
# Rules and reasons: pc/plans/A-startup-and-shell.md § 4.2. Total 23,182 KB in 24 files.
#
# access — local files only (filesystem 72 KB)
plugins/access/libfilesystem_plugin.dll
#
# demux — the five containers; mov is mp4, webm is mkv (mp4 323, mkv 1708, avi 135)
plugins/demux/libmp4_plugin.dll
plugins/demux/libmkv_plugin.dll
plugins/demux/libavi_plugin.dll
#
# packetizer — one per codec; copy carries VP9 and anything the demuxer hands over whole (172, 68, 57, 44)
plugins/packetizer/libpacketizer_h264_plugin.dll
plugins/packetizer/libpacketizer_av1_plugin.dll
plugins/packetizer/libpacketizer_mpeg4video_plugin.dll
plugins/packetizer/libpacketizer_copy_plugin.dll
#
# codec — avcodec decodes H.264, MPEG-4 part 2 and VP9; dav1d decodes AV1 and outscores avcodec's AV1 (16868, 1846)
plugins/codec/libavcodec_plugin.dll
plugins/codec/libdav1d_plugin.dll
#
# video output — the frame-callback sink, and the dummy VLC falls back to (45, 44)
plugins/video_output/libvmem_plugin.dll
plugins/video_output/libvdummy_plugin.dll
#
# video chroma — I420, 8- and 10-bit, to RV32 at pane size (993, 60, 83, 146, 42, 70)
plugins/video_chroma/libswscale_plugin.dll
plugins/video_chroma/libi420_rgb_plugin.dll
plugins/video_chroma/libi420_rgb_mmx_plugin.dll
plugins/video_chroma/libi420_rgb_sse2_plugin.dll
plugins/video_chroma/librv32_plugin.dll
plugins/video_chroma/libchain_plugin.dll
#
# video filter — VLC inserts this itself for interlaced content; without it combing is shown (162)
plugins/video_filter/libdeinterlace_plugin.dll
#
# stream filter — the read-ahead VLC 3 puts in front of a file access (46, 45, 47)
plugins/stream_filter/libcache_read_plugin.dll
plugins/stream_filter/libcache_block_plugin.dll
plugins/stream_filter/libprefetch_plugin.dll
#
# audio output — so "--aout=dummy" resolves if part D keeps it; unused under --no-audio (41)
plugins/audio_output/libadummy_plugin.dll
#
# logger — libvlccore asks for a "logger" module at libvlc_new; the only one in the package (65)
plugins/logger/libconsole_logger_plugin.dll
```

What goes, by directory, with the measured size it takes with it:

| Dropped | KB | Why it is not needed |
|---|---|---|
| `codec/` minus avcodec, dav1d | 21,094 | `libass` 3.0 MB, `vpx` 4.4 MB, `aom` 2.0 MB, `x26410b`, `schroedinger`, `zvbi`, every audio codec, every subtitle codec, every hardware decoder. `vpx` and `aom` are lower-scoring fallbacks for codecs avcodec and dav1d already decode — the harness confirms which module VLC actually picks (§ 5.3) |
| `access/` minus filesystem | 14,676 | network, discs, capture, `srt` 3.6 MB, `dcp` 2.4 MB, `bluray` 2.1 MB |
| `demux/` minus mp4, mkv, avi | 7,238 | `adaptive` 2.4 MB (DASH/HLS), `gme`, `mod`, `ts`, playlists, raw ES, subtitles |
| `access_output/` + `stream_out/` + `mux/` | 9,224 | streaming and transcoding output; this app produces nothing |
| `misc/` | 3,504 | `gnutls` 2.1 MB, `xml` 1.1 MB, addons, fingerprinter |
| `video_output/` minus vmem, vdummy | 3,275 | `d3d11`, `d3d9`, `directdraw`, `wingdi`, `gl*`, `wgl`, `caca`, `drawable`, `winhibit`, `yuv`, `flaschen` — the panes are bitmaps, not VLC windows. If part D takes the HWND fallback of `PC_CLIENT_PLAN.md` § 6.2 it adds `libdirect3d11_plugin.dll`, `libdirect3d9_plugin.dll`, `libdrawable_plugin.dll`, `d3d11/`, `d3d9/` to this manifest — five lines |
| `text_renderer/`, `spu/`, `video_splitter/`, `visualization/` | 6,148 | subtitles, OSD, mosaics, visualisers |
| `video_filter/` minus deinterlace | 2,262 | effects |
| `video_chroma/` minus the six kept | 878 | YUY2/NV12/P010/grey paths serve outputs this app never requests |
| `audio_filter/`, `audio_mixer/`, `audio_output/` minus adummy | 2,663 | no audio decoding under `--no-audio` |
| `packetizer/` minus the four kept | 627 | HEVC (a `SPEC.md` non-goal), VC-1, Dirac, audio packetizers |
| `stream_filter/` minus the three kept, `stream_extractor/`, `keystore/`, `meta_engine/`, `services_discovery/`, `lua/` | 4,124 | archives, tags, credentials, metadata, discovery, Lua |
| `libvlc.lib`, `libvlccore.lib`, `vlc.lib`, `vlccore.lib`, `hrtfs\`, `lua\` | 849 + dirs | import libraries and data for features not shipped |

Result: **24 plugins, 22.6 MB + libvlccore 2.7 MB + libvlc 0.2 MB ≈ 25.5 MB** against ~100 MB, and
24 files to `stat` at wake instead of 320 to `LoadLibrary`.

**The rule for changing the list**, so nobody argues from taste: a plugin is added when the harness
(§ 5.3) shows VLC using it on a corpus file with the full set, or when a part's plan names a VLC
option that needs it (with the module name written next to the line). A plugin is never removed
below this list without the harness passing on the reduced set.

### 4.3 The index — `plugins.dat`

**Mechanism (from source).** `libvlc_new` with `--reset-plugins-cache` scans the plugin directory,
loads every plugin once, and writes `<plugins dir>\plugins.dat` through a temp file and a rename
(`src/modules/cache.c`, `CacheSave`; on Windows `unlink` then `rename`). On a normal run
`AllocatePluginFile` looks each on-disk plugin up in the cache by **relative path** and accepts the
entry only if `mtime` and `size` match (`msg_Err "stale plugins cache: modified %s"` otherwise, and
that one plugin is loaded the slow way). The cache header carries `"cache VLC 3.0.21"` plus
subversion 34, so a cache from another VLC build is rejected as a whole. The default plugin
directory is `<directory of libvlccore.dll>\plugins`; `VLC_PLUGIN_PATH` adds more; `--plugin-path`
is obsolete and ignored.

**Where it is built: on the owner's PC, after the files are in place, by our own exe.**
`PC_CLIENT_PARTS.md` says "generating the index at publish time"; publish happens on Linux, where
the Windows plugins cannot be loaded, so the index is generated at *install* time instead — the
first thing that runs on the PC after the files land, and before the first launch. The effect the
sentence asks for (no scan on any launch) is the same.

```
RankMaster2.exe --build-vlc-cache
  LibVlcLayout.NativeDir = <exe dir>\libvlc\win-x64        (exit 2 if libvlc.dll is not there)
  Core.Initialize(NativeDir)                               LibVLCSharp loads libvlccore.dll then libvlc.dll from that directory
  using var vlc = new LibVLC("--quiet", "--reset-plugins-cache")   the vlc-cache-gen argument list, verbatim
  dispose                                                  the cache is written during libvlc_new; dispose releases the bank
  verify <PluginsDir>\plugins.dat exists, > 0 bytes, mtime within the last minute   (exit 3 otherwise)
  delete any leftover <PluginsDir>\plugins.dat.<pid> temp files
  LibVlcIndex.WriteStamp()                                 (below)
  print one line to stdout: "plugins.dat <bytes> bytes, <n> plugins, <ms> ms"   (also appended to startup.log as "vlc_index_built")
  exit 0
```

`VLC_PLUGIN_PATH` is **not** set: the default directory is already the right one, and setting it to
the same path would scan it twice.

**Why not on this box or in CI.** It cannot run on Linux (the plugins are Windows DLLs), and a cache
produced on a Windows CI runner would be validated against `mtime` values that a zip round-trip
rounds to two-second DOS time — every entry could come out stale. Built after extraction, on the
same file system, the values match exactly.

**The stamp — `plugins.stamp`.** Written by the builder: SHA-256 over the sorted lines
`<relative path>|<size>|<mtime UTC ticks>` of every `*.dll` under `plugins\`, as hex, plus the
libvlc version string on a second line. `LibVlcIndex.IsCurrent()` recomputes it (24 `stat`s, well
under a millisecond) and compares; it is false if `plugins.dat` or the stamp is missing. This is
how the gate (§ 3.3) self-heals a publish that forgot the install step or a hand-copied folder,
without parsing VLC's log.

**What the install script asserts:** exit code 0 and `plugins.dat` present. If not, it says so in
red and continues — the app self-heals at first wake, only more slowly, and the measurement block
will show it.

### 4.4 `pc/publish.sh` — the Linux side

Bash, run from the repository root by the agent; no `git` commands inside it (the executor runs
`git` separately, per the parts' rules).

```
1. export PATH="$HOME/.dotnet:$PATH"; set -euo pipefail
2. VERSION = the <Version> from pc/Directory.Build.props; SHA = `git rev-parse --short HEAD` if available, else "nogit"
   (reading the sha is the one git call; it is read-only and may be skipped with --no-sha)
3. rm -rf pc/dist/RankMaster2-pc
4. dotnet publish pc/src/RankMaster2.Pc -c Release -r win-x64 --self-contained \
        -o pc/dist/RankMaster2-pc -nologo -v minimal
   (PublishReadyToRun and the rest come from the csproj; nothing is passed that the csproj already decides)
5. verify the shape — every failure is fatal:
   - RankMaster2.exe, RankMaster2.dll, RankMaster2.pdb exist
   - av_libglesv2.dll, libSkiaSharp.dll, libHarfBuzzSharp.dll exist
   - libvlc/win-x64/libvlc.dll and libvlccore.dll exist
   - `find libvlc/win-x64/plugins -name '*.dll' | wc -l` == number of non-comment lines in pc/libvlc/plugins.keep.txt (24), and every manifest path exists
   - no *.lib, no lua/, no hrtfs/, no plugins.dat (it is built on the PC, never shipped), no *.xml docs, no createdump.exe
   - RankMaster2.dll is really R2R: `rm2probe is-r2r <dll>` opens it with System.Reflection.PortableExecutable.PEReader
     and reports whether the CLI header's ManagedNativeHeader directory is non-empty (that is the definition of an
     R2R image); the script asserts "yes" for RankMaster2.dll and for Avalonia.Base.dll. rm2probe is built by the same
     script run, linux-x64, into pc/dist/probe-linux/ (it is needed by the harness anyway)
   - total size < 150 MB; file count printed
6. write VERSION.txt: "<VERSION> <SHA> <UTC timestamp>"
7. copy pc/install.ps1 into the folder
8. zip: (cd pc/dist && zip -qr RankMaster2-pc-<VERSION>.zip RankMaster2-pc)   — deterministic order; print size
9. upload: cp to /var/www/bormin/web/rm2/RankMaster2-pc-<VERSION>.zip; write /var/www/bormin/web/rm2/latest.txt = "<VERSION>"; cp pc/install.ps1 to /var/www/bormin/web/rm2/install.ps1
   (the directory is nginx static per CLAUDE.md; create it if missing; --no-upload skips this step)
10. print the owner's one line (§ 4.5) and the zip URL
```

The zip is expected around 45–60 MB compressed (estimate: the probe's 109 MB folder compresses to
roughly half; the 22.6 MB plugin set to about 10 MB). It is hosted, not sent through the bot.

### 4.5 `pc/install.ps1` — the Windows side, and the owner's one line

The owner opens a terminal (`Win`+`X`, Terminal) and pastes:

```
irm https://bormin.fintebtc.de/rm2/install.ps1 | iex
```

That line, every time there is a new build. It does, in order, printing one line per step:

```
1.  $root = "C:\Utils\rank-master-2"; $dst = "$root\pc"; $tmp = "$env:TEMP\rm2pc"
2.  read latest.txt → VERSION; download RankMaster2-pc-<VERSION>.zip to $tmp (Invoke-WebRequest, show size)
3.  Expand-Archive to $tmp\new (fresh directory)
4.  Stop-Process -Name RankMaster2 where Path starts with $dst (the client only; never the old exe at $root, never the tray)
5.  if $dst exists: Rename-Item $dst "$dst.old"; Move-Item $tmp\new\RankMaster2-pc $dst; Remove-Item "$dst.old" -Recurse
    (a swap, so a failed extraction cannot leave a half-folder; nothing under pc\ is user data)
6.  $p = Start-Process "$dst\RankMaster2.exe" -ArgumentList "--build-vlc-cache" -Wait -PassThru
    assert $p.ExitCode -eq 0 and Test-Path "$dst\libvlc\win-x64\plugins\plugins.dat"   → "index: <bytes> bytes" or a red line
7.  measure (unless -NoMeasure): the same function as Measure-Startup.ps1 (§ 6.3), dot-sourced from the folder:
    one "fresh" launch (this is the first launch of these files), then 5 warm launches; each launch killed 400 ms after the window appears
8.  read the last 6 lines of %LOCALAPPDATA%\RankMaster2\pc\startup.log (the in-app clock for those launches)
9.  print the result block (§ 6.7), Set-Clipboard it, and say: "copied — paste it into the chat"
```

Steps 7–9 are why install and measurement are one command: after every update the owner sees the
number and we get it back by paste. `-NoMeasure` exists for the day he is in a hurry.

The script never touches `C:\Utils\rank-master-2\RankMaster2.exe`, `libvlc\` at the root, `tray\`,
`server\`, or anything in `%LOCALAPPDATA%\RankMaster2\server`.

### 4.6 The kit publish — `pc/kit/build-kit.sh`

Builds the Phase A0 candidates (§ 6.2) into `pc/dist/kit/`, zips to `rm2-startup-kit-<date>.zip`,
uploads beside the client zip together with `kit.ps1`. Steps per candidate:

```
old-r2r:        dotnet publish src/RankMaster2.App -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true \
                  -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DebugType=None -o pc/dist/kit/old-r2r
                then DELETE pc/dist/kit/old-r2r/libvlc  (the NuGet targets copied all 100 MB; the script on the PC copies the
                owner's shipped libvlc\ next to it instead — same files, 40 MB less to download)
empty-wpf:      dotnet publish pc/kit/EmptyWpf      -c Release -r win-x64 --self-contained -p:EnableWindowsTargeting=true -p:PublishReadyToRun=true -o pc/dist/kit/empty-wpf
empty-avalonia: dotnet publish pc/kit/EmptyAvalonia -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o pc/dist/kit/empty-avalonia
rm2probe:       dotnet publish pc/tools/rm2probe    -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o pc/dist/kit/rm2probe
                then copy the pruned libvlc set (libvlc.dll, libvlccore.dll, the 24 manifest plugins) to pc/dist/kit/libvlc-pruned/win-x64/
                (reuse the csproj target: publish RankMaster2.Pc once and copy its libvlc\ folder, so the kit's set IS the manifest)
scripts:        Measure-Startup.ps1, Measure.cmd → pc/dist/kit/
```

`EmptyWpf`: `net8.0-windows`, `UseWPF`, `EnableWindowsTargeting`, `App.xaml` with `StartupUri`, a
`Window` with `WindowStyle=None`, `ResizeMode=NoResize`, `WindowState=Maximized`,
`Background=Black`, one `TextBlock "Rank Master 2"` in white 24 pt so text rendering is on the path.
No VLC, no packages. `EmptyAvalonia`: `net8.0`, the same four Avalonia packages as the client, the
same `Program.cs` shape as § 3.1 (Simple theme, Dark, Inter, `UseWin32().UseSkia()`), a `Window` with
`SystemDecorations=None`, `WindowState=FullScreen`, `Background=Black`, `CanResize=false`, the same
`TextBlock`. Both self-contained R2R folders. They differ from the client only in having no content
— that is the point: they price the two shells on the owner's hardware, which is the
`PC_CLIENT_PLAN.md` § 4 gate.

Kit size estimate: old-r2r ~150 MB (WPF self-contained), empty-wpf ~150 MB, empty-avalonia ~110 MB,
rm2probe ~80 MB plus the pruned set 25 MB — roughly 500 MB unpacked, 180–220 MB zipped. Hosted; the
owner downloads it once.

### 4.7 What each packaging option costs

| Option | Size | Files | Start-time effect | Verdict |
|---|---|---|---|---|
| Self-contained **folder**, R2R | **109 MB** (measured, Avalonia probe with Fluent; Simple is smaller) | **215** | private runtime, every assembly an R2R image mapped from its own file; no install prerequisite | **chosen** |
| Self-contained single-file, R2R | 120 MB: 106.6 MB exe + 3 natives (measured) | 4 | same code; one bundle to open and one file for Defender to look at; Skia/ANGLE still beside the exe | the one switch (below) |
| Framework-dependent, R2R | 38 MB (measured) | 33 | needs ".NET Runtime 8" installed on the PC; hostfxr resolves the shared framework (+5–15 ms, estimate); no page-cache sharing because the tray is self-contained | rejected |
| + `PublishTrimmed` (partial) | −30–50 MB (estimate) | fewer | ≈ 0 on start (unloaded assemblies cost nothing at launch); breaks reflection it cannot see | rejected for v1 |
| + `PublishReadyToRunComposite` | +20–40 MB (estimate) | same | framework and app compiled as one image; tens of ms (estimate); crossgen2 cross-OS composite is **unverified** from Linux | Phase A3 knob |
| NativeAOT | 40–60 MB (estimate) | ~5 | the largest single gain (−100 to −300 ms, estimate: no runtime init, no JIT, no R2R fixups) | needs a Windows runner; deferred (§ 9, A4) |

**The single-file switch.** The folder is measured by the kit as "fresh" (first launch after
extraction) versus "warm" (median of five). If `fresh − warm > 300 ms` for the folder **and** a
single-file publish of the same client measured the same way shows `fresh − warm ≤ 150 ms`, then
`PublishSingleFile=true` (with `IncludeNativeLibrariesForSelfExtract=false` and
`EnableCompressionInSingleFile=false`, both explicit) is switched on and the folder verdict is
struck. The hypothesis under test is Defender: it may scan each of 215 fresh files on first load, or
it may have marked them clean at extraction time and scan nothing — nobody knows, and this is how
we find out. Until the number exists, the folder stands.

**NativeAOT compatibility — what it means in code, so it stays reachable:**

- `IsAotCompatible=true` on the client project (also sets `IsTrimmable`, `EnableTrimAnalyzer`,
  `EnableAotAnalyzer`, `EnableSingleFileAnalyzer`). Warnings from these analyzers in `App/` are
  fixed, not suppressed; warnings in other parts are reported to their owners.
- Compiled bindings only (`x:DataType` on every view; `AvaloniaUseCompiledBindingsByDefault`).
- `UseWin32()`/`UseX11()` chosen by `OperatingSystem.IsWindows()`, not `UsePlatformDetect()`.
- Part B's JSON through `System.Text.Json` source generation (its plan; stated here because the
  csproj setting will flag anything else).
- The route, when the number says R2R is not enough: `.github/workflows/pc-aot.yml`, `windows-latest`,
  `dotnet publish pc/src/RankMaster2.Pc -c Release -r win-x64 -p:PublishAot=true`, artifact
  uploaded; the same `install.ps1` installs it. Not built in this plan.

---

## 5. The hard problems

### 5.1 Measuring "on screen" from outside, on both apps, without a profiler

The old app cannot be instrumented (it is frozen) and the comparison must use one clock. The external
clock is `Measure-Startup.ps1` (§ 6.3): `Start-Process`, then poll `$p.Refresh(); $p.MainWindowHandle`
every 2 ms. .NET's `MainWindowHandle` enumerates the process's top-level windows and returns the
first that is **unowned and `IsWindowVisible`** — so "handle is non-zero" means "a visible top-level
window exists", which is the instant the compositor has something of ours to show. Start is
`$p.StartTime`, the kernel's creation timestamp, so the host's own startup (hostfxr, coreclr,
assembly mapping) is inside the number, as the owner experiences it.

Its weakness — a window can be visible a frame before its content is painted — is covered by the
in-app clock (§ 6.5): the new app marks `first_frame` from Avalonia's `RequestAnimationFrame` after
`Opened`, which fires when a frame with our content has been rendered. The two numbers are printed
side by side; they must agree within 100 ms. If they do not, the window is appearing empty before
its content, which is a real defect to fix, not a measurement quirk to average away.

Killing the launched app 400 ms after the handle appears is safe for both: neither writes anything
at start (the old app reads `last-folder.txt`; the new one too). The new client will start part B's
connect after the first frame; killing it then is harmless, and if it had begun launching the tray,
the tray simply stays up.

### 5.2 An index that the shipped generator cannot build

Solved by the source reading in § 0 and the mechanism in § 4.3: the generator is `libvlc_new` with
one flag, our exe calls it on the owner's machine after the files land, and a stamp makes the app
rebuild it whenever the plugin set changes underneath it. Whether LibVLCSharp's `LibVLC`
constructor passes `--reset-plugins-cache` through unmodified is proven **here**, not on his PC:
the harness (§ 5.3) runs the same builder code against Debian's libvlc and asserts `plugins.dat`
appears and the next init reports a cache hit. The install script's assertion (`plugins.dat`
exists, exit 0) then only confirms the Windows build behaves like the Linux one.

### 5.3 Pruning without running Windows VLC — the twin harness

The plugin set is Windows DLLs, and nothing Windows runs here. But VLC's modules have the same names
on every platform, Debian trixie's `vlc-plugin-base` is VLC **3.0.23** (checked inside
`debian:trixie-slim`, which docker pulls on this box), and the corpus at `tests/corpus/media/video/`
has one clip in each of the five containers plus AV1 in mp4. So the *set* is validated by playing
the corpus on Linux through the `.so` twins of exactly the manifest's names.

`pc/tools/prune-check.sh`:

```
1.  publish rm2probe for linux-x64 (self-contained, InvariantGlobalization) to pc/dist/probe-linux/
2.  docker run --rm -v pc/dist/probe-linux:/probe -v tests/corpus/media/video:/media:ro \
      -v pc/libvlc/plugins.keep.txt:/keep.txt:ro -v pc/dist/prune-report:/out debian:trixie-slim bash -c '
      apt-get update -qq && apt-get install -y -qq --no-install-recommends libvlc-dev vlc-plugin-base ca-certificates
        (libvlc-dev provides the unversioned libvlc.so that DllImport("libvlc") resolves; vlc-plugin-base is the plugin set)
      P=/usr/lib/x86_64-linux-gnu/vlc/plugins
      mv $P $P.full && mkdir $P
      for line in $(grep -v "^#" /keep.txt): so=$P.full/${line#plugins/}; so=${so%.dll}.so
          [ -f "$so" ] && ln -s "$so" "$P/${line#plugins/}" (creating the subdir) || echo "no twin: $line" >> /out/no-twin.txt
      /probe/rm2probe vlc-cache /usr/lib/x86_64-linux-gnu/vlc   # the § 4.3 builder, same code as --build-vlc-cache; asserts $P/plugins.dat appears
                                                                #   → proves LibVLCSharp passes --reset-plugins-cache through to libvlc_new, on Linux, before the owner ever runs it
      /probe/rm2probe vlc-init  /usr/lib/x86_64-linux-gnu/vlc   # must log "loading plugins cache file" and cache=hit
      /probe/rm2probe play /media --seconds 3 --min-frames 10 --report /out/pruned.txt          # must succeed for every file
      rm -rf $P && mv $P.full $P
      /probe/rm2probe play /media --seconds 3 --min-frames 10 --report /out/full.txt            # records every "using X module Y"
    '
3.  diff: every module named in /out/full.txt must appear in plugins.keep.txt (by stem); print the difference; exit 1 if non-empty
4.  /out/no-twin.txt must be empty (every manifest entry has a Linux twin; a Windows-only entry would be unvalidated)
```

Why the `mv`: `AllocateAllPlugins` scans `<libdir>/plugins` **first** and `VLC_PLUGIN_PATH` after,
so pointing the env var at a twin directory would still load the full Debian set. Replacing the
directory is the only way to isolate.

The corpus clips are 426×240. If `ffmpeg -encoders` on this box lists `libsvtav1` (it does), the
harness also generates once, into `pc/dist/prune-media/`, a 3840×2160 AV1 clip of 2 s from the
corpus source (`ffmpeg -i _source.webm -t 2 -vf scale=3840:2160 -c:v libsvtav1 -preset 10 av1_4k.mp4`)
and a 10-bit variant (`-pix_fmt yuv420p10le`), and plays both: they exercise dav1d at size, the
10-bit → RV32 conversion, and swscale's scaling path. The report lists modules used per file.

What the harness proves: the 24 names are sufficient for these containers and codecs on VLC 3.0.x.
What it cannot prove: that the Windows build of a plugin behaves like the Linux one, or that the
owner's real files contain nothing outside the corpus. The second is covered on his PC by
`rm2probe play` over his actual video folder (§ 6.4), and by the app itself: a "no suitable
decoder/demux module" from VLC is a part D failure state on the pane, never a silent skip
(`PC_CLIENT_PLAN.md` § 6.5).

### 5.4 The first launch after a publish

Acceptance point 1 of `PC_CLIENT_PLAN.md` § 10 says the first launch after a publish may not be
slower than the tenth. Three things can make it slower: self-extraction (gone by construction — the
folder layout extracts nothing), Defender's first look at fresh files (measured as fresh-vs-warm,
decided by the switch in § 4.7), and the plugin scan (gone by construction — the index is built by
the install step before the first launch, and T1 never touches VLC anyway). What remains is the OS
file cache: ~110 MB read once from an SSD, ~100 ms (estimate). The number the kit reports is what
decides whether anything more is needed.

### 5.5 The engine asleep, when the classification belongs to the server

The server decides `policy` (`SERVER_SPEC.md` § 9: `still | video`; a mixed folder is `still`). The
client cannot wait for that answer without losing the overlap, and cannot trust a local guess alone.
§ 3.2 does both: `IMediaProbe` first (local, milliseconds, a false positive is cheap), the snapshot's
`policy` second (authoritative, a false negative is corrected late but correctly). The probe's
contract wording matters (§ 7.4): "would this folder rank as video under `SPEC.md` § Media policy",
which is `MediaExtensions.RankPolicy` over the top-level names — not "does it contain any video".

### 5.6 What Avalonia does on his Windows 11, which nobody here has seen

Full screen over the taskbar, per-monitor DPI, the first frame's colour (a white flash before black
would be visible and wrong), whether ANGLE's D3D11 device creation is on the first-frame path (it
is, by design, and the empty window prices it). None of this can be checked here. The kit's
`empty-avalonia` window is built exactly like the shell — black, FullScreen, Inter text — so the
owner sees all of it in Phase A0, before a line of the client exists, and the report asks him one
yes/no: *did the black window cover the taskbar, with no flash?* If Avalonia fails that in two
rounds, `PC_CLIENT_PLAN.md` § 4 names the fallback (WPF) and the client layer moves unchanged.

---

## 6. The measurement kit

### 6.1 Definitions

| Symbol | From | To | Measured by |
|---|---|---|---|
| **T1_ext** | process creation (`Process.StartTime`) | first visible top-level window (`MainWindowHandle != 0`) | `Measure-Startup.ps1`, identically for every candidate |
| **T1_app** | process creation | `first_frame` (Avalonia `RequestAnimationFrame` after `Opened`) | `StartupClock`, new client only |
| **pre_main** | process creation | entry to `Main` | `StartupClock` — the runtime's own cost, invisible to any in-process timer otherwise |
| **T2** | `open_requested` (Resume/Open clicked) | `panes_painted` (part E's mark after both panes rendered) | `StartupClock` |
| **T_wake** | `vlc_wake_begin` | `vlc_wake_end` | `StartupClock` (gate) |
| **T_vlc_init** | before `Core.Initialize` | after `new LibVLC` returns | `rm2probe vlc-init`, each run in a fresh process |
| **fresh** | the first launch after the files were extracted | | one launch |
| **warm** | any later launch, machine not rebooted | | median and max of 5 |
| **cold** | the first launch after a reboot | | one launch, optional, `-Cold` |

`T_vlc_init` must run each sample in a **fresh process**: libvlccore's module bank is process-global
and a second `libvlc_new` in the same process does not scan again. `rm2probe vlc-init` therefore
spawns itself with `--once` three times and reads the child's stdout.

### 6.2 Candidates

| Name | What it is | What comparing it tells |
|---|---|---|
| `shipped` | `C:\Utils\rank-master-2\RankMaster2.exe` as installed today | **the baseline** — the number the owner is complaining about |
| `old-r2r` | the frozen app's code, R2R, folder layout, the owner's own `libvlc\` copied beside it, no `plugins.dat` | `shipped − old-r2r` = JIT + single-file cost (candidate 2 of `PC_CLIENT_PLAN.md` § 3.2) |
| `old-r2r-cache` | the same folder after `rm2probe vlc-cache` built `plugins.dat` in its `libvlc\` copy | `old-r2r − old-r2r-cache` = the 320-plugin scan (candidate 1) |
| `empty-wpf` | one black borderless maximized WPF window, R2R, self-contained | WPF's floor on his hardware |
| `empty-avalonia` | one black FullScreen Avalonia window, Simple + Inter, R2R, self-contained | Avalonia's floor — the § 4 gate; also the § 5.6 yes/no |
| `pc` (from Phase A3) | the new client | the deliverable, against `shipped` |

`old-r2r-cache − empty-wpf` is what remains of the old app's own startup once JIT and scan are gone
(the remaining LibVLC init, XAML, two players) — the number that says whether a repackaged 1.1.5
would have been enough (`PC_CLIENT_PLAN.md` § 9 Phase 0 decision).

### 6.3 `Measure-Startup.ps1`

Parameters: `-Candidates <hashtable name → exe path>` (defaults to the kit's layout plus the shipped
exe), `-Warm 5`, `-Cold` (one launch each, no re-extraction), `-Out results.txt`.

```
function Measure-Launch($exe):
  $p = Start-Process -FilePath $exe -PassThru -WorkingDirectory (Split-Path $exe)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  do { Start-Sleep -Milliseconds 2; $p.Refresh() } until ($p.MainWindowHandle -ne 0 -or $p.HasExited -or $sw.ElapsedMilliseconds -gt 20000)
  $seen = Get-Date                     # taken the instant the loop exits, before anything else
  $t1 = if ($p.HasExited) { "crashed(exit $($p.ExitCode))" } elseif ($p.MainWindowHandle -eq 0) { "timeout" } else { [int]($seen - $p.StartTime).TotalMilliseconds }
  Start-Sleep -Milliseconds 400
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  Start-Sleep -Milliseconds 300     # let the desktop settle before the next launch
  return $t1

per candidate:
  fresh = Measure-Launch (the first launch of these files in this run)
  warm  = 1..$Warm | Measure-Launch → median, max
  for old-r2r: after its measurements, run rm2probe vlc-cache <old-r2r>\libvlc\win-x64, then measure again as old-r2r-cache
preconditions printed once:
  Windows build, CPU name, RAM, whether the drive holding C:\Utils is SSD (Get-PhysicalDisk MediaType), display count and scale (Get-CimInstance Win32_VideoController → CurrentHorizontalResolution; DPI from the registry LogPixels)
  Test-Path C:\Utils\rank-master-2\libvlc\win-x64\plugins\plugins.dat      → expected False (§ 0); reported either way
  whether RankMaster2.Tray is running
report: § 6.7; written to $Out, Set-Clipboard, and echoed
```

Time on the owner's PC: 6 launches × 5 candidates ≈ 30 launches, under two minutes including the
probe. Windows will flash black and vanish thirty times; the instructions say so and ask him not to
touch the keyboard.

### 6.4 `rm2probe` — one console, both jobs

`pc/tools/rm2probe`, `net8.0`, references `LibVLCSharp 3.10.1`, `SkiaSharp 3.119.0` (+ Win32 and
Linux native assets), `src/RankMaster2.Catalog` and `src/RankMaster2.Core` (for `JsonCatalog.Scan`,
which is read-only — lines 16–33 of `JsonCatalog.cs` contain no write; `Save` starts at line 34 and
the probe never calls it). Published win-x64 for the kit and linux-x64 for the harness. Every
subcommand prints one block of plain text and exits 0/1; nothing it does writes inside the owner's
media folders.

| Subcommand | Does | Prints |
|---|---|---|
| `vlc-init <libvlcDir> [--runs 3]` | spawns `rm2probe vlc-init <dir> --once` per run; `--once` times `Core.Initialize` and `new LibVLC("--quiet")` separately, subscribes to `LibVLC.Log`, counts `plug-ins loaded: N modules` and notes whether `loading plugins cache file` appeared | `init=<ms> new=<ms> plugins=<N> cache=<hit|none|stale(k)>` per run |
| `vlc-cache <libvlcDir>` | the § 4.3 build, on a *copy* if `--copy-to <dir>` is given (the kit uses this against the owner's shipped `libvlc\` so his install is never modified) | `plugins.dat <bytes> bytes in <ms> ms` |
| `play <file or dir> [--libvlc <dir>] [--seconds 3] [--min-frames 10] [--report <file>]` | `vmem` callbacks at RV32 1920×1080, `--no-audio --no-spu --no-osd --avcodec-hw=none --verbose=2`; per file: ms to first frame, frames in `--seconds`, every `using <capability> module "<name>"` log line deduplicated | one line per file plus the module list; exit 1 if any file has fewer than `--min-frames` |
| `scan <folder>` | `JsonCatalog.Scan` ×3 (read-only), file count, `rankmaster_db.json` size, drive type of the folder | `files=<n> json=<KB> scan=<ms,ms,ms>` |
| `decode <file or folder> [--width 2160]` | SkiaSharp decode-at-size of the given still or of the largest still in the folder, ×3 | `<file> <MP> decode@2160=<ms,ms,ms>` |
| `is-r2r <dll>` | `PEReader`: is the CLI header's `ManagedNativeHeader` directory non-empty | `<dll> r2r=yes|no`; exit 1 on `no`. Used by `publish.sh` (§ 4.4) |

In the kit, `Measure-Startup.ps1` runs after the launches:

```
rm2probe vlc-init  <kit>\old-r2r\libvlc\win-x64                 (the owner's 320-plugin set, no cache)
rm2probe vlc-cache <kit>\old-r2r\libvlc\win-x64                 (builds it there — his real folder untouched)
rm2probe vlc-init  <kit>\old-r2r\libvlc\win-x64                 (320 plugins, cached)
rm2probe vlc-init  <kit>\libvlc-pruned\win-x64                  (24 plugins, no cache)
rm2probe vlc-cache <kit>\libvlc-pruned\win-x64 ; rm2probe vlc-init <kit>\libvlc-pruned\win-x64   (24, cached — the design's promise)
rm2probe scan   <last-folder.txt>                               (his real folder, read-only)
rm2probe decode <last-folder.txt>                               (largest still there)
rm2probe play   <last-folder.txt> --libvlc <kit>\libvlc-pruned\win-x64 --seconds 3   if the folder is video; else "skipped: stills folder"
```

The `play` line over his real folder is the on-PC half of § 5.3. If his video library is elsewhere,
the report asks him to drag one video onto `Probe-Video.cmd` (which runs `play` on it).

### 6.5 `StartupClock` — the in-app clock

`App/StartupClock.cs`. `Start()` records `Stopwatch.GetTimestamp()` at `Main` entry and the wall
clock; `Mark(string name)` appends `(name, elapsed ms)` to a lock-free list (an array of 64 slots and
an `Interlocked` index; a 65th mark is dropped, never thrown). At `first_frame`, on a thread-pool
thread, it reads `Process.GetCurrentProcess().StartTime`, computes `pre_main`, and writes the line;
`Flush()` rewrites it at exit with every later mark. One line per launch, appended to
`%LOCALAPPDATA%\RankMaster2\pc\startup.log` (created on demand; kept to the last 200 lines):

```
2026-09-20T18:04:11Z 2.0.0 pre_main=38 main=0 avalonia_built=61 window_opened=172 first_frame=198 probe_still=2410 snapshot=2496 panes_painted=2905
```

All values are ms since process creation. Any part may call `StartupClock.Mark`; the marks each part
owns are: A — `main`, `avalonia_built`, `window_opened`, `first_frame`, `open_requested`,
`probe_video`/`probe_still`, `vlc_wake_begin`, `vlc_wake_end`, `vlc_index_rebuilt`, `snapshot`,
`quit`; B — `link_connecting`, `link_ready`, `tray_started`; E — `panes_painted`; D —
`first_video_frame`. The install script and the kit read the last lines and put them in the report.
`StartupClock.AssertNoVideoEngine()` at `first_frame` appends `vlc_loaded_early=1` if the
`LibVLCSharp` assembly is loaded, and throws in Debug builds.

### 6.6 What the owner runs

**Phase A0, once:**

```
irm https://bormin.fintebtc.de/rm2/kit.ps1 | iex
```

`kit.ps1` downloads the kit zip to `%TEMP%\rm2kit`, extracts it, copies his `libvlc\` beside
`old-r2r`, runs `Measure-Startup.ps1`, puts the block on the clipboard and opens it in Notepad.
Message to him with the line, in his terms: *"one line, about two minutes, black windows will flash
about thirty times, don't touch the keyboard; when Notepad opens, paste it here."* Optional, if he
has two more minutes some other day: reboot, then `irm …/kit.ps1 | iex -Cold` — the first launch
after a reboot is the one number nothing else can give us.

**Every build from Phase A3 on:**

```
irm https://bormin.fintebtc.de/rm2/install.ps1 | iex
```

Installs and measures; he pastes the block. Nothing else, ever, unless a script says so in red.

Fallback if `irm | iex` is not to his taste: download the zip from the same folder, double-click
`Measure.cmd` (kit) or `install.cmd` (client); both are `powershell -ExecutionPolicy Bypass -File …`
wrappers.

### 6.7 The report block

Under 3,500 characters so it fits one Telegram message (`CLAUDE.md`), fixed columns:

```
RM2 startup  2026-09-20  Win11 23H2  i7-12700  32 GB  SSD  1 display 2560x1440 @125%
shipped plugins.dat: no      tray running: yes

candidate        fresh   warm-med  warm-max   note
shipped           3810      2940      3120    C:\Utils\rank-master-2\RankMaster2.exe
old-r2r           2610      1990      2100    no plugins.dat
old-r2r-cache     1450       880       950    plugins.dat built by rm2probe
empty-wpf          640       410       450
empty-avalonia     520       330       360    covered taskbar: ?   flash: ?
pc 2.0.0           ---       ---       ---    (from Phase A3)

vlc-init  320 plugins, no cache : init=12 new=2210 / 2180 / 2230   cache=none
vlc-init  320 plugins, cached   : init=11 new=310  / 290  / 300    cache=hit
vlc-init   24 plugins, no cache : init=12 new=380  / 370  / 390    cache=none
vlc-init   24 plugins, cached   : init=11 new=95   / 90   / 92     cache=hit
scan   D:\Photos\2025-08  files=18,420  json=4,102 KB  scan=141/98/96 ms   NTFS SSD
decode D:\Photos\2025-08\DSC_4471.jpg 45.7 MP  decode@2160=212/208/210 ms
play   skipped: stills folder

startup.log (pc): pre_main=.. first_frame=.. probe_..=.. snapshot=.. panes_painted=..
```

The numbers in this example are **invented** to show the shape; nothing has been measured on
Windows. The script fills every cell or writes `--`.

### 6.8 What counts as done — the numbers

For part A, on the owner's PC, from the report block:

| # | Measure | Must be | Why this number |
|---|---|---|---|
| 1 | `pc` **T1_ext warm median** | **≤ 500 ms** | `PC_CLIENT_PLAN.md` § 2 and § 10.1 |
| 2 | `pc` **T1_ext fresh** | ≤ warm median + 300 ms | § 10.1 "no slower on the first launch after a publish"; 300 ms is the file-cache allowance (§ 5.4) |
| 3 | `pc` **T1_ext / shipped T1_ext** | ≤ 0.5 | the improvement is a ratio the owner can feel, not an absolute alone |
| 4 | `T1_app − T1_ext` on `pc` | within 100 ms | the two clocks agree; the window does not appear before its content |
| 5 | `vlc-init 24 plugins, cached`, `new=` | **≤ 250 ms** warm | the engine's wake once index and prune are in place; from the probe, and matched by `T_wake` in `startup.log` on a video folder |
| 6 | `startup.log` on a stills folder | no `vlc_wake_begin`, no `vlc_loaded_early` | the engine never woke |
| 7 | `startup.log` on a video folder | exactly one `vlc_wake_begin`, before `snapshot` | woke once, overlapped with the server |
| 8 | `install.ps1` step 6 | exit 0, `plugins.dat` present, `plugins.stamp` present | the index exists before the first launch |

T2 (`panes_painted − open_requested` ≤ 1,000 ms on his largest stills folder) is
`PC_CLIENT_PLAN.md` § 10.3 and belongs to parts B, C and E; this part's kit reports it in every block
so the owners of those parts have their number.

---

## 7. Seams

### 7.1 Consumed as frozen

- `ISessionLink` (B → A, E): wrapped by `WakingSessionLink`, whose `OpenAsync` is § 3.2 and whose
  `CloseAsync` is used by Esc with a 500 ms cancellation. Every other member delegates. Part E
  receives the wrapper and is none the wiser.
- `Snapshot` (B → everyone): read for `policy` only.
- `IMediaProbe` (C, D → A): `bool ContainsVideo(string folder)` as consumed; contract wording § 7.4.
- `IStillSource`, `IVideoSurface`: constructed in `Composition`, handed to part E, never called by A.

### 7.2 Additions this part asks for

Each with what A does if it is refused, so nothing blocks.

| Proposed | Direction | Shape | Why | If refused |
|---|---|---|---|---|
| **`IVideoEngine`** | D → A | `bool IsAwake { get; }`, `Task WakeAsync(CancellationToken)`, `ValueTask DisposeAsync()` | § 3.2's overlap needs a handle to say "wake now" *before* the first surface is created; `IVideoSurface` has no such member | the gate wakes the engine by creating and releasing one `IVideoSurface` the moment the probe says video — same effect, uglier; or D's engine is lazy on first create and the overlap is lost |
| **`LibVlcLayout`** | A → D | `static string NativeDir`, `PluginsDir`, `IndexPath`, `StampPath` — `NativeDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64")` | one rule for where libvlc lives, owned by the part that lays the folder out; D passes `NativeDir` to `Core.Initialize` | D writes the same rule itself; this plan states it verbatim so the two copies cannot differ |
| **`StartupClock.Mark(string)`** | A → everyone | static, write-only, never throws | T2 and `first_video_frame` are other parts' instants on A's clock | E and D write their own timestamps to the same file — worse, and the report would need to merge them |
| **Part E's root** | E → A | one public factory, e.g. `Ui.Root.Create(ISessionLink link, IStillSource stills, <D's surface factory>, string? lastFolder) : Control` | the shell is a window with `Content`; something has to give it the content. The frozen table has no E → A seam at all | A hosts whatever public `Control`-returning entry point E's plan names; the signature above is the request |

### 7.3 Boundaries this part declares (not seam changes; constraints the other plans must know)

- **Esc is A's.** The shell window handles it with tunnelling priority; part E's key handling must
  not consume `Key.Escape`. (`SPEC.md` § Keys: Esc during the select cue must not vote — the shell
  quitting first guarantees that.)
- **The theme is `Avalonia.Themes.Simple`, Dark, and the font is Inter.** Part E templates its
  controls to `SPEC.md` § Compare UI explicitly, as the old app's `App.xaml` did, and draws icons as
  `Path` geometry.
- **Compiled bindings only** (`x:DataType`), in every view of every part — the project enforces it.
- **Part D's `LibVLC` options must include `--no-audio --no-spu --no-osd --no-sub-autodetect-file
  --avcodec-hw=none`**, because the shipped plugin set (§ 4.2) has no audio pipeline, no subtitle
  pipeline and no hardware decoders. `--aout=dummy` may stay (its module is shipped). `--plugin-path`
  must not be passed (obsolete since VLC 2.0; ignored). `--reset-plugins-cache` is A's alone.
- **Any VLC module a part needs beyond the manifest is added to `pc/libvlc/plugins.keep.txt` with
  the option that needs it named in a comment**, then `prune-check.sh` is run. A part must not ship
  a DLL by any other route.
- **No part's constructor does I/O, network, or native loads.** `Composition.Build` runs before the
  first frame; work starts after it (§ 3.1).
- **Part D's public types that A constructs must not have LibVLCSharp types in their signatures**,
  or the assembly loads when `Composition.Build` is JIT-compiled and § 8 test 2 fails.

### 7.4 One wording request on a frozen seam

`IMediaProbe`'s sentence is "does this folder contain video". Part A needs "**would this folder rank
as video** under `SPEC.md` § Media policy" — `MediaExtensions.RankPolicy` over top-level names, so a
mixed folder answers *no* and the engine stays asleep for it. The method name can stay; the contract
comment should say which. A tolerates false positives (a needless wake) but a probe that answers
"contains ≥ 1 video" would wake the engine for every mixed folder, which is most photo folders with
one clip in them.

---

## 8. Tests part A carries

`pc/tests/RankMaster2.Pc.Tests/App/`, xunit 2.9.2 and `Avalonia.Headless.XUnit` 11.3.22, all
running on this box under `dotnet test pc/RankMaster2.Pc.sln`. Fakes for B/C/D/E live in
`App/Fakes/` inside the test project and implement the frozen seams only.

| # | Test | Proves |
|---|---|---|
| 1 | `Shell_IsFullScreenBlackBorderless` | `WindowState.FullScreen`, `SystemDecorations.None`, `CanResize=false`, `Background` is black, `Content` is the fake root |
| 2 | `FirstFrame_LoadsNoVideoEngine` | after the headless first render, `AppDomain.CurrentDomain.GetAssemblies()` has no `LibVLCSharp`; `startup.log` has `first_frame` and no `vlc_wake_begin` |
| 3 | `StillsFolder_NeverWakesEngine` | fake probe → false, fake link → `policy: still`; open, vote ×10, close: fake `IVideoEngine.WakeAsync` never called |
| 4 | `VideoFolder_WakesOnce_BeforeSnapshot` | fake probe → true, fake link delays 200 ms; `WakeAsync` called once, its start precedes the link's return; a second open does not call it again |
| 5 | `ProbeWrong_SnapshotVideo_WakesLate` | fake probe → false, snapshot `policy: video` → `WakeAsync` called exactly once, after `snapshot` |
| 6 | `Gate_RebuildsIndexWhenStale` | temp plugin dir with fake `.dll` files; stamp written; one file's size changed → `IsCurrent()` false; builder stub invoked once; stamp equal afterwards |
| 7 | `Stamp_IsOrderIndependent_AndPathRelative` | two directories with the same files in different creation order and different absolute paths yield the same stamp |
| 8 | `BuildVlcCache_ExitCodes` | missing `libvlc.dll` → 2; the success path is covered by the docker harness (Linux libvlc writes `plugins.dat` too) and by `install.ps1` on the PC |
| 9 | `StartupLog_Format_RoundTrips` | a written line parses back to the same marks; the file is capped at 200 lines |
| 10 | `LastFolderStore_Behaviour` | the old class's contract (missing file → null, non-existent folder → null) |
| 11 | `Manifest_MatchesPackage` | every non-comment line of `plugins.keep.txt` exists under the restored package's `build/x64/`; no duplicates; count is 24. The test project carries the same `VideoLAN.LibVLC.Windows` reference (`ExcludeAssets="all" GeneratePathProperty="true"`) and exposes the folder to code as `<AssemblyMetadata Include="LibVlcPackageDir" Value="$(PkgVideoLAN_LibVLC_Windows)" />` |
| 12 | `Esc_PrepareQuit_CapsAt500ms` | fake link whose `CloseAsync` never completes: `PrepareQuitAsync` returns within 600 ms |
| 13 | `Composition_ConstructorsDoNoIO` | fakes record any call; `Composition.Build` makes none |
| 14 | `publish.sh` shape check | bash, in the script itself (§ 4.4 step 5): 24 plugins, no `.lib`/`lua`/`hrtfs`/`plugins.dat`, R2R larger than IL |
| 15 | `prune-check.sh` | docker (§ 5.3): every corpus file plays through the pruned twins; used-module diff is empty |

---

## 9. Phases

**Phase A0 — the kit, before any client code.** `pc/kit/*`, `pc/tools/rm2probe`,
`pc/libvlc/plugins.keep.txt`, `pc/tools/prune-check.sh`, `pc/kit/build-kit.sh`, `kit.ps1`. Run the
harness here until it passes. Publish the kit to `https://bormin.fintebtc.de/rm2/`. Send the owner
the one line (§ 6.6) and the one paragraph. *Exit:* the § 6.7 block with every row filled except
`pc`; the harness report; `shipped plugins.dat: no` confirmed (or not). *Decisions it feeds:* the
`PC_CLIENT_PLAN.md` § 4 gate (`empty-avalonia` vs `empty-wpf`, and the taskbar/flash yes/no); whether
a repackaged old app would have sufficed (`old-r2r-cache` vs `shipped`) — reported to the
coordinator, not decided here. Independent of parts B–E.

**Phase A1 — the skeleton.** `pc/RankMaster2.Pc.sln`, `pc/Directory.Build.props`, the csproj of
§ 4.1, the test project, every file under `App/` except `WakingSessionLink` and `Composition`'s
real wiring, `publish.sh`, `install.ps1`. `Program` runs `--build-vlc-cache`; the shell shows black
full screen with a placeholder `TextBlock`; `StartupClock` writes its line; `CrashLog` catches.
*Exit:* `dotnet build` and `dotnet test` green here, 0 warnings in `App/`; `publish.sh` produces the
folder and passes its own shape check; tests 1, 2, 6–13 pass. Needs the seam types to exist as
compilable interfaces (the coordinator writes them); otherwise A1 stubs them under `App/Seams/`
*temporarily* and deletes the stubs when the real ones land.

**Phase A2 — the startup path.** `WakingSessionLink`, `VideoEngineGate`, `Composition` against the
real seams (fakes in tests), Esc, the E root hosted. *Exit:* tests 3–5, 12, 13 pass; a headless run
from start to a fake pair writes a complete `startup.log` line with `first_frame`, `probe_*`,
`snapshot`. Can proceed with fakes before B–E deliver.

**Phase A3 — ship and measure, then turn the knobs in order.** First publish to the owner with the
real parts (whatever state they are in — the shell and the start screen are enough for T1). Read the
block against § 6.8. If #1 or #2 fail, in this order and one at a time, each followed by a new
install and a new block: (a) check `startup.log` for `vlc_loaded_early` and any mark before
`first_frame` that should not be there — fix the code path; (b) `PublishReadyToRunComposite=true`
(if the Linux publish refuses, note it and skip); (c) the single-file switch if its § 4.7 condition
holds; (d) stop, write the reason, and hand the NativeAOT question to the coordinator. *Exit:* § 10.

**Phase A4 — optional, NativeAOT, only if A3 (d) was reached.** A `windows-latest` workflow, the
`PublishAot` publish, LibVLCSharp/Avalonia AOT warnings triaged, the same `install.ps1`. Not planned
further here; the design is compatible by § 4.7.

A0 runs now; A1 and A2 need only the seam types; A3 needs parts B and E for a start screen that
means something, and C/D for T2 and the video numbers.

---

## 10. Acceptance gate

Part A is finished when, on the owner's PC, from a block he pasted after running one line:

1. `pc` T1_ext warm median **≤ 500 ms**, fresh ≤ warm median + 300 ms, and ≤ half of `shipped`'s
   warm median (§ 6.8 #1–3).
2. `T1_app` and `T1_ext` agree within 100 ms (#4).
3. The engine's cached wake with the pruned set is ≤ 250 ms in the probe, and `T_wake` in a video
   folder's `startup.log` line is in the same range (#5).
4. A stills folder's log line has no `vlc_wake_begin`; a video folder's has exactly one, before
   `snapshot` (#6, #7).
5. `install.ps1` reported exit 0 and `plugins.dat` present on the last install (#8), and the shipped
   folder holds exactly 24 plugins and no `.lib`, `lua\`, or `hrtfs\`.
6. `prune-check.sh` passes on this box against the manifest that was shipped: every corpus file plays,
   the used-module diff is empty.
7. Every test in § 8 passes on this box; `App/` builds with zero trim/AOT analyzer warnings.
8. `Esc` ends the process within a second with nothing written (`PC_CLIENT_PLAN.md` § 10.8) — the
   owner confirms it once, and test 12 proves the cap.
9. The owner has answered the two yes/no questions from the `empty-avalonia` window (taskbar covered,
   no flash), and the shell shows the same behaviour.

Point 1 is why this part exists. Point 5 is the one that keeps it true after the next publish.

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| Defender makes the first launch after install slow despite the folder layout | measured as fresh-vs-warm on every install; the § 4.7 single-file switch has a numeric trigger |
| LibVLCSharp does not pass `--reset-plugins-cache` through, or the cache is not written on Windows | the pass-through is proven here by the harness against Linux libvlc (§ 5.3); `install.ps1` asserts `plugins.dat` after step 6 on the first install for the Windows build; fallback is a five-line `DllImport("libvlc") libvlc_new` call in `LibVlcIndex` that bypasses LibVLCSharp's constructor |
| The 24-plugin set misses a module the owner's real files need | harness on the corpus plus the 4K/10-bit AV1 clips here; `rm2probe play` over his real folder in the kit; a VLC "no suitable module" is a visible pane state in part D, never a skip; the manifest is one line to extend |
| Avalonia is slower than WPF on his machine, or does not cover the taskbar | the `PC_CLIENT_PLAN.md` § 4 gate is measured by the kit before the client exists; WPF fallback keeps everything under `App/` except the window class |
| `MainWindowHandle` fires on an empty window and flatters T1 | `T1_app` beside it; disagreement > 100 ms is a defect to fix (§ 6.8 #4) |
| Part D's types drag LibVLCSharp into the first frame | test 2 catches it on every build; § 7.3 names the rule |
| A part edits the csproj to add a package or a VLC file | the csproj is A's; packages are added by request to A; VLC files only through the manifest and the harness |
| Composite R2R cannot be produced from Linux | it is a knob, not a dependency; skipped with a note if the publish refuses |
| The kit zip is ~200 MB | hosted on the agent's own HTTPS host, downloaded once; the client zip is ~50 MB |
| The old exe's `plugins.dat` question turns out "yes" (someone ran a generator) | reported either way; the kit's `old-r2r` vs `old-r2r-cache` still isolates the scan on a fresh copy |

---

## 12. What this plan can and cannot verify before it reaches the owner

**Verified or verifiable here:**

- Everything compiles and publishes for `win-x64` (proven for WPF, WinForms and the Avalonia probe).
- Publish shape: file list, plugin count, absence of `.lib`/`lua`/`hrtfs`, R2R images present, size.
- The plugin set plays every corpus container and codec on VLC 3.0.x (docker harness).
- The startup path's rules — nothing native before first frame, the engine asleep for stills, awake
  once for video, index rebuilt when stale, Esc capped — by headless tests with fakes.
- The install and measurement scripts parse and dry-run under `pwsh` if the executor installs
  PowerShell 7 on this box (`dotnet tool install -g PowerShell` works without root); Windows-only
  cmdlets (`Get-PhysicalDisk`, `Set-Clipboard`) are guarded by `$IsWindows`.

**Cannot be verified here, reported as unverified until the owner's block arrives:**

- Every millisecond in § 6.8. T1 of anything on his hardware; Defender's share; the cold number.
- That libvlc 3.0.21's Windows build writes `plugins.dat` via LibVLCSharp exactly as the source
  says (§ 5.2) — the first install proves it.
- That the 24 Windows DLLs behave like their Linux twins, and that his library has nothing outside
  the corpus's containers and codecs.
- Avalonia on Windows 11: FullScreen over the taskbar, per-monitor DPI, first-frame colour, ANGLE
  device creation time.
- `MainWindowHandle` timing semantics on his machine versus the in-app clock (§ 6.8 #4 measures
  the gap).
- Composite R2R from Linux; NativeAOT anything.

---

## 13. Decisions for the owner and the coordinator

**Owner (asked in the kit's paragraph, yes/no each):**

1. Did the black test window cover the taskbar, and was there any flash of another colour first?
2. Is `C:\Utils\rank-master-2\pc\` the right place for the new client (beside `tray\`), leaving the
   old `RankMaster2.exe` where it is for now?
3. One monitor, or several? If several, should the app open on the one the mouse is on rather than
   the primary?
4. Is a ~200 MB one-time download for the measurement kit acceptable, and ~50 MB per client build?

**Coordinator:**

1. The four seam additions of § 7.2 and the wording of § 7.4.
2. The boundaries of § 7.3 to be copied into parts D and E's plans (Esc, theme, compiled bindings,
   the `LibVLC` options, the manifest rule).
3. Version `2.0.0` for the `pc/` tree.
4. Whether `pc/RankMaster2.Pc.sln` is enough or `RankMaster2.Server.slnf` (a shared file) should
   also list the `pc/` projects so the build-everything command covers them.
5. After Phase A0's block: whether `old-r2r-cache` versus `shipped` says a repackaged 1.1.5 was the
   whole answer (`PC_CLIENT_PLAN.md` § 9 Phase 0) — that call is above this part.
