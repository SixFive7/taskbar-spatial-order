// The logic harness of Taskbar Spatial Order: the mod's logic run against a
// model of the taskbar, with no Explorer, no window and no input.
// run-harness.ps1 builds it with Windhawk's compiler and runs it.
//
// The mod is compiled in Windhawk's editing mode, where the Wh_* functions are
// inline stubs, and its calls to user32 and dwmapi go to the functions below.
// They answer for fake windows and taskbars and pass other questions on to
// Windows. Messages and hooks go only to a thread of the harness that stands
// in for the taskbar's. The busy check sees no mouse button, no foreground
// window and no menu unless a test opens one, and WinEvent hooks are counted,
// never set.
//
// What it checks:
// - FindDpaSlot and ButtonRanks, with getters shaped like the real ones;
// - RanksToMove on every order of up to 8 buttons and random ones of up to
//   40: it keeps a longest increasing subsequence in place, so the fewest
//   buttons move;
// - FixButtonOrder on 50,000 random groups over one to three taskbars,
//   against a model of how the taskbar adds a button and its thumbnail: both
//   end in item list order with the fewest moves, and no button group is
//   emptied on the way; with a TaskDestroyed that does nothing, nothing moves;
// - the moves stopping before they use what a move may have changed or freed:
//   an item list that shrank, a taskbar left with one button, a group change;
// - the sort key, WindowPosition and SortGroup, with the settings and the
//   groups left alone;
// - SortAllGroups over two taskbars found the way the mod finds real ones,
//   the windows it tracks, and the pass stopping when a group changes;
// - the worker: stopped right after it starts, also before it has a message
//   queue; its first pass; a burst of group changes making one pass;
// - the handoff to the taskbar's thread: a sort taken in its message loop and
//   never while it waits for a call it made, a menu found busy, the taskbar
//   window going away, unloading while a request is pending or a sort runs.
//
// It can't check Explorer itself: that its functions do what the model does.
// The live test does that (test/README.md).
//
// Build it with -fno-strict-aliasing. The mod reads Explorer's objects through
// casts such as void** and HDPA, which is safe while the compiler can't see
// them. It sees the fakes here, and without the flag Clang's type-based alias
// analysis drops some of those reads, and checks with them.

// Everything the mod includes, first, so that the redirections below apply to
// the mod's own code only.
#include <windhawk_utils.h>

#include <dwmapi.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <random>
#include <set>
#include <span>
#include <string>
#include <tuple>
#include <unordered_set>
#include <utility>
#include <vector>

////////////////////////////////////////////////////////////////////////////////
// Fake windows and taskbars

struct FakeWindow {
    RECT frame;     // the visible frame
    RECT restored;  // where it sits when not minimised, maximised or snapped
    bool minimised;
};

static std::map<HWND, FakeWindow> g_fakeWindows;
static std::set<HWND> g_closedWindows;
static std::map<HWND, int> g_windowQueries;  // IsWindow calls per window
static std::function<void(HWND)> g_onWindowQuery;

// The windows from a taskbar to its task list, as FindTaskLists walks them.
static std::map<HWND, std::wstring> g_fakeClasses;
static std::map<HWND, HWND> g_fakeTaskbands;  // the TaskbandHWND property
static std::map<std::pair<HWND, std::wstring>, HWND> g_fakeChildren;
static std::map<HWND, LONG_PTR> g_fakeWindowLongs;  // window long 0
static std::vector<HWND> g_fakeThreadWindows;       // EnumThreadWindows

// The taskbar window and its thread, while a test stands one in.
static std::atomic<HWND> g_fakeTray;
static std::atomic<bool> g_fakeTrayAlive;
static std::atomic<DWORD> g_standInThreadId;
static std::atomic<bool> g_taskbarMenuOpen;
static HANDLE g_ping;  // a message sent to the stand-in
static HANDLE g_pong;  // its reply

// What the mod did.
static std::atomic<int> g_taskbarLookups;   // FindTaskbarWindow calls
static std::atomic<int> g_taskListLookups;  // FindTaskLists calls
static std::atomic<int> g_quitPostFailures;
static std::atomic<int> g_messageHooks;  // WH_GETMESSAGE hooks in place
static std::atomic<int> g_roundTrips;
static std::atomic<int> g_hooksAtRoundTrip;
static std::atomic<int> g_winEventHooksSet;
static std::atomic<int> g_winEventHooksRemoved;

static bool g_createSuspended;  // the mod's threads start suspended

static BOOL WINAPI Harness_IsWindow(HWND h) {
    if (h && h == g_fakeTray) {
        return g_fakeTrayAlive;
    }
    if (g_fakeWindows.contains(h)) {
        g_windowQueries[h]++;
        if (g_onWindowQuery) {
            g_onWindowQuery(h);
        }
        return TRUE;
    }
    return !g_closedWindows.contains(h) && IsWindow(h);
}

static BOOL WINAPI Harness_IsIconic(HWND h) {
    auto it = g_fakeWindows.find(h);
    return it != g_fakeWindows.end() ? it->second.minimised : IsIconic(h);
}

static HRESULT WINAPI Harness_DwmGetWindowAttribute(HWND h,
                                                    DWORD attribute,
                                                    PVOID value,
                                                    DWORD size) {
    auto it = g_fakeWindows.find(h);
    if (it == g_fakeWindows.end()) {
        return DwmGetWindowAttribute(h, attribute, value, size);
    }
    if (attribute != DWMWA_EXTENDED_FRAME_BOUNDS || size != sizeof(RECT)) {
        return E_INVALIDARG;
    }
    *(RECT*)value = it->second.frame;
    return S_OK;
}

static BOOL WINAPI Harness_GetWindowRect(HWND h, LPRECT rect) {
    auto it = g_fakeWindows.find(h);
    if (it == g_fakeWindows.end()) {
        return GetWindowRect(h, rect);
    }
    *rect = it->second.frame;
    return TRUE;
}

static BOOL WINAPI Harness_GetWindowPlacement(HWND h,
                                              WINDOWPLACEMENT* placement) {
    auto it = g_fakeWindows.find(h);
    if (it == g_fakeWindows.end()) {
        return GetWindowPlacement(h, placement);
    }
    placement->rcNormalPosition = it->second.restored;
    placement->showCmd =
        it->second.minimised ? SW_SHOWMINIMIZED : SW_SHOWNORMAL;
    return TRUE;
}

// Fake windows belong to this process, and so does the stand-in's taskbar
// while it's there.
static DWORD WINAPI Harness_GetWindowThreadProcessId(HWND h,
                                                     LPDWORD processId) {
    DWORD threadId;
    if (h && h == g_fakeTray) {
        threadId = g_fakeTrayAlive ? g_standInThreadId.load() : 0;
    } else if (g_fakeWindows.contains(h)) {
        threadId = GetCurrentThreadId();
    } else {
        return GetWindowThreadProcessId(h, processId);
    }
    if (processId) {
        *processId = threadId ? GetCurrentProcessId() : 0;
    }
    return threadId;
}

// The fake taskbars belong to whichever thread asks.
static BOOL WINAPI Harness_EnumThreadWindows(DWORD threadId,
                                             WNDENUMPROC proc,
                                             LPARAM lParam) {
    if (g_fakeThreadWindows.empty()) {
        return EnumThreadWindows(threadId, proc, lParam);
    }
    g_taskListLookups++;
    for (HWND h : g_fakeThreadWindows) {
        if (!proc(h, lParam)) {
            break;
        }
    }
    return TRUE;
}

static int WINAPI Harness_GetClassName(HWND h, LPWSTR name, int size) {
    auto it = g_fakeClasses.find(h);
    if (it == g_fakeClasses.end()) {
        return GetClassNameW(h, name, size);
    }
    wcsncpy_s(name, size, it->second.c_str(), _TRUNCATE);
    return (int)wcslen(name);
}

