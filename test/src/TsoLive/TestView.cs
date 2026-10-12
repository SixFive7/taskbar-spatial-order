// The test windows' view of an Explorer snapshot: per group, the item list (L1) and each
// taskbar's button list (L2) as window keys, the group's place on each taskbar, and the item
// object behind each window.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TsoLive;

public sealed class TestGroup
{
    public long Ptr;
    public readonly List<string> L1 = new();
    /// <summary>Per taskbar (index into TestView.Trays): the group's buttons, as keys.</summary>
    public readonly SortedDictionary<int, List<string>> L2 = new();
    /// <summary>Per taskbar: the index of the group's button group among all button groups.</summary>
    public readonly SortedDictionary<int, int> Index = new();

    public string L1Text => Logic.Join(L1);

    public string L2Text => string.Join(" | ", L2.Select(kv => $"t{kv.Key}:{Logic.Join(kv.Value)}"));
}

public sealed class TestView
{
    public double T;
    public int Pid;
    public readonly List<IntPtr> Trays = new();
    public readonly List<string> TrayMonitors = new();
    public readonly List<TestGroup> Groups = new();
    /// <summary>Per taskbar: the CTaskGroup of every button group, in order.</summary>
    public readonly Dictionary<int, List<long>> Sequence = new();
    public readonly Dictionary<string, long> ItemOf = new();
    public readonly List<string> Warnings = new();
    public string Signature = "";

    public TestGroup GroupOf(string key) => Groups.FirstOrDefault(g => g.L1.Contains(key));

    /// <summary>The group holding most of the given keys (the main group, when keys move between groups).</summary>
    public TestGroup GroupOfAny(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet();
        return Groups.Where(g => g.L1.Any(set.Contains)).OrderByDescending(g => g.L1.Count(set.Contains)).FirstOrDefault();
    }

    public static TestView From(ProbeSnapshot s, IReadOnlyDictionary<IntPtr, string> keys, double t)
    {
        var v = new TestView { T = t, Pid = s.Pid };
        v.Warnings.AddRange(s.Warnings);
        foreach (var tb in s.Taskbars)
        {
            v.Trays.Add(tb.Tray);
            v.TrayMonitors.Add(tb.Monitor);
        }

        var groups = new Dictionary<long, TestGroup>();
        foreach (var g in s.Groups.Values)
        {
            if (!g.Hwnds.Any(keys.ContainsKey))
            {
                continue;
            }
            var tg = new TestGroup { Ptr = g.Ptr };
            for (int i = 0; i < g.Hwnds.Count; i++)
            {
                if (keys.TryGetValue(g.Hwnds[i], out string k))
                {
                    tg.L1.Add(k);
                    v.ItemOf[k] = g.Items[i];
                }
            }
            groups[g.Ptr] = tg;
            v.Groups.Add(tg);
        }

        for (int ti = 0; ti < s.Taskbars.Count; ti++)
        {
            var tb = s.Taskbars[ti];
            v.Sequence[ti] = tb.ButtonGroups.Select(b => b.GroupPtr).ToList();
            foreach (var bg in tb.ButtonGroups)
            {
                if (!groups.TryGetValue(bg.GroupPtr, out var tg))
                {
                    continue;
                }
                tg.L2[ti] = bg.Hwnds.Select(h => keys.TryGetValue(h, out string k) ? k : "?").ToList();
                tg.Index[ti] = bg.Index;
            }
        }

        v.Groups.Sort((a, b) => string.CompareOrdinal(a.L1.FirstOrDefault() ?? "", b.L1.FirstOrDefault() ?? ""));
        var sig = new StringBuilder();
        sig.Append(v.Pid);
        foreach (var g in v.Groups)
        {
            sig.Append(" [").Append(g.L1Text).Append(" / ").Append(g.L2Text).Append(" @")
               .Append(string.Join(",", g.Index.Select(kv => $"t{kv.Key}#{kv.Value}"))).Append(']');
        }
        v.Signature = sig.ToString();
        return v;
    }

    public override string ToString() => Signature;
}
