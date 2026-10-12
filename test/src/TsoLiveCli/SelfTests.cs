// Unit tests of the live test's pure logic. No windows, no input, no Explorer.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TsoLive;
using TsoShared;

namespace TsoLiveCli;

static class SelfTests
{
    static int _checks, _failures;

    static void Eq<T>(T want, T have, string what)
    {
        _checks++;
        if (!EqualityComparer<T>.Default.Equals(want, have))
        {
            _failures++;
            Console.WriteLine($"FAIL {what}: want <{want}> have <{have}>");
        }
    }

    static void True(bool cond, string what)
    {
        _checks++;
        if (!cond)
        {
            _failures++;
            Console.WriteLine($"FAIL {what}");
        }
    }

    static void Throws<TEx>(Action a, string what) where TEx : Exception
    {
        _checks++;
        try
        {
            a();
            _failures++;
            Console.WriteLine($"FAIL {what}: no {typeof(TEx).Name}");
        }
        catch (TEx)
        {
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine($"FAIL {what}: {e.GetType().Name} instead of {typeof(TEx).Name}");
        }
    }

    static WinGeom G(string k, int x, int y, bool min = false, long h = 0) =>
        new() { Key = k, X = x, Y = y, Minimised = min, Hwnd = new IntPtr(h == 0 ? k[0] : h) };