static HANDLE WINAPI Harness_GetProp(HWND h, LPCWSTR name) {
    auto it = g_fakeTaskbands.find(h);
    if (it == g_fakeTaskbands.end()) {
        return GetPropW(h, name);
    }
    return wcscmp(name, L"TaskbandHWND") == 0 ? (HANDLE)it->second : nullptr;
}

static HWND WINAPI Harness_FindWindowEx(HWND parent,
                                        HWND after,
                                        LPCWSTR className,
                                        LPCWSTR title) {
    if (!parent && wcscmp(className, L"Shell_TrayWnd") == 0) {
        if (!after) {
            g_taskbarLookups++;
        }
        if (g_fakeTray && g_fakeTrayAlive) {
            return after ? nullptr : g_fakeTray.load();
        }
    }
    auto it = g_fakeChildren.find({parent, className});
    if (it != g_fakeChildren.end()) {
        return after ? nullptr : it->second;
    }
    return FindWindowExW(parent, after, className, title);
}

static LONG_PTR WINAPI Harness_GetWindowLongPtr(HWND h, int index) {
    auto it = g_fakeWindowLongs.find(h);
    if (it == g_fakeWindowLongs.end() || index != 0) {
        return GetWindowLongPtrW(h, index);
    }
    return it->second;
}

static HANDLE WINAPI Harness_CreateThread(LPSECURITY_ATTRIBUTES attributes,
                                          SIZE_T stackSize,
                                          LPTHREAD_START_ROUTINE start,
                                          LPVOID parameter,
                                          DWORD flags,
                                          LPDWORD threadId) {
    if (g_createSuspended) {
        flags |= CREATE_SUSPENDED;
    }
    return CreateThread(attributes, stackSize, start, parameter, flags,
                        threadId);
}

static BOOL WINAPI Harness_PostThreadMessage(DWORD threadId,
                                             UINT message,
                                             WPARAM wParam,
                                             LPARAM lParam) {
    BOOL posted = PostThreadMessageW(threadId, message, wParam, lParam);
    if (message == WM_QUIT && !posted) {
        g_quitPostFailures++;
    }
    return posted;
}

static SHORT WINAPI Harness_GetAsyncKeyState(int) {
    return 0;
}

static HWND WINAPI Harness_GetForegroundWindow() {
    return nullptr;
}

static BOOL WINAPI Harness_GetGUIThreadInfo(DWORD threadId,
                                            PGUITHREADINFO info) {
    DWORD size = info->cbSize;
    *info = {};
    info->cbSize = size;
    if (threadId && threadId == g_standInThreadId && g_taskbarMenuOpen) {
        info->flags = GUI_INMENUMODE;
    }
    return TRUE;
}

// Messages go to the stand-in's taskbar only. One posted there goes to its
// thread's queue.
static BOOL WINAPI Harness_PostMessage(HWND h,
                                       UINT message,
                                       WPARAM wParam,
                                       LPARAM lParam) {
    return h && h == g_fakeTray && g_fakeTrayAlive &&
           PostThreadMessageW(g_standInThreadId, message, wParam, lParam);
}

// One sent there is a ping, which the stand-in answers whenever it waits, as a
// thread handles sent messages when it takes messages or waits in
// SendMessage.
static LRESULT WINAPI Harness_SendMessageTimeout(HWND h,
                                                 UINT,
                                                 WPARAM,
                                                 LPARAM,
                                                 UINT,
                                                 UINT timeout,
                                                 PDWORD_PTR) {
    if (!h || h != g_fakeTray) {
        return 0;
    }
    g_roundTrips++;
    g_hooksAtRoundTrip = g_messageHooks.load();
    if (!g_fakeTrayAlive) {
        return 0;
    }
    ResetEvent(g_pong);
    SetEvent(g_ping);
    return WaitForSingleObject(g_pong, timeout) == WAIT_OBJECT_0;
}

// Hooks too: the stand-in's thread only.
static HHOOK WINAPI Harness_SetWindowsHookEx(int id,
                                             HOOKPROC proc,
                                             HINSTANCE module,
                                             DWORD threadId) {
    if (!threadId || threadId != g_standInThreadId) {
        return nullptr;
    }
    HHOOK hook = SetWindowsHookExW(id, proc, module, threadId);
    if (hook) {
        g_messageHooks++;
    }
    return hook;
}

static BOOL WINAPI Harness_UnhookWindowsHookEx(HHOOK hook) {
    BOOL removed = UnhookWindowsHookEx(hook);
    if (removed) {
        g_messageHooks--;
    }
    return removed;
}

// Counted, never set: a real one would see every window on the desktop.
static HWINEVENTHOOK WINAPI Harness_SetWinEventHook(DWORD,
                                                    DWORD,
                                                    HMODULE,
                                                    WINEVENTPROC,
                                                    DWORD,
                                                    DWORD,
                                                    DWORD) {
    return (HWINEVENTHOOK)(UINT_PTR)(0x100 + ++g_winEventHooksSet);
}

static BOOL WINAPI Harness_UnhookWinEvent(HWINEVENTHOOK) {
    g_winEventHooksRemoved++;
    return TRUE;
}

#define IsWindow Harness_IsWindow
#define IsIconic Harness_IsIconic
#define DwmGetWindowAttribute Harness_DwmGetWindowAttribute
#define GetWindowRect Harness_GetWindowRect
#define GetWindowPlacement Harness_GetWindowPlacement
#define GetWindowThreadProcessId Harness_GetWindowThreadProcessId
#define EnumThreadWindows Harness_EnumThreadWindows
#undef GetClassName
#define GetClassName Harness_GetClassName
#undef GetProp
#define GetProp Harness_GetProp
#undef FindWindowEx
#define FindWindowEx Harness_FindWindowEx
#undef GetWindowLongPtr
#define GetWindowLongPtr Harness_GetWindowLongPtr
#define CreateThread Harness_CreateThread
#undef PostThreadMessage
#define PostThreadMessage Harness_PostThreadMessage
#define GetAsyncKeyState Harness_GetAsyncKeyState
#define GetForegroundWindow Harness_GetForegroundWindow
#define GetGUIThreadInfo Harness_GetGUIThreadInfo
#undef PostMessage
#define PostMessage Harness_PostMessage
#undef SendMessageTimeout
#define SendMessageTimeout Harness_SendMessageTimeout
#undef SetWindowsHookEx
#define SetWindowsHookEx Harness_SetWindowsHookEx
#define UnhookWindowsHookEx Harness_UnhookWindowsHookEx
#define SetWinEventHook Harness_SetWinEventHook
#define UnhookWinEvent Harness_UnhookWinEvent

#include MOD_FILE

////////////////////////////////////////////////////////////////////////////////
// Checks

static int g_checks;
static int g_failures;
static std::set<int> g_failedLines;

// Counts a check, and reports a failing one once, however often it fails.
static void Check(int line, const char* condition, bool passed) {
    g_checks++;
    if (passed) {
        return;
    }
    g_failures++;
    if (g_failedLines.insert(line).second) {
        printf("FAIL line %d: %s\n", line, condition);
    }
}

#define CHECK(...) Check(__LINE__, #__VA_ARGS__, (__VA_ARGS__))

static double NowMs() {
    LARGE_INTEGER frequency, count;
    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&count);
    return 1000.0 * count.QuadPart / frequency.QuadPart;
}

static bool WaitUntil(const std::function<bool()>& condition, DWORD ms) {
    for (DWORD start = GetTickCount(); !condition(); Sleep(2)) {
        if (GetTickCount() - start > ms) {
            return false;
        }
    }
    return true;
}

static std::vector<int> FirstN(int n) {
    std::vector<int> v(n);
    for (int i = 0; i < n; i++) {
        v[i] = i;
    }
    return v;
}

static bool StrictlyIncreasing(const std::vector<int>& v) {
    return std::ranges::adjacent_find(v, std::greater_equal{}) == v.end();
}

// The length of a longest increasing subsequence, by patience sorting, so
// worked out apart from RanksToMove.
static int LisLength(const std::vector<int>& v) {
    std::vector<int> tails;
    for (int x : v) {
        auto it = std::ranges::lower_bound(tails, x);
        if (it == tails.end()) {
            tails.push_back(x);
        } else {
            *it = x;
        }
    }
    return (int)tails.size();
}

