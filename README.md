# Taskbar Spatial Order

A [Windhawk](https://windhawk.net/) mod that keeps each app's windows on the
Windows 11 taskbar in the order they sit on your screens, left to right, then
top to bottom, rather than roughly the order they were opened. Hover over an
app's taskbar button and its leftmost window is the leftmost thumbnail;
uncombined buttons follow the same order.

The order follows as windows move, are minimised or restored, open or close.
The mod waits until things have been still for a moment, and while a window is
dragged or the Start menu, search, notifications or a jump list is open.

## Install

From Windhawk's mod catalogue, once it has been approved there. Until then:

1. In Windhawk, choose **Create a new mod**.
2. Replace the sample code with the contents of
   [`taskbar-spatial-order.wh.cpp`](taskbar-spatial-order.wh.cpp).
3. Choose **Compile Mod**, then **Exit editing mode**.

On Windhawk 2.0 this is one command:
`windhawk-cli mod install --file taskbar-spatial-order.wh.cpp`.

A copy installed this way gets no update offers. Once the mod is in the
catalogue, install it from there and remove this copy.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| Minimised windows last | On | Puts minimised windows after the visible ones. Either way, a minimised window is placed by its restored position: where it sits when not minimised, maximised or snapped. |
| Skip groups where every window is minimised | On | Leaves such a group's order alone. |
| Excluded apps | None | Executable names, for example `mspaint.exe`, whose groups are never reordered. Store apps whose windows belong to `ApplicationFrameHost.exe` can only be excluded together, by that name. |

## Limitations

- Windows 11's current taskbar only. Tested on build 26300. The mod relies on
  internal taskbar functions found through Windows debug symbols, which an
  update can change; if one is missing or looks unexpected, the mod logs it
  and does nothing.
- A button that moves is taken off the taskbar and put back, which can show as
  a brief animation. Only buttons that are out of place move, and they keep
  their progress bar, overlay badge and thumbnail toolbar buttons.
- The mod's order wins: a window moved to another place in its group, by hand
  or by another mod, goes back at the next sort.

## How it works

Explorer keeps one item list per app, and every taskbar builds that app's
buttons and thumbnails from it, in that order. The mod sorts the item list by
the left edge of each window's visible frame, then the top edge, then the
window handle, so that two windows in the same spot always come out in the
same order. It then takes each button that is out of place off the taskbar
and puts it straight back, which makes the taskbar place the button, and its
thumbnail, by the sorted list.

## Credits

The move-and-refresh technique and the code that finds the taskbar's lists are
adapted from [Taskbar Thumbnail Reorder][reorder] by
[Michael Maltsev (m417z)][m417z], including repositioning several thumbnails in
one move from his [development version][dev]. Smaller parts come from his
[Cycle taskbar buttons with mouse wheel][wheel] and
[Taskbar Labels for Windows 11][labels]. The header of
`taskbar-spatial-order.wh.cpp` lists what came from where.

[reorder]: https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-thumbnail-reorder.wh.cpp
[m417z]: https://github.com/m417z
[dev]: https://github.com/m417z/my-windhawk-mods/commit/935c372e3de33982b3c0dfb2c6871ef88158545e
[wheel]: https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-wheel-cycle.wh.cpp
[labels]: https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-labels.wh.cpp

## Licence

[GNU General Public License v3.0](LICENSE), like the mods it builds on.
