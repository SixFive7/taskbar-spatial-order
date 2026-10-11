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
//
// Parts of this mod are adapted from GPL-3.0 mods by Michael Maltsev (m417z):
// - Taskbar Thumbnail Reorder: the lookup of each taskbar and of a group's
//   item list.
//   https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-thumbnail-reorder.wh.cpp
// - Cycle taskbar buttons with mouse wheel: the lookup of a taskbar's button
//   groups.
//   https://github.com/ramensoftware/windhawk-mods/blob/main/mods/taskbar-wheel-cycle.wh.cpp

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

#include <algorithm>
#include <optional>
#include <string>
#include <vector>

struct {
    bool minimisedLast;
    bool skipFullyMinimisedGroups;
    std::vector<std::wstring> excludedApps;
} g_settings;

////////////////////////////////////////////////////////////////////////////////
// Taskbar structures
//
// Explorer keeps one item list per app (a CTaskGroup) and, on every taskbar, a
// button group (CTaskBtnGroup) whose buttons are a subset of that list in the
// same order. Everything is reached through functions found by symbol name,
// and every object is checked against its class's vtable before it is used.

// https://www.geoffchappell.com/studies/windows/shell/comctl32/api/da/dpa/dpa.htm
typedef struct _DPA {
    int cpItems;
    PVOID* pArray;
    HANDLE hHeap;
    int cpCapacity;
    int cpGrow;
} DPA, *HDPA;

void* CTaskListWnd_ITaskListUI_vftable;
void* CTaskBtnGroup_ITaskBtnGroup_vftable;
void* CTaskGroup_ITaskGroup_vftable;
void* CWindowTaskItem_ITaskItem_vftable;
void* CImmersiveTaskItem_ITaskItem_vftable;

using GetInt_t = int(WINAPI*)(void* pThis);
GetInt_t CTaskListWnd_GetButtonGroupCount;
GetInt_t CTaskBtnGroup_GetGroupType;
GetInt_t CTaskBtnGroup_GetNumItems;
GetInt_t CTaskGroup_GetNumItems;

using CTaskBtnGroup_GetGroup_t = void*(WINAPI*)(void* pThis);
CTaskBtnGroup_GetGroup_t CTaskBtnGroup_GetGroup;

using CTaskBtnGroup_GetTaskItem_t = void*(WINAPI*)(void* pThis, int index);
CTaskBtnGroup_GetTaskItem_t CTaskBtnGroup_GetTaskItem;

using TaskItem_GetWindow_t = HWND(WINAPI*)(void* pThis);
TaskItem_GetWindow_t CWindowTaskItem_GetWindow;
TaskItem_GetWindow_t CImmersiveTaskItem_GetWindow;

// Positions, in pointer-sized slots, of the DPA of button groups in a
// taskbar's ITaskListUI and of the DPA of items in a CTaskGroup.
int g_buttonGroupsSlot;
int g_itemsSlot;

// Finds which slot of an object a getter reads its DPA from. The getters are
// `return m_dpa ? DPA_GetPtrCount(m_dpa) : 0;`, so called on an array of
// pointers to the numbers 0, 1, 2, ..., one returns the slot it read. This is
// m417z's technique; it takes the offset from Windows' own code instead of
// guessing it.
int FindDpaSlot(GetInt_t getCount) {
    constexpr int kSlots = 256;
    int numbers[kSlots];
    int* pointers[kSlots];
    for (int i = 0; i < kSlots; i++) {
        numbers[i] = i;
        pointers[i] = &numbers[i];
    }

    int slot = getCount(pointers);
    return slot > 0 && slot < kSlots ? slot : -1;
}

bool HasVtable(void* object, void* vtable) {
    return object && *(void**)object == vtable;
}

void* FindInterface(void* object, void* vtable) {
    for (int i = 0; i < 16; i++) {
        if (((void**)object)[i] == vtable) {
            return (void**)object + i;
        }
    }

    return nullptr;
}