////////////////////////////////////////////////////////////////////////////////
// A model of the taskbar
//
// Per app an item list (FakeTaskGroup) and, on each taskbar that shows the
// app, a button group (FakeButtonGroup) with its buttons and thumbnails; a
// taskbar's task list (FakeTaskList) holds its button groups. The fakes of
// TaskDestroyed and TaskInclusionChanged act as the taskbar does: one takes
// an item's button and thumbnail off; the other adds them right after the
// button of the closest earlier item in the item list that has one there, at
// the same index in both lists.

static int g_taskListVtable, g_buttonGroupVtable, g_taskGroupVtable;
static int g_windowItemVtable, g_immersiveItemVtable, g_otherVtable;

struct FakeItem {
    void* vtable = &g_windowItemVtable;
    HWND hWnd = nullptr;
};

struct FakeTaskGroup {
    void* vtable = &g_taskGroupVtable;
    HDPA items = nullptr;  // slot 1: g_itemsSlot
};

struct FakeButtonGroup {
    void* vtable = &g_buttonGroupVtable;
    int type = 1;  // window buttons; 2 is a pinned app without windows
    FakeTaskGroup* group = nullptr;
    std::vector<void*> buttons;     // the button list, as items
    std::vector<void*> thumbnails;  // the XAML model's list, as items
    std::set<void*> shown;          // the items this taskbar shows
    int removals = 0;
    bool emptied = false;
    bool freed = false;  // as if Explorer had freed it
};

// FindInterface finds the ITaskListUI in slot 2, and the button group list is
// g_buttonGroupsSlot slots further on.
struct FakeTaskList {
    void* slots[16] = {};
    std::vector<PVOID> array;
    DPA buttonGroups{};

    explicit FakeTaskList(const std::vector<FakeButtonGroup*>& groups)
        : array(groups.begin(), groups.end()) {
        buttonGroups.cpItems = (int)array.size();
        buttonGroups.pArray = array.data();
        slots[2] = CTaskListWnd_ITaskListUI_vftable;
        slots[2 + g_buttonGroupsSlot] = &buttonGroups;
    }
    FakeTaskList(const FakeTaskList&) = delete;

    void* TaskList() { return &slots[2]; }
};

static bool g_destroyWorks = true;  // false: TaskDestroyed does nothing
static std::function<void()> g_duringNextDestroy;  // Explorer's other work
static bool g_usedFreed;  // the mod used a freed button group
static bool g_staleUsed;  // the mod used a freed slot of an item list
static void* const kStale = (void*)(UINT_PTR)0xDEAD0;  // what such a slot holds

// A button group as the mod's getters see it.
static FakeButtonGroup& Use(void* buttonGroup) {
    auto& b = *(FakeButtonGroup*)buttonGroup;
    g_usedFreed = g_usedFreed || b.freed;
    return b;
}

// A group's button group on a taskbar, as the taskbar finds it: never one it
// has freed.
static FakeButtonGroup* ButtonGroupOf(void* taskList, void* group) {
    auto* list = (FakeTaskList*)((void**)taskList - 2);  // from its slot 2
    for (PVOID p : list->array) {
        auto* b = (FakeButtonGroup*)p;
        if (b->group == group && !b->freed) {
            return b;
        }
    }
    return nullptr;
}

// Takes an item's button and thumbnail off; false if it has no button.
static bool TakeOff(FakeButtonGroup& b, void* item) {
    auto button = std::ranges::find(b.buttons, item);
    if (button == b.buttons.end()) {
        return false;
    }
    b.buttons.erase(button);
    std::erase(b.thumbnails, item);
    return true;
}

static HRESULT WINAPI Fake_TaskDestroyed(void* taskList,
                                         void* group,
                                         void* item) {
    g_staleUsed = g_staleUsed || item == kStale;
    if (auto during = std::exchange(g_duringNextDestroy, nullptr)) {
        during();
    }
    FakeButtonGroup* b = ButtonGroupOf(taskList, group);
    if (!g_destroyWorks || !b || !TakeOff(*b, item)) {
        return S_FALSE;
    }
    b->removals++;
    b->emptied = b->emptied || b->buttons.empty();
    return S_OK;
}

static HRESULT WINAPI Fake_TaskInclusionChanged(void* taskList,
                                                void* group,
                                                void* item) {
    g_staleUsed = g_staleUsed || item == kStale;
    FakeButtonGroup* b = ButtonGroupOf(taskList, group);
    if (!b || !b->shown.contains(item) ||
        std::ranges::find(b->buttons, item) != b->buttons.end()) {
        return S_FALSE;
    }
    HDPA items = ((FakeTaskGroup*)group)->items;
    int index = 0;
    for (int i = 0; i < items->cpItems && items->pArray[i] != item; i++) {
        auto button = std::ranges::find(b->buttons, items->pArray[i]);
        if (button != b->buttons.end()) {
            index = (int)(button - b->buttons.begin()) + 1;
        }
    }
    b->buttons.insert(b->buttons.begin() + index, item);
    b->thumbnails.insert(b->thumbnails.begin() + index, item);
    return S_OK;
}

static int WINAPI Fake_GetNumItems(void* buttonGroup) {
    return (int)Use(buttonGroup).buttons.size();
}

static void* WINAPI Fake_GetTaskItem(void* buttonGroup, int i) {
    auto& buttons = Use(buttonGroup).buttons;
    return i >= 0 && i < (int)buttons.size() ? buttons[i] : nullptr;
}

static int WINAPI Fake_GetGroupType(void* buttonGroup) {
    return Use(buttonGroup).type;
}

static void* WINAPI Fake_GetGroup(void* buttonGroup) {
    return Use(buttonGroup).group;
}

static HWND WINAPI Fake_GetWindow(void* item) {
    return ((FakeItem*)item)->hWnd;
}

// Points the mod at the model, as HookTaskbarSymbols points it at Explorer.
static void InstallModel() {
    CTaskListWnd_ITaskListUI_vftable = &g_taskListVtable;
    CTaskBtnGroup_ITaskBtnGroup_vftable = &g_buttonGroupVtable;
    CTaskGroup_ITaskGroup_vftable = &g_taskGroupVtable;
    CWindowTaskItem_ITaskItem_vftable = &g_windowItemVtable;
    CImmersiveTaskItem_ITaskItem_vftable = &g_immersiveItemVtable;
    CTaskBtnGroup_GetGroupType = Fake_GetGroupType;
    CTaskBtnGroup_GetGroup = Fake_GetGroup;
    CTaskBtnGroup_GetNumItems = Fake_GetNumItems;
    CTaskBtnGroup_GetTaskItem = Fake_GetTaskItem;
    CWindowTaskItem_GetWindow = Fake_GetWindow;
    CImmersiveTaskItem_GetWindow = Fake_GetWindow;
    CTaskListWnd_TaskDestroyed = Fake_TaskDestroyed;
    CTaskListWnd_TaskInclusionChanged = Fake_TaskInclusionChanged;
    g_buttonGroupsSlot = 3;
    g_itemsSlot = 1;
}

// A fake window with its frame at (x, y), or, if it's minimised, its restored
// rectangle there and its frame parked off screen.
static HWND FakeWindowAt(int x, int y, bool minimised = false) {
    static UINT_PTR next = 0x7EE00000;
    HWND h = (HWND)(next += 0x10);
    RECT restored = {x, y, x + 300, y + 200};
    RECT parked = {-32000, -32000, -31700, -31800};
    g_fakeWindows[h] = {minimised ? parked : restored, restored, minimised};
    return h;
}

static std::vector<HWND> Windows(std::span<void* const> items) {
    std::vector<HWND> windows;
    for (void* item : items) {
        windows.push_back(((FakeItem*)item)->hWnd);
    }
    return windows;
}

