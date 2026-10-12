# Design notes

Why Taskbar Spatial Order is built the way it is, for anyone maintaining or
forking it, say after a Windows update breaks it. The [README](../README.md)
covers what it does, its settings and limitations;
[test/README.md](../test/README.md) covers testing. The code,
[`taskbar-spatial-order.wh.cpp`](../taskbar-spatial-order.wh.cpp), is the
ground truth; names like `SortGroup` are its functions, names with a class
are Explorer's. Explorer's internals are described as observed on Windows 11
build 26300, x86-64. Much of the mod is adapted from [m417z][m417z]'s mods;
the README and the mod's header say what came from where.

## Three lists

- **The item list.** Each app's `CTaskGroup` holds a DPA of task items, one
  per window (`CWindowTaskItem`, or `CImmersiveTaskItem` for Store apps),
  shared by all taskbars. Explorer appends an item when it learns of a
  window, hence roughly the order the windows were opened.
  `CTaskGroup::AddTaskItem` can insert before an item, but the path that adds
  windows doesn't use that.
- **The button list.** On each taskbar, the app's `CTaskBtnGroup` holds its
  buttons there in item-list order: all of them, or, with buttons shown where
  the window is open, those on that monitor.
- **The XAML model.** The XAML front end's own list per group, from which it
  draws the buttons and the hover preview's thumbnails.

The last two follow the item list only when a button is added: it goes right
after the button of the closest earlier item that has one on that taskbar,
and its thumbnail to the same index. Reordering the item list alone changes
nothing on screen. So the mod reorders it, then takes each misplaced button
off with `CTaskListWnd::TaskDestroyed` and puts it back with
`CTaskListWnd::TaskInclusionChanged`; button and thumbnail land where the
list now says. Both calls take effect before they return, so moves need no
pause. The item stays in its group, so the window keeps its progress bar,
overlay badge and thumbnail toolbar buttons.

