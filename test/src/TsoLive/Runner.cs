// The live-test runner: infrastructure (logs, checks, waits, test windows, safety, cleanup).
// The steps themselves are in Steps.cs.
//
// Progress log (machine-readable, nothing else in it):
//   TOTAL eta=<s>
//   STEP i/N <name> START eta=<s>
//   STEP i/N <name> PASS|FAIL <detail> took=<s>
//   DONE pass=<n> fail=<n> [aborted=<reason>]
// Detail log: everything, including one CHECK line per check.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using TsoShared;

namespace TsoLive;

public sealed class RunOptions
{
    /// <summary>The kit's work folder (.work\test): state\ holds what -Recover and step 2 need between runs.</summary>
    public string WorkDir;
    public string RunDir;
    public string ModPath;
    /// <summary>A set of step numbers ("all", "1-13,16", ...); the steps run in the table's order, so
    /// step 16 (the README demo) always runs right after step 2.</summary>
    public string Steps = "all";
    public string TestAppExe;
    public bool DryRun;
    public bool GlomPrimaryOnly;
    public int DeadmanSec = 3600;
    public int IdleSec = 240;
    public string Prefix = "TSO-TEST";
    public string Aumid = "TaskbarSpatialOrder.LiveTest";
}

public sealed class AbortRunException : Exception
{
    public AbortRunException(string reason) : base(reason) { }
}

sealed class StepDef
{
    public int Id;
    public string Name;
    public double Eta;
    public bool NeedsMod;
    public Action Body;
}

sealed class CheckRecord
{
    public string Id, Name, Detail;
    public bool Pass, Dry, Info;
}

public sealed partial class LiveRunner
{
    const double SetupEta = 10;

    readonly RunOptions O;
    readonly Windhawk Wh;
    readonly Stopwatch Clock = Stopwatch.StartNew();
    StreamWriter _progressLog, _detailLog;
    readonly object _logLock = new();
    string _journalPath;
    JournalData J;
    TestAppClient App;
    Watcher W;
    ModLog Log;
    string ModId, ModVersion;
    List<MonitorInfo> Mons;
    MonitorInfo Primary, LeftMon, RightMon;
    volatile IReadOnlyDictionary<IntPtr, string> _keys = new Dictionary<IntPtr, string>();
    readonly Dictionary<string, IntPtr> _hwnd = new();
    readonly Dictionary<string, long> _itemRef = new();
    readonly List<CheckRecord> _checks = new();
    readonly List<CheckRecord> Survival = new();
    (bool minimisedLast, bool skip, string excluded) Settings = (true, true, "");
    bool _modOk;
    int _pass, _fail, _checkSeq;
    string _abort;
    StepDef _step;
    double _lastActionT;
    POINT _cursorAtStart;
    IntPtr _foregroundAtStart;
    Guid _desktopAtStart;
    List<Guid> _desktops = new();
    volatile bool _cancel;
    bool _stopping;
    bool _taskbarStateRequested;
    List<StepDef> _table;

    LiveRunner(RunOptions o, Func<string, string, string> windhawk)
    {
        O = o;
        Wh = new Windhawk(windhawk, s => Detail(s));
    }

    /// <summary>Runs the live test. Returns 0 if every step passed, 1 if any failed, 2 if aborted.</summary>
    public static int Run(RunOptions options, Func<string, string, string> windhawk) => new LiveRunner(options, windhawk).Execute();