// An app: an item per window, its item list and its button groups.
struct App {
    std::vector<FakeItem> items;
    std::vector<PVOID> array;
    DPA dpa{};
    FakeTaskGroup group;
    std::vector<std::unique_ptr<FakeButtonGroup>> buttonGroups;

    explicit App(const std::vector<HWND>& windows) : items(windows.size()) {
        for (size_t i = 0; i < windows.size(); i++) {
            items[i].hWnd = windows[i];
            array.push_back(&items[i]);
        }
        dpa.cpItems = (int)array.size();
        dpa.pArray = array.data();
        group.items = &dpa;
    }
    App(const App&) = delete;

    // A button group showing these items, its buttons in this order.
    FakeButtonGroup* On(const std::vector<int>& shown) {
        auto& b =
            *buttonGroups.emplace_back(std::make_unique<FakeButtonGroup>());
        b.group = &group;
        for (int i : shown) {
            b.buttons.push_back(&items[i]);
            b.shown.insert(&items[i]);
        }
        b.thumbnails = b.buttons;
        return &b;
    }

    std::vector<HWND> Order() const {
        return Windows({dpa.pArray, (size_t)dpa.cpItems});
    }
};

// FixButtonOrder on a taskbar that shows the app with this button group.
static int FixButtons(App& app, FakeButtonGroup* buttons) {
    FakeTaskList taskList({buttons});
    return FixButtonOrder({taskList.TaskList(), buttons}, &app.group, &app.dpa,
                          g_groupChanges);
}

// SortGroup on taskbars that show the app with these button groups. Returns
// the windows it tracked.
static std::unordered_set<HWND> SortApp(
    App& app,
    const std::vector<FakeButtonGroup*>& buttonGroups) {
    std::vector<std::unique_ptr<FakeTaskList>> taskLists;
    Group group{&app.group, {}};
    for (FakeButtonGroup* b : buttonGroups) {
        auto& taskList = *taskLists.emplace_back(
            std::make_unique<FakeTaskList>(std::vector{b}));
        group.taskbars.push_back({taskList.TaskList(), b});
    }
    std::unordered_set<HWND> tracked;
    SortGroup(group, tracked);
    return tracked;
}

// An app on one taskbar.
struct OneTaskbar {
    App app;
    FakeButtonGroup* buttons;
    std::unordered_set<HWND> tracked;

    OneTaskbar(const std::vector<HWND>& windows, const std::vector<int>& shown)
        : app(windows), buttons(app.On(shown)) {}
    explicit OneTaskbar(const std::vector<HWND>& windows)
        : OneTaskbar(windows, FirstN((int)windows.size())) {}

    int Fix() { return FixButtons(app, buttons); }
    void Sort() { tracked = SortApp(app, {buttons}); }
    std::vector<HWND> Buttons() const { return Windows(buttons->buttons); }
};

// The two taskbar windows, and a window an earlier pass tracked.
static const HWND kPrimaryTray = (HWND)(UINT_PTR)0x7EF00000;
static const HWND kSecondaryTray = (HWND)(UINT_PTR)0x7EF00100;
static const HWND kTrackedEarlier = (HWND)(UINT_PTR)0x7EDFFFF0;

// A taskbar window and the windows on the way to its task list.
static void AddTaskbarWindow(HWND tray, FakeTaskList& taskList) {
    HWND taskSw = (HWND)((UINT_PTR)tray + 1);
    HWND taskListWnd = (HWND)((UINT_PTR)tray + 2);
    if (tray == kPrimaryTray) {
        g_fakeClasses[tray] = L"Shell_TrayWnd";
        g_fakeTaskbands[tray] = taskSw;
    } else {
        g_fakeClasses[tray] = L"Shell_SecondaryTrayWnd";
        g_fakeChildren[{tray, L"WorkerW"}] = taskSw;
    }
    g_fakeChildren[{taskSw, L"MSTaskListWClass"}] = taskListWnd;
    g_fakeWindowLongs[taskListWnd] = (LONG_PTR)&taskList;
    g_fakeThreadWindows.push_back(tray);
}

// Two taskbars. On the primary: app A with three windows, a pinned app, app B
// with two and app C with one. On the secondary: two of A's windows. A and B
// are out of order; C can't be.
struct World {
    HWND a1 = FakeWindowAt(500, 100), a2 = FakeWindowAt(100, 100),
         a3 = FakeWindowAt(300, 100);
    HWND b1 = FakeWindowAt(900, 100), b2 = FakeWindowAt(200, 100);
    HWND c1 = FakeWindowAt(50, 50);
    App a{{a1, a2, a3}}, b{{b1, b2}}, c{{c1}};
    FakeButtonGroup pinned;
    FakeButtonGroup* aPrimary = a.On({0, 1, 2});
    FakeButtonGroup* aSecondary = a.On({0, 2});
    FakeButtonGroup* bPrimary = b.On({0, 1});
    FakeTaskList primary{{aPrimary, &pinned, bPrimary, c.On({0})}};
    FakeTaskList secondary{{aSecondary}};
    const std::vector<HWND> aSorted{a2, a3, a1}, bSorted{b2, b1};

    World() {
        pinned.type = 2;
        AddTaskbarWindow(kPrimaryTray, primary);
        AddTaskbarWindow(kSecondaryTray, secondary);
        g_windowQueries.clear();
    }

    ~World() {
        g_fakeClasses.clear();
        g_fakeTaskbands.clear();
        g_fakeChildren.clear();
        g_fakeWindowLongs.clear();
        g_fakeThreadWindows.clear();
        g_onWindowQuery = nullptr;
    }

    bool Sorted() const {
        return a.Order() == aSorted && b.Order() == bSorted &&
               Windows(aPrimary->buttons) == aSorted &&
               Windows(aSecondary->buttons) == std::vector{a3, a1} &&
               Windows(bPrimary->buttons) == bSorted &&
               aPrimary->thumbnails == aPrimary->buttons &&
               aSecondary->thumbnails == aSecondary->buttons &&
               bPrimary->thumbnails == bPrimary->buttons;
    }

    bool Untouched() const {
        return a.Order() == std::vector{a1, a2, a3} &&
               b.Order() == std::vector{b1, b2} && !aPrimary->removals &&
               !aSecondary->removals && !bPrimary->removals;
    }

    // The windows of the groups of two or more.
    std::unordered_set<HWND> Grouped() const { return {a1, a2, a3, b1, b2}; }
};

static std::unordered_set<HWND> TrackedWindows() {
    std::lock_guard lock(g_trackedWindowsMutex);
    return g_trackedWindows;
}

static void SetTrackedWindows(std::unordered_set<HWND> windows) {
    std::lock_guard lock(g_trackedWindowsMutex);
    g_trackedWindows = std::move(windows);
}

////////////////////////////////////////////////////////////////////////////////
// Finding the lists

// A getter shaped like the real ones: the count of the DPA at an offset.
static int g_getterOffset;
static int WINAPI CountAtOffset(void* object) {
    HDPA dpa = *(HDPA*)((char*)object + g_getterOffset);
    return dpa ? dpa->cpItems : 0;
}

static void TestFindDpaSlot() {
    for (int slot : {1, 3, 9, 255}) {
        g_getterOffset = 8 * slot;
        CHECK(FindDpaSlot(CountAtOffset) == slot);
    }

    // Slot 0 holds the vtable, and a getter that returns something else gives
    // no slot.
    g_getterOffset = 0;
    CHECK(FindDpaSlot(CountAtOffset) == -1);
    CHECK(FindDpaSlot([](void*) WINAPI { return 256; }) == -1);
}

static void TestButtonRanks() {
    App app(std::vector<HWND>(3));
    FakeButtonGroup* b = app.On({2, 0});
    CHECK(ButtonRanks(b, &app.dpa) == std::vector{2, 0});

    // A button whose item isn't in the item list.
    FakeItem stray;
    b->buttons.push_back(&stray);
    CHECK(!ButtonRanks(b, &app.dpa));

    b->buttons.clear();
    CHECK(ButtonRanks(b, &app.dpa) == std::vector<int>{});
}

