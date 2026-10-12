// The recovery journal: everything the run changed that has to be put back, written before each
// change, so that a safe stop, a -Recover run or the test app's dead-man guardian can undo it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace TsoShared;

public sealed class RegValueSnapshot
{
    public string Name { get; set; }
    public bool Existed { get; set; }
    public string Kind { get; set; }      // RegistryValueKind name when it existed
    public long? Number { get; set; }     // DWord / QWord
    public string Text { get; set; }      // String / ExpandString
    public string BytesHex { get; set; }  // Binary

    public override string ToString() =>
        !Existed ? $"{Name}=<absent>" : $"{Name}={(Number?.ToString() ?? Text ?? BytesHex)} ({Kind})";
}

public sealed class GlomJournal
{
    public string KeyPath { get; set; } = RegistryValues.AdvancedKey;
    public List<RegValueSnapshot> Original { get; set; } = new();
    public bool Changed { get; set; }
    public bool Restored { get; set; }
    public string RestoredBy { get; set; }
    public string RestoredUtc { get; set; }
}

public sealed class JournalData
{
    public string RunId { get; set; }
    public string StartedUtc { get; set; }
    public int RunnerPid { get; set; }
    public string ProgressLog { get; set; }
    public string DetailLog { get; set; }
    public bool DryRun { get; set; }
    public GlomJournal Glom { get; set; } = new();
    public string OriginalDesktop { get; set; }
    public bool DesktopSwitched { get; set; }
    public string ModId { get; set; }
    public bool ModSettingsChanged { get; set; }
    public bool ModLoggingEnabledByRun { get; set; }
    public bool LogCaptureStarted { get; set; }
    public bool WindhawkUpgraded { get; set; }
    public string WindhawkVersionBefore { get; set; }
    public int TestAppPid { get; set; }
    public string SafeStopReason { get; set; }
    public bool Complete { get; set; }
    /// <summary>The README demo's temporary virtual desktop (Ctrl+Win+D), while it exists; the desktops
    /// that existed before it, so that none of them is ever mistaken for it.</summary>
    public string TempDesktop { get; set; }
    public List<string> DesktopsBeforeTemp { get; set; } = new();
    /// <summary>The demo disabled the mod for its "without" scene and has not enabled it again yet.</summary>
    public bool ModDisabledByDemo { get; set; }
    public List<string> Notes { get; set; } = new();
}

public static class Journal
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static JournalData Load(string path)
    {
        return JsonSerializer.Deserialize<JournalData>(File.ReadAllText(path), Options);
    }

    /// <summary>Writes atomically: a temp file next to the target, then a replace.</summary>
    public static void Save(string path, JournalData data)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>
    /// The guardian's part of a recovery, used when the runner died: puts the taskbar grouping
    /// values back if the run changed them and nobody restored them yet. Returns what it did.
    /// </summary>
    public static string GuardianRestore(string journalPath, string reason, bool broadcast)
    {
        if (string.IsNullOrEmpty(journalPath) || !File.Exists(journalPath))
        {
            return "no journal";
        }

        var j = Load(journalPath);
        var did = new List<string>();
        if (j.Glom.Changed && !j.Glom.Restored && !j.DryRun)
        {
            string result = RegistryValues.RestoreAll(Registry.CurrentUser, j.Glom.KeyPath, j.Glom.Original);
            if (broadcast)
            {
                Win32.BroadcastTraySettings();
            }
            j.Glom.Restored = result == "ok";
            j.Glom.RestoredBy = "guardian:" + reason;
            j.Glom.RestoredUtc = DateTime.UtcNow.ToString("o");
            did.Add("glom " + result);
        }

        if (!j.Complete && j.SafeStopReason == null)
        {
            j.SafeStopReason = "runner-died:" + reason;
        }
        j.Notes.Add($"{DateTime.UtcNow:o} guardian ({reason}): {(did.Count == 0 ? "nothing to restore" : string.Join(", ", did))}");
        Save(journalPath, j);

        if (!string.IsNullOrEmpty(j.ProgressLog) && !j.Complete)
        {
            ProgressLog.AppendDoneIfMissing(j.ProgressLog, "runner-died:" + reason);
        }
        return did.Count == 0 ? "nothing to restore" : string.Join(", ", did);
    }
}

public static class RegistryValues
{
    public const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public static readonly string[] GlomNames = { "TaskbarGlomLevel", "MMTaskbarGlomLevel" };