// The ITaskListUI of every taskbar, one per monitor. All taskbars belong to
// the calling thread.
std::vector<void*> FindTaskLists() {
    std::vector<void*> taskLists;

    auto proc = [](HWND hWnd, LPARAM lParam) -> BOOL {
        WCHAR className[32];
        if (!GetClassName(hWnd, className, ARRAYSIZE(className))) {
            return TRUE;
        }

        HWND hTaskSwWnd;
        if (_wcsicmp(className, L"Shell_TrayWnd") == 0) {
            hTaskSwWnd = (HWND)GetProp(hWnd, L"TaskbandHWND");
        } else if (_wcsicmp(className, L"Shell_SecondaryTrayWnd") == 0) {
            hTaskSwWnd = FindWindowEx(hWnd, nullptr, L"WorkerW", nullptr);
        } else {
            return TRUE;
        }

        HWND hTaskListWnd = hTaskSwWnd
                                ? FindWindowEx(hTaskSwWnd, nullptr,
                                               L"MSTaskListWClass", nullptr)
                                : nullptr;
        void* taskListWnd =
            hTaskListWnd ? (void*)GetWindowLongPtr(hTaskListWnd, 0) : nullptr;
        void* taskList =
            taskListWnd
                ? FindInterface(taskListWnd, CTaskListWnd_ITaskListUI_vftable)
                : nullptr;
        if (taskList) {
            ((std::vector<void*>*)lParam)->push_back(taskList);
        } else {
            Wh_Log(L"No task list found for taskbar %08X",
                   (DWORD)(DWORD_PTR)hWnd);
        }

        return TRUE;
    };

    EnumThreadWindows(GetCurrentThreadId(), proc, (LPARAM)&taskLists);
    return taskLists;
}

// A taskbar that shows a group: its ITaskListUI and the group's button group
// there.
struct Taskbar {
    void* taskList;
    void* buttonGroup;
};

struct Group {
    void* taskGroup;
    std::vector<Taskbar> taskbars;
};

// Every group that has window buttons on a taskbar. Returns false if
// something doesn't look as expected.
bool FindGroups(std::vector<Group>& groups) {
    for (void* taskList : FindTaskLists()) {
        HDPA buttonGroups = (HDPA)((void**)taskList)[g_buttonGroupsSlot];
        for (int i = 0; buttonGroups && i < buttonGroups->cpItems; i++) {
            void* buttonGroup = buttonGroups->pArray[i];
            if (!HasVtable(buttonGroup, CTaskBtnGroup_ITaskBtnGroup_vftable)) {
                Wh_Log(L"Unexpected object in the button group list");
                return false;
            }

            // 1: one button, or several uncombined ones. 2: a pinned app
            // without windows. 3: several combined buttons.
            int type = CTaskBtnGroup_GetGroupType(buttonGroup);
            if (type != 1 && type != 3) {
                continue;
            }

            void* taskGroup = CTaskBtnGroup_GetGroup(buttonGroup);
            if (!HasVtable(taskGroup, CTaskGroup_ITaskGroup_vftable)) {
                Wh_Log(L"Unexpected task group object");
                return false;
            }

            auto it = std::ranges::find(groups, taskGroup, &Group::taskGroup);
            if (it == groups.end()) {
                it = groups.insert(it, {taskGroup, {}});
            }

            it->taskbars.push_back({taskList, buttonGroup});
        }
    }

    return true;
}

// The window of an item, for the two kinds of items that have one: desktop
// windows and Store app windows.
HWND ItemWindow(void* item) {
    if (HasVtable(item, CWindowTaskItem_ITaskItem_vftable)) {
        return CWindowTaskItem_GetWindow(item);
    }

    if (HasVtable(item, CImmersiveTaskItem_ITaskItem_vftable)) {
        return CImmersiveTaskItem_GetWindow(item);
    }

    return nullptr;
}

int IndexOfItem(HDPA items, void* item) {
    for (int i = 0; i < items->cpItems; i++) {
        if (items->pArray[i] == item) {
            return i;
        }
    }

    return -1;
}