////////////////////////////////////////////////////////////////////////////////
// Moving buttons

// What stays is in order and a longest increasing subsequence, so the fewest
// buttons move, and the moves come in increasing order.
static void CheckRanksToMove(const std::vector<int>& ranks) {
    std::vector<int> moving = RanksToMove(ranks);
    std::vector<int> staying;
    for (int rank : ranks) {
        if (!std::ranges::binary_search(moving, rank)) {
            staying.push_back(rank);
        }
    }
    CHECK(StrictlyIncreasing(moving));
    CHECK(StrictlyIncreasing(staying));
    CHECK(moving.size() + staying.size() == ranks.size());
    CHECK((int)staying.size() == LisLength(ranks));
}

static void TestRanksToMove() {
    for (int n = 0; n <= 8; n++) {
        std::vector<int> ranks = FirstN(n);
        do {
            CheckRanksToMove(ranks);
        } while (std::ranges::next_permutation(ranks).found);
    }

    std::mt19937 rng(7);
    for (int trial = 0; trial < 20000; trial++) {
        std::vector<int> ranks = FirstN(1 + rng() % 40);
        std::ranges::shuffle(ranks, rng);
        CheckRanksToMove(ranks);
    }
}

// Random groups of 1 to 14 windows over 1 to 3 taskbars, each showing a
// random part of them, its buttons in item list order or, one time in five,
// scrambled. The item list is put in a random new order, as SortGroup does,
// and `check` is called for each taskbar with the fewest moves that fix it.
static void RandomGroups(
    const std::function<void(App&, FakeButtonGroup*, int)>& check) {
    std::mt19937 rng(2);
    for (int trial = 0; trial < 50000; trial++) {
        App app(std::vector<HWND>(1 + rng() % 14));
        std::vector<FakeButtonGroup*> taskbars;
        for (int count = 1 + rng() % 3; count > 0; count--) {
            std::vector<int> shown;
            for (int i = 0; i < (int)app.items.size(); i++) {
                if (rng() % 3) {
                    shown.push_back(i);
                }
            }
            if (rng() % 5 == 0) {
                std::ranges::shuffle(shown, rng);
            }
            taskbars.push_back(app.On(shown));
        }

        std::ranges::shuffle(app.array, rng);
        for (FakeButtonGroup* b : taskbars) {
            std::vector<int> ranks;
            for (void* item : b->buttons) {
                ranks.push_back((int)(std::ranges::find(app.array, item) -
                                      app.array.begin()));
            }
            check(app, b, (int)ranks.size() - LisLength(ranks));
        }
    }
}

// Buttons and thumbnails end in the new order, with the fewest moves, and the
// button group keeps a button throughout.
static void TestFixButtonOrder() {
    RandomGroups([](App& app, FakeButtonGroup* b, int fewest) {
        std::vector<void*> expected;
        std::ranges::copy_if(
            app.array, std::back_inserter(expected),
            [&](void* item) { return b->shown.contains(item); });
        int moved = FixButtons(app, b);
        CHECK(b->buttons == expected);
        CHECK(b->thumbnails == expected);
        CHECK(moved == fewest && b->removals == fewest);
        CHECK(!b->emptied);
    });
}

// With a TaskDestroyed that does nothing, FixButtonOrder makes, and so logs,
// the same moves, but nothing changes: the signature of that failure.
static void TestTaskDestroyedDoingNothing() {
    g_destroyWorks = false;
    RandomGroups([](App& app, FakeButtonGroup* b, int fewest) {
        std::vector<void*> before = b->buttons;
        CHECK(FixButtons(app, b) == fewest);
        CHECK(b->buttons == before && b->thumbnails == before);
    });
    g_destroyWorks = true;
}

// Explorer could change the lists while it handles a move. The moves stop
// before reading past the end of an item list that shrank, emptying a button
// group, or using what a group change freed.
static void TestMovesStopping() {
    // The item list shrinks from five items to two during the first move:
    // the moves stop at the first rank past its end, without reading it.
    {
        OneTaskbar t(std::vector<HWND>(5), {4, 3, 2, 1, 0});  // four moves
        g_staleUsed = false;
        g_duringNextDestroy = [&] {
            while (t.app.dpa.cpItems > 2) {
                t.app.array[--t.app.dpa.cpItems] = kStale;
            }
        };
        CHECK(t.Fix() == 2);
        CHECK(!g_staleUsed && !t.buttons->emptied);
    }

    // Two of three windows leave this taskbar during the first move, say for
    // another monitor's: with one button left, the moves stop, so the group
    // keeps it.
    {
        OneTaskbar t(std::vector<HWND>(3), {2, 1, 0});  // two moves
        g_duringNextDestroy = [&] {
            t.buttons->shown = {&t.app.items[1]};
            TakeOff(*t.buttons, &t.app.items[2]);
        };
        CHECK(t.Fix() == 1);
        CHECK(!t.buttons->emptied && t.buttons->buttons.size() == 1);
    }

    // A window joins or leaves the group during the first move, which frees
    // the button group and the rest of the item list: nothing more is used.
    {
        OneTaskbar t(std::vector<HWND>(5), {4, 3, 2, 1, 0});
        g_usedFreed = g_staleUsed = false;
        g_duringNextDestroy = [&] {
            OnGroupChanged();
            t.buttons->freed = true;
            for (int i = 1; i < t.app.dpa.cpItems; i++) {
                t.app.array[i] = kStale;
            }
        };
        CHECK(t.Fix() == 1);
        CHECK(!g_usedFreed && !g_staleUsed);
    }

    // The same during the one move on the first of two taskbars, freeing the
    // button group on the second, which shows one window: SortGroup doesn't
    // go on to it.
    {
        App app({FakeWindowAt(500, 100), FakeWindowAt(100, 100),
                 FakeWindowAt(300, 100)});
        FakeButtonGroup* first = app.On({0, 1, 2});
        FakeButtonGroup* second = app.On({2});
        g_usedFreed = false;
        g_duringNextDestroy = [second] {
            OnGroupChanged();
            second->freed = true;
        };
        SortApp(app, {first, second});
        CHECK(first->removals == 1 && second->removals == 0);
        CHECK(!g_usedFreed && !g_duringNextDestroy);
    }
}

////////////////////////////////////////////////////////////////////////////////
// Sorting

// The rule as the README gives it, spelled out apart from SortKey: minimised
// windows last if so set, then the left edge, the top edge and the window
// handle.
static bool ComesFirst(const Window& a, const Window& b) {
    if (g_settings.minimisedLast && a.minimised != b.minimised) {
        return !a.minimised;
    }
    if (a.position.x != b.position.x) {
        return a.position.x < b.position.x;
    }
    if (a.position.y != b.position.y) {
        return a.position.y < b.position.y;
    }
    return (UINT_PTR)a.hWnd < (UINT_PTR)b.hWnd;
}