    public static int Run()
    {
        SortRule();
        Sequences();
        Selection();
        ProgressLines();
        UiaNames();
        ModLogParsing();
        DragAnalysis();
        Versions();
        Cpu();
        ProtocolParsing();
        TestViewFromSnapshot();
        JournalAndRegistry();
        ColourAnalysis();
        WindhawkJson();
        Metadata();
        CrashDetection();
        StepOrder();
        DemoGeometry();
        Glides();
        Console.WriteLine($"selftest: {_checks} checks, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    static void SortRule()
    {
        Eq("A,B,C,D", Logic.Join(Logic.ExpectedOrder(new[] { G("C", 300, 0), G("A", 100, 0), G("D", 400, 0), G("B", 200, 0) }, true)), "sort by left edge");
        Eq("D,C,B,A", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 2700, 600), G("B", 1900, 600), G("C", 1100, 600), G("D", 300, 600) }, true)), "right to left");
        Eq("B,A", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 100, 50), G("B", 100, 10) }, true)), "same left: top edge");
        Eq("A,B", Logic.Join(Logic.ExpectedOrder(new[] { G("B", 0, 0, h: 0x200), G("A", 0, 0, h: 0x100) }, true)), "same corner: handle");
        Eq("B,A", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 0, 0, h: unchecked((long)0xFFFF_FFFF_0000_0001)), G("B", 0, 0, h: 0x100) }, true)), "handle compared unsigned");
        Eq("A,C,D,B", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 300, 0), G("B", 1100, 0, true), G("C", 1900, 0), G("D", 2700, 0) }, true)), "minimised last");
        Eq("A,B,C,D", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 300, 0), G("B", 1100, 0, true), G("C", 1900, 0), G("D", 2700, 0) }, false)), "minimised by place");
        Eq("C,D,A,B", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 300, 0, true), G("B", 1100, 0, true), G("C", 1900, 0), G("D", 2700, 0) }, true)), "two minimised");
        Eq("A,B,C,D", Logic.Join(Logic.ExpectedOrder(new[] { G("D", 2700, 0, true), G("C", 1900, 0, true), G("B", 1100, 0, true), G("A", 300, 0, true) }, true)), "all minimised");
        Eq("D,B,E,C,A", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 2600, 300), G("B", 600, 500), G("C", 2100, 300), G("D", -1500, 300), G("E", 1200, 500) }, true)), "three monitors");
        Eq("C,A,B,D", Logic.Join(Logic.ExpectedOrder(new[] { G("A", 200, 500), G("B", 700, 500), G("C", 0, 0), G("D", 1400, 500) }, true)), "maximised at 0,0");
    }

    static void Sequences()
    {
        True(Logic.IsOrderedSubsequence(new[] { "B", "D" }, new[] { "A", "B", "C", "D" }), "subsequence");
        True(!Logic.IsOrderedSubsequence(new[] { "D", "B" }, new[] { "A", "B", "C", "D" }), "out of order");
        True(Logic.IsOrderedSubsequence(Array.Empty<string>(), new[] { "A" }), "empty subsequence");
        True(!Logic.IsOrderedSubsequence(new[] { "E" }, new[] { "A" }), "missing element");
        True(!Logic.IsOrderedSubsequence(new[] { "A", "A" }, new[] { "A" }), "duplicate needs two");
        Eq(null, Logic.CompareGroupSequence(new long[] { 1, 2, 3, 4 }, new long[] { 1, 2, 3, 4 }, p => p.ToString()), "same group order");
        Eq(null, Logic.CompareGroupSequence(new long[] { 1, 2, 3 }, new long[] { 1, 3, 2 }.Where(x => x != 2).Append(5).ToArray(), p => p.ToString()), "removed and added groups only");
        True(Logic.CompareGroupSequence(new long[] { 1, 2, 3, 4 }, new long[] { 1, 3, 4, 2 }, p => p.ToString()) != null, "group 2 jumped to the end");
        Eq("A|B", string.Join("|", Logic.Split(" A, B ,")), "split csv");
    }

    static void Selection()
    {
        Eq("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15", string.Join(",", Logic.ParseSelection("", 15)), "empty = all");
        Eq("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15", string.Join(",", Logic.ParseSelection("all", 15)), "all");
        Eq("3,4,7", string.Join(",", Logic.ParseSelection("7,3,4,3", 15)), "list sorted distinct");
        Eq("3,4,5,6,9", string.Join(",", Logic.ParseSelection("3-6, 9", 15)), "range and list");
        Eq("1,15", string.Join(",", Logic.ParseSelection("15,1", 15)), "bounds");
        Throws<FormatException>(() => Logic.ParseSelection("0", 15), "zero");
        Throws<FormatException>(() => Logic.ParseSelection("16", 15), "too high");
        Throws<FormatException>(() => Logic.ParseSelection("5-3", 15), "reversed range");
        Throws<FormatException>(() => Logic.ParseSelection("a", 15), "garbage");
        Throws<FormatException>(() => Logic.ParseSelection("3;4", 15), "wrong separator");
    }

    static void ProgressLines()
    {
        Eq("TOTAL eta=412", Logic.TotalLine(411.6), "total");
        Eq("STEP 2/15 s02-install-mod-with-log START eta=60", Logic.StepStartLine(2, 15, "s02-install-mod-with-log", 60), "start");
        Eq("STEP 2/15 s02-x PASS 5 checks passed took=12.3", Logic.StepEndLine(2, 15, "s02-x", true, "5 checks\n passed", 12.34), "end pass, newline folded");
        Eq("STEP 3/3 s03-x FAIL - took=0.0", Logic.StepEndLine(3, 3, "s03-x", false, "", 0.01), "end fail, empty detail");
        Eq("DONE pass=14 fail=1", Logic.DoneLine(14, 1), "done");
        Eq("DONE pass=1 fail=1 aborted=explorer-crash: x", Logic.DoneLine(1, 1, "aborted=explorer-crash: x"), "done aborted");
        foreach (string l in new[]
        {
            Logic.TotalLine(5), Logic.StepStartLine(1, 2, "s01-a", 3), Logic.StepEndLine(1, 2, "s01-a", false, "x y", 1), Logic.DoneLine(0, 0),
        })
        {
            True(Logic.ProgressPattern.IsMatch(l), "progress pattern: " + l);
        }
        True(!Logic.ProgressPattern.IsMatch("STEP 1/2 has space START eta=3"), "a step name has no spaces");
        var path = Path.Combine(Scratch(), "progress.log");
        try
        {
            File.WriteAllLines(path, new[] { "TOTAL eta=10", "STEP 1/2 s01-a START eta=5", "STEP 1/2 s01-a PASS ok took=1.0", "STEP 2/2 s02-b START eta=5", "STEP 2/2 s02-b FAIL bad took=2.0" });
            Eq((1, 1, false), ProgressLog.Count(path), "count results");
            ProgressLog.AppendDoneIfMissing(path, "runner-died:test");
            Eq("DONE pass=1 fail=1 aborted=runner-died:test", File.ReadAllLines(path).Last(), "guardian DONE line");
            ProgressLog.AppendDoneIfMissing(path, "again");
            Eq(1, File.ReadAllLines(path).Count(l => l.StartsWith("DONE")), "DONE only once");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path), true);
        }
    }

    /// <summary>A new folder for one test's files, in the kit's work folder (bin\TsoLiveCli\..\..).</summary>
    static string Scratch() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "..", "..", "selftest-" + Guid.NewGuid().ToString("N"))).FullName;

    static void UiaNames()
    {
        Eq(3, Logic.RunningWindows("Paint - 3 running windows pinned"), "3 running windows pinned");
        Eq(1, Logic.RunningWindows("Notepad - 1 running window"), "1 running window");
        Eq(51, Logic.RunningWindows("Example App - 51 running windows pinned"), "51");
        Eq(0, Logic.RunningWindows("File Explorer pinned"), "pinned only");
        Eq(new IntPtr(0x1A2B), Logic.WindowFromAutomationId("Window: 0x1A2B"), "window id");
        Eq(new IntPtr(0x100e8), Logic.WindowFromAutomationId("Window: 0x100e8"), "window id lower case");
        Eq(IntPtr.Zero, Logic.WindowFromAutomationId("Appid: X"), "not a window id");
        Eq("Example.App_1a2b3c4d5e6f7!App", Logic.AppIdFromAutomationId("Appid: Example.App_1a2b3c4d5e6f7!App"), "app id");
        Eq(null, Logic.AppIdFromAutomationId("StartButton"), "no app id");
    }

    static void ModLogParsing()
    {
        var lines = new[]
        {
            "[WH] [local@taskbar-spatial-order] [1007:Wh_ModInit]: >",
            "[WH] [local@taskbar-spatial-order] [997:HookTaskbarSymbols]: Button group list in slot 9, item list in slot 3",
            "[WH] [local@taskbar-spatial-order] [1014:Wh_ModAfterInit]: >",
            "[WH] [local@taskbar-spatial-order] [563:SortGroup]: TsoTestApp.exe: 1A2B 3C4D 5E6F -> 5E6F 1A2B 3C4D",
            "[WH] [local@taskbar-spatial-order] [582:SortGroup]: TsoTestApp.exe: moved 1 buttons on taskbar 000002A1B2C3D4E0",
            "[WH] [local@taskbar-spatial-order] [563:SortGroup]: example.exe: 10 20 -> 20 10",
            "[WH] [local@taskbar-spatial-order] [330:ButtonRanks]: Button without an item in its group's item list",
            "[WH] [local@taskbar-spatial-order] [410:FixButtonOrder]: Lists changed while moving buttons",
            "",
        };
        var f = Logic.AnalyzeModLog(lines);
        Eq(9, f.ButtonGroupsSlot, "button group slot");
        Eq(3, f.ItemsSlot, "item slot");
        Eq(0, f.Unexpected.Count, "no Unexpected");
        Eq(0, f.Failures.Count, "no failures");
        Eq(2, f.Warnings.Count, "two warnings");
        Eq(2, f.Reorders.Count, "two reorders");
        Eq(("TsoTestApp.exe", "1A2B 3C4D 5E6F", "5E6F 1A2B 3C4D"), f.Reorders[0], "reorder parsed");
        Eq(("TsoTestApp.exe", 1), f.Moves[0], "moves parsed");
        Eq(2, f.Lifecycle, "lifecycle lines");
        Eq(8, f.Lines, "non-empty lines");
        var g = Logic.AnalyzeModLog(new[]
        {
            "x Unexpected object in the button group list", "y HookSymbols failed", "z Button group list in slot -1, item list in slot 3",
            "Unexpected taskbar code", "Couldn't hook the taskbar's thread",
        });
        Eq(2, g.Unexpected.Count, "Unexpected lines");
        Eq(3, g.Failures.Count, "failure lines");
        Eq(-1, g.ButtonGroupsSlot, "slot -1");
    }

    static void DragAnalysis()
    {
        var good = new List<(double, string, string)>
        {
            (0.5, "A,B,C,D", "t0:A,B,C,D"),
            (3.43, "D,A,B,C", "t0:A,B,C"),
            (3.45, "D,A,B,C", "t0:D,A,B,C"),
        };
        var v = Logic.AnalyzeDrag(good, 1.0, 3.0, 5.5, "D,A,B,C");
        True(v.Pass, "good drag passes: " + v.Detail);
        Eq(0, v.ChangesDuringDrag, "no change during drag");
        Eq(1, v.L1ChangesAfterUp, "one L1 change");
        True(Math.Abs(v.FirstChangeDelay - 0.43) < 1e-9, "delay 0.43");

        var during = new List<(double, string, string)>(good) { (2.0, "D,A,B,C", "t0:D,A,B,C") };
        var d = Logic.AnalyzeDrag(during, 1.0, 3.0, 5.5, "D,A,B,C");
        True(!d.Pass && d.ChangesDuringDrag == 1, "change during the drag fails: " + d.Detail);

        var twice = new List<(double, string, string)>(good) { (4.2, "A,B,C,D", "t0:A,B,C,D"), (4.8, "D,A,B,C", "t0:D,A,B,C") };
        var t = Logic.AnalyzeDrag(twice, 1.0, 3.0, 5.5, "D,A,B,C");
        True(!t.Pass && t.L1ChangesAfterUp == 3, "several sorts fail: " + t.Detail);

        var late = new List<(double, string, string)> { (0.5, "A,B,C,D", "x"), (4.5, "D,A,B,C", "y") };
        True(!Logic.AnalyzeDrag(late, 1.0, 3.0, 5.5, "D,A,B,C").Pass, "too late fails");
        var early = new List<(double, string, string)> { (0.5, "A,B,C,D", "x"), (3.1, "D,A,B,C", "y") };
        True(!Logic.AnalyzeDrag(early, 1.0, 3.0, 5.5, "D,A,B,C").Pass, "too early fails (no settle)");
        var none = new List<(double, string, string)> { (0.5, "A,B,C,D", "x") };
        var n = Logic.AnalyzeDrag(none, 1.0, 3.0, 5.5, "D,A,B,C");
        True(!n.Pass && n.L1ChangesAfterUp == 0, "no sort fails");
        var wrong = new List<(double, string, string)> { (0.5, "A,B,C,D", "x"), (3.4, "A,D,B,C", "y") };
        True(!Logic.AnalyzeDrag(wrong, 1.0, 3.0, 5.5, "D,A,B,C").Pass, "wrong final order fails");
        var slowButtons = new List<(double, string, string)> { (0.5, "A,B,C,D", "x"), (3.4, "D,A,B,C", "y"), (4.3, "D,A,B,C", "z") };
        True(!Logic.AnalyzeDrag(slowButtons, 1.0, 3.0, 5.5, "D,A,B,C").Pass, "buttons settling late fails");
    }

    static void Versions()
    {
        True(Logic.IsPrerelease("2.0a7"), "2.0a7 pre");
        True(Logic.IsPrerelease("v2.0.0-alpha.7"), "alpha pre");
        True(Logic.IsPrerelease("2.0 beta 2"), "beta pre");
        True(!Logic.IsPrerelease("1.6.1"), "1.6.1 stable");
        True(!Logic.IsPrerelease("1.6.1 (stub)"), "stub suffix stable");
        True(Logic.CompareVersions("2.0a7", "1.6.1") > 0, "2.0a7 > 1.6.1");
        True(Logic.CompareVersions("2.0a7", "2.0") < 0, "2.0a7 < 2.0");
        True(Logic.CompareVersions("2.0a8", "2.0a7") > 0, "a8 > a7");
        Eq(0, Logic.CompareVersions("1.6.1", "v1.6.1"), "v prefix");
        Eq(0, Logic.CompareVersions("1.6", "1.6.0"), "trailing zero");
        True(Logic.IsPrerelease("2.0.0-alpha.7"), "Windhawk's alpha ProductVersion");
        Eq(7, Logic.ParseVersion("2.0.0-alpha.7").preNumber, "alpha number");
        True(Logic.CompareVersions("2.0.0-alpha.7", "1.6.1") > 0, "alpha newer than stable");
        True(Logic.CompareVersions("2.0.0-alpha.8", "2.0.0-alpha.7") > 0, "alpha.8 > alpha.7");
    }

    static void Cpu()
    {
        True(Logic.CpuVerdict(3, 2, 30, 25, 15, 40).pass, "small increase passes");
        True(!Logic.CpuVerdict(30, 2, 30, 25, 15, 40).pass, "thread increase fails");
        True(!Logic.CpuVerdict(3, 2, 90, 25, 15, 40).pass, "process increase fails");
        True(!Logic.CpuVerdict(double.NaN, 2, 3, 2, 15, 40).pass, "NaN fails");
    }

    static void ProtocolParsing()
    {
        var c = Command.Parse("open A 300 600 640 420 color=C81E1E title=\"TSO-TEST A\" aumid=TaskbarSpatialOrder.LiveTest.Sentinel");
        Eq("open", c.Verb, "verb");
        Eq("A", c.Key(), "key");
        Eq(300, c.ArgInt(1), "x");
        Eq(-5, Command.Parse("move A -5 0 640 420").ArgInt(1), "negative positional");
        Eq("TSO-TEST A", c.Opt("title"), "quoted named");
        Eq("TaskbarSpatialOrder.LiveTest.Sentinel", c.Opt("aumid"), "named");
        Eq(0xC81E1E, Command.ParseRgb(c.Opt("color")), "rgb");
        Eq("Z1", Command.Parse("close z1").Key(), "key upper-cased");
        Throws<FormatException>(() => Command.Parse("close AA").Key(), "bad key");
        Throws<FormatException>(() => Command.Parse("close").Key(), "missing key");
        Throws<FormatException>(() => Command.Parse(""), "empty");
        Throws<FormatException>(() => Command.Parse("title A \"open"), "unterminated quote");
        Eq("a|b c|d\"e|x=y z", string.Join("|", Command.Tokenize("a \"b c\" \"d\\\"e\" x=\"y z\"")), "tokenizer");
        string built = Command.Build("open", "A", 1, -2, new Command.Kv("title", "TSO-TEST A"), new Command.Kv("aumid", null), true);
        Eq("open A 1 -2 title=\"TSO-TEST A\" 1", built, "build");
        var round = Command.Parse(built);
        Eq("TSO-TEST A", round.Opt("title"), "round trip");
        Eq(5, Command.Parse("x A n=0x5").OptInt("n", 0), "hex option");
        True(Command.Parse("x A on=yes").OptBool("on"), "bool option");
    }

    static void TestViewFromSnapshot()
    {
        var s = new ProbeSnapshot { Pid = 42 };
        var tb0 = new ProbeTaskbar { Tray = new IntPtr(1), Monitor = "monitor 1" };
        var tb1 = new ProbeTaskbar { Tray = new IntPtr(2), Monitor = "monitor 2" };
        s.Taskbars.Add(tb0);
        s.Taskbars.Add(tb1);
        IntPtr ha = new(0xA), hb = new(0xB), hc = new(0xC), hz = new(0xF), foreign = new(0x99);
        var main = new ProbeGroup { Ptr = 100 };
        foreach (var (h, item) in new[] { (hb, 1001L), (ha, 1002L), (hc, 1003L) })
        {
            main.Hwnds.Add(h);
            main.Items.Add(item);
        }
        var other = new ProbeGroup { Ptr = 200 };
        other.Hwnds.Add(foreign);
        other.Items.Add(2001);
        var sentinel = new ProbeGroup { Ptr = 300 };
        sentinel.Hwnds.Add(hz);
        sentinel.Items.Add(3001);
        s.Groups[100] = main;
        s.Groups[200] = other;
        s.Groups[300] = sentinel;
        var bg = new ProbeButtonGroup { Ptr = 5000, GroupPtr = 100, Index = 1 };
        bg.Hwnds.AddRange(new[] { hb, ha });
        bg.Items.AddRange(new[] { 1001L, 1002L });
        tb0.ButtonGroups.Add(new ProbeButtonGroup { Ptr = 4000, GroupPtr = 200, Index = 0 });
        tb0.ButtonGroups.Add(bg);
        tb0.ButtonGroups.Add(new ProbeButtonGroup { Ptr = 6000, GroupPtr = 300, Index = 2 });
        var bg1 = new ProbeButtonGroup { Ptr = 7000, GroupPtr = 100, Index = 0 };
        bg1.Hwnds.Add(hc);
        bg1.Items.Add(1003);
        tb1.ButtonGroups.Add(bg1);
        var keys = new Dictionary<IntPtr, string> { [ha] = "A", [hb] = "B", [hc] = "C", [hz] = "Z" };
        var v = TestView.From(s, keys, 1.5);
        Eq(2, v.Groups.Count, "two test groups (foreign group left out)");
        Eq("B,A,C", v.GroupOf("A").L1Text, "L1 as keys");
        Eq("t0:B,A | t1:C", v.GroupOf("A").L2Text, "L2 per taskbar");
        Eq(1, v.GroupOf("A").Index[0], "group index on taskbar 0");
        Eq(1002L, v.ItemOf["A"], "item of A");
        Eq("200,100,300", string.Join(",", v.Sequence[0]), "group sequence");
        Eq(v.GroupOf("B"), v.GroupOfAny(new[] { "A", "B", "Z" }), "group of most keys");
        True(v.Signature.Contains("[B,A,C / t0:B,A | t1:C @t0#1,t1#0]"), "signature: " + v.Signature);
        var v2 = TestView.From(s, keys, 2.0);
        Eq(v.Signature, v2.Signature, "signature is stable");
    }

    static void JournalAndRegistry()
    {
        string scratch = @"Software\TsoLiveTestScratch-" + Guid.NewGuid().ToString("N");
        try
        {
            RegistryValues.WriteDword(Registry.CurrentUser, scratch, "TaskbarGlomLevel", 0);
            var orig = RegistryValues.ReadAll(Registry.CurrentUser, scratch, RegistryValues.GlomNames);
            Eq(true, orig[0].Existed, "present value read");
            Eq(0L, orig[0].Number, "present value");
            Eq("DWord", orig[0].Kind, "kind");
            Eq(false, orig[1].Existed, "absent value read");
            Eq("set TaskbarGlomLevel=0 (DWord)", RegistryValues.Plan(orig[0]), "plan set");
            Eq("delete MMTaskbarGlomLevel", RegistryValues.Plan(orig[1]), "plan delete");

            RegistryValues.WriteDword(Registry.CurrentUser, scratch, "TaskbarGlomLevel", 2);
            RegistryValues.WriteDword(Registry.CurrentUser, scratch, "MMTaskbarGlomLevel", 2);
            Eq("ok", RegistryValues.RestoreAll(Registry.CurrentUser, scratch, orig), "restore all");
            var now = RegistryValues.ReadAll(Registry.CurrentUser, scratch, RegistryValues.GlomNames);
            True(RegistryValues.Same(orig[0], now[0]), "restored value");
            Eq(false, now[1].Existed, "absent value deleted, not written as 0");

            // The guardian, as the test app runs it after the runner died.
            string dir = Scratch();
            try
            {
                RegistryValues.WriteDword(Registry.CurrentUser, scratch, "TaskbarGlomLevel", 2);
                RegistryValues.WriteDword(Registry.CurrentUser, scratch, "MMTaskbarGlomLevel", 2);
                string progress = Path.Combine(dir, "progress.log");
                File.WriteAllLines(progress, new[] { "TOTAL eta=10", "STEP 1/1 s07-x START eta=10" });
                var j = new JournalData { ProgressLog = progress, Glom = new GlomJournal { KeyPath = scratch, Original = orig, Changed = true } };
                string jp = Path.Combine(dir, "journal.json");
                Journal.Save(jp, j);
                string did = Journal.GuardianRestore(jp, "selftest", broadcast: false);
                Eq("glom ok", did, "guardian restored");
                var after = RegistryValues.ReadAll(Registry.CurrentUser, scratch, RegistryValues.GlomNames);
                True(RegistryValues.Same(orig[0], after[0]) && !after[1].Existed, "guardian put the values back");
                var j2 = Journal.Load(jp);
                True(j2.Glom.Restored && j2.Glom.RestoredBy == "guardian:selftest", "journal marked restored");
                Eq("runner-died:selftest", j2.SafeStopReason, "safe stop reason");
                Eq("DONE pass=0 fail=0 aborted=runner-died:selftest", File.ReadAllLines(progress).Last(), "guardian closed the progress log");
                Eq("nothing to restore", Journal.GuardianRestore(jp, "again", broadcast: false), "guardian is idempotent");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
        }
    }

    static void ColourAnalysis()
    {
        using var bmp = new Bitmap(300, 60);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(32, 32, 32));
            g.FillRectangle(new SolidBrush(Color.FromArgb(0x1E, 0x46, 0xC8)), 10, 5, 40, 40);   // C
            g.FillRectangle(new SolidBrush(Color.FromArgb(0xC8, 0x1E, 0x1E)), 120, 5, 40, 40);  // A
            g.FillRectangle(new SolidBrush(Color.FromArgb(0x1E, 0x8C, 0x1E)), 230, 5, 40, 40);  // B
            g.FillRectangle(new SolidBrush(Color.FromArgb(0xFF, 0x00, 0xFF)), 60, 5, 12, 12);   // badge A colour
            g.FillRectangle(new SolidBrush(Color.FromArgb(0x10, 0xB0, 0x10)), 0, 55, 150, 4);   // green progress, half
        }
        var colours = new Dictionary<string, int> { ["A"] = 0xC81E1E, ["B"] = 0x1E8C1E, ["C"] = 0x1E46C8 };
        Eq("C,A,B", Logic.Join(Capture.OrderByColors(bmp, new Rectangle(0, 0, 300, 50), colours, 40, 150)), "order by colour");
        var p = Capture.Progress(bmp, new Rectangle(0, 0, 300, 60));
        Eq("green", p.Hue, "progress hue");
        True(Math.Abs(p.Fill - 0.5) < 0.02, $"progress fill {p.Fill}");
        Eq("red", Capture.HueClass(unchecked((int)0xFFE81123)), "red hue");
        Eq("yellow", Capture.HueClass(unchecked((int)0xFFFFB900)), "yellow hue");
        Eq(null, Capture.HueClass(unchecked((int)0xFF808080)), "grey has no hue");
        True(!Capture.IsBlank(bmp), "not blank");
        using var blank = new Bitmap(10, 10);
        True(Capture.IsBlank(blank), "blank");
    }

    static void WindhawkJson()
    {
        var state = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"Stub\":true,\"Windhawk\":{\"AppVersion\":\"2.0a7\"},\"Mods\":[{\"Id\":\"local@taskbar-spatial-order\",\"Disabled\":false,\"Version\":\"1.0\"}]}");
        Eq("2.0a7", Windhawk.FindVersion(state), "Windhawk's version, not the mod's");
        Eq("1.6.1", Windhawk.FindVersion(System.Text.Json.Nodes.JsonNode.Parse("{\"Mods\":{\"x\":{\"Version\":\"9\"}},\"Version\":\"1.6.1\"}")), "top-level Version");
        Eq(null, Windhawk.FindVersion(System.Text.Json.Nodes.JsonNode.Parse("{\"InstalledMods\":[{\"Version\":\"9\"}]}")), "only a mod version: none");
        var mod = Windhawk.FindMod(state, "taskbar-spatial-order");
        True(mod != null, "mod by id with prefix");
        Eq("false", Windhawk.Find(mod, "Disabled"), "disabled flag");
        Eq("true", Windhawk.Find(state, "Stub"), "stub marker");
        var byKey = System.Text.Json.Nodes.JsonNode.Parse("{\"mods\":{\"taskbar-spatial-order\":{\"enabled\":true}}}");
        True(Windhawk.FindMod(byKey, "taskbar-spatial-order") != null, "mod by key");
        Eq(null, Windhawk.FindMod(byKey, "other"), "unknown mod");
    }

    /// <summary>The watcher flags a shell with another pid or start time, and a taskbar window that
    /// stops responding, unless a restart is expected; fed by a fake shell, not Explorer.</summary>
    static void CrashDetection()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var shell = new ShellInfo { Pid = 100, Creation = 1 };
        using (var w = new Watcher(clock, () => new Dictionary<IntPtr, string>(), _ => { }) { ProbeEnabled = false, PollMs = 20 })
        {
            w.ShellSource = () => shell;
            w.Start();
            System.Threading.Thread.Sleep(300);
            True(w.Crash == null && w.Hang == null, "steady shell: no crash, no hang");
            w.ExpectRestart = true;
            shell = new ShellInfo { Pid = 101, Creation = 2 };
            System.Threading.Thread.Sleep(400);
            True(w.Crash == null, "expected restart: not a crash");
            w.Rebaseline();
            w.ExpectRestart = false;
            System.Threading.Thread.Sleep(300);
            True(w.Crash == null, "after rebaselining: no crash");
            shell = new ShellInfo { Pid = 101, Creation = 3 };  // same pid, new start time
            System.Threading.Thread.Sleep(400);
            True(w.Crash != null && w.Crash.Contains("shell changed"), "restart with a reused pid detected: " + w.Crash);
        }
        shell = new ShellInfo { Pid = 100, Creation = 1 };
        using (var w = new Watcher(clock, () => new Dictionary<IntPtr, string>(), _ => { }) { ProbeEnabled = false, PollMs = 20, HangS = 0.5 })
        {
            w.ShellSource = () => shell;
            w.Start();
            shell = new ShellInfo { Pid = 100, Creation = 1, Hung = true };
            System.Threading.Thread.Sleep(1000);
            True(w.Hang != null && w.Crash == null, "hung taskbar detected: " + w.Hang);
        }
        shell = new ShellInfo { Pid = 100, Creation = 1 };
        using (var w = new Watcher(clock, () => new Dictionary<IntPtr, string>(), _ => { }) { ProbeEnabled = false, PollMs = 20 })
        {
            w.ShellSource = () => shell;
            w.Start();
            shell = new ShellInfo();  // no Shell_TrayWnd at all
            System.Threading.Thread.Sleep(400);
            True(w.Crash != null, "shell gone detected: " + w.Crash);
        }
    }

    /// <summary>A selection is a set; the steps run in the table's order, the demo right after step 2.</summary>
    static void StepOrder()
    {
        static string Names(string steps) =>
            string.Join(",", LiveRunner.Plan(steps).Where(l => l.StartsWith("STEP ", StringComparison.Ordinal)).Select(l => l.Split(' ')[2].Split('-')[0]));
        Eq("s01,s02,s16,s03,s04,s05,s06,s07,s08,s09,s10,s11,s12,s13", Names("1-13,16"), "1-13,16: the demo after step 2");
        Eq("s01,s02,s16,s03,s04,s05,s06,s07,s08,s09,s10,s11,s12,s13,s14,s15", Names("all"), "all: sixteen steps, the demo third");
        Eq("s02,s16,s03", Names("16,3,2"), "the order is the table's, not the selection's");
        Eq("s14,s15", Names("14-15"), "14-15");
        Eq("s01,s02,s03", Names("1-3"), "1-3 leaves the demo out");
        Throws<FormatException>(() => LiveRunner.Plan("17").ToList(), "no step 17");
        var total = LiveRunner.Plan("1-13,16").Last();
        True(total.StartsWith("TOTAL eta=", StringComparison.Ordinal), "plan ends with the total: " + total);
        True(Logic.ProgressPattern.IsMatch(Logic.StepStartLine(3, 14, "s16-readme-demo", 35)), "the demo's progress line matches the pattern");
    }

    /// <summary>The README demo's layout on a few monitor geometries.</summary>
    static void DemoGeometry()
    {
        foreach (var (monitor, work, bx) in new[]
        {
            (new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1032), 1100),     // the test button near the middle
            (new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1032), 1880),     // a button at the right end
            (new RECT(0, 0, 1920, 1080), new RECT(0, 0, 1920, 1032), 30),       // at the left end
            (new RECT(0, 0, 1600, 900), new RECT(0, 0, 1600, 852), 800),        // a smaller monitor
            (new RECT(-1920, 0, 0, 1080), new RECT(-1920, 0, 0, 1032), -960),   // a monitor left of the origin
        })
        {
            var button = new RECT(bx - 22, work.Bottom, bx + 22, monitor.Bottom);
            var L = DemoLayout.Compute(monitor, work, button);
            string at = $"button at {bx} on {monitor}";
            True(L.Crop.Width == 1280 && L.Crop.Height == 800 && L.Crop.Width <= 1300 && L.Crop.Height <= 900, $"crop 1280x800: {L.Crop} ({at})");
            True(L.Crop.Left >= monitor.Left && L.Crop.Right <= monitor.Right && L.Crop.Top >= monitor.Top && L.Crop.Bottom == monitor.Bottom, $"crop on the monitor, down to its bottom: {L.Crop} ({at})");
            True((L.Crop.Left & 1) == 0 && (L.Crop.Top & 1) == 0, $"crop at even pixels: {L.Crop}");
            True(L.Crop.Left <= button.Left && L.Crop.Right >= button.Right, $"the button in the crop ({at})");
            True(L.Slots.Length == 5 && L.Slots.All(s => s.Left >= L.Crop.Left && s.Right <= L.Crop.Right && s.Top >= L.Crop.Top), $"five slots inside the crop ({at})");
            True(L.Slots.Zip(L.Slots.Skip(1)).All(p => p.First.Right + 20 <= p.Second.Left), "slots left to right with gaps");
            True(L.Slots.All(s => s.Bottom + 40 <= L.FlyoutZone.Top), $"windows above the flyout zone: bottom {L.Slots[0].Bottom}, zone {L.FlyoutZone}");
            True(L.FlyoutZone.Bottom <= work.Bottom && L.FlyoutZone.Top >= L.Crop.Top, $"flyout zone above the taskbar, inside the crop: {L.FlyoutZone}");
            True(L.Slots.All(s => s.Width >= 100 && s.Height >= 80), "windows big enough for the test app");
            True(L.Rest.X > L.Crop.Left && L.Rest.X < L.Crop.Right && L.Rest.Y > L.Crop.Top && L.Rest.Y < L.FlyoutZone.Top, $"cursor rest inside the crop: {L.Rest}");
        }
        // A 48-pixel taskbar at the bottom of a 1920x1080 monitor.
        var mon = new RECT(0, 0, 1920, 1080);
        var wk = new RECT(0, 0, 1920, 1032);
        var test = new RECT(1078, 1032, 1122, 1080);
        var crop = DemoLayout.Compute(mon, wk, test).Crop;
        var buttons = new List<RECT>
        {
            new(1034, 1032, 1078, 1080),   // the neighbour on the left
            new(1122, 1032, 1166, 1080),   // on the right
            test,                          // the test button itself
            new(1084, 1038, 1116, 1070),   // something inside the test button (its image)
            new(1034, 1032, 1078, 1080),   // a duplicate
            new(1039, 1036, 1073, 1076),   // nested in the left neighbour
            new(100, 1032, 144, 1080),     // outside the crop
            new(700, 1032, 1700, 1080),    // a container wider than a button
            new(1000, 500, 1044, 548),     // not in the taskbar band
            new(crop.Left - 20, 1032, crop.Left + 24, 1080),   // across the crop's edge
        };
        var masks = DemoLayout.MaskRects(crop, wk, mon, buttons, test);
        Eq($"{new RECT(crop.Left, 1032, crop.Left + 24, 1080)} {new RECT(1034, 1032, 1078, 1080)} {new RECT(1122, 1032, 1166, 1080)}",
            string.Join(" ", masks), "masks: neighbours and the clipped edge one; not the test button, nested, duplicate, outside, container or off-band");
        var sample = DemoLayout.TaskbarSample(crop, wk, mon, masks, test);
        True(sample is POINT q && q.Y == 1056 && masks.Append(test).All(b => q.X < b.Left - 8 || q.X >= b.Right + 8) && q.X >= crop.Left && q.X < crop.Right,
            $"taskbar sample point clear of every button: {sample}");
        Eq(null, DemoLayout.TaskbarSample(crop, wk, mon, new[] { new RECT(crop.Left - 10, 1032, crop.Right + 10, 1080) }, test), "no sample point when buttons cover the band");
    }

    static void Glides()
    {
        var path = Input.GlidePath(new POINT(100, 900), new POINT(1100, 400), 600, 8);
        Eq(75, path.Count, "600 ms at 8 ms a step");
        Eq(new POINT(1100, 400).ToString(), path[^1].ToString(), "ends on the target");
        True(path.Zip(path.Skip(1)).All(p => p.Second.X >= p.First.X && p.Second.Y <= p.First.Y), "monotonic");
        int firstStep = path[0].X - 100, midStep = path[38].X - path[37].X, lastStep = 1100 - path[^2].X;
        True(firstStep < midStep && lastStep < midStep, $"eased: slow start {firstStep}, fast middle {midStep}, slow end {lastStep}");
        Eq(0.5, Input.Ease(0.5), "ease midpoint");
        Eq(0.0, Input.Ease(-1), "ease clamps below");
        Eq(1.0, Input.Ease(2), "ease clamps above");
    }

    static void Metadata()
    {
        string src = "// ==WindhawkMod==\n// @id              taskbar-spatial-order\n// @version         1.0\n// ==/WindhawkMod==\n";
        Eq("taskbar-spatial-order", Logic.ModMetadata(src, "id"), "@id");
        Eq("1.0", Logic.ModMetadata(src, "version"), "@version");
        Eq(null, Logic.ModMetadata(src, "author"), "missing key");
    }
}