// The rank of each button of a button group: the index of its item in the item
// list. Nothing if an item isn't there.
std::optional<std::vector<int>> ButtonRanks(void* buttonGroup, HDPA items) {
    std::vector<int> ranks(CTaskBtnGroup_GetNumItems(buttonGroup));
    for (int i = 0; i < (int)ranks.size(); i++) {
        ranks[i] =
            IndexOfItem(items, CTaskBtnGroup_GetTaskItem(buttonGroup, i));
        if (ranks[i] == -1) {
            Wh_Log(L"Button without an item in its group's item list");
            return std::nullopt;
        }
    }

    return ranks;
}

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

bool HookTaskbarSymbols() {
    HMODULE module =
        LoadLibraryEx(L"taskbar.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!module) {
        Wh_Log(L"Couldn't load taskbar.dll");
        return false;
    }

    WindhawkUtils::SYMBOL_HOOK taskbarDllHooks[] = {
        {
            {LR"(const CTaskListWnd::`vftable'{for `ITaskListUI'})"},
            &CTaskListWnd_ITaskListUI_vftable,
        },
        {
            {LR"(public: virtual int __cdecl CTaskListWnd::GetButtonGroupCount(void))"},
            &CTaskListWnd_GetButtonGroupCount,
        },
        {
            {LR"(const CTaskBtnGroup::`vftable'{for `ITaskBtnGroup'})"},
            &CTaskBtnGroup_ITaskBtnGroup_vftable,
        },
        {
            {LR"(public: virtual enum eTBGROUPTYPE __cdecl CTaskBtnGroup::GetGroupType(void))"},
            &CTaskBtnGroup_GetGroupType,
        },
        {
            {LR"(public: virtual struct ITaskGroup * __cdecl CTaskBtnGroup::GetGroup(void))"},
            &CTaskBtnGroup_GetGroup,
        },
        {
            {LR"(public: virtual int __cdecl CTaskBtnGroup::GetNumItems(void))"},
            &CTaskBtnGroup_GetNumItems,
        },
        {
            {LR"(public: virtual struct ITaskItem * __cdecl CTaskBtnGroup::GetTaskItem(int))"},
            &CTaskBtnGroup_GetTaskItem,
        },
        {
            {LR"(const CTaskGroup::`vftable'{for `ITaskGroup'})"},
            &CTaskGroup_ITaskGroup_vftable,
        },
        {
            {LR"(public: virtual int __cdecl CTaskGroup::GetNumItems(void))"},
            &CTaskGroup_GetNumItems,
        },
        {
            {LR"(const CWindowTaskItem::`vftable'{for `ITaskItem'})"},
            &CWindowTaskItem_ITaskItem_vftable,
        },
        {
            {LR"(public: virtual struct HWND__ * __cdecl CWindowTaskItem::GetWindow(void))"},
            &CWindowTaskItem_GetWindow,
        },
        {
            {LR"(const CImmersiveTaskItem::`vftable'{for `ITaskItem'})"},
            &CImmersiveTaskItem_ITaskItem_vftable,
        },
        {
            {LR"(public: virtual struct HWND__ * __cdecl CImmersiveTaskItem::GetWindow(void))"},
            &CImmersiveTaskItem_GetWindow,
        },
    };

    if (!WindhawkUtils::HookSymbols(module, taskbarDllHooks,
                                    ARRAYSIZE(taskbarDllHooks))) {
        Wh_Log(L"HookSymbols failed");
        return false;
    }

    g_buttonGroupsSlot = FindDpaSlot(CTaskListWnd_GetButtonGroupCount);
    g_itemsSlot = FindDpaSlot(CTaskGroup_GetNumItems);
    Wh_Log(L"Button group list in slot %d, item list in slot %d",
           g_buttonGroupsSlot, g_itemsSlot);
    if (g_buttonGroupsSlot == -1 || g_itemsSlot == -1) {
        Wh_Log(L"Unexpected taskbar code");
        return false;
    }

    return true;
}

BOOL Wh_ModInit() {
    Wh_Log(L">");

    LoadSettings();
    return HookTaskbarSymbols();
}

BOOL Wh_ModSettingsChanged(BOOL* bReload) {
    Wh_Log(L">");

    *bReload = TRUE;
    return TRUE;
}