static void TestSortKey() {
    auto W = [](UINT_PTR h, LONG x, LONG y, bool minimised = false) {
        return Window{nullptr, (HWND)h, minimised, {x, y}};
    };
    auto sorted = [](std::vector<Window> windows) {
        std::ranges::sort(windows, {}, SortKey);
        std::vector<UINT_PTR> handles;
        for (const Window& window : windows) {
            handles.push_back((UINT_PTR)window.hWnd);
        }
        return handles;
    };

    // The left edge first, also left of the primary monitor, then the top
    // edge, then the handle.
    CHECK(sorted({W(1, 100, 0), W(2, -1200, 300), W(3, 50, 900)}) ==
          std::vector<UINT_PTR>{2, 3, 1});
    CHECK(sorted({W(9, 0, 500), W(7, 0, 100), W(8, 0, 100), W(5, 0, 100)}) ==
          std::vector<UINT_PTR>{5, 7, 8, 9});

    // Minimised windows last, by their own position; with the setting off,
    // every window by position.
    std::vector<Window> mixed = {W(1, 10, 0, true), W(2, 500, 0),
                                 W(3, -5, 0, true), W(4, 20, 0)};
    CHECK(sorted(mixed) == std::vector<UINT_PTR>{4, 2, 3, 1});
    g_settings.minimisedLast = false;
    CHECK(sorted(mixed) == std::vector<UINT_PTR>{3, 1, 4, 2});

    // Random groups, a third of them with many ties: SortKey orders every
    // pair of windows as the rule does, under both settings.
    std::mt19937 rng(7);
    for (int trial = 0; trial < 20000; trial++) {
        int span = trial % 3 ? 100000 : 3;
        size_t count = 1 + rng() % 12;
        std::set<UINT_PTR> handles;
        while (handles.size() < count) {
            handles.insert(0x10000 + rng() % 4096);
        }
        std::vector<Window> windows;
        for (UINT_PTR h : handles) {
            LONG x = (LONG)(rng() % span) - span / 2;
            LONG y = (LONG)(rng() % span) - span / 2;
            windows.push_back(W(h, x, y, rng() % 3 == 0));
        }
        for (bool minimisedLast : {false, true}) {
            g_settings.minimisedLast = minimisedLast;
            for (const Window& a : windows) {
                for (const Window& b : windows) {
                    CHECK((SortKey(a) < SortKey(b)) == ComesFirst(a, b));
                }
            }
        }
    }
}

static void TestWindowPosition() {
    auto at = [](POINT p, LONG x, LONG y) { return p.x == x && p.y == y; };
    CHECK(at(WindowPosition(FakeWindowAt(100, 120), false), 100, 120));

    // Minimised: where it sits when restored.
    CHECK(at(WindowPosition(FakeWindowAt(50, 70, true), true), 50, 70));

    // Parked off every screen without being minimised: the same.
    HWND parked = FakeWindowAt(-32000, -32000);
    g_fakeWindows[parked].restored = {600, 70, 900, 270};
    CHECK(at(WindowPosition(parked, false), 600, 70));
}

static std::wstring OwnExeName() {
    WCHAR path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    PCWSTR name = wcsrchr(path, L'\\');
    return name ? name + 1 : path;
}

static void TestSortGroup() {
    HWND left = FakeWindowAt(100, 100);
    HWND middle = FakeWindowAt(400, 100);
    HWND right = FakeWindowAt(700, 50);
    HWND lower = FakeWindowAt(400, 300);  // below middle
    HWND minLeft = FakeWindowAt(50, 100, true);
    HWND minRight = FakeWindowAt(800, 100, true);
    const std::vector<HWND> all = {minRight, right,  lower,
                                   minLeft,  middle, left};

    // Minimised last: the visible windows by position, then the minimised
    // ones by theirs, in the item list and the buttons. All are tracked, and
    // sorting again moves nothing.
    {
        OneTaskbar t(all);
        t.Sort();
        std::vector<HWND> expected = {left,  middle,  lower,
                                      right, minLeft, minRight};
        CHECK(t.app.Order() == expected && t.Buttons() == expected);
        CHECK(t.buttons->thumbnails == t.buttons->buttons);
        CHECK(!t.buttons->emptied);
        CHECK(t.tracked == std::unordered_set<HWND>(all.begin(), all.end()));
        int removals = t.buttons->removals;
        t.Sort();
        CHECK(t.app.Order() == expected && t.buttons->removals == removals);
    }

    // With the setting off, minimised windows go by where they sit restored.
    {
        g_settings.minimisedLast = false;
        OneTaskbar t(all);
        t.Sort();
        std::vector<HWND> expected = {minLeft, left,  middle,
                                      lower,   right, minRight};
        CHECK(t.app.Order() == expected && t.Buttons() == expected);
        g_settings.minimisedLast = true;
    }

    // One window out of place: one move.
    {
        OneTaskbar t({middle, lower, right, left});
        t.Sort();
        CHECK(t.app.Order() == std::vector{left, middle, lower, right});
        CHECK(t.buttons->removals == 1);
    }

    // A group whose windows are all minimised is left alone, unless the
    // setting is off, and its windows are tracked either way.
    {
        OneTaskbar t({minRight, minLeft});
        t.Sort();
        CHECK(t.app.Order() == std::vector{minRight, minLeft});
        CHECK(t.buttons->removals == 0);
        CHECK(t.tracked == std::unordered_set{minRight, minLeft});
        g_settings.skipFullyMinimisedGroups = false;
        t.Sort();
        CHECK(t.app.Order() == std::vector{minLeft, minRight});
        g_settings.skipFullyMinimisedGroups = true;
    }

    // Excluded apps, matched against each window's process ignoring case: the
    // fake windows are this process's.
    {
        std::wstring exe = OwnExeName();
        std::ranges::transform(exe, exe.begin(), ::towupper);
        g_settings.excludedApps = {L"other.exe", exe};
        OneTaskbar t({right, left});
        t.Sort();
        CHECK(t.app.Order() == std::vector{right, left});
        CHECK(t.buttons->removals == 0);
        g_settings.excludedApps = {L"other.exe"};
        t.Sort();
        CHECK(t.app.Order() == std::vector{left, right});
        g_settings.excludedApps.clear();
    }

    // A Store app's window is sorted like any other.
    {
        OneTaskbar t({right, left});
        t.app.items[0].vtable = &g_immersiveItemVtable;
        t.Sort();
        CHECK(t.app.Order() == std::vector{left, right});
    }

    // An item of an unknown kind: the group is left alone.
    {
        OneTaskbar t({right, left});
        t.app.items[1].vtable = &g_otherVtable;
        t.Sort();
        CHECK(t.app.Order() == std::vector{right, left});
        CHECK(t.buttons->removals == 0);
    }

    // A button whose item isn't in the item list: nothing is touched.
    {
        OneTaskbar t({right, left});
        FakeItem stray{.hWnd = middle};
        t.buttons->buttons.push_back(&stray);
        t.Sort();
        CHECK(t.app.Order() == std::vector{right, left});
        CHECK(t.buttons->removals == 0);
    }

    // A window that has closed: the group is left alone until the next pass.
    {
        HWND closed = FakeWindowAt(10, 10);
        g_fakeWindows.erase(closed);
        g_closedWindows.insert(closed);
        OneTaskbar t({right, closed, left});
        t.Sort();
        CHECK(t.app.Order() == std::vector{right, closed, left});
        CHECK(t.buttons->removals == 0);
    }

    // A taskbar that shows one of the group's windows has nothing to move.
    {
        OneTaskbar t({right, left}, {0});
        t.Sort();
        CHECK(t.app.Order() == std::vector{left, right});
        CHECK(t.buttons->removals == 0 && t.buttons->buttons.size() == 1);
    }
}

////////////////////////////////////////////////////////////////////////////////
// Every group

// A whole pass: every group in order on every taskbar, and the tracked
// windows replaced by those of the groups of two or more. With something
// unexpected in a button group list, nothing is sorted, and the tracked
// windows stay as they were.
static void TestSortAllGroups() {
    {
        World w;
        SetTrackedWindows({kTrackedEarlier});
        SortAllGroups();
        CHECK(w.Sorted());
        CHECK(w.c.Order() == std::vector{w.c1});
        CHECK(TrackedWindows() == w.Grouped());
    }

    {
        World w;
        w.pinned.vtable = &g_otherVtable;
        SetTrackedWindows({kTrackedEarlier});
        SortAllGroups();
        CHECK(w.Untouched());
        CHECK(TrackedWindows() == std::unordered_set{kTrackedEarlier});
    }
}