This move-and-refresh technique is from m417z's [Taskbar Thumbnail
Reorder][reorder], which takes the button off by calling
`TaskInclusionChanged` while a hook on `TaskItemFilter::IsTaskAllowed`
rejects the item. On 26300 that path ends in `TaskDestroyed`, so the mod
calls it directly, without the hook; [When an update breaks
it](#when-an-update-breaks-it) has the way back.

## Finding the lists

`HookTaskbarSymbols` loads `taskbar.dll` from System32 and resolves 17 names
from its debug symbols with `WindhawkUtils::HookSymbols`: five vtables, ten
functions it calls, and `CTaskGroup::AddTaskItem` and `RemoveTaskItem`, which
it hooks. Windhawk fetches Microsoft's public symbols and caches the result.

Two positions are found rather than hard-coded.
`CTaskListWnd::GetButtonGroupCount` and `CTaskGroup::GetNumItems` return the
count of a DPA kept in one pointer-sized slot of their object; `FindDpaSlot`,
m417z's technique, calls each on a fake object whose slot *n* points to the
number *n*, so it returns the slot it reads. On 26300 the result matches a
separate reading of Explorer's memory.

Each pass looks everything up afresh on the taskbar's thread. `FindTaskLists`
reaches each task list from `Shell_TrayWnd` through its `TaskbandHWND`
property, or from `Shell_SecondaryTrayWnd` through its `WorkerW` child.
`FindGroups` keeps button groups of type 1 (one button, or several
uncombined) and 3 (several combined), not 2 (a pinned app without windows).
Every object is checked against its class's vtable first. These lookups are
adapted from Taskbar Thumbnail Reorder, the walk over button groups from
[Cycle taskbar buttons with mouse wheel][wheel].

A missing name or an implausible slot fails `Wh_ModInit`, so the mod doesn't
load. At run time a wrong vtable abandons the pass; an item of unknown kind,
a window that's gone or a button outside the item list skips the group; a
taskbar without a task list is skipped; each is logged. Behaviour can't be
checked: a function can keep its name and change what it does,
`TaskDestroyed` above all. And `FindDpaSlot` assumes the getters only read a
pointer and a count; one that did more could crash on the fake object.

## The sort key

`SortKey` orders a group's windows by minimised or not, if *Minimised windows
last* is on (the default), then by left edge, top edge and window handle.

`WindowPosition` takes the top-left corner of the visible frame
(`DWMWA_EXTENDED_FRAME_BOUNDS`, without the invisible resize border, or
`GetWindowRect` if that fails). These virtual-screen coordinates already run
left to right across monitors, unlike monitor handles, so there's no monitor
term. A minimised window's rectangle is parked far off screen, so for it, and
any window on no monitor, the mod uses the restored rectangle from
`GetWindowPlacement`: where it sits when not minimised, maximised or snapped.

The top edge orders windows stacked on one monitor. The handle makes the
order total, so windows at the same spot always come out the same way;
without it ties fall arbitrarily, which made an earlier, unpublished
standalone version's order churn (see [Alternatives tried and
rejected](#alternatives-tried-and-rejected)).
Process IDs wouldn't do, as many apps open every window from one process, nor
titles, which change as people work. Coordinates are compared exactly, by
decision; bucketing the left edge would absorb near-ties if they ever flip.

Minimised windows take part, since one left out keeps the place Explorer gave
it, possibly first, for good. They go last by default as they aren't on
screen, and a group whose windows are all minimised is left alone by default:
there's nothing on screen to mirror.

## Moving only what's out of place

`SortGroup` writes the items into the item list's array in sorted order, if
needed, then calls `FixButtonOrder` per taskbar. That ranks each button by its
item's index, keeps a longest increasing subsequence of the ranks in place
(`RanksToMove`) and moves the rest: the fewest moves, each a visible rebuild.
They go one at a time, in list order, so each lands beside a button already in
place and the group always keeps one; a button group left with none is
rebuilt at the end of the taskbar, taking the app's button with it. Moving
several buttons in one pass, not a slot at a time as a drag does, follows
m417z's [development version][dev]. Checking every pass also repairs a
taskbar that was out of order, and a sorted group moves nothing.

## When to sort

The worker, a thread of the mod's own, schedules a pass on:

- **group changes:** the hooks on `CTaskGroup::AddTaskItem` and
  `RemoveTaskItem` bump a counter and wake it when a window opens, closes,
  changes group or is re-added by Explorer, which puts it last;
- **moves** of windows in groups of two or more at the last pass, the only
  ones that can change an order: system-wide, out-of-context WinEvent hooks
  for moves and resizes ending, minimising and restoring, and
  `EVENT_OBJECT_LOCATIONCHANGE`, since the move-size events cover only
  interactive moves, not snaps from the keyboard, window tools or apps moving
  their own windows;
- **uncloaking** of any window, as after a virtual desktop switch a window
  can reappear in a group that had no buttons;
- **start-up**.

Each restarts a 400 ms countdown, `kSettleMs`, so a burst, such as a drag or
Explorer adding every window at start-up, ends in one pass 0.4 s after the
last event. The standalone version chose 400 ms to bridge the pauses within a
drag, as 300 ms split some drags in two. Here a held mouse button counts as
busy, so the countdown only folds bursts into one pass, without making the
order lag. In live tests the order caught up 0.4 to 0.5 s after a window was
moved, snapped, minimised or restored, up to about 1 s after a settings
change, about 1.7 s after a desktop switch, and within five seconds of an
Explorer restart.

A pass moves no window and adds no item to a group, so it doesn't trigger the
next: in the live tests the group-change counter never moved during a pass,
even one with many moves. Explorer has no taskbar while it starts, nor in a
process that only shows folders; the worker then lets its timer lapse until
the next group change, and in a folder-only process never installs its
WinEvent hooks.

## Two threads

One thread owns every taskbar and its lists, and Explorer changes them there,
so the mod does too. The first version watched windows there as well, but
`EVENT_OBJECT_LOCATIONCHANGE` covers every move of the mouse pointer and an
out-of-context hook delivers each event to the thread that set it: the thread
that draws the taskbar woke for every pointer move only to drop it. The
worker now takes that load, as m417z's [Taskbar auto-hide when
maximized][autohide] does (no code is taken from it), and drops events that
aren't about a window before taking any lock.