    /// <summary>
    /// Undoes what an interrupted run left behind, from its journal: test app processes, the
    /// taskbar grouping values, the virtual desktop, the log capture, changed mod settings and the
    /// mod's logging; optionally disables the mod. Windhawk is only touched when windhawk is given.
    /// </summary>
    public static int Recover(string journalPath, Func<string, string, string> windhawk, bool disableMod)
    {
        void Say(string s) => Console.WriteLine("RECOVER " + s);
        var j = Journal.Load(journalPath);
        Say($"journal {journalPath}: run {j.RunId}, complete={j.Complete}, safe stop={j.SafeStopReason ?? "none"}");
        int problems = 0;
        if (j.DryRun)
        {
            // A dry run changed nothing real; its journal records what the stubs were asked to do.
            Say("dry-run journal: nothing real to undo; only stray test app processes are closed");
            windhawk = null;
            j.Glom.Changed = false;
            j.LogCaptureStarted = j.ModSettingsChanged = j.ModLoggingEnabledByRun = j.ModDisabledByDemo = false;
            j.OriginalDesktop = null;
            j.TempDesktop = null;
        }

        foreach (var p in Process.GetProcessesByName("TsoTestApp"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
                Say($"killed test app pid {p.Id}");
            }
            catch (Exception e)
            {
                Say($"could not kill test app pid {p.Id}: {e.Message}");
                problems++;
            }
        }

        if (j.Glom.Changed && !j.Glom.Restored)
        {
            string r = RegistryValues.RestoreAll(Registry.CurrentUser, j.Glom.KeyPath, j.Glom.Original);
            Win32.BroadcastTraySettings();
            j.Glom.Restored = r == "ok";
            j.Glom.RestoredBy = "recover";
            j.Glom.RestoredUtc = DateTime.UtcNow.ToString("o");
            Say($"grouping restored: {r} ({string.Join(", ", j.Glom.Original.Select(RegistryValues.Plan))})");
            problems += r == "ok" ? 0 : 1;
        }
        var now = RegistryValues.ReadAll(Registry.CurrentUser, j.Glom.KeyPath, RegistryValues.GlomNames);
        bool same = j.Glom.Original.Count == 0 || j.Glom.Original.All(o => now.Any(n => RegistryValues.Same(n, o)));
        Say($"grouping now {string.Join(", ", now)}: {(same ? "as before the run" : "DIFFERENT from before the run: " + string.Join(", ", j.Glom.Original))}");
        problems += same ? 0 : 1;

        if (Guid.TryParse(j.TempDesktop, out Guid temp))
        {
            // The README demo's temporary desktop: closed (Ctrl+Win+F4) only while it is the current one.
            Input.Enabled = true;
            bool gone = CloseTempDesktopCore(temp, j.DesktopsBeforeTemp.Select(g => Guid.Parse(g)).ToList(), s => Say("desktop: " + s));
            Input.Enabled = false;
            if (gone)
            {
                j.TempDesktop = null;
                Thread.Sleep(600);
            }
            problems += gone ? 0 : 1;
        }

        if (Guid.TryParse(j.OriginalDesktop, out Guid original))
        {
            var (desktops, current) = Monitors.VirtualDesktops();
            int cur = desktops.IndexOf(current), want = desktops.IndexOf(original);
            if (cur >= 0 && want >= 0 && cur != want)
            {
                Input.Enabled = true;
                for (int i = 0; i < Math.Abs(cur - want); i++)
                {
                    Input.Chord("Ctrl+Win+" + (want > cur ? "Right" : "Left"), Input.VK_LCONTROL, Input.VK_LWIN, want > cur ? Input.VK_RIGHT : Input.VK_LEFT);
                    Thread.Sleep(400);
                }
                Input.Enabled = false;
                Say($"virtual desktop switched back: {Monitors.VirtualDesktops().current == original}");
            }
        }

        if (windhawk != null && j.ModId != null)
        {
            var wh = new Windhawk(windhawk, s => Say(s));
            if (j.LogCaptureStarted)
            {
                j.LogCaptureStarted = !wh.StopLogCapture().Ok;
            }
            if (j.ModSettingsChanged)
            {
                bool ok = wh.SetModSetting(j.ModId, "excludedApps", Array.Empty<string>()).Ok &
                          wh.SetModSetting(j.ModId, "minimisedLast", true).Ok &
                          wh.SetModSetting(j.ModId, "skipFullyMinimisedGroups", true).Ok;
                j.ModSettingsChanged = !ok;
                problems += ok ? 0 : 1;
            }
            if (j.ModLoggingEnabledByRun)
            {
                j.ModLoggingEnabledByRun = !wh.SetModLogging(j.ModId, false).Ok;
            }
            if (disableMod)
            {
                problems += wh.DisableMod(j.ModId).Ok ? 0 : 1;
                j.ModDisabledByDemo = false;
            }
            else if (j.ModDisabledByDemo)
            {
                // The demo switched the mod off for its first scene. Back on, unless the run stopped
                // because Explorer crashed or hung: then it stays off until someone has looked.
                string why = j.SafeStopReason ?? "";
                if (why.StartsWith("explorer-crash", StringComparison.Ordinal) || why.StartsWith("explorer-hung", StringComparison.Ordinal))
                {
                    Say($"the demo left the mod disabled and the run stopped with {why}: left disabled; Enable-Mod -Id {j.ModId} when it is safe");
                    problems++;
                }
                else
                {
                    bool on = wh.EnableMod(j.ModId).Ok;
                    j.ModDisabledByDemo = !on;
                    Say($"the demo left the mod disabled: enabled again: {on}");
                    problems += on ? 0 : 1;
                }
            }
        }
        else if (j.LogCaptureStarted || j.ModSettingsChanged || j.ModLoggingEnabledByRun || j.ModDisabledByDemo)
        {
            Say("Windhawk functions not available: log capture, mod settings and logging left as they are");
            problems++;
        }

        j.Complete = true;
        j.Notes.Add($"{DateTime.UtcNow:o} recovered ({problems} problems)");
        Journal.Save(journalPath, j);
        if (!string.IsNullOrEmpty(j.ProgressLog))
        {
            ProgressLog.AppendDoneIfMissing(j.ProgressLog, "recovered");
        }
        Say($"done, {problems} problems");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>The step table (number, name, estimated seconds) in the order the steps run, for listing.</summary>
    public static IEnumerable<string> Describe()
    {
        var r = new LiveRunner(new RunOptions(), (_, _) => "{\"ok\":true}");
        return r.Table().Select(s => $"{s.Id,2} {s.Name,-34} eta={s.Eta,4:0}s{(s.NeedsMod ? "" : "  (no mod needed)")}");
    }

    /// <summary>
    /// What a run with this step selection would do, without doing anything: the steps in the order
    /// they run, each with its estimate, and the total (with the setup). Throws FormatException for a
    /// bad selection. Touches nothing (no Explorer, no windows, no Windhawk).
    /// </summary>
    public static IEnumerable<string> Plan(string steps)
    {
        var r = new LiveRunner(new RunOptions { Steps = steps }, (_, _) => "{\"ok\":true}");
        var table = r.Table();
        var selected = SelectSteps(table, steps);
        int i = 0;
        foreach (var s in selected)
        {
            i++;
            yield return $"STEP {i}/{selected.Count} {s.Name} eta={s.Eta:0}";
        }
        yield return $"TOTAL eta={selected.Sum(s => s.Eta) + SetupEta:0} (steps {selected.Sum(s => s.Eta):0} + setup {SetupEta:0})";
    }

    /// <summary>The selected steps in the table's order. The selection is a set of step numbers.</summary>
    static List<StepDef> SelectSteps(List<StepDef> table, string spec)
    {
        var ids = Logic.ParseSelection(spec, table.Max(s => s.Id));
        return table.Where(s => ids.Contains(s.Id)).ToList();
    }

    int Execute()
    {
        Directory.CreateDirectory(O.RunDir);
        _progressLog = OpenLog("progress.log");
        _detailLog = OpenLog("detail.log");
        Input.Log = s => Detail(s);
        Input.Enabled = !O.DryRun;
        Console.CancelKeyPress += OnCancel;
        _table = Table();

        List<StepDef> selected;
        try
        {
            selected = SelectSteps(_table, O.Steps);
        }
        catch (FormatException e)
        {
            Detail("bad -Steps: " + e.Message);
            Progress(Logic.TotalLine(0));
            Progress(Logic.DoneLine(0, 1, "aborted=bad-steps"));
            return 2;
        }

        Progress(Logic.TotalLine(selected.Sum(s => s.Eta) + SetupEta));
        Detail($"run dir {O.RunDir}; steps {string.Join(",", selected.Select(s => s.Id))}; dry={O.DryRun}; mod={O.ModPath}");
        try
        {
            Preflight(selected);
            int ordinal = 0;
            foreach (var def in selected)
            {
                ordinal++;
                RunStep(ordinal, selected.Count, def);
                if (_abort != null)
                {
                    break;
                }
            }
        }
        catch (AbortRunException e)
        {
            _abort = e.Message;
            Detail("ABORT " + e.Message);
        }
        catch (Exception e)
        {
            _abort = "runner-error: " + e.Message;
            Detail("RUNNER ERROR " + e);
        }
        finally
        {
            if (_abort != null)
            {
                SafeStop(_abort);
            }
            Cleanup();
            Progress(Logic.DoneLine(_pass, _fail, _abort != null ? "aborted=" + _abort : null));
            if (J != null)
            {
                J.Complete = true;
                SaveJournal();
            }
            Console.CancelKeyPress -= OnCancel;
            _progressLog?.Dispose();
            _detailLog?.Dispose();
        }
        return _abort != null ? 2 : _fail > 0 ? 1 : 0;
    }

    void OnCancel(object sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _cancel = true;
        Detail("Ctrl+C: stopping safely at the next checkpoint");
    }

    StreamWriter OpenLog(string name) =>
        new(new FileStream(Path.Combine(O.RunDir, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };

    // ------------------------------------------------------------------ logging

    void Progress(string line)
    {
        lock (_logLock)
        {
            try
            {
                _progressLog?.WriteLine(line);
                _detailLog?.WriteLine($"{Clock.Elapsed.TotalSeconds,8:0.000} {line}");
                Console.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    void Detail(string line)
    {
        lock (_logLock)
        {
            try
            {
                _detailLog?.WriteLine($"{Clock.Elapsed.TotalSeconds,8:0.000} {line}");
            }
            catch (ObjectDisposedException)
            {
                // A background thread logging after the run ended.
            }
        }
    }

    void Check(string name, bool pass, string detail, bool dry = false)
    {
        bool isDry = dry || (O.DryRun && !pass);
        var c = new CheckRecord { Id = $"s{_step?.Id ?? 0:00}.{++_checkSeq}", Name = name, Pass = pass, Dry = isDry, Detail = Logic.OneLine(detail) };
        _checks.Add(c);
        string verdict = isDry ? "DRY" : pass ? "PASS" : "FAIL";
        Detail($"CHECK {c.Id} {name} {verdict} {c.Detail}");
        if (!isDry && !pass)
        {
            Console.WriteLine($"  CHECK {c.Id} {name} FAIL {c.Detail}");
        }
    }

    void Info(string name, string detail)
    {
        var c = new CheckRecord { Id = $"s{_step?.Id ?? 0:00}.{++_checkSeq}", Name = name, Pass = true, Info = true, Detail = Logic.OneLine(detail) };
        _checks.Add(c);
        Detail($"INFO {c.Id} {name} {c.Detail}");
    }

    // ------------------------------------------------------------------ steps

    void RunStep(int ordinal, int count, StepDef def)
    {
        _step = def;
        _checks.Clear();
        _checkSeq = 0;
        Progress(Logic.StepStartLine(ordinal, count, def.Name, def.Eta));
        var sw = Stopwatch.StartNew();
        string detail;
        bool pass;
        try
        {
            CheckStop();
            if (def.NeedsMod && !_modOk && !O.DryRun)
            {
                throw new InvalidOperationException("prerequisite: the mod is not loaded in Explorer (see step 2)");
            }
            W?.Mark($"step {def.Id} {def.Name}");
            def.Body();
            var failed = _checks.Where(c => !c.Pass && !c.Dry && !c.Info).ToList();
            pass = failed.Count == 0;
            int dry = _checks.Count(c => c.Dry), passed = _checks.Count(c => c.Pass && !c.Info);
            detail = pass
                ? $"{passed} checks passed{(dry > 0 ? $", {dry} dry" : "")}"
                : $"{failed.Count} of {_checks.Count(c => !c.Info)} checks failed: " + string.Join("; ", failed.Select(f => $"{f.Id} {f.Name}: {Trim(f.Detail, 160)}"));
        }
        catch (AbortRunException e)
        {
            _abort = e.Message;
            pass = false;
            detail = "aborted: " + e.Message;
        }
        catch (Exception e)
        {
            pass = false;
            detail = $"exception: {e.GetType().Name}: {e.Message}";
            Detail("STEP EXCEPTION " + e);
        }
        if (pass)
        {
            _pass++;
        }
        else
        {
            _fail++;
        }
        Progress(Logic.StepEndLine(ordinal, count, def.Name, pass, Trim(detail, 600), sw.Elapsed.TotalSeconds));
    }

    static string Trim(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "...";

    void CheckStop()
    {
        if (_stopping)
        {
            return;  // safe stop and cleanup run to the end
        }
        if (W?.Crash != null)
        {
            throw new AbortRunException("explorer-crash: " + W.Crash);
        }
        if (W?.Hang != null)
        {
            throw new AbortRunException("explorer-hung: " + W.Hang);
        }
        if (_cancel || File.Exists(Path.Combine(O.RunDir, "STOP")))
        {
            throw new AbortRunException("stop-requested");
        }
    }

    void Sleep(double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            CheckStop();
            Thread.Sleep((int)Math.Max(1, Math.Min(20, (seconds - sw.Elapsed.TotalSeconds) * 1000)));
        }
    }

    // ------------------------------------------------------------------ journal

    string StateDir => Path.Combine(O.WorkDir, "state");

    void SaveJournal()
    {
        try
        {
            Journal.Save(_journalPath, J);
        }
        catch (Exception e)
        {
            Detail("journal save failed: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ preflight

    void Preflight(List<StepDef> selected)
    {
        _journalPath = Path.Combine(O.RunDir, "journal.json");
        J = new JournalData
        {
            RunId = Path.GetFileName(O.RunDir),
            StartedUtc = DateTime.UtcNow.ToString("o"),
            RunnerPid = Environment.ProcessId,
            ProgressLog = Path.Combine(O.RunDir, "progress.log"),
            DetailLog = Path.Combine(O.RunDir, "detail.log"),
            DryRun = O.DryRun,
        };
        J.Glom.Original = RegistryValues.ReadAll(Registry.CurrentUser, RegistryValues.AdvancedKey, RegistryValues.GlomNames);
        SaveJournal();
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(Path.Combine(O.RunDir, "captures"));
        if (!O.DryRun)
        {
            // What -Recover without -RunDir picks up: the last live run.
            File.WriteAllText(Path.Combine(StateDir, "active-run.txt"), _journalPath);
        }

        Win32.SetThreadDpiAwarenessContext(Win32.DPI_PER_MONITOR_AWARE_V2);
        Detail($"os {Environment.OSVersion.VersionString} ubr={Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", "?")}");
        var shell = ExplorerInfo.Shell();
        Detail("shell " + shell);
        if (shell.Pid == 0)
        {
            throw new AbortRunException("preflight: no shell (Shell_TrayWnd) found");
        }

        Mons = Monitors.All();
        foreach (var m in Mons)
        {
            Detail("monitor " + m);
        }
        Primary = Mons.First(m => m.Primary);
        LeftMon = Mons.Where(m => m.Bounds.Right <= Primary.Bounds.Left).OrderByDescending(m => m.Bounds.Right).FirstOrDefault();
        RightMon = Mons.Where(m => m.Bounds.Left >= Primary.Bounds.Right).OrderBy(m => m.Bounds.Left).FirstOrDefault();
        Detail("glom original: " + string.Join(", ", J.Glom.Original));
        var mode = RegistryValues.Read(Registry.CurrentUser, RegistryValues.AdvancedKey, "MMTaskbarMode");
        Detail("MMTaskbarMode: " + mode);

        (_desktops, _desktopAtStart) = Monitors.VirtualDesktops();
        J.OriginalDesktop = _desktopAtStart.ToString();
        Detail($"virtual desktops: {string.Join(", ", _desktops)}; current {_desktopAtStart}");
        _cursorAtStart = Input.Cursor();
        _foregroundAtStart = Win32.GetForegroundWindow();
        Detail($"cursor {_cursorAtStart}; foreground 0x{_foregroundAtStart.ToInt64():X} {Win32.ClassOf(_foregroundAtStart)}");

        using (var probe = new Probe())
        {
            string d;
            try
            {
                d = probe.Describe();
            }
            catch (Exception e)
            {
                throw new AbortRunException("preflight: Explorer's lists cannot be read: " + e.Message);
            }
            Detail("probe " + d);
            var snap = probe.Read();
            Detail($"probe read {snap.Taskbars.Count} taskbars, {snap.Groups.Count} groups in {snap.ReadMs:0.0} ms; warnings: {snap.Warnings.Count}");
        }

        string source = File.ReadAllText(O.ModPath);
        ModId = Logic.ModMetadata(source, "id");
        ModVersion = Logic.ModMetadata(source, "version");
        if (string.IsNullOrEmpty(ModId))
        {
            throw new AbortRunException($"preflight: no // @id in {O.ModPath}");
        }
        J.ModId = ModId;
        SaveJournal();
        Detail($"mod {ModId} {ModVersion} from {O.ModPath}");
        Log = new ModLog(Path.Combine(O.RunDir, "mod.log"), ModId);

        if (!File.Exists(O.TestAppExe))
        {
            throw new AbortRunException("preflight: test app not built: " + O.TestAppExe);
        }

        // Leftovers of an earlier run: our own processes and windows only.
        foreach (var p in Process.GetProcessesByName("TsoTestApp"))
        {
            Detail($"leftover TsoTestApp pid={p.Id}: killing");
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        var stray = ExplorerInfo.WindowsWithPrefix(O.Prefix);
        if (stray.Count > 0)
        {
            Detail("windows with the test prefix before the run: " + string.Join(", ", stray.Select(s => $"0x{s.hwnd.ToInt64():X} pid={s.pid} '{s.title}'")));
        }

        var state = Wh.State();
        Detail("windhawk state before: " + state);
        if (O.DryRun && Windhawk.Find(state.Result, "Stub") != "true")
        {
            throw new AbortRunException("preflight: dry run but the Windhawk functions are not the stubs");
        }
        J.WindhawkVersionBefore = Windhawk.FindVersion(state.Result);
        SaveJournal();

        _modOk = ModLoadedInExplorer();
        Detail($"mod loaded in Explorer before the run: {_modOk}");
        if (!selected.Any(s => s.Id == 2) && selected.Any(s => s.NeedsMod))
        {
            // A rerun of later steps: log and capture the mod that is already installed.
            var r = Wh.SetModLogging(ModId, true);
            J.ModLoggingEnabledByRun = r.Ok;
            Detail("logging for a rerun: " + r);
            r = Wh.StartLogCapture(Log.Path);
            J.LogCaptureStarted = r.Ok;
            SaveJournal();
            Detail("log capture for a rerun: " + r);
        }

        W = new Watcher(Clock, () => _keys, s => Detail(s));
        W.Start();
        for (int i = 0; i < 100 && W.LatestOrNull() == null && W.LastProbeError == null; i++)
        {
            Thread.Sleep(20);
        }
        Detail("watcher started; baseline " + W.Baseline);
    }

    bool ModLoadedInExplorer()
    {
        try
        {
            var shell = ExplorerInfo.Shell();
            return shell.Pid != 0 && ExplorerInfo.Modules(shell.Pid, n => n.Contains(ModId, StringComparison.OrdinalIgnoreCase)).Count > 0;
        }
        catch (Exception e)
        {
            Detail("module check failed: " + e.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ test app and windows

    void EnsureApp()
    {
        if (App != null && App.Alive)
        {
            return;
        }
        if (App != null)
        {
            Detail("test app is not alive; starting a new one");
            App.Kill();
            App = null;
        }
        _hwnd.Clear();
        _itemRef.Clear();
        Publish();
        _taskbarStateRequested = false;
        string pipe = $"tso-livetest-{Environment.ProcessId}-{Clock.ElapsedMilliseconds}";
        App = TestAppClient.Start(O.TestAppExe, pipe, Environment.ProcessId, _journalPath, Path.Combine(O.RunDir, "testapp.log"),
            O.DryRun, O.DeadmanSec, O.IdleSec, O.Aumid, O.Prefix);
        App.Log = s => Detail(s);
        App.StartPinger();
        J.TestAppPid = App.Process.Id;
        SaveJournal();
        W.SetTestPid(App.Process.Id);
        Detail($"test app pid={App.Process.Id} hidden={O.DryRun} pipe={pipe}");
    }

    void Publish() => _keys = _hwnd.ToDictionary(kv => kv.Value, kv => kv.Key);

    IntPtr H(string key) => _hwnd.TryGetValue(key, out var h) ? h : throw new InvalidOperationException($"window {key} is not open");

    bool IsOpen(string key) => _hwnd.TryGetValue(key, out var h) && Win32.IsWindow(h);

    void Act(string what)
    {
        CheckStop();
        _lastActionT = W.Now;
        W.Mark(what);
        Detail("ACT " + what);
    }

    static readonly Dictionary<string, int> WinRgb = new()
    {
        ["A"] = 0xC81E1E, ["B"] = 0x1E8C1E, ["C"] = 0x1E46C8, ["D"] = 0xB8860B, ["E"] = 0x7A1E9C, ["F"] = 0x1E8C8C,
    };
    static readonly Dictionary<string, int> BadgeRgb = new()
    {
        ["A"] = 0xFF00FF, ["B"] = 0x00FFFF, ["C"] = 0xFF8000, ["D"] = 0x0000FF, ["E"] = 0x00FF00,
    };
    static readonly Dictionary<string, (string state, int value, string hue)> ProgressSpec = new()
    {
        ["A"] = ("normal", 25, "green"), ["B"] = ("error", 50, "red"), ["C"] = ("paused", 75, "yellow"), ["D"] = ("normal", 100, "green"),
    };
    static readonly string[] Main4 = { "A", "B", "C", "D" };
    static readonly string[] MainKeys = { "A", "B", "C", "D", "E", "F" };
    string SentinelAumid => O.Aumid + ".Sentinel";
    string AltAumid => O.Aumid + ".Alt";

    static int Rgb(string key) => WinRgb.TryGetValue(key, out int v) ? v : 0x606060;

    void Open(string key, RECT frame, string aumid = null)
    {
        Act($"open {key} at {frame}{(aumid != null ? " aumid=" + aumid : "")}");
        var r = App.Cmd("open", key, frame.Left, frame.Top, frame.Width, frame.Height,
            new Command.Kv("color", Rgb(key).ToString("X6")), new Command.Kv("aumid", aumid));
        _hwnd[key] = ParseHwnd(r["window"]?["hwnd"]?.ToString());
        _itemRef.Remove(key);
        Publish();
    }

    static IntPtr ParseHwnd(string hex) =>
        hex != null && hex.StartsWith("0x") ? new IntPtr(long.Parse(hex.Substring(2), NumberStyles.HexNumber)) : throw new FormatException("bad hwnd " + hex);

    void Close(string key)
    {
        Act($"close {key}");
        App.Cmd("close", key);
        _hwnd.Remove(key);
        _itemRef.Remove(key);
        Publish();
    }

    void Move(string key, RECT frame)
    {
        Act($"move {key} to {frame}");
        App.Cmd("move", key, frame.Left, frame.Top, frame.Width, frame.Height);
    }

    void Show(string verb, string key)
    {
        Act($"{verb} {key}");
        App.Cmd(verb, key);
    }

    RECT Frame(string key)
    {
        Win32.FrameRect(H(key), out RECT r);
        return r;
    }

    // ------------------------------------------------------------------ input safety
    // Injected clicks and keys must only ever reach the test app's windows or the taskbar: every
    // click checks what is under the point first, every key what is in the foreground.

    static IntPtr RootAt(POINT p) => Native.GetAncestor(Native.WindowFromPoint(p), Native.GA_ROOT);

    /// <summary>Whether the window under a point is the given test window (always true in a dry run).</summary>
    bool PointIsWindow(POINT p, string key, out string what)
    {
        what = "dry";
        if (O.DryRun)
        {
            return true;
        }
        IntPtr root = RootAt(p);
        what = $"0x{root.ToInt64():X} {Win32.ClassOf(root)} '{Win32.TitleOf(root)}'";
        return root == H(key);
    }

    /// <summary>Whether the window under a point is a taskbar of the shell.</summary>
    bool PointIsTaskbar(POINT p, out string what)
    {
        what = "dry";
        if (O.DryRun)
        {
            return true;
        }
        IntPtr root = RootAt(p);
        string cls = Win32.ClassOf(root);
        what = $"0x{root.ToInt64():X} {cls}";
        return (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && Win32.PidOf(root) == ExplorerInfo.Shell().Pid;
    }

    /// <summary>Puts test windows on top (without activating them).</summary>
    void Raise(params string[] keys)
    {
        foreach (var k in keys.Where(IsOpen))
        {
            App.Cmd("raise", k);
        }
    }

    /// <summary>The standard place of a window on the primary monitor.</summary>
    RECT Std(string key) => key switch
    {
        "A" => Primary.At(0.08, 0.28, 640, 420),
        "B" => Primary.At(0.29, 0.28, 640, 420),
        "C" => Primary.At(0.49, 0.28, 640, 420),
        "D" => Primary.At(0.70, 0.28, 640, 420),
        "E" => Primary.At(0.39, 0.40, 640, 420),
        "Z" => Primary.At(0.86, 0.62, 400, 300),
        _ => Primary.At(0.5, 0.5, 500, 350),
    };

    RECT AtX(string key, double fx) => Primary.At(fx, 0.28, Std(key).Width, Std(key).Height);

    /// <summary>A, B, C, D restored at their standard places on the primary monitor, the sentinel Z
    /// open after them, nothing else open, default settings, original grouping. Ends sorted A,B,C,D.</summary>
    void Baseline(bool waitSorted = true)
    {
        EnsureApp();
        RestoreGlom("baseline glom");
        EnsureDefaultSettings();
        RestoreDesktop();
        foreach (var k in _hwnd.Keys.ToList())
        {
            if (!Main4.Contains(k) && k != "Z")
            {
                Close(k);
            }
        }
        foreach (var k in _changedAumid.ToList())
        {
            if (IsOpen(k))
            {
                App.Cmd("aumid", k, "default");
            }
            _changedAumid.Remove(k);
        }
        foreach (var k in _onOtherDesktop.ToList())
        {
            if (IsOpen(k))
            {
                App.Cmd("vdmove", k, _desktopAtStart.ToString());
            }
            _onOtherDesktop.Remove(k);
        }
        foreach (var k in Main4)
        {
            if (!IsOpen(k))
            {
                Open(k, Std(k));
                RequestTaskbarState(k);
            }
            else
            {
                if (!O.DryRun && Win32.IsIconic(H(k)))
                {
                    Show("unmin", k);
                }
                if (!O.DryRun && Win32.IsZoomed(H(k)))
                {
                    Show("restore", k);
                }
                Move(k, Std(k));
            }
        }
        if (!IsOpen("Z"))
        {
            Open("Z", Std("Z"), SentinelAumid);
        }
        Raise("A", "B", "C", "D", "Z");
        if (waitSorted)
        {
            ExpectOrder("baseline", "A,B,C,D", timeoutS: 4);
        }
    }

    readonly HashSet<string> _changedAumid = new();
    readonly HashSet<string> _onOtherDesktop = new();

    /// <summary>Back on the desktop the run started on, if a step left it elsewhere.</summary>
    void RestoreDesktop()
    {
        if (O.DryRun || _desktops.Count < 2 || _desktopAtStart == Guid.Empty)
        {
            return;
        }
        int cur = _desktops.IndexOf(CurrentDesktop()), want = _desktops.IndexOf(_desktopAtStart);
        for (int i = 0; cur >= 0 && want >= 0 && cur != want && i < 4; i++)
        {
            Input.Chord("Ctrl+Win+" + (want > cur ? "Right" : "Left"), Input.VK_LCONTROL, Input.VK_LWIN, want > cur ? Input.VK_RIGHT : Input.VK_LEFT);
            Thread.Sleep(500);
            cur = _desktops.IndexOf(CurrentDesktop());
        }
        J.DesktopSwitched = CurrentDesktop() != _desktopAtStart;
        SaveJournal();
    }

    void RequestTaskbarState(string key)
    {
        if (!ProgressSpec.ContainsKey(key))
        {
            return;
        }
        var p = ProgressSpec[key];
        App.Cmd("progress", key, p.state, p.value);
        App.Cmd("overlay", key, BadgeRgb[key].ToString("X6"));
        App.Cmd("thumbbar", key, 2);
        _taskbarStateRequested = true;
    }

    // ------------------------------------------------------------------ expectations

    List<WinGeom> Geoms(IEnumerable<string> keys) => keys.Select(k => Logic.Geometry(k, H(k))).ToList();

    string Model(IEnumerable<string> keys) => Logic.Join(Logic.ExpectedOrder(Geoms(keys), Settings.minimisedLast));

    (bool ok, TestView view, double latency) WaitFor(Func<TestView, bool> pred, double timeoutS, double stableS)
    {
        double deadline = W.Now + timeoutS, since = -1;
        TestView last = null;
        while (true)
        {
            CheckStop();
            TestView v;
            try
            {
                v = W.Latest();
            }
            catch (InvalidOperationException)
            {
                v = null;
            }
            if (v != null)
            {
                last = v;
                if (pred(v))
                {
                    if (since < 0)
                    {
                        since = v.T;
                    }
                    if (v.T - since >= stableS)
                    {
                        return (true, v, since - _lastActionT);
                    }
                }
                else
                {
                    since = -1;
                }
            }
            if (W.Now > deadline)
            {
                return (false, last, double.NaN);
            }
            Thread.Sleep(10);
        }
    }

    static bool ButtonsConsistent(TestGroup g, IList<string> withButtons, out string why)
    {
        foreach (var kv in g.L2)
        {
            if (!Logic.IsOrderedSubsequence(kv.Value, g.L1))
            {
                why = $"t{kv.Key} buttons {Logic.Join(kv.Value)} are not in item-list order";
                return false;
            }
        }
        var all = g.L2.Values.SelectMany(x => x).ToList();
        var missing = withButtons.Where(k => !all.Contains(k)).ToList();
        var extra = all.Where(k => !withButtons.Contains(k)).ToList();
        var dup = all.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        if (missing.Count > 0 || dup.Count > 0 || extra.Count > 0)
        {
            why = $"buttons: missing [{Logic.Join(missing)}] extra [{Logic.Join(extra)}] duplicated [{Logic.Join(dup)}]";
            return false;
        }
        why = null;
        return true;
    }

    /// <summary>
    /// Waits until the group holding these windows has the item list `expected` and every taskbar's
    /// buttons of it follow that list, and stay so for stableS. The order the mod should produce is
    /// also computed from the windows' geometry with the mod's rule; a difference means the test's
    /// layout is not what it intended, and is reported as a failed "model" check.
    /// </summary>
    bool ExpectOrder(string label, string expected, double timeoutS = 3.0, double stableS = 0.35, string[] noButtons = null, bool model = true)
    {
        var keys = Logic.Split(expected);
        if (model)
        {
            // In a dry run the windows are hidden but placed, so this still checks the layout.
            string m = Model(keys);
            Check(label + " layout", m == expected, m == expected
                ? $"the mod's rule on the windows' geometry gives {m}"
                : $"the mod's rule gives {m}, the test expects {expected}: {string.Join(" ", Geoms(keys))}");
        }
        var withButtons = keys.Where(k => noButtons == null || !noButtons.Contains(k)).ToList();
        if (O.DryRun)
        {
            var v = W.LatestOrNull();
            Check(label, true, $"would wait {timeoutS}s for L1={expected}; Explorer has: {v?.GroupOfAny(keys)?.L1Text ?? "no test group (hidden windows)"}", dry: true);
            return true;
        }
        var (ok, view, latency) = WaitFor(v =>
        {
            var g = v.GroupOfAny(keys);
            return g != null && g.L1Text == expected && ButtonsConsistent(g, withButtons, out _);
        }, timeoutS, stableS);
        var group = view?.GroupOfAny(keys);
        string why = "";
        if (group == null)
        {
            why = "group not found";
        }
        else if (!ButtonsConsistent(group, withButtons, out string w))
        {
            why = w;
        }
        Check(label, ok, ok
            ? $"L1={expected} buttons {group.L2Text} after {latency:0.000}s"
            : $"want L1={expected}; have L1={group?.L1Text} buttons {group?.L2Text} {why}");
        if (ok)
        {
            CheckIdentity(label, view);
        }
        return ok;
    }

    /// <summary>Checks that the group's lists do not change for durationS (a sort that must not happen).</summary>
    bool ExpectUnchanged(string label, string expected, double durationS)
    {
        var keys = Logic.Split(expected);
        if (O.DryRun)
        {
            Check(label, true, $"would watch L1={expected} stay unchanged for {durationS}s", dry: true);
            return true;
        }
        double t0 = W.Now;
        var start = W.Latest().GroupOfAny(keys);
        string l1 = start?.L1Text, l2 = start?.L2Text;
        Sleep(durationS);
        var changes = W.Timeline(t0, W.Now).Where(e => e.Kind == "state").Select(e => e.View.GroupOfAny(keys))
                       .Where(g => g != null && (g.L1Text != l1 || g.L2Text != l2)).Select(g => g.L1Text + " " + g.L2Text).ToList();
        bool ok = l1 == expected && changes.Count == 0;
        Check(label, ok, ok ? $"L1={l1} unchanged for {durationS}s" : $"start L1={l1} (want {expected}); changes: {string.Join("; ", changes.Take(5))}");
        return ok;
    }

    /// <summary>The window buttons of the group on each taskbar, as "monitor index:keys".</summary>
    bool ExpectTrays(string label, string expected, Dictionary<MonitorInfo, string> perMonitor, double timeoutS = 3.0)
    {
        var keys = Logic.Split(expected);
        string m = Model(keys);
        Check(label + " layout", m == expected, m == expected
            ? $"the mod's rule on the windows' geometry gives {m}"
            : $"the mod's rule gives {m}, the test expects {expected}: {string.Join(" ", Geoms(keys))}");
        foreach (var kv in perMonitor)
        {
            // Which monitor each window is on (its frame's centre), as the taskbars should show them.
            var on = keys.Where(k => Monitors.Of(Frame(k), Mons)?.Device == kv.Key.Device).ToList();
            string wantHere = Logic.Join(Logic.Split(expected).Where(on.Contains));
            if (wantHere != kv.Value)
            {
                Check(label + " layout per monitor", false, $"{kv.Key.Device} holds {wantHere}, the test expects {kv.Value}");
            }
        }
        if (O.DryRun)
        {
            Check(label, true, "would check the buttons per taskbar: " + string.Join("; ", perMonitor.Select(kv => $"{kv.Key.Device}={kv.Value}")), dry: true);
            return true;
        }
        string Describe(TestView v, TestGroup g) =>
            string.Join("; ", v.TrayMonitors.Select((m, i) => $"{m}={(g.L2.TryGetValue(i, out var l) ? Logic.Join(l) : "-")}"));
        bool Match(TestView v)
        {
            var g = v.GroupOfAny(keys);
            if (g == null || g.L1Text != expected)
            {
                return false;
            }
            foreach (var kv in perMonitor)
            {
                int ti = v.TrayMonitors.IndexOf(kv.Key.Device);
                string have = ti >= 0 && g.L2.TryGetValue(ti, out var l) ? Logic.Join(l) : "";
                if (have != kv.Value)
                {
                    return false;
                }
            }
            return true;
        }
        var (ok, view, latency) = WaitFor(Match, timeoutS, 0.3);
        var group = view?.GroupOfAny(keys);
        Check(label, ok, ok
            ? $"L1={expected}; {Describe(view, group)} after {latency:0.000}s"
            : $"want L1={expected} and {string.Join("; ", perMonitor.Select(kv => $"{kv.Key.Device}={kv.Value}"))}; have L1={group?.L1Text}; {(group != null ? Describe(view, group) : "")}");
        return ok;
    }

    /// <summary>Survival: the Explorer item behind each window is the same object as before (a
    /// rebuilt item would have lost the window's progress, badge and toolbar buttons).</summary>
    void CheckIdentity(string label, TestView view)
    {
        var changed = new List<(string key, long before, long after)>();
        foreach (var kv in view.ItemOf)
        {
            if (!_hwnd.ContainsKey(kv.Key))
            {
                continue;
            }
            if (_itemRef.TryGetValue(kv.Key, out long before) && before != kv.Value)
            {
                changed.Add((kv.Key, before, kv.Value));
            }
            _itemRef[kv.Key] = kv.Value;
        }
        string text = string.Join(", ", changed.Select(c => $"{c.key} 0x{c.before:X}->0x{c.after:X}"));
        var rec = new CheckRecord { Name = "item identity after " + label, Pass = changed.Count == 0, Detail = changed.Count == 0 ? "same items" : text };
        Survival.Add(rec);
        if (changed.Count > 0)
        {
            Check("item identity after " + label, false, "Explorer items were replaced: " + text);
        }
    }

    /// <summary>Checks the mod's log since a mark: no "Unexpected", no failure lines.</summary>
    ModLogFacts CheckLog(string label, long mark)
    {
        var f = Log.Facts(mark);
        if (O.DryRun && !Log.Exists)
        {
            Check(label, true, "no log in a dry run", dry: true);
            return f;
        }
        bool ok = f.Unexpected.Count == 0 && f.Failures.Count == 0;
        Check(label, ok, ok
            ? $"{f.Lines} new lines, no Unexpected; reorders of the test app {f.Reorders.Count(r => r.process.Equals("TsoTestApp.exe", StringComparison.OrdinalIgnoreCase))}"
            : $"Unexpected: {string.Join(" | ", f.Unexpected.Take(3))} failures: {string.Join(" | ", f.Failures.Take(3))}");
        foreach (var w in f.Warnings.Take(5))
        {
            Info(label + " warning", w);
        }
        return f;
    }

    /// <summary>Waits until the log since the mark satisfies a condition.</summary>
    (bool ok, ModLogFacts facts) WaitLog(long mark, Func<ModLogFacts, bool> pred, double timeoutS)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            CheckStop();
            var f = Log.Facts(mark);
            if (pred(f))
            {
                return (true, f);
            }
            if (sw.Elapsed.TotalSeconds > timeoutS)
            {
                return (false, f);
            }
            Thread.Sleep(100);
        }
    }

    // ------------------------------------------------------------------ settings

    /// <summary>Changes one of the mod's settings. Booleans are passed as true/false (windhawk-cli
    /// checks each value against the type of the setting's default); excludedApps as a whole array.</summary>
    void SetSetting(string key, object value, string label)
    {
        long mark = Log.Mark();
        string shown = value is string[] a ? "[" + string.Join(",", a) + "]" : value?.ToString();
        Act($"setting {key}={shown}");
        var r = Wh.SetModSetting(ModId, key, value);
        Check(label + " Set-ModSetting", r.Ok, r.ToString());
        J.ModSettingsChanged = true;
        SaveJournal();
        switch (key)
        {
            case "minimisedLast": Settings.minimisedLast = (bool)value; break;
            case "skipFullyMinimisedGroups": Settings.skip = (bool)value; break;
            case "excludedApps": Settings.excluded = string.Join(",", (string[])value); break;
        }
        if (!O.DryRun)
        {
            // A settings change reloads the mod: it logs its offsets again.
            var (ok, f) = WaitLog(mark, x => x.ButtonGroupsSlot != null, 6);
            if (ok)
            {
                Info(label + " reload", $"mod reloaded ({f.Lifecycle} lifecycle lines)");
            }
            else
            {
                Check(label + " reload", false, "no reload in the log within 6s");
            }
            Sleep(0.2);
        }
    }

    void EnsureDefaultSettings()
    {
        if (Settings.excluded != "")
        {
            SetSetting("excludedApps", Array.Empty<string>(), "default excludedApps");
        }
        if (!Settings.minimisedLast)
        {
            SetSetting("minimisedLast", true, "default minimisedLast");
        }
        if (!Settings.skip)
        {
            SetSetting("skipFullyMinimisedGroups", true, "default skipFullyMinimisedGroups");
        }
    }

    // ------------------------------------------------------------------ grouping (glom)

    bool _glomDry;

    bool NeverCombineOn => _glomDry || (J.Glom.Changed && !J.Glom.Restored);

    void SetNeverCombine(string label)
    {
        Act("grouping: never combine");
        if (O.DryRun)
        {
            // Nothing is written, so nothing is journaled (a guardian must not "restore" anything).
            _glomDry = true;
            Detail("DRY skip: write TaskbarGlomLevel=2" + (O.GlomPrimaryOnly ? "" : ", MMTaskbarGlomLevel=2") + " and broadcast");
            Check(label, true, "dry", dry: true);
            return;
        }
        J.Glom.Changed = true;
        J.Glom.Restored = false;
        J.Glom.RestoredBy = null;
        SaveJournal();  // before the change, so every path can put it back
        double hang = W.HangS;
        W.HangS = 30;  // uncombining every taskbar's buttons can keep the taskbar busy for a while
        try
        {
            RegistryValues.WriteDword(Registry.CurrentUser, J.Glom.KeyPath, "TaskbarGlomLevel", 2);
            if (!O.GlomPrimaryOnly)
            {
                RegistryValues.WriteDword(Registry.CurrentUser, J.Glom.KeyPath, "MMTaskbarGlomLevel", 2);
            }
            Win32.BroadcastTraySettings();
            var keys = Main4.Where(IsOpen).ToList();
            var sw = Stopwatch.StartNew();
            List<string> seen = null;
            while (sw.Elapsed.TotalSeconds < 10)
            {
                CheckStop();
                try
                {
                    seen = WindowButtons(Primary.Device);
                    if (keys.All(seen.Contains))
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    Detail("UIA while waiting for uncombined buttons: " + e.Message);
                }
                Sleep(0.25);
            }
            Check(label, seen != null && keys.All(seen.Contains), $"per-window buttons on the primary taskbar: {Logic.Join(seen ?? new List<string>())} after {sw.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            W.HangS = hang;
        }
    }

    void RestoreGlom(string label)
    {
        if (!NeverCombineOn)
        {
            return;
        }
        Act("grouping: restore");
        if (O.DryRun)
        {
            _glomDry = false;
            var unchanged = RegistryValues.ReadAll(Registry.CurrentUser, J.Glom.KeyPath, RegistryValues.GlomNames);
            Check(label, true, $"dry: would {string.Join(", ", J.Glom.Original.Select(RegistryValues.Plan))}; values now {string.Join(", ", unchanged)}", dry: true);
            return;
        }
        double hang = W.HangS;
        W.HangS = 30;
        string result = RegistryValues.RestoreAll(Registry.CurrentUser, J.Glom.KeyPath, J.Glom.Original);
        Win32.BroadcastTraySettings();
        var now = RegistryValues.ReadAll(Registry.CurrentUser, J.Glom.KeyPath, RegistryValues.GlomNames);
        bool same = J.Glom.Original.All(o => now.Any(n => RegistryValues.Same(n, o)));
        Check(label, result == "ok" && same, $"restore: {string.Join(", ", J.Glom.Original.Select(RegistryValues.Plan))}; read back {string.Join(", ", now)}");
        J.Glom.Restored = result == "ok" && same;
        J.Glom.RestoredBy = "runner";
        J.Glom.RestoredUtc = DateTime.UtcNow.ToString("o");
        SaveJournal();
        W.HangS = hang;
        if (!O.DryRun)
        {
            var sw = Stopwatch.StartNew();
            UiaButton group = null;
            bool anyOpen = Main4.Any(IsOpen);
            while (anyOpen && sw.Elapsed.TotalSeconds < 10)
            {
                CheckStop();
                try
                {
                    group = Uia.TaskbarButtons().FirstOrDefault(b => b.AppId == O.Aumid);
                    if (group != null)
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    Detail("UIA while waiting for the combined button: " + e.Message);
                }
                Sleep(0.25);
            }
            if (anyOpen)
            {
                Check(label + " combined again", group != null, group != null ? $"group button '{group.Name}' after {sw.Elapsed.TotalSeconds:0.0}s" : "no combined group button within 10s");
            }
        }
    }

    /// <summary>The test windows' own buttons (grouping off) on a monitor's taskbar, left to right.</summary>
    List<string> WindowButtons(string monitorDevice)
    {
        return Uia.TaskbarButtons()
            .Where(b => KeyOfButton(b) != null)
            .Where(b => { Win32.GetWindowRect(b.Tray, out RECT r); return Monitors.Of(r, Mons)?.Device == monitorDevice; })
            .OrderBy(b => b.Rect.X).Select(KeyOfButton).ToList();
    }

    /// <summary>The test window a taskbar button stands for (grouping off): by its AutomationId
    /// "Window: 0x...", or else by its name starting with the window's title. Null otherwise.</summary>
    string KeyOfButton(UiaButton b)
    {
        if (b.Window != IntPtr.Zero && _keys.TryGetValue(b.Window, out string k))
        {
            return k;
        }
        if (b.Name != null && b.Name.StartsWith(O.Prefix + " ", StringComparison.Ordinal))
        {
            string byTitle = KeyOfTitle(b.Name);
            return byTitle.StartsWith("?", StringComparison.Ordinal) ? null : byTitle;
        }
        return null;
    }

    // ------------------------------------------------------------------ survival of progress, badge, toolbar

    // The previous reading in this step, its image and the region of the test buttons in it.
    int _visualStep = -1;
    string _visualLabel;
    Dictionary<string, ButtonReading> _visualRead;
    System.Drawing.Bitmap _visualImage;
    System.Drawing.Rectangle _visualRegion;

    static string FileTag(string label) => System.Text.RegularExpressions.Regex.Replace(label, @"[^\w]+", "-").Trim('-');

    static Dictionary<string, int> IconColours(bool withSentinel = false)
    {
        var d = Main4.ToDictionary(k => k, Rgb);
        if (withSentinel)
        {
            d["Z"] = 0x606060;
        }
        return d;
    }

    IntPtr PrimaryTray() => Uia.Trays().FirstOrDefault(t => t.cls == "Shell_TrayWnd").hwnd;

    string CapturePath(string name) => Path.Combine(O.RunDir, "captures", name);

    /// <summary>The part of the taskbar image that holds the test buttons (from their icons), with room
    /// for the labels; the whole image if no icon is found.</summary>
    static System.Drawing.Rectangle TestButtonRegion(System.Drawing.Bitmap img)
    {
        var found = ButtonReader.ReadAll(img, IconColours(withSentinel: true)).Values.Where(r => r.Found).ToList();
        if (found.Count == 0)
        {
            return new System.Drawing.Rectangle(0, 0, img.Width, img.Height);
        }
        int x0 = Math.Max(0, found.Min(r => r.Icon.Left) - 70), x1 = Math.Min(img.Width, found.Max(r => r.Icon.Right) + 170);
        return new System.Drawing.Rectangle(x0, 0, x1 - x0, img.Height);
    }

    /// <summary>Reads every test window's own button from a settled taskbar image and checks it shows
    /// the badge and progress the test app set, and the same as before (if given).</summary>
    Dictionary<string, ButtonReading> CheckButtonImage(string label, System.Drawing.Bitmap img, Dictionary<string, ButtonReading> before)
    {
        var read = ButtonReader.ReadAll(img, IconColours());
        var problems = new List<string>();
        foreach (var k in Main4)
        {
            var spec = ProgressSpec[k];
            string why = ButtonReader.Mismatch(read[k], badge: true, spec.state, spec.value);
            if (why != null)
            {
                problems.Add($"{k}: {why}");
            }
            if (before != null && before.TryGetValue(k, out var b) && b.Found && read[k].Found &&
                (b.Badge != read[k].Badge || b.Progress != read[k].Progress || Math.Abs(b.Fill - read[k].Fill) > 0.07))
            {
                problems.Add($"{k} changed: {b.Short} -> {read[k].Short}");
            }
        }
        var rec = new CheckRecord
        {
            Name = "badge and progress " + label,
            Pass = problems.Count == 0,
            Detail = string.Join("  ", Main4.Select(k => read[k].ToString())) + (problems.Count > 0 ? " | " + string.Join("; ", problems) : ""),
        };
        Survival.Add(rec);
        Check(rec.Name, rec.Pass, rec.Detail);
        return read;
    }

    /// <summary>Whether each key's icon lies inside the UI Automation rectangle of its own button.</summary>
    void CrossCheckWithUia(Dictionary<string, ButtonReading> read, IntPtr tray)
    {
        Win32.GetWindowRect(tray, out RECT tr);
        var parts = new List<string>();
        foreach (var b in Uia.TaskbarButtons().Where(b => b.Tray == tray))
        {
            string k = KeyOfButton(b);
            if (k == null || !read.TryGetValue(k, out var r) || !r.Found)
            {
                continue;
            }
            var icon = new System.Drawing.Rectangle(r.Icon.X + tr.Left, r.Icon.Y + tr.Top, r.Icon.Width, r.Icon.Height);
            bool inside = b.Rect.Contains(icon.X + icon.Width / 2.0, icon.Y + icon.Height / 2.0);
            parts.Add($"{k}:{(inside ? "inside" : "OUTSIDE")} its button {b.Rect.X:0},{b.Rect.Width:0}");
        }
        Info("icons found in the image vs UIA button rectangles", parts.Count == 0 ? "no per-window buttons via UIA" : string.Join(" ", parts));
    }

    /// <summary>
    /// With grouping off: every test window's own button on the primary taskbar shows the badge and
    /// progress the test app set, read from a settled image of the taskbar with ButtonReader (two
    /// identical captures in a row, 3 s at most), and the same as at the previous reading in this
    /// step. Saves the image, a zoomed crop of the test buttons and, from the second reading of a
    /// step on, the previous reading's crop above this one (x3).
    /// </summary>
    void CheckTaskbarStateVisual(string label)
    {
        IntPtr tray = PrimaryTray();
        string tag = $"{_step.Id:00}-{FileTag(label)}";
        if (O.DryRun)
        {
            // Hidden windows have no buttons: exercise capture, settle, reader and crop on the real taskbar.
            using var dry = Capture.Window(tray);
            var dryRegion = TestButtonRegion(dry);
            var (settledDry, framesDry, secsDry, okDry) = ButtonReader.WaitSettled(() => Capture.Window(tray), dryRegion, 1.0);
            using (settledDry)
            {
                ButtonReader.SaveStacked(new[] { dry, settledDry }, new System.Drawing.Rectangle(dryRegion.X, 0, Math.Min(dryRegion.Width, 600), dryRegion.Height), 3, CapturePath(tag + "-dry-crop.png"));
            }
            Check("badge and progress " + label, true, $"dry: {framesDry} captures, settled={okDry} in {secsDry:0.00}s; readings on the real taskbar: {string.Join(" ", ButtonReader.ReadAll(dry, IconColours()).Values)}", dry: true);
            return;
        }
        System.Drawing.Rectangle region;
        using (var first = Capture.Window(tray))
        {
            region = TestButtonRegion(first);
        }
        var (img, frames, secs, settled) = ButtonReader.WaitSettled(() => Capture.Window(tray), region, 3.0);
        Info("taskbar image " + label, $"{(settled ? "settled" : "NOT settled")} after {frames} captures in {secs:0.00}s; test buttons in x {region.Left}..{region.Right}");
        img.Save(CapturePath(tag + "-taskbar.png"));
        ButtonReader.SaveStacked(new[] { img }, region, 3, CapturePath(tag + "-buttons-x3.png"));
        bool sameStep = _visualStep == _step.Id && _visualImage != null;
        var read = CheckButtonImage(label, img, sameStep ? _visualRead : null);
        CrossCheckWithUia(read, tray);
        if (sameStep)
        {
            var both = System.Drawing.Rectangle.Union(_visualRegion, region);
            string crop = CapturePath($"{_step.Id:00}-{FileTag(_visualLabel)}-above-{FileTag(label)}-x3.png");
            ButtonReader.SaveStacked(new[] { _visualImage, img }, both, 3, crop);
            Info("zoomed crop of the test buttons", $"'{_visualLabel}' above '{label}', x3: {crop}");
        }
        _visualImage?.Dispose();
        _visualImage = img;
        _visualRegion = region;
        _visualRead = read;
        _visualLabel = label;
        _visualStep = _step.Id;
    }

    // ------------------------------------------------------------------ flyout

    UiaButton GroupButton(string aumid, string monitorDevice = null)
    {
        foreach (var b in Uia.TaskbarButtons().Where(b => b.AppId == aumid))
        {
            Win32.GetWindowRect(b.Tray, out RECT r);
            if (monitorDevice == null || Monitors.Of(r, Mons)?.Device == monitorDevice)
            {
                return b;
            }
        }
        return null;
    }

    static POINT Center(System.Windows.Rect r) => new((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));

    /// <summary>Hovers a taskbar button until the flyout shows thumbnails; returns the last UIA read,
    /// and leaves the order by window colour and the toolbar-colour pixel count of a screen copy of
    /// the flyout in _lastImageOrder and _lastThumbPixels.</summary>
    FlyoutRead HoverRead(POINT target, string tag, Func<FlyoutRead, bool> done, double timeoutS = 4, Action whileOpen = null)
    {
        var shell = ExplorerInfo.Shell();
        POINT saved = Input.Cursor();
        FlyoutRead read = null;
        _lastImageOrder = new List<string>();
        _lastThumbPixels = 0;
        Act($"hover {target} ({tag})");
        using (Input.Hover(target))
        {
            var sw = Stopwatch.StartNew();
            do
            {
                CheckStop();
                try
                {
                    read = Uia.Flyout(shell.Pid, O.Prefix);
                }
                catch (Exception e)
                {
                    Detail("flyout read: " + e.Message);
                }
                if (O.DryRun || (read != null && read.Open && done(read)))
                {
                    break;
                }
                Sleep(0.15);
            }
            while (sw.Elapsed.TotalSeconds < timeoutS);
            CaptureFlyout(read, target, tag);
            whileOpen?.Invoke();
        }
        if (!O.DryRun)
        {
            Input.MoveTo(target.X, target.Y - 700);
            Thread.Sleep(250);
            Input.RestoreCursor(saved);
        }
        return read;
    }

    void CaptureFlyout(FlyoutRead read, POINT target, string tag)
    {
        string dir = Path.Combine(O.RunDir, "captures");
        if (read != null)
        {
            File.WriteAllLines(Path.Combine(dir, $"{_step.Id:00}-{tag}-flyout-uia.txt"),
                read.Hosts.Select(h => $"host 0x{h.ToInt64():X} {Win32.ClassOf(h)}").Concat(read.All.Select(n => n.ToString())));
        }
        if (O.DryRun)
        {
            return;
        }
        var union = (read?.Thumbnails ?? new List<UiaNode>()).Select(t => t.Rect)
            .Aggregate(System.Windows.Rect.Empty, (a, b) => { a.Union(b); return a; });
        RECT region;
        if (!union.IsEmpty)
        {
            region = new RECT((int)union.X - 20, (int)union.Y - 60, (int)union.Right + 20, (int)union.Bottom + 80);
        }
        else
        {
            // No thumbnails via UIA: the area above the button, where the flyout opens.
            var mon = Monitors.Of(new RECT(target.X, target.Y, target.X + 1, target.Y + 1), Mons) ?? Primary;
            int bottom = mon.Work.Bottom - 4;
            region = new RECT(Math.Max(mon.Bounds.Left, target.X - 700), Math.Max(mon.Bounds.Top, bottom - 420),
                Math.Min(mon.Bounds.Right, target.X + 700), bottom);
        }
        try
        {
            using var img = Capture.Screen(region);
            var whole = new System.Drawing.Rectangle(0, 0, img.Width, img.Height);
            var colours = WinRgb.Where(kv => _hwnd.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            _lastImageOrder = Capture.OrderByColors(img, whole, colours, 40, 150);
            _lastThumbPixels = Capture.Stats(img, whole, 0xFF0080, 60).Count;
            // Toolbar buttons per thumbnail; the image is saved cropped to the thumbnails (with their
            // titles and toolbars), not the screen around them.
            var (perKey, crop) = Capture.ToolbarsBelowThumbnails(img, colours, 0xFF0080);
            _lastThumbPixelsByKey = perKey;
            using (var part = img.Clone(crop, img.PixelFormat))
            {
                part.Save(Path.Combine(dir, $"{_step.Id:00}-{tag}-flyout.png"));
            }
            Info(tag + " flyout image", $"region {region}; by window colour: {Logic.Join(_lastImageOrder)}; toolbar-colour pixels {_lastThumbPixels} " +
                $"({string.Join(" ", _lastThumbPixelsByKey.Select(kv => kv.Key + "=" + kv.Value))})");
        }
        catch (Exception e)
        {
            Detail("flyout capture failed: " + e.Message);
        }
    }

    string KeyOfTitle(string name)
    {
        foreach (var k in _hwnd.Keys.OrderByDescending(k => k.Length))
        {
            string t = $"{O.Prefix} {k}";
            if (name == t || name.StartsWith(t + " ", StringComparison.Ordinal) || name.StartsWith(t + ",", StringComparison.Ordinal))
            {
                return k;
            }
        }
        return "?" + name;
    }

    List<string> ThumbnailKeys(FlyoutRead read) =>
        read == null ? new List<string>() : read.ThumbnailNames().Select(KeyOfTitle).ToList();

    // ------------------------------------------------------------------ explorer restart, desktops

    void RestartExplorer()
    {
        if (O.DryRun)
        {
            Detail("DRY skip: restart Explorer");
            return;
        }
        var before = ExplorerInfo.Shell();
        W.ExpectRestart = true;
        try
        {
            Act($"restart Explorer (kill pid {before.Pid})");
            using (var p = Process.GetProcessById(before.Pid))
            {
                p.Kill();
            }
            var sw = Stopwatch.StartNew();
            bool started = false;
            ShellInfo now;
            while (true)
            {
                if (_cancel)
                {
                    throw new AbortRunException("stop-requested during the Explorer restart");
                }
                now = ExplorerInfo.Shell();
                if (now.Pid != 0 && !now.SameProcess(before))
                {
                    break;
                }
                if (!started && sw.Elapsed.TotalSeconds > 8)
                {
                    // An elevated runner must not become the parent of an elevated shell: start it
                    // at the basic-user trust level instead.
                    bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    Detail($"Explorer did not restart by itself within 8s; starting explorer.exe ({(elevated ? "runas /trustlevel:0x20000, the runner is elevated" : "directly")})");
                    var psi = elevated
                        ? new ProcessStartInfo("runas.exe", "/trustlevel:0x20000 explorer.exe") { UseShellExecute = false, CreateNoWindow = true }
                        : new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                    Process.Start(psi)?.Dispose();
                    started = true;
                }
                if (sw.Elapsed.TotalSeconds > 30)
                {
                    throw new AbortRunException("explorer did not come back within 30s");
                }
                Thread.Sleep(200);
            }
            Detail($"new shell {now} after {sw.Elapsed.TotalSeconds:0.0}s");
            while (sw.Elapsed.TotalSeconds < 45)
            {
                if (Uia.Trays().Count >= Mons.Count && W.LatestOrNull() is { } v && v.Pid == now.Pid && v.Trays.Count >= Mons.Count)
                {
                    break;
                }
                Thread.Sleep(250);
            }
            Detail($"taskbars ready after {sw.Elapsed.TotalSeconds:0.0}s");
        }
        finally
        {
            W.Rebaseline();
            W.ExpectRestart = false;
            _itemRef.Clear();
        }
    }

    Guid CurrentDesktop() => Monitors.VirtualDesktops().current;

    bool SwitchDesktop(bool right, string label)
    {
        Guid before = CurrentDesktop();
        Act($"switch virtual desktop {(right ? "right" : "left")}");
        Input.Chord(right ? "Ctrl+Win+Right" : "Ctrl+Win+Left", Input.VK_LCONTROL, Input.VK_LWIN, right ? Input.VK_RIGHT : Input.VK_LEFT);
        if (O.DryRun)
        {
            Check(label, true, "dry", dry: true);
            return true;
        }
        var sw = Stopwatch.StartNew();
        Guid now = before;
        while (sw.Elapsed.TotalSeconds < 3 && (now = CurrentDesktop()) == before)
        {
            Sleep(0.05);
        }
        J.DesktopSwitched = now != _desktopAtStart;
        SaveJournal();
        Check(label, now != before, $"current desktop {before} -> {now} after {sw.Elapsed.TotalSeconds:0.00}s");
        return now != before;
    }

    // ------------------------------------------------------------------ safe stop and cleanup

    void SafeStop(string reason)
    {
        _stopping = true;
        Detail("SAFE STOP: " + reason);
        if (J == null)
        {
            return;
        }
        J.SafeStopReason = reason;
        SaveJournal();
        if (ModId != null)
        {
            try
            {
                var r = Wh.DisableMod(ModId);
                if (!r.Ok)
                {
                    Detail("SAFE STOP: Disable-Mod failed, entering safe mode");
                    Wh.EnterSafeMode();
                }
            }
            catch (Exception e)
            {
                Detail("SAFE STOP: disabling the mod failed: " + e.Message);
            }
        }
        try
        {
            RestoreGlom("safe stop glom");
        }
        catch (Exception e)
        {
            Detail("SAFE STOP: glom restore failed: " + e.Message);
        }
        QuitApp();
    }

    void QuitApp()
    {
        if (App == null)
        {
            return;
        }
        bool exited = App.Quit(5000);
        Detail($"test app quit: {(exited ? "exited" : "killed")}");
        App = null;
        _hwnd.Clear();
        Publish();
    }

    void Cleanup()
    {
        _stopping = true;
        try
        {
            // A shell popup left open (a jump list) keeps the mod waiting: Esc, only into a shell host.
            if (!O.DryRun && JumpListProbe.ForegroundIsHost(out string fgHost))
            {
                Detail($"cleanup: a shell popup host has the foreground ({fgHost}): Esc");
                Input.Chord("Esc", Input.VK_ESCAPE);
            }
            QuitApp();
            if (!O.DryRun && J?.TempDesktop != null)
            {
                // The demo's desktop, after its windows are gone (closing a desktop moves its windows).
                Detail("cleanup: the demo's temporary desktop is still there: closing it");
                CloseTempDesktop();
            }
            if (J != null && J.ModDisabledByDemo && J.SafeStopReason == null && ModId != null)
            {
                var r = Wh.EnableMod(ModId);
                J.ModDisabledByDemo = !r.Ok;
                Detail("cleanup: the demo left the mod disabled; enabled again: " + r);
            }
            _visualImage?.Dispose();
            _visualImage = null;
            if (J != null && NeverCombineOn)
            {
                RestoreGlom("cleanup glom");
            }
            if (!O.DryRun && J != null && _desktops.Count > 1 && CurrentDesktop() != _desktopAtStart && _desktopAtStart != Guid.Empty)
            {
                int cur = _desktops.IndexOf(CurrentDesktop()), want = _desktops.IndexOf(_desktopAtStart);
                for (int i = 0; i < Math.Abs(cur - want) && i < 4; i++)
                {
                    Input.Chord("Ctrl+Win+" + (want > cur ? "Right" : "Left"), Input.VK_LCONTROL, Input.VK_LWIN, want > cur ? Input.VK_RIGHT : Input.VK_LEFT);
                    Thread.Sleep(400);
                }
                J.DesktopSwitched = CurrentDesktop() != _desktopAtStart;
                Detail($"desktop restored: {!J.DesktopSwitched}");
            }
            if (!O.DryRun)
            {
                Input.RestoreCursor(_cursorAtStart);
            }
            if (J != null && J.ModSettingsChanged && J.SafeStopReason == null && _modOk)
            {
                try
                {
                    EnsureDefaultSettings();
                }
                catch (Exception e)
                {
                    Detail("cleanup: settings reset failed: " + e.Message);
                }
            }
            if (J != null && J.LogCaptureStarted)
            {
                var r = Wh.StopLogCapture();
                J.LogCaptureStarted = !r.Ok;
            }
        }
        catch (Exception e)
        {
            Detail("CLEANUP ERROR " + e);
        }
        finally
        {
            W?.Dispose();
            W = null;
            var children = ExplorerInfo.Children(Environment.ProcessId);
            Detail("child processes left: " + (children.Count == 0 ? "none" : string.Join(", ", children.Select(c => $"{c.exe}({c.pid})"))));
            var stray = ExplorerInfo.WindowsWithPrefix(O.Prefix);
            Detail("test windows left: " + (stray.Count == 0 ? "none" : string.Join(", ", stray.Select(s => $"0x{s.hwnd.ToInt64():X} '{s.title}'"))));
            SaveJournal();
        }
    }
}