// A window joins or leaves a group in the middle of a pass, here while a
// group's windows are looked at. The group stops before its next taskbar, the
// groups after it aren't looked at, and the tracked windows stay as they were.
static void TestGroupChangeDuringPass() {
    // While A's windows are looked at.
    {
        World w;
        SetTrackedWindows({kTrackedEarlier});
        g_onWindowQuery = [&](HWND h) {
            if (h == w.a1) {
                OnGroupChanged();
            }
        };
        SortAllGroups();
        CHECK(w.a.Order() == w.aSorted);
        CHECK(Windows(w.aPrimary->buttons) == std::vector{w.a1, w.a2, w.a3});
        CHECK(w.b.Order() == std::vector{w.b1, w.b2});
        CHECK(g_windowQueries[w.b1] + g_windowQueries[w.b2] == 0);
        CHECK(TrackedWindows() == std::unordered_set{kTrackedEarlier});
    }

    // While B's, after A is sorted.
    {
        World w;
        SetTrackedWindows({kTrackedEarlier});
        g_onWindowQuery = [&](HWND h) {
            if (h == w.b2) {
                OnGroupChanged();
            }
        };
        SortAllGroups();
        CHECK(Windows(w.aPrimary->buttons) == w.aSorted);
        CHECK(Windows(w.aSecondary->buttons) == std::vector{w.a3, w.a1});
        CHECK(w.b.Order() == w.bSorted && w.bPrimary->removals == 0);
        CHECK(TrackedWindows() == std::unordered_set{kTrackedEarlier});
    }
}

////////////////////////////////////////////////////////////////////////////////
// The worker and the handoff

// The mod's globals as a fresh load of it has them: the harness loads it once.
static void FreshLoad() {
    g_unloading = false;
    g_request = 0;
    g_sortTimer = 0;
    g_winEventHooks.clear();
}

// The worker in this process, which has no taskbar: stopped right after it
// starts, also before it has a message queue; its first pass, kSettleMs after
// the start, finding no taskbar and letting the timer lapse; and a burst of
// group changes making one more pass, kSettleMs after the last.
static void TestWorker() {
    // The worker looks for a taskbar of this process among the real windows,
    // which no fake window may stand in for.
    g_fakeWindows.clear();
    g_closedWindows.clear();

    for (int i = 0; i < 200; i++) {
        FreshLoad();
        Wh_ModAfterInit();
        CHECK(g_workerThread && g_workerThreadId);
        Wh_ModUninit();
        CHECK(g_workerThreadId == 0);
    }

    // Held before its first instruction, so that it surely has no message
    // queue: Wh_ModUninit keeps posting until the worker is let go.
    FreshLoad();
    g_createSuspended = true;
    Wh_ModAfterInit();
    g_createSuspended = false;
    int failures = g_quitPostFailures;
    HANDLE resumer = CreateThread(
        nullptr, 0,
        [](PVOID worker) -> DWORD {
            Sleep(200);
            ResumeThread(worker);
            return 0;
        },
        g_workerThread, 0, nullptr);
    double start = NowMs();
    Wh_ModUninit();
    double stopMs = NowMs() - start;
    WaitForSingleObject(resumer, INFINITE);
    CloseHandle(resumer);
    CHECK(g_quitPostFailures > failures && stopMs >= 150);

    // The first pass finds no taskbar, and no other follows.
    int lookups = g_taskbarLookups;
    FreshLoad();
    start = NowMs();
    Wh_ModAfterInit();
    CHECK(WaitUntil([&] { return g_taskbarLookups == lookups + 1; }, 30000));
    double firstMs = NowMs() - start;
    Sleep(1000);
    CHECK(g_taskbarLookups == lookups + 1);

    // Five group changes 20 ms apart: one pass, and no other.
    double lastChange = 0;
    for (int i = 0; i < 5; i++) {
        if (i) {
            Sleep(20);
        }
        OnGroupChanged();
        lastChange = NowMs();
    }
    CHECK(WaitUntil([&] { return g_taskbarLookups == lookups + 2; }, 30000));
    double settleMs = NowMs() - lastChange;
    Sleep(1000);
    CHECK(g_taskbarLookups == lookups + 2);
    CHECK(firstMs >= kSettleMs - 30 && settleMs >= kSettleMs - 50);
    CHECK(g_winEventHooks.empty());
    Wh_ModUninit();
}

// The stand-in for the taskbar's thread: a thread of this process, without a
// window, that runs a message loop or, as a test tells it, waits for a call
// it made or runs a menu's modal loop.

constexpr UINT kNestedWaitCmd = WM_APP + 1;  // wParam: how long, in ms
constexpr UINT kModalLoopCmd = WM_APP + 2;   // wParam: how long, in ms

static HANDLE g_standIn;
static std::atomic<bool> g_inNestedWait;
static std::atomic<int> g_requestsTaken;  // the mod's request messages

static void AnswerSentMessage(DWORD ms) {
    if (WaitForSingleObject(g_ping, ms) == WAIT_OBJECT_0) {
        SetEvent(g_pong);
    }
}

static void Took(const MSG& msg) {
    if (msg.message == WM_NULL && msg.lParam == (LPARAM)&g_request) {
        g_requestsTaken++;
    }
}

// Waiting for a call it made, as in SendMessage to another thread: it handles
// sent messages but takes no posted ones.
static void NestedWait(DWORD ms) {
    g_inNestedWait = true;
    for (DWORD start = GetTickCount(); GetTickCount() - start < ms;) {
        AnswerSentMessage(5);
    }
    g_inNestedWait = false;
}

// A menu's modal loop: it takes posted messages while the menu is open.
static void ModalLoop(DWORD ms) {
    g_taskbarMenuOpen = true;
    MSG msg;
    for (DWORD start = GetTickCount(); GetTickCount() - start < ms;) {
        if (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) {
            Took(msg);
        } else {
            AnswerSentMessage(5);
        }
    }
    g_taskbarMenuOpen = false;
}

// The message loop takes posted messages with PeekMessage(PM_REMOVE), so the
// mod's WH_GETMESSAGE hook runs, and handles sent messages whenever it waits.
static DWORD WINAPI StandIn(PVOID ready) {
    MSG msg;
    PeekMessage(&msg, nullptr, 0, 0, PM_NOREMOVE);  // makes the queue
    SetEvent(ready);
    for (;;) {
        if (MsgWaitForMultipleObjects(1, &g_ping, FALSE, INFINITE,
                                      QS_ALLPOSTMESSAGE) == WAIT_OBJECT_0) {
            SetEvent(g_pong);
            continue;
        }
        while (PeekMessage(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) {
                return 0;
            }
            Took(msg);
            if (msg.message == kNestedWaitCmd) {
                NestedWait((DWORD)msg.wParam);
            } else if (msg.message == kModalLoopCmd) {
                ModalLoop((DWORD)msg.wParam);
            }
        }
    }
}

static void StartStandIn() {
    g_ping = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    g_pong = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    DWORD id = 0;
    g_standIn = CreateThread(nullptr, 0, StandIn, ready, 0, &id);
    WaitForSingleObject(ready, INFINITE);
    CloseHandle(ready);
    g_standInThreadId = id;
    g_fakeTray = kPrimaryTray;
    g_fakeTrayAlive = true;
}

static void StopStandIn() {
    g_fakeTray = nullptr;
    g_fakeTrayAlive = false;
    PostThreadMessageW(g_standInThreadId, WM_QUIT, 0, 0);
    WaitForSingleObject(g_standIn, INFINITE);
    CloseHandle(g_standIn);
    CloseHandle(g_ping);
    CloseHandle(g_pong);
    g_standInThreadId = 0;
}

static void Tell(UINT command, DWORD ms) {
    PostThreadMessageW(g_standInThreadId, command, ms, 0);
}

