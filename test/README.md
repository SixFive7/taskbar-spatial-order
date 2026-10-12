# The live test kit

This folder holds the tests of [Taskbar Spatial Order](../README.md): a
[logic harness](#the-logic-harness) that runs the mod against a model of the taskbar, and a live
test that drives it on a real Windows taskbar. A live run installs the mod through Windhawk, opens
windows of a test app of its own, moves them about the way a person would, and checks after every
action that the taskbar shows them in on-screen order. Why the mod works the way it does is in
[`../docs/design.md`](../docs/design.md).

Four witnesses are checked against each other, so that none of them has to be trusted alone:

- **Explorer's own lists**, read from its memory with `ReadProcessMemory`: per app the item list
  that orders the thumbnails, and per taskbar the button list. Nothing is written, nothing is
  injected. Each object is recognised by its class's vtable from Microsoft's public symbols for the
  `taskbar.dll` that is loaded, and each field is found at run time, so the reading follows Windows
  updates.
- **UI Automation**, which is what a screen reader sees: the taskbar buttons, and the thumbnails in
  an open flyout.
- **Images** of the taskbars and of the flyouts, read by colour.
- **The mod's own log**, captured from the debug output.

## What a live run changes on your PC

> [!WARNING]
> A live run takes over the PC for several minutes, about seven with all the steps. Do not touch
> the mouse or the keyboard while it runs, and close what you are working on first. Everything below
> is put back at the end; the next section explains what happens if a run is interrupted.

- **Windhawk is upgraded to 2.0.0-alpha.7 and back.** The 2.0 alpha brings `windhawk-cli`, which
  the run needs. Step 1 records the version you have and fetches its offline installer first; step
  14 uninstalls the alpha (keeping your mods and settings) and installs your version again, after a
  backup to `.work\test\windhawk`. The kit upgrades a 1.x release, or runs on the alpha itself.
- **The mod is installed from this repository** as the local mod `local@taskbar-spatial-order`,
  with its log on for the run. It stays installed and enabled afterwards, with its default
  settings: steps 6 and 11 change them and step 15 sets the defaults again. While it is enabled it
  sorts the taskbar groups of all your apps, which is what the mod is for.
- **The taskbar grouping** (`TaskbarGlomLevel` and `MMTaskbarGlomLevel` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`) is set to "never combine" in
  steps 7 and 13, then put back and read back to check.
- **Explorer is restarted** once, in step 11. Open File Explorer windows close, and every app's
  progress bars, badges and thumbnail buttons on the taskbar are gone until the app sets them again.
- **A temporary virtual desktop** is added with Ctrl+Win+D for the README demo (step 16) and closed
  with Ctrl+Win+F4. Step 10 switches to a neighbouring desktop and back.
- **The mouse, the keyboard and the focus are taken over.** The cursor hovers, drags and circles,
  a right-click opens a jump list, and Win+Left, Ctrl+Win+arrows, Ctrl+Win+D, Ctrl+Win+F4 and Esc
  are pressed. The test windows come to the front. The cursor goes back where it was at the end.
- **The debug output is captured** while the run lasts, so a debug-output viewer such as
  DebugView cannot run at the same time.
- **Downloads**, on first use, into `.work\test`: the Windhawk installers from GitHub, checked by
  their SHA-256 and signature, and the public symbols of `taskbar.dll` from Microsoft's symbol
  server.

Your own windows are never moved, resized, closed or clicked. Every file the kit writes goes to
`.work\test` in the repository, which git ignores.

## How it protects your PC

- **The journal.** Before every change, the run writes what it is about to change into
  `journal.json` in its run folder: the original grouping values, the virtual desktops that existed,
  whether it changed the mod's settings or turned its log on, whether it started the log capture or
  upgraded Windhawk.
- **A safe stop.** The run stops safely when Explorer crashes, restarts (other than the installer's
  in steps 1 and 14 and the planned one in step 11) or stops responding for six seconds, when a file
  named `STOP` appears in the run folder, or on Ctrl+C. A safe stop disables the mod (or turns on
  Windhawk's safe mode if that fails), puts the grouping back and checks it, and closes the test
  windows.
- **The test app looks after itself.** It closes its windows as soon as the runner's process ends,
  after an hour in any case, and after four minutes without a command (the runner pings it every
  five seconds). When the runner has died, it also puts the grouping values back from the journal.
- **`-Recover`** undoes what an interrupted run left behind, from the journal of the last live run
  or of `-RunDir`: stray test app processes, the grouping, the temporary desktop, the current
  desktop, the log capture, the mod's settings and log, and a mod the demo left disabled (unless the
  run stopped because Explorer crashed or hung: then it stays off until you have looked). With
  `-DisableMod` it disables the mod as well. It does not touch the Windhawk version: run steps 14
  and 15 for that (`-Steps 14-15`).
- **Input goes only where it is meant to.** A click is sent only after checking that the window
  under the point is the test window or the taskbar it is meant for. Keys that go to a window
  (Win+Left, Esc) are sent only while the test window or the jump list has the focus; the virtual
  desktop shortcuts go to Windows itself. The test app only acts on windows it created, whose
  titles start with `TSO-TEST` and which carry an app id of their own.
- **If Explorer keeps crashing** with the mod enabled, the header of
  [`windhawk/windhawk.ps1`](windhawk/windhawk.ps1) has a panic procedure: Windhawk's safe mode,
  from its tray window, from any PowerShell or from Task Manager.

## What you need

- **Windows 11 24H2 or later, with its own taskbar** (not a replacement shell). The kit was
  developed on build 26300.
- **[Windhawk](https://windhawk.net/) 1.x**, installed for all users in `Program Files\Windhawk`.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** to build the kit, and
  **PowerShell 7.6 or later** to run it: the runner is a .NET 10 library that PowerShell loads.
- **An elevated PowerShell** (Run as administrator) for a live run, because it changes Windhawk.
  `-Recover` needs one too when it has to put the mod's settings, log or state back; nothing else
  in the kit does.
- **[ffmpeg](https://ffmpeg.org/)**, with `ffprobe`, on the PATH, for `-Record` and the GIFs.
- **Internet access** the first time the kit reads Explorer's lists (a dry run, a live run or the
  oracle check) and on the first live run, for the downloads above.

The steps assume a setup that some PCs do not have:

| Step | Assumes | Otherwise |
| --- | --- | --- |
| 6, 7, 9, 10, 11, 14 | a primary monitor more than 2,200 pixels wide, so that a 640-pixel test window can be moved to the right of four others | the layout checks fail |
| 3, 9, 16 | "Combine taskbar buttons and hide labels" set to **Always** (the default) | the test group has no single button to hover |
| 7 | a monitor to the right of the primary one | that part is skipped |
| 8 | a monitor on each side of the primary one, the taskbar on all displays and apps on the taskbar where their window is open | the step fails |
| 10 | at least two virtual desktops | the step fails |

Leave out what your PC cannot do, for example `-Steps 1-7,9-16`. Your PC's monitors, as the kit
sees them, are in the `detail.log` of every run, and `tso-live monitors` lists them.

## Building

```powershell
pwsh -NoProfile -File test\build.ps1
```

It builds three programs into `.work\test\bin`: the test app (`TsoTestApp\TsoTestApp.exe`), the
runner and the witnesses (`TsoLive\TsoLive.dll`), and the same from the command line with the
self-tests (`TsoLiveCli\tso-live.exe`). `Invoke-LiveTest.ps1` builds by itself when a source is
newer than the last build; `build.ps1 -Idle` builds at idle priority, for building while the PC is
in use.

`compile-mod.ps1` compiles the mod with Windhawk's own compiler, as Windhawk does but with all
warnings on, for x86-64 and ARM64, without installing it: a quick check before a live run.

## The logic harness

```powershell
pwsh -NoProfile -File test\harness\run-harness.ps1
```

[`harness/harness.cpp`](harness/harness.cpp) checks the mod's logic in about half a minute, with no
Explorer, no window and no input: it changes nothing on the PC and needs only Windhawk, for its
compiler. It compiles the mod in Windhawk's editing mode, where Windhawk's functions are stubs,
with the Windows functions the mod calls redirected to fakes, which answer for the harness's own
windows and taskbars and pass anything else on to Windows, read-only: Explorer's lists exist only
in the harness, and a thread of the harness stands in for the taskbar's. It checks:

- the fewest moves, on every order of up to eight buttons and random ones of up to 40; buttons and
  thumbnails ending in the item list's order on 50,000 random groups over one to three taskbars,
  without a group ever losing its last button; and, with a `TaskDestroyed` that does nothing,
  nothing moving, which is what that failure looks like;
- the moves stopping before they use what Explorer may have changed or freed meanwhile: an item
  list that shrank, a taskbar left with one button, a window joining or leaving a group;
- the sort key and the window positions, both settings, excluded apps and the groups left alone;
- whole passes over two taskbars found the way the mod finds real ones, the windows it tracks, and
  a pass stopping when a group changes;
- the worker thread: stopped right after it starts, also before it has a message queue, and a
  burst of group changes making one pass;
- the handoff: a sort runs on the taskbar's thread only in its message loop, never while it waits
  for a call it made; an open menu holds it back; the taskbar going away or the mod unloading calls
  a pending request off, and unloading waits for a running sort.

It prints a line per test and the total, also into `.work\test\harness\run.log`. The exit code is 0
when the harness builds without a warning and every check passes. `-ModPath` checks another copy of
the mod.

It can't check Explorer itself: that `TaskDestroyed` and `TaskInclusionChanged` still do what its
model of the taskbar does, that the mod finds its functions and lists in this build's
`taskbar.dll`, how window events, timers and hooks behave inside Explorer, or what the screen
shows. That takes a live run.

## Running

Start with a dry run. It goes through every step without changing anything: the test windows are
created hidden, no input is sent, nothing is written to the registry, Explorer is not restarted and
Windhawk is replaced by stubs. The witnesses run for real, since they only read.

```powershell
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -DryRun
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -ListSteps         # the steps and their estimates
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -PlanOnly -Steps 3-9
```

A live run, from an elevated PowerShell, with nothing else going on:

```powershell
pwsh -NoProfile -File test\Invoke-LiveTest.ps1                    # all sixteen steps
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -Steps 1-13,16     # and later -Steps 14-15
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -ModPath my-variant.wh.cpp -Steps 2-4,15
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -Recover           # after an interrupted run
```

A selection is a set of step numbers: the steps always run in the table's order, so step 16 runs
right after step 2. Every step sets up what it needs, so one can be run again after a fix. Steps 3
to 13 and 16 need the mod loaded; without step 2 they use the mod that is already installed.
`-GlomPrimaryOnly` changes the grouping on the primary taskbar only.

## The steps

| # | Step | What it does and checks |
| --- | --- | --- |
| 1 | `s01-upgrade-windhawk` | Records the installed Windhawk version and fetches its installer, then upgrades to the 2.0 alpha. Checks the version and that Explorer is alive. |
| 2 | `s02-install-mod-with-log` | Disables any other mod the kit installed in an earlier run (with `-ModPath`), installs the mod with its log on and enables it. Checks that the mod found the two lists where the witness reads them, that its log has nothing unexpected, that its DLL is in Explorer, that no app's group moved along the taskbar, and that every app's group is now in on-screen order. |
| 16 | `s16-readme-demo` | The README demo, on a temporary virtual desktop: four windows opened in a shuffled order with the mod off, the flyout hovered; then the mod on, the flyout hovered, a window dragged to the far left, the flyout hovered again. Writes `demo.json` for the GIFs. |
| 3 | `s03-open-right-to-left` | Opens windows A to D right to left; they must be sorted D, C, B, A, under one combined button, in the flyout as well. Sets each window's progress bar, badge and two thumbnail buttons. |
| 4 | `s04-drag-across` | Drags D by its title bar to the far left, with a pause halfway. Nothing may change during the drag, and exactly one reorder must follow 0.3 to 1 second after the release. |
| 5 | `s05-snap-maximise-restore` | Win+Left on D, a snap by the program, maximise and restore. |
| 6 | `s06-minimise-and-settings` | Minimises the windows one by one, then all of them, and switches each setting off and on, excluding the test app as well. |
| 7 | `s07-never-combine` | Grouping set to never combine: each window's own button must follow the order, the group must keep its place on the taskbar, and each button must keep its badge and progress bar (read from an image of the taskbar). Repeats it with two windows on the right-hand monitor. |
| 8 | `s08-three-monitors` | Spreads the windows over three monitors; every taskbar must show its own windows in order, with the right count. |
| 9 | `s09-flyout-and-jump-list` | Moves a window while the flyout is open, then while the jump list is open: nothing may move while the jump list is open, and the reorder must follow within 1.5 seconds of it closing. |
| 10 | `s10-open-close-identity-desktops` | Opens a window between two others, closes one, gives one another app id and back, moves two to another virtual desktop and back, switches desktops. |
| 11 | `s11-disable-enable-restart` | Disables the mod (nothing may be sorted), enables it, changes a setting, restarts Explorer: the new Explorer must load the mod and sort. |
| 12 | `s12-explorer-cpu` | Explorer's processor time while the mouse circles over a window, with the mod on, off and on again. |
| 13 | `s13-progress-badge-toolbar` | Progress bars, badges and thumbnail buttons must have survived every reorder of the run, and survive one more with grouping off, in the taskbar images and in UI Automation. |
| 14 | `s14-restore-windhawk-stable` | Windhawk back to the recorded version; the mod must still be installed, enabled, loaded and sorting. |
| 15 | `s15-clean-up` | Default settings, log off, no test app, no test windows, grouping and virtual desktop as before, nothing left running. |

The estimates add up to six and a half minutes for all the steps; `-PlanOnly` adds up a
selection. The first run also waits for the downloads.

## Reading the results

A run writes into `.work\test\runs\<date-time>` (`<date-time>-dry` for a dry run, or `-RunDir`):

| File | What is in it |
| --- | --- |
| `progress.log` | One line per event: `TOTAL eta=`, then `STEP i/N <name> START` and `STEP i/N <name> PASS\|FAIL <detail> took=` for each step, and `DONE pass= fail=` with `aborted=<reason>` after a safe stop. The same lines go to the console. |
| `detail.log` | Everything, with seconds since the start: one `CHECK <id> <name> PASS\|FAIL\|DRY <detail>` line per check, `INFO` lines, and each action as `ACT`. |
| `mod.log` | The mod's log. |
| `captures\` | Images of the taskbars and flyouts, and the flyouts as UI Automation saw them. |
| `journal.json` | What the run changed, for `-Recover`. |
| `demo.json` | The demo's scenes, for the GIFs. |
| `testapp.log`, `windhawk-stub-calls.log` | The test app's log; what the stubs were asked to do in a dry run. |

The exit code is 0 when every step passed, 1 when one failed, 2 when the run stopped early. A
failed step names its failed checks; `detail.log` has the full `CHECK` line and the actions around
it. In a dry run, `DRY` marks a check that needs a live run.

## The oracle check

```powershell
.work\test\bin\TsoLiveCli\tso-live.exe oracle-check
```

It reads Explorer's lists from its memory and compares them with UI Automation, on the taskbars as
they are now: the number of buttons on every taskbar, the number of windows behind every button,
and that every taskbar's buttons follow its app's item list. It also says where it found each field
in Explorer's objects, and how many groups are already in the mod's order. It changes nothing,
sends no input and can run at any time.

`tso-live` has more read-only tools: `probe` (Explorer's lists), `uia` (the taskbar buttons and an
open flyout), `explorer` (the shell process and its processor time), `capture <folder>` (images of
the taskbars), `jumplist-probe` (what the jump list detection sees), `monitors` and `demo-layout`.
`tso-live` without arguments lists them all.

## Rebuilding the README GIFs

From an elevated PowerShell, with ffmpeg installed:

```powershell
pwsh -NoProfile -File test\Invoke-LiveTest.ps1 -Steps 1,2,16,14,15 -Record
```

Step 16 switches the mod off and on with the 2.0 alpha's command line, so the run includes steps 1
and 2, and ends with 14 and 15 to put Windhawk back.

`-Record` films the primary monitor for the whole run with `rec\record.ps1`, then
`rec\make-gifs.ps1` cuts the two scenes of step 16 out of the film with `demo.json`, paints over the
other taskbar buttons in the picture, and writes `without.gif`, `with.gif` and a contact sheet to
the run folder's `gifs\`. Look at them, then copy the two GIFs to [`../images`](../images). The demo
plays on a temporary virtual desktop, so only the test windows are in the picture, but the film in
`recording\screen.mkv` shows whatever the primary monitor showed during the run.

ffmpeg encodes on the processor (libx264); `-Encoder h264_nvenc` or `-Encoder h264_amf` uses the
graphics card instead. A monitor in HDR mode is filmed brighter than it looks; `make-gifs.ps1`
measures that from points of known colour and undoes it.

## The kit's own tests

| Command | What it tests |
| --- | --- |
| `harness\run-harness.ps1` | The mod's logic against a model of the taskbar (see [The logic harness](#the-logic-harness)). |
| `tso-live selftest` | The logic, without windows: the sort rule, step selection, the log parser, the drag analysis, reading images by colour, the journal, crash detection, the demo's geometry. It writes and deletes a key `HKCU\Software\TsoLiveTestScratch-<id>` to test restoring registry values. |
| `tso-live testapp-hidden <TsoTestApp.exe> <folder>` | The test app's commands, with hidden windows. |
| `tso-live deadman <TsoTestApp.exe> <folder>` | The test app closing its windows when the runner dies, after its lifetime and when idle, and putting registry values back (in a scratch key). |
| `windhawk\Test-Windhawk.ps1` | `windhawk.ps1` against made-up `windhawk-cli` replies, in PowerShell's strict mode, and that the stubs match it. |
| `rec\Test-MakeGifs.ps1` | `make-gifs.ps1` on a made-up recording, brightened as an HDR capture would be. |
| `Invoke-LiveTest.ps1 -DryRun` | Every step, without changing anything. |

## How to observe the taskbar

The mod's whole problem is that the order of a group's thumbnails is not something Windows lets
anyone read or set. Verifying it is a research task of its own; this is what was learned, so that
it does not have to be found out again. It was gathered on Windows 11 25H2 and on build 26300. The
taskbar is XAML and changes between builds: treat window classes and capture behaviour as specific
to a version.

### Four ways to read a group's order

1. **Open the flyout and look at it.** The only way to see what a person sees. It needs the hover
   below. Up to roughly ten windows the flyout shows thumbnails; above that it turns into a **text
   list**, and anything that reads the images stops working. That is why order is tested with four
   windows, not with dozens.
2. **Turn grouping off and read the buttons, without touching the mouse.** Set
   `TaskbarGlomLevel` (and `MMTaskbarGlomLevel` for the other monitors' taskbars) under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` to `2`, never combine, then
   broadcast `WM_SETTINGCHANGE` with `"TraySettings"`. It applies at once, with no Explorer restart.
   Every window then has a button of its own, whose UI Automation `AutomationId` is literally
   `Window: 0x<window handle>`: exact identity, with no matching of titles and no screenshots. A
   combined button's is `Appid: <AppUserModelID>`. This reads the order of the **buttons**; the
   thumbnails come from a different list, the group's item list. They agreed every time they were
   compared, but that is not proof that they are the same, so say which one you read.
3. **Count the windows behind a button.** UI Automation names a combined button
   `"<App> - N running windows"`. That shows a window gone missing, the typical failure when
   something waits too short, without reading any order. Cheap and safe.
4. **Read Explorer's lists from its memory.** What this kit does: the item list of every app and
   the button list of every taskbar, read out of the process with `ReadProcessMemory`, with each
   object recognised by its class's vtable from Microsoft's public symbols. It reads both lists, so
   it does not depend on them agreeing.

**Grouping changes must be put back exactly.** Write the original values down before changing
anything. A value that was absent must be restored by deleting it, not by writing 0: put back
exactly what was there. Read the values back to check.

### Driving the hover

**`SetCursorPos` does not open a flyout.** It moves the cursor without putting anything into the
input queue, so the taskbar never hears that the pointer arrived and its hover timer never starts,
although `WindowFromPoint` confirms the cursor is over the taskbar. Use **injected input**
(`SendInput` with `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE`) for the whole way.

1. **Park well outside the taskbar first** (the kit uses 700 pixels above the button) and wait about
   700 ms. The hover only re-arms after a real mouse-leave: if the cursor already rests on the
   taskbar, nothing done afterwards opens the flyout. This one causes long runs of silent failures.
2. **Glide in** in small injected steps (30 steps of 18 ms), not in one jump.
3. **Wiggle on the button**: move by one pixel back and forth every 60 ms for as long as the flyout
   is being read. It keeps the hover alive.
4. **Put the cursor back** where it was.

The hover time is `SPI_GETMOUSEHOVERTIME`, 400 ms by default. No system file of the build where
this was checked refers to the registry value `ExtendedUIHoverTime` that older guides mention: do
not rely on it.

Find the button with UI Automation first: `Shell_TrayWnd` and every `Shell_SecondaryTrayWnd`, their
descendants of type Button with the class `Taskbar.TaskListButtonAutomationPeer`. The code is in
[`src/TsoLive/Input.cs`](src/TsoLive/Input.cs) and [`src/TsoLive/Uia.cs`](src/TsoLive/Uia.cs).

### Capturing the flyout

The flyout's window, `XamlExplorerHostIslandWindow`, is untitled, as tall as the screen, and
**created once and then only shown and hidden**. Looking for a new top-level window to find the
flyout therefore works the first time only. `Xaml_WindowedPopupClass` windows named `PopupHost` are
tooltips, not the flyout, and the classic thumbnail window `TaskListThumbnailWnd` no longer exists
in current builds.

What works: UI Automation finds the thumbnails, as `TaskItemThumbnailViewAutomationPeer`
elements in the shell's popup windows, with their thumbnail buttons as
`ThumbBarButtonAutomationPeer`; and a copy of the screen above the button
(`Graphics.CopyFromScreen`) shows them.

**Read the order by colour.** Give every test window a solid colour of its own, count the pixels
of the copy that are close to each of those colours, take the mean x of each colour and sort. It is
exact and needs no text recognition; the kit records it next to what UI Automation reports. It
stops working when the flyout turns into a text list.

### On a locked PC

What can and can't be read or driven while the workstation is locked:

| | while the PC is locked |
| --- | --- |
| Detecting the lock | `WTSQuerySessionInformation` with `WTSSessionInfoEx`: `SessionFlags` 0 means locked |
| The input desktop | still `Default`, not `Winlogon`: the lock screen is not the secure desktop |
| UI Automation on the taskbar | works: every button, with its name and rectangle |
| `SetCursorPos`, `SendInput` | work: the cursor moves and `GetLastInputInfo` advances |
| `Graphics.CopyFromScreen` | useless: it returns the lock screen |
| `PrintWindow` with `PW_RENDERFULLCONTENT` on a taskbar | returns the real taskbar, although it is hidden |
| DWM thumbnails | work |

**A hover is impossible while locked.** At every point of the taskbar, `WindowFromPoint` returns
the lock app's window on the primary monitor and an Explorer window that covers the lock screen on
the others: the input arrives, but it cannot reach a taskbar button. On a locked PC, read the order
without the flyout (the second, third or fourth way above); a live run needs an unlocked PC.

### Safety rules for test harnesses

- **Never touch a window you did not create.** Check both who owns a window and how it is named
  before acting on it, and stop before the first action if either does not match. The test app
  only acts on its own windows, and the runner only clicks after checking what is under the point.
- **Give whatever hosts test windows a dead-man switch.** It must close its windows when its parent
  dies, after a fixed time, and when it stops hearing from its parent, so that a killed run cannot
  leave windows on the taskbar. Make the silence it tolerates longer than anything the run does
  between two signs of life, or it will close the windows in the middle of a test and produce a
  confusing run of failures.
- **Write down what you change before you change it**, so that whatever stops the run can put it
  back: the run itself, the window host after the run has died, or a recovery afterwards.
- **Put things back in a `finally`**, and check them by reading them back.
- **Send keys only when you know where they go**: to the foreground window, and only when that is
  yours or the popup you opened.
- **Clean up every process and window you start, and check that they are gone**: step 15 checks for
  test windows, the test app and child processes left behind.

## The files

| Path | What it is |
| --- | --- |
| `Invoke-LiveTest.ps1` | The live test: build, `-ListSteps`, `-PlanOnly`, `-DryRun`, `-Record`, `-Recover`. |
| `build.ps1` | Builds the C# projects into `.work\test\bin`. |
| `compile-mod.ps1` | Compiles the mod with Windhawk's compiler, without installing it. |
| `harness/harness.cpp`, `harness/run-harness.ps1` | The logic harness, and the script that builds and runs it. |
| `windhawk/windhawk.ps1` | Everything the run does to Windhawk, through `windhawk-cli`, and the panic procedure. |
| `windhawk/windhawk-stub.ps1` | The same functions as stubs, for `-DryRun`. |
| `windhawk/Test-Windhawk.ps1` | Tests both without Windhawk. |
| `rec/record.ps1` | Films the primary monitor with ffmpeg. |
| `rec/make-gifs.ps1`, `rec/Test-MakeGifs.ps1` | Cuts the README GIFs out of the film, and its test. |
| `src/TsoTestApp` | The test app: windows with an app id of their own, run over a named pipe. |
| `src/TsoLive` | The runner (`Runner.cs`, `Steps.cs`, `Demo.cs`) and the witnesses: Explorer's lists (`ExplorerLists.cs`, `Symbols.cs`), UI Automation (`Uia.cs`), images (`Capture.cs`, `ButtonReader.cs`), the jump list (`JumpList.cs`), input (`Input.cs`). |
| `src/TsoLiveCli` | `tso-live`: the tools and the self-tests. |
| `src/Shared` | The journal, the test app's command protocol, Windows declarations. |