The handoff is `SortOnTaskbarThread`. The worker hooks the taskbar's thread
with `WH_GETMESSAGE` and posts the taskbar window a `WM_NULL` carrying the
address of `g_request`. When that thread takes the message from its queue,
`RequestHook` claims the request, checks again that the user isn't busy,
sorts and signals. The worker waits in 50 ms steps, calling a still-pending
request off if the taskbar window goes away or the mod unloads, and waiting
out a started sort. Then it removes the hook, waits until no call of it is
running, and makes one `SendMessageTimeout` round trip (1 s limit), which
shows the taskbar's thread has left the hook.

Posted, not sent: a thread handles sent messages whenever it waits, even
inside its own `SendMessage`, perhaps halfway through changing its lists, but
takes posted ones only in its message loops, where its own timers fire too.
The first version sent the request, with a `WH_CALLWNDPROC` hook as m417z's
Taskbar Labels does, so a sort could have run in the middle of Explorer's own
work; the [harness](../test/harness/) shows a posted request waiting such a
call out. The hook exists only while a request is pending, and a late
`WM_NULL` is harmless.

Besides the request's atomics and event, the shared state is small: an atomic
group-change counter; the tracked windows, swapped in by each complete pass
under a mutex never held across a wait for the other thread; and the
settings, written only in `Wh_ModInit`, before the worker exists.

`Wh_ModUninit` sets `g_unloading`, posts `WM_QUIT` to the worker, retrying
until it has a message queue, and waits for it. Every request ends with the
taskbar's thread out of the mod's code, so nothing of the mod runs there
afterwards. When Explorer exits, `Wh_ModUninit` isn't called, but the globals
only free memory and the worker is a plain thread handle, so nothing blocks
or crashes. Disabling the mod leaves the order as last sorted.

## Waiting while the user is busy

`IsUserBusy` holds a pass back while the left or right mouse button is down
(dragging a window or a taskbar button, pressing a thumbnail); while the
foreground window is being moved or sized, also from the keyboard; while the
taskbar's thread has the mouse captured or is in a menu or a move; and while
the foreground window belongs to `ShellExperienceHost.exe` (jump lists,
notifications, the calendar), `StartMenuExperienceHost.exe` or
`SearchHost.exe`. Those popups are drawn by other processes, so the taskbar's
thread is idle while they're open; before they were listed, a sort closed an
open jump list. A process that can't be identified counts as not busy.

The worker checks before posting, and the taskbar's thread again on taking
the request, in case it has opened a menu since. While the user is busy the
timer keeps firing every 400 ms, so the pass runs at the first tick after. In
a live test, a two-second drag with a pause longer than the countdown moved
nothing until release; the sort followed 0.4 s later.

## Stopping when a group changes

`SortAllGroups` collects every group and button group before sorting any. If
Explorer handled a window joining or leaving a group mid-pass, objects the
pass holds could be freed. That could only happen inside the pass's calls
into the taskbar, if something in them waits and lets other work in; it was
never seen in testing, but nothing rules it out. So the counter the
`CTaskGroup` hooks bump is checked after each group, before each taskbar and
before each move; before a move, the button group must also still have two
buttons and the item list still be long enough. A change stops the group or
the pass with a log line and keeps the last complete pass's tracked windows;
the change has already scheduled another pass. The
[harness](../test/harness/) covers these stops.

## Exclusions and settings

The mod never decides which windows belong on the taskbar; it reorders items
and rebuilds buttons Explorer already has. The standalone version's rules for
leaving windows out (windows that hid their own button, ghost windows of hung
apps, tool windows, cloaked windows) have no counterpart, and an item without
a button on a taskbar, such as a window on another virtual desktop, isn't
touched there.

Left alone: groups of fewer than two items; pinned apps without windows;
groups with an item of unknown kind, a window that's gone or a button outside
the item list; groups with a window of an excluded app; and, by default,
groups whose windows are all minimised. *Excluded apps* matches the
executable name of each window's process, ignoring case, after trimming
spaces and tabs; the list ends at the first empty entry. Store apps' windows
belong to `ApplicationFrameHost.exe`, so they can only be excluded together;
a process that can't be identified isn't excluded. As the mod's order always
wins, excluding an app is how to keep an order of one's own for it. Settings
are read once, at load; changing one reloads the mod.

## Limitations accepted on purpose

