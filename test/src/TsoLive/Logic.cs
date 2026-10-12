// Pure logic of the live test, covered by the self-tests in TsoLiveCli.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TsoShared;

namespace TsoLive;

/// <summary>A window as the mod sees it when sorting.</summary>
public sealed class WinGeom
{
    public string Key;
    public IntPtr Hwnd;
    public bool Minimised;
    public int X, Y;

    public override string ToString() => $"{Key}{(Minimised ? "(min)" : "")}@{X},{Y}";
}

public sealed class DragVerdict
{
    public bool Pass;
    public int ChangesDuringDrag;
    public int L1ChangesAfterUp;
    public int L2ChangesAfterUp;
    public double FirstChangeDelay = double.NaN;
    public double LastChangeDelay = double.NaN;
    public string BeforeL1, FinalL1;
    public string Detail;
}

public sealed class ModLogFacts
{
    public int? ButtonGroupsSlot, ItemsSlot;
    public readonly List<string> Unexpected = new();
    public readonly List<string> Failures = new();
    public readonly List<string> Warnings = new();
    public readonly List<(string process, string before, string after)> Reorders = new();
    public readonly List<(string process, int moved)> Moves = new();
    public int Lifecycle;  // lines whose message is ">" (Wh_ModInit, AfterInit, Uninit, SettingsChanged)
    public int Lines;
}

public static class Logic
{
    // ------------------------------------------------------------------ the sort rule

    /// <summary>The mod's SortKey: visible before minimised (if minimisedLast), then left edge,
    /// then top edge, then window handle.</summary>
    public static int Compare(WinGeom a, WinGeom b, bool minimisedLast)
    {
        if (minimisedLast && a.Minimised != b.Minimised)
        {
            return a.Minimised ? 1 : -1;
        }
        if (a.X != b.X)
        {
            return a.X.CompareTo(b.X);
        }
        if (a.Y != b.Y)
        {
            return a.Y.CompareTo(b.Y);
        }
        return ((ulong)a.Hwnd.ToInt64()).CompareTo((ulong)b.Hwnd.ToInt64());
    }

    public static List<string> ExpectedOrder(IEnumerable<WinGeom> windows, bool minimisedLast)
    {
        var list = windows.ToList();
        list.Sort((a, b) => Compare(a, b, minimisedLast));
        return list.Select(w => w.Key).ToList();
    }

    /// <summary>Reads a window's sort position the way the mod's WindowPosition does.</summary>
    public static WinGeom Geometry(string key, IntPtr hwnd)
    {
        var g = new WinGeom { Key = key, Hwnd = hwnd, Minimised = Win32.IsIconic(hwnd) };
        if (!g.Minimised && Win32.FrameRect(hwnd, out RECT r) &&
            Win32.MonitorFromRect(ref r, Win32.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
        {
            g.X = r.Left;
            g.Y = r.Top;
            return g;
        }
        var wp = Win32.Placement(hwnd);
        g.X = wp.rcNormalPosition.Left;
        g.Y = wp.rcNormalPosition.Top;
        return g;
    }

    // ------------------------------------------------------------------ sequences

    public static string Join(IEnumerable<string> keys) => string.Join(",", keys);

    public static List<string> Split(string csv) =>
        string.IsNullOrWhiteSpace(csv) ? new List<string>() : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Whether sub appears in full in the same relative order (gaps allowed).</summary>
    public static bool IsOrderedSubsequence(IList<string> sub, IList<string> full)
    {
        int j = 0;
        foreach (string s in sub)
        {
            while (j < full.Count && full[j] != s)
            {
                j++;
            }
            if (j == full.Count)
            {
                return false;
            }
            j++;
        }
        return true;
    }

    /// <summary>Null if the groups present both before and after kept their relative order on a
    /// taskbar; otherwise a description (a group that jumped, typically to the end).</summary>
    public static string CompareGroupSequence(IList<long> before, IList<long> after, Func<long, string> name)
    {
        var a = before.Where(after.Contains).ToList();
        var b = after.Where(before.Contains).ToList();
        if (a.SequenceEqual(b))
        {
            return null;
        }
        return $"group order changed: [{string.Join(" ", a.Select(name))}] -> [{string.Join(" ", b.Select(name))}]";
    }

    // ------------------------------------------------------------------ step selection

    /// <summary>"1-15", "3,4,7", "3-6,9", "all" or "" (all). Sorted, distinct, each within 1..max.</summary>
    public static List<int> ParseSelection(string spec, int max)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(1, max).ToList();
        }
        var set = new SortedSet<int>();
        foreach (string raw in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(raw, @"^(\d+)(?:\s*-\s*(\d+))?$");
            if (!m.Success)
            {
                throw new FormatException($"bad step selection '{raw}'");
            }
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a;
            if (a < 1 || b > max || a > b)
            {
                throw new FormatException($"step range '{raw}' is outside 1-{max}");
            }
            for (int i = a; i <= b; i++)
            {
                set.Add(i);
            }
        }
        if (set.Count == 0)
        {
            throw new FormatException("empty step selection");
        }
        return set.ToList();
    }