static void TestHandoff() {
    StartStandIn();
    g_requestDone = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    FreshLoad();

    // The stand-in's message loop takes the request: the sort runs on its
    // thread, and when the worker carries on, the hook is gone and a round
    // trip has been made since.
    {
        World w;
        std::atomic<DWORD> sortedOn = 0;
        g_onWindowQuery = [&](HWND) { sortedOn = GetCurrentThreadId(); };
        int roundTrips = g_roundTrips;
        bool busy = SortOnTaskbarThread(g_fakeTray);
        CHECK(!busy && g_request == kSorted);
        CHECK(sortedOn == g_standInThreadId);
        CHECK(w.Sorted() && TrackedWindows() == w.Grouped());
        CHECK(g_messageHooks == 0 && g_requestHookCalls == 0);
        CHECK(g_roundTrips == roundTrips + 1 && g_hooksAtRoundTrip == 0);
    }

    // While the taskbar's thread waits for a call it made, it takes no posted
    // message, so the sort waits until the call returns.
    {
        World w;
        std::atomic<bool> sortedInWait = false;
        g_onWindowQuery = [&](HWND) {
            sortedInWait = sortedInWait || g_inNestedWait;
        };
        Tell(kNestedWaitCmd, 300);
        CHECK(WaitUntil([] { return g_inNestedWait.load(); }, 5000));
        bool busy = SortOnTaskbarThread(g_fakeTray);
        CHECK(!busy && g_request == kSorted);
        CHECK(!sortedInWait && w.Sorted());
    }

    // A menu's modal loop takes the request, but the check on the taskbar's
    // thread finds the menu open: no sort, and the worker is told so.
    {
        World w;
        Tell(kModalLoopCmd, 300);
        CHECK(WaitUntil([] { return g_taskbarMenuOpen.load(); }, 5000));
        bool busy = SortOnTaskbarThread(g_fakeTray);
        CHECK(busy && g_request == kFoundUserBusy);
        CHECK(w.Untouched() && g_messageHooks == 0);
        CHECK(WaitUntil([] { return !g_taskbarMenuOpen; }, 5000));
    }

    // No taskbar window: nothing is hooked or asked.
    {
        g_fakeTrayAlive = false;
        int roundTrips = g_roundTrips;
        bool busy = SortOnTaskbarThread(g_fakeTray);
        CHECK(!busy && g_messageHooks == 0 && g_roundTrips == roundTrips);
        g_fakeTrayAlive = true;
    }

    // The taskbar window goes away while the request is pending: the request
    // is called off without waiting for the taskbar's thread, which later
    // takes the message with no hook in place.
    {
        World w;
        Tell(kNestedWaitCmd, 2000);
        CHECK(WaitUntil([] { return g_inNestedWait.load(); }, 5000));
        int taken = g_requestsTaken;
        HANDLE closer = CreateThread(
            nullptr, 0,
            [](PVOID) -> DWORD {
                Sleep(150);
                g_fakeTrayAlive = false;
                return 0;
            },
            nullptr, 0, nullptr);
        bool busy = SortOnTaskbarThread(g_fakeTray);
        CHECK(!busy && g_request == kCalledOff);
        CHECK(g_inNestedWait && g_messageHooks == 0);
        WaitForSingleObject(closer, INFINITE);
        CloseHandle(closer);
        CHECK(WaitUntil([&] { return g_requestsTaken == taken + 1; }, 10000));
        CHECK(w.Untouched());
        g_fakeTrayAlive = true;
    }

    CloseHandle(g_requestDone);  // from here on, the worker makes its own

    // Unloading while the worker's request is pending: Wh_ModUninit calls it
    // off without waiting for the taskbar's thread.
    {
        World w;
        Tell(kNestedWaitCmd, 3000);
        CHECK(WaitUntil([] { return g_inNestedWait.load(); }, 5000));
        int taken = g_requestsTaken;
        int set = g_winEventHooksSet;
        int removed = g_winEventHooksRemoved;
        FreshLoad();
        Wh_ModAfterInit();
        CHECK(WaitUntil(
            [] { return g_request == kPending && g_messageHooks == 1; },
            10000));
        Wh_ModUninit();
        CHECK(g_inNestedWait && g_request == kCalledOff);
        CHECK(g_messageHooks == 0);
        CHECK(g_winEventHooksSet - set == 3 &&
              g_winEventHooksRemoved - removed == 3);
        CHECK(WaitUntil([&] { return g_requestsTaken == taken + 1; }, 10000));
        CHECK(w.Untouched());
    }

    // Unloading while a sort runs: Wh_ModUninit returns only after it.
    {
        World w;
        bool slowed = false;
        g_onWindowQuery = [&](HWND) {
            if (!std::exchange(slowed, true)) {
                Sleep(300);
            }
        };
        FreshLoad();
        Wh_ModAfterInit();
        CHECK(WaitUntil([] { return g_request == kTaken; }, 10000));
        Wh_ModUninit();
        CHECK(g_request == kSorted && w.Sorted());
        CHECK(g_messageHooks == 0 && g_requestHookCalls == 0);
    }

    // The worker waits out a menu: its own check finds it open, so it asks
    // nothing of the taskbar's thread until it closes, then sorts. The request
    // is counted once Wh_ModUninit returns, after the handoff's round trip.
    {
        World w;
        std::atomic<bool> sortedInMenu = false;
        g_onWindowQuery = [&](HWND) {
            sortedInMenu = sortedInMenu || g_taskbarMenuOpen;
        };
        int taken = g_requestsTaken;
        Tell(kModalLoopCmd, 1000);
        CHECK(WaitUntil([] { return g_taskbarMenuOpen.load(); }, 5000));
        FreshLoad();
        Wh_ModAfterInit();
        CHECK(WaitUntil([] { return g_request == kSorted; }, 10000));
        Wh_ModUninit();
        CHECK(g_requestsTaken == taken + 1 && !sortedInMenu && w.Sorted());
    }

    // A life with a taskbar: the first pass sorts and starts watching windows,
    // a group change makes another pass, and unloading removes the hooks.
    {
        World w;
        int lookups = g_taskListLookups;
        int set = g_winEventHooksSet;
        int removed = g_winEventHooksRemoved;
        FreshLoad();
        Wh_ModAfterInit();
        CHECK(WaitUntil(
            [&] {
                return g_taskListLookups == lookups + 1 && g_request == kSorted;
            },
            10000));
        CHECK(w.Sorted() && g_winEventHooksSet - set == 3);
        OnGroupChanged();
        CHECK(
            WaitUntil([&] { return g_taskListLookups == lookups + 2; }, 10000));
        Wh_ModUninit();
        CHECK(g_winEventHooksSet - set == 3 &&
              g_winEventHooksRemoved - removed == 3);
        CHECK(g_messageHooks == 0 && g_requestHookCalls == 0);
    }

    StopStandIn();
}

////////////////////////////////////////////////////////////////////////////////

static void Run(const char* name, void (*test)()) {
    g_settings.minimisedLast = true;
    g_settings.skipFullyMinimisedGroups = true;
    g_settings.excludedApps.clear();
    g_duringNextDestroy = nullptr;
    g_usedFreed = g_staleUsed = false;
    int checks = g_checks;
    int failures = g_failures;
    double start = NowMs();
    test();
    printf("%-28s %8d checks %5.1f s", name, g_checks - checks,
           (NowMs() - start) / 1000);
    if (int failed = g_failures - failures) {
        printf(", %d failed", failed);
    }
    printf("\n");
}

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);

    // A hang fails the run instead of stopping it.
    CreateThread(
        nullptr, 0,
        [](PVOID) -> DWORD {
            Sleep(5 * 60 * 1000);
            printf("FAIL: still running after five minutes\n");
            ExitProcess(3);
        },
        nullptr, 0, nullptr);

    InstallModel();
    Run("FindDpaSlot", TestFindDpaSlot);
    Run("ButtonRanks", TestButtonRanks);
    Run("RanksToMove", TestRanksToMove);
    Run("FixButtonOrder", TestFixButtonOrder);
    Run("TaskDestroyed doing nothing", TestTaskDestroyedDoingNothing);
    Run("Moves stopping", TestMovesStopping);
    Run("SortKey", TestSortKey);
    Run("WindowPosition", TestWindowPosition);
    Run("SortGroup", TestSortGroup);
    Run("SortAllGroups", TestSortAllGroups);
    Run("Group change during a pass", TestGroupChangeDuringPass);
    Run("Worker", TestWorker);
    Run("Handoff", TestHandoff);
    printf("%d checks, %d failures\n", g_checks, g_failures);
    return g_failures ? 1 : 0;
}
