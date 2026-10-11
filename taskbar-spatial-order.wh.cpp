// ==WindhawkMod==
// @id              taskbar-spatial-order
// @name            Taskbar Spatial Order
// @description     Keeps each app's windows on the taskbar in the order they sit on screen, left to right, in thumbnail previews and uncombined buttons
// @version         1.0
// @author          Jori Huisman
// @github          https://github.com/SixFive7
// @include         explorer.exe
// @architecture    x86-64
// @license         GPL-3.0
// ==/WindhawkMod==

// Taskbar Spatial Order
// Copyright (C) 2026 Jori Huisman
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License version 3 as published by
// the Free Software Foundation. This program is distributed WITHOUT ANY
// WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR
// A PARTICULAR PURPOSE. See https://www.gnu.org/licenses/gpl-3.0.html for the
// full licence.

// ==WindhawkModReadme==
/*
# Taskbar Spatial Order

Keeps each app's windows on the taskbar in the order they sit on your screens,
left to right, then top to bottom, rather than roughly the order they were
opened. Hover over an app's taskbar button and its leftmost window is the
leftmost thumbnail; uncombined buttons follow the same order.

The order follows as windows move, are minimised or restored, open or close.
The mod waits until things have been still for a moment and never sorts while
a window is being dragged.

By default, minimised windows go last and a group whose windows are all
minimised is left alone. The settings can change both and exclude apps.
*/
// ==/WindhawkModReadme==

// ==WindhawkModSettings==
/*
- minimisedLast: true
  $name: Minimised windows last
  $description: >-
    Put minimised windows after the visible ones. Either way, a minimised
    window is placed by its restored position: where it sits when not
    minimised, maximised or snapped.
- skipFullyMinimisedGroups: true
  $name: Skip groups where every window is minimised
- excludedApps: [""]
  $name: Excluded apps
  $description: >-
    Executable names, for example mspaint.exe, whose groups are never
    reordered. Store apps whose windows belong to ApplicationFrameHost.exe can
    only be excluded together, by that name.
*/
// ==/WindhawkModSettings==

#include <windhawk_utils.h>

#include <string>
#include <vector>

struct {
    bool minimisedLast;
    bool skipFullyMinimisedGroups;
    std::vector<std::wstring> excludedApps;
} g_settings;

////////////////////////////////////////////////////////////////////////////////
// Mod

void LoadSettings() {
    g_settings.minimisedLast = Wh_GetIntSetting(L"minimisedLast");
    g_settings.skipFullyMinimisedGroups =
        Wh_GetIntSetting(L"skipFullyMinimisedGroups");

    g_settings.excludedApps.clear();
    for (int i = 0;; i++) {
        auto app = WindhawkUtils::StringSetting::make(L"excludedApps[%d]", i);
        std::wstring name = app.get();
        name.erase(0, name.find_first_not_of(L" \t"));
        name.erase(name.find_last_not_of(L" \t") + 1);
        if (name.empty()) {
            break;
        }

        g_settings.excludedApps.push_back(name);
    }
}

BOOL Wh_ModInit() {
    Wh_Log(L">");

    LoadSettings();
    return TRUE;
}

BOOL Wh_ModSettingsChanged(BOOL* bReload) {
    Wh_Log(L">");

    *bReload = TRUE;
    return TRUE;
}