    // ------------------------------------------------------------------ progress lines

    public static string OneLine(string s) =>
        Regex.Replace(s ?? "", @"\s+", " ").Trim();

    static string Sec(double s) => Math.Round(s).ToString("0", CultureInfo.InvariantCulture);

    public static string TotalLine(double etaSeconds) => $"TOTAL eta={Sec(etaSeconds)}";

    public static string StepStartLine(int i, int n, string name, double etaSeconds) =>
        $"STEP {i}/{n} {name} START eta={Sec(etaSeconds)}";

    public static string StepEndLine(int i, int n, string name, bool pass, string detail, double tookSeconds)
    {
        string d = OneLine(detail);
        if (d.Length == 0)
        {
            d = "-";
        }
        return $"STEP {i}/{n} {name} {(pass ? "PASS" : "FAIL")} {d} took={tookSeconds.ToString("0.0", CultureInfo.InvariantCulture)}";
    }

    public static string DoneLine(int pass, int fail, string extra = null) =>
        $"DONE pass={pass} fail={fail}" + (string.IsNullOrEmpty(extra) ? "" : " " + OneLine(extra));

    public static readonly Regex ProgressPattern = new(
        @"^(TOTAL eta=\d+|STEP \d+/\d+ \S+ START eta=\d+|STEP \d+/\d+ \S+ (PASS|FAIL) .+ took=\d+\.\d|DONE pass=\d+ fail=\d+( .*)?)$",
        RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ UIA names

    /// <summary>"Paint - 7 running windows" -> 7, "Paint - 1 running window pinned" -> 1, else 0.</summary>
    public static int RunningWindows(string name)
    {
        var m = Regex.Match(name ?? "", @"-\s*(\d+)\s+running\s+windows?\b", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>"Window: 0x1A2B" -> 0x1A2B, else zero.</summary>
    public static IntPtr WindowFromAutomationId(string id)
    {
        var m = Regex.Match(id ?? "", @"Window:\s*0x([0-9A-Fa-f]+)");
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v)
            ? new IntPtr(v) : IntPtr.Zero;
    }

    /// <summary>"Appid: X" -> X, else null.</summary>
    public static string AppIdFromAutomationId(string id)
    {
        var m = Regex.Match(id ?? "", @"^Appid:\s*(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // ------------------------------------------------------------------ the mod's log

    static readonly Regex OffsetsLine = new(@"Button group list in slot (-?\d+), item list in slot (-?\d+)");
    static readonly Regex ReorderLine = new(@"([^\s:\]\[]+\.exe): ([0-9A-Fa-f]+(?: [0-9A-Fa-f]+)*) -> ([0-9A-Fa-f]+(?: [0-9A-Fa-f]+)*)\s*$");
    static readonly Regex MovedLine = new(@"([^\s:\]\[]+\.exe): moved (\d+) buttons on taskbar");
    static readonly string[] FailureTexts =
    {
        "HookSymbols failed", "Couldn't load taskbar.dll", "Unexpected taskbar code", "CreateThread failed",
        "CreateEvent failed", "Couldn't hook the taskbar's thread",
    };
    static readonly string[] WarningTexts =
    {
        "No task list found", "SetWinEventHook failed", "Button without an item", "Lists changed while moving buttons",
        "Groups changed while sorting", "Skipping a group with an item that has no window",
    };

    public static ModLogFacts AnalyzeModLog(IEnumerable<string> lines)
    {
        var f = new ModLogFacts();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }
            f.Lines++;
            var m = OffsetsLine.Match(line);
            if (m.Success)
            {
                f.ButtonGroupsSlot = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                f.ItemsSlot = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            if (line.Contains("Unexpected", StringComparison.Ordinal))
            {
                f.Unexpected.Add(line);
            }
            if (FailureTexts.Any(t => line.Contains(t, StringComparison.Ordinal)))
            {
                f.Failures.Add(line);
            }
            if (WarningTexts.Any(t => line.Contains(t, StringComparison.Ordinal)))
            {
                f.Warnings.Add(line);
            }
            m = ReorderLine.Match(line);
            if (m.Success)
            {
                f.Reorders.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
            }
            m = MovedLine.Match(line);
            if (m.Success)
            {
                f.Moves.Add((m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            }
            if (line.EndsWith(" >", StringComparison.Ordinal) || line.EndsWith(":>", StringComparison.Ordinal) || line == ">")
            {
                f.Lifecycle++;
            }
        }
        return f;
    }

    // ------------------------------------------------------------------ drag timeline

    /// <summary>
    /// Judges a drag from the group's states over time (t, L1, L2; one entry per change, the
    /// first being the state before the drag). Pass: no change between button down and up, then
    /// exactly one change of the item list, minDelay..maxDelay after the release, ending in the
    /// expected order, with the button lists done within settleS of it.
    /// </summary>
    public static DragVerdict AnalyzeDrag(IReadOnlyList<(double t, string l1, string l2)> states, double tDown, double tUp,
        double tEnd, string expectedFinal, double minDelay = 0.30, double maxDelay = 1.0, double settleS = 0.6)
    {
        var v = new DragVerdict();
        var ordered = states.OrderBy(s => s.t).ToList();
        var before = ordered.LastOrDefault(s => s.t <= tDown);
        if (before.l1 == null)
        {
            before = ordered.FirstOrDefault();
        }
        v.BeforeL1 = before.l1;
        string prevL1 = before.l1, prevL2 = before.l2;
        double firstL1 = double.NaN, lastL2 = double.NaN;
        foreach (var s in ordered.Where(s => s.t > tDown && s.t <= tEnd))
        {
            bool l1Changed = s.l1 != prevL1, l2Changed = s.l2 != prevL2;
            if (!l1Changed && !l2Changed)
            {
                continue;
            }
            if (s.t <= tUp)
            {
                v.ChangesDuringDrag++;
            }
            else
            {
                if (l1Changed)
                {
                    v.L1ChangesAfterUp++;
                    if (double.IsNaN(firstL1))
                    {
                        firstL1 = s.t;
                    }
                }
                if (l2Changed)
                {
                    v.L2ChangesAfterUp++;
                    lastL2 = s.t;
                }
            }
            prevL1 = s.l1;
            prevL2 = s.l2;
        }
        v.FinalL1 = prevL1;
        v.FirstChangeDelay = double.IsNaN(firstL1) ? double.NaN : firstL1 - tUp;
        v.LastChangeDelay = double.IsNaN(lastL2) ? double.NaN : lastL2 - tUp;

        var problems = new List<string>();
        if (v.ChangesDuringDrag != 0) problems.Add($"{v.ChangesDuringDrag} change(s) during the drag");
        if (v.L1ChangesAfterUp != 1) problems.Add($"{v.L1ChangesAfterUp} item-list change(s) after release, want 1");
        if (!double.IsNaN(v.FirstChangeDelay) && (v.FirstChangeDelay < minDelay || v.FirstChangeDelay > maxDelay))
            problems.Add($"first change {v.FirstChangeDelay:0.000}s after release, want {minDelay:0.00}-{maxDelay:0.00}s");
        if (!double.IsNaN(v.LastChangeDelay) && !double.IsNaN(v.FirstChangeDelay) && v.LastChangeDelay - v.FirstChangeDelay > settleS)
            problems.Add($"button lists still changing {v.LastChangeDelay - v.FirstChangeDelay:0.000}s after the item list");
        if (v.FinalL1 != expectedFinal) problems.Add($"final order {v.FinalL1}, want {expectedFinal}");
        v.Pass = problems.Count == 0;
        v.Detail = $"before={v.BeforeL1} final={v.FinalL1} duringDrag={v.ChangesDuringDrag} l1AfterUp={v.L1ChangesAfterUp} " +
                   $"l2AfterUp={v.L2ChangesAfterUp} delay={(double.IsNaN(v.FirstChangeDelay) ? "none" : v.FirstChangeDelay.ToString("0.000", CultureInfo.InvariantCulture) + "s")}" +
                   (problems.Count > 0 ? " | " + string.Join("; ", problems) : "");
        return v;
    }

    // ------------------------------------------------------------------ versions

    /// <summary>Numeric parts and a pre-release flag: "1.6.1", "2.0a7", "v2.0.0-alpha.7", "2.0 beta 2".</summary>
    public static (int[] numbers, bool pre, int preNumber) ParseVersion(string v)
    {
        string s = (v ?? "").Trim().TrimStart('v', 'V');
        var m = Regex.Match(s, @"^(\d+(?:\.\d+)*)\s*[-._ ]?\s*(alpha|beta|preview|pre|rc|a|b)?\.?\s*(\d+)?", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return (Array.Empty<int>(), false, 0);
        }
        var nums = m.Groups[1].Value.Split('.').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        bool pre = m.Groups[2].Success;
        int preNumber = pre && m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return (nums, pre, preNumber);
    }

    public static bool IsPrerelease(string v) => ParseVersion(v).pre;

    public static int CompareVersions(string a, string b)
    {
        var x = ParseVersion(a);
        var y = ParseVersion(b);
        int n = Math.Max(x.numbers.Length, y.numbers.Length);
        for (int i = 0; i < n; i++)
        {
            int p = i < x.numbers.Length ? x.numbers[i] : 0, q = i < y.numbers.Length ? y.numbers[i] : 0;
            if (p != q)
            {
                return p.CompareTo(q);
            }
        }
        if (x.pre != y.pre)
        {
            return x.pre ? -1 : 1;  // 2.0a7 < 2.0
        }
        return x.preNumber.CompareTo(y.preNumber);
    }

    // ------------------------------------------------------------------ CPU

    public static (bool pass, string detail) CpuVerdict(double onThread, double offThread, double onProcess, double offProcess,
        double maxThreadDelta, double maxProcessDelta)
    {
        double dt = onThread - offThread, dp = onProcess - offProcess;
        bool pass = !double.IsNaN(dt) && !double.IsNaN(dp) && dt <= maxThreadDelta && dp <= maxProcessDelta;
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"taskbar thread on={onThread:0.0} off={offThread:0.0} delta={dt:+0.0;-0.0} ms/s (max {maxThreadDelta}); " +
            $"process on={onProcess:0.0} off={offProcess:0.0} delta={dp:+0.0;-0.0} ms/s (max {maxProcessDelta})");
        return (pass, detail);
    }

    // ------------------------------------------------------------------ mod file

    /// <summary>The value of a "// @key value" metadata line in a .wh.cpp file.</summary>
    public static string ModMetadata(string source, string key)
    {
        var m = Regex.Match(source, @"^//\s*@" + Regex.Escape(key) + @"\s+(.+?)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }
}