    public static RegValueSnapshot Read(RegistryKey root, string keyPath, string name)
    {
        using var key = root.OpenSubKey(keyPath, false);
        var s = new RegValueSnapshot { Name = name };
        object v = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (v == null)
        {
            return s;
        }

        s.Existed = true;
        var kind = key.GetValueKind(name);
        s.Kind = kind.ToString();
        switch (kind)
        {
            case RegistryValueKind.DWord: s.Number = unchecked((uint)(int)v); break;
            case RegistryValueKind.QWord: s.Number = (long)v; break;
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString: s.Text = (string)v; break;
            case RegistryValueKind.MultiString: s.Text = string.Join("\n", (string[])v); break;
            default: s.BytesHex = Convert.ToHexString(v as byte[] ?? Array.Empty<byte>()); break;
        }
        return s;
    }

    public static List<RegValueSnapshot> ReadAll(RegistryKey root, string keyPath, IEnumerable<string> names) =>
        names.Select(n => Read(root, keyPath, n)).ToList();

    public static bool Same(RegValueSnapshot a, RegValueSnapshot b) =>
        a.Name == b.Name && a.Existed == b.Existed &&
        (!a.Existed || (a.Kind == b.Kind && a.Number == b.Number && a.Text == b.Text && a.BytesHex == b.BytesHex));

    /// <summary>What restoring a value means: delete it if it was absent, else write it back.</summary>
    public static string Plan(RegValueSnapshot original) =>
        original.Existed ? $"set {original.Name}={original.Number?.ToString() ?? original.Text ?? original.BytesHex} ({original.Kind})"
                         : $"delete {original.Name}";

    public static void Restore(RegistryKey root, string keyPath, RegValueSnapshot original)
    {
        using var key = root.CreateSubKey(keyPath, true);
        if (!original.Existed)
        {
            if (key.GetValue(original.Name) != null)
            {
                key.DeleteValue(original.Name, false);
            }
            return;
        }

        var kind = Enum.Parse<RegistryValueKind>(original.Kind);
        object value = kind switch
        {
            RegistryValueKind.DWord => unchecked((int)(uint)original.Number.Value),
            RegistryValueKind.QWord => original.Number.Value,
            RegistryValueKind.String or RegistryValueKind.ExpandString => original.Text,
            RegistryValueKind.MultiString => original.Text.Split('\n'),
            _ => Convert.FromHexString(original.BytesHex ?? ""),
        };
        key.SetValue(original.Name, value, kind);
    }

    /// <summary>Restores every value and re-reads it. Returns "ok" or a description of the mismatch.</summary>
    public static string RestoreAll(RegistryKey root, string keyPath, IEnumerable<RegValueSnapshot> originals)
    {
        var problems = new List<string>();
        foreach (var o in originals)
        {
            try
            {
                Restore(root, keyPath, o);
                var now = Read(root, keyPath, o.Name);
                if (!Same(now, o))
                {
                    problems.Add($"{o.Name}: want {o}, read back {now}");
                }
            }
            catch (Exception e)
            {
                problems.Add($"{o.Name}: {e.Message}");
            }
        }
        return problems.Count == 0 ? "ok" : string.Join("; ", problems);
    }

    public static void WriteDword(RegistryKey root, string keyPath, string name, int value)
    {
        using var key = root.CreateSubKey(keyPath, true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}

public static class ProgressLog
{
    /// <summary>Counts the PASS and FAIL step results in a progress log.</summary>
    public static (int pass, int fail, bool done) Count(string path)
    {
        int pass = 0, fail = 0;
        bool done = false;
        if (!File.Exists(path))
        {
            return (0, 0, false);
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string line;
        while ((line = sr.ReadLine()) != null)
        {
            var parts = line.Split(' ');
            if (parts.Length >= 4 && parts[0] == "STEP")
            {
                if (parts[3] == "PASS") pass++;
                else if (parts[3] == "FAIL") fail++;
            }
            else if (parts.Length >= 1 && parts[0] == "DONE")
            {
                done = true;
            }
        }
        return (pass, fail, done);
    }

    public static void AppendDoneIfMissing(string path, string reason)
    {
        var (pass, fail, done) = Count(path);
        if (!done)
        {
            File.AppendAllText(path, $"DONE pass={pass} fail={fail} aborted={reason}{Environment.NewLine}");
        }
    }
}