Besides those in the [README](../README.md#limitations), three points from
the catalogue review were left as they are:

- **Hover previews update live.** An open preview sets no capture or menu
  mode and isn't drawn by a process the busy check knows, so a pass can run
  under it, say 400 ms after the hovered app opens a window. The thumbnails
  are rebuilt in place, in the right order at once. Counting a pointer over a
  taskbar or its preview as busy would change that.
- **Tested on build 26300 only.** All 17 names exist in 26200's `taskbar.dll`
  too, and 26100 shares the code base, but the mod hasn't run on either. No
  check can catch `TaskDestroyed` doing more than taking the button off.
- **No version check.** On 22H2 and 23H2 the mod loads if it finds its
  symbols. A check in `Wh_ModInit` would make the stated range hold; the mod
  relies on its symbol and vtable checks instead, and the README says older
  versions are untested.

A smaller one: a restored rectangle is in workspace coordinates, off from
screen coordinates by a taskbar or app bar at the top or left, which matters
only where such a window is compared with one placed by its frame: with
*Minimised windows last* off, or for a window on no monitor.

## When an update breaks it

Turn on the mod's logging in Windhawk: it gives the slots `FindDpaSlot`
found, each reordered group's window handles before and after, and each
taskbar's count of buttons moved.

- **The mod doesn't load:** a name is gone or a getter changed. The
  [Windhawk Symbol Helper][helper] lists the new names, and a `SYMBOL_HOOK`
  entry takes the first match among several, so a renamed function can go
  beside its old name.
- **`Unexpected` lines:** a class changed; compare its vtable and getters.
- **"moved N buttons" but nothing moves:** `TaskDestroyed` no longer takes
  the button off. Go back to m417z's path: hook
  `TaskItemFilter::IsTaskAllowed` to reject every item while a flag is set,
  call `TaskInclusionChanged` with it set, then clear it and call again. His
  mod also first moves the button within the button list; if buttons then
  come out right but thumbnails don't, the reject step didn't take.
- **Buttons vanish, an app's button jumps to the end, or Explorer restarts
  during a sort:** `TaskDestroyed` does more than it did. The filter path
  ends in the same call on 26300, so it helps only if the direct call is at
  fault.
- **A popup closes as a sort lands:** it has probably moved to a process the
  busy check doesn't list.

## Traps

Besides those above (the item list alone changes nothing on screen, an
emptied button group goes to the end, the shell's popups live in other
processes, sent messages arrive mid-work, minimised rectangles are off screen
and ties need a final key):

- Taskbar windows can be recreated while Explorer runs, so nothing is cached
  between passes; only the primary taskbar has `TaskbandHWND`.
- `PostThreadMessage` fails until the receiving thread has a message queue,
  so unloading retries.
- Explorer is per-monitor DPI aware (PerMonitorV2), so the mod reads real
  pixels; code reading positions from another process must be too.
- `taskbar.dll`'s version string was the same on 26200 and 26300 though the
  files differ, so it can't tell builds apart.

## Alternatives tried and rejected

**A standalone program**, the unpublished version that came before the mod,
reordered a group from outside Explorer by unregistering and re-registering
its windows with `ITaskbarList::DeleteTab` and `AddTab`, which amount to the
shell-hook messages a window's destruction and creation send the taskbar; it
sent those itself, with a timeout. Re-registering appends, so its only move
was to the end, and it couldn't read the current order. That cost:

- each window's overlay badge, progress bar, thumbnail toolbar buttons and
  thumbnail clip, destroyed on unregistering; Explorer posts
  `TaskbarButtonCreated` so apps can restore them, but not all do, and
  nothing outside can tell;
- a pause: Explorer removes an item asynchronously and drops a re-add that
  arrives first as a duplicate. Without a pause between the halves the order
  came out wrong every time; 25 ms or more gave it exactly (four windows, two
  runs per setting);
- one window kept registered, as an emptied group's button moves to the end
  of the taskbar;
- a journal, as a crash between the halves left windows without a button
  that a scan can't tell from apps hiding their own.

Inside Explorer the mod reorders the list itself, rebuilds only buttons and
keeps every window's state, at the price of depending on internal names an
update can change.

**Sorting just in time, on hover.** The preview opens after the system's
mouse hover time, 400 ms by default and 200 ms when moving between buttons
with a preview open; `ExtendedUIHoverTime` had no effect on the build
checked. Buttons start at the taskbar's top edge, so entering it gives no
warning, and a click often comes sooner. The standalone version's passes over
large groups could take longer. In the mod, uncombined buttons are always
visible and must be right before anyone looks, and a sort then rebuilds the
very button being aimed at.

**The first version's threading**, watching windows on the taskbar's thread
and sending it the request, was replaced; see [Two threads](#two-threads).

**Narrower window events.** In testing, a system-wide location hook got a few
hundred events a second while the pointer moved, nine in ten for the cursor,
and about a dozen at rest. A hook on one thread still got cursor events with
the pointer over its window, and narrow hooks would need renewing as windows
come and go. So the system-wide hook stayed, on the worker; with the pointer
moving nonstop, the lower of two samples with the mod on was within a few
milliseconds a second of Explorer's time with it off; the other was several
times higher, which the test puts down to Explorer's own work.

**APIs that look right:** `ITaskbarList3::SetTabOrder` orders tabs an app
registered under one window, not separate windows; `ITaskListUI`'s
`ReorderTaskGroup` and `SetRelativeTaskOrder` order whole apps.

**Identifiable thumbnails instead.** When an app's titles share a long
beginning, every caption reads alike, but titles are the app's business;
position works whatever they say. It would complement the mod, not replace
it.

**Ordering by hand** is what Taskbar Thumbnail Reorder offers; users who
order its thumbnails by monitor have asked for that order to be kept
([#1626][i1626]) and to survive a program's restart ([#479][i479]).

## What was verified

On Windows 11 build 26300, x86-64, with Windhawk 1.7.3 and 2.0.0-alpha.7;
[test/README.md](../test/README.md) says how to repeat it.

- **[Harness](../test/harness/):** compiled against a model of the taskbar
  and its rule for inserting buttons and thumbnails, the current code passes
  3,021,499 checks: moves in 50,000 random groups over one to three taskbars,
  the sort key, whole passes, the worker's life, the handoff against a
  stand-in thread (taken only in its message loop, never during a wait on a
  call; a menu found busy; the taskbar going away; unloading mid-request and
  mid-sort) and the stops when lists change mid-pass. With `TaskDestroyed`
  doing nothing, buttons and thumbnails keep their old order: that failure's
  signature.
- **Live**, with test windows of its own, reading Explorer's lists from
  memory as well as the screen: opening right to left, a drag, keyboard and
  programmatic snaps, maximise and restore, minimising under both settings,
  exclusion, uncombined buttons, taskbars on more than one monitor showing
  buttons where each window is, a sort under an open preview, a jump list
  waited out, windows opening, closing, leaving and rejoining a group,
  virtual desktop switches, disable, enable, settings changes, an Explorer
  restart, CPU use, and moved buttons keeping their progress bar, badge and
  toolbar buttons. No app's button jumped to the end, and nothing unexpected
  was logged.

Not verified: other builds, ARM64 (on ARM64 PCs Windhawk builds and loads
this mod as ARM64; the code compiles for it but has never run there) and
replacement taskbars. The last live run predates the final code change, the
stops before each taskbar and each move, which only the harness covers. In
the last full live run 11 of 14 steps passed: the README demo found no point
on its narrow window's title bar to drag it by (fixed in the kit since), and
one check couldn't find a moved button to read its badge, which also failed
step 13's summary; a separate focused check that moved a button and read its
state passed. The kit in `test/` was tidied after that run and has only been
dry-run since.

[m417z]: https://github.com/m417z
[reorder]: https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-thumbnail-reorder.wh.cpp
[dev]: https://github.com/m417z/my-windhawk-mods/commit/935c372e3de33982b3c0dfb2c6871ef88158545e
[wheel]: https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-wheel-cycle.wh.cpp
[autohide]: https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-auto-hide-when-maximized.wh.cpp
[helper]: https://github.com/ramensoftware/windhawk-symbol-helper/releases/latest
[i1626]: https://github.com/ramensoftware/windhawk-mods/issues/1626
[i479]: https://github.com/ramensoftware/windhawk-mods/issues/479
