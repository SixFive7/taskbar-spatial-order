// Windhawk, only through the functions of windhawk\windhawk.ps1 (or windhawk\windhawk-stub.ps1),
// called back into PowerShell through a delegate the runner script passes in:
//   string invoke(string functionName, string argumentsJson)  ->  {"ok":true,"result":...} or {"ok":false,"error":"..."}
// The arguments are splatted as named parameters.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TsoLive;

public sealed class WhResult
{
    public string Function;
    public bool Ok;
    public JsonNode Result;
    public string Error;
    public double Seconds;

    public override string ToString() =>
        $"{Function} {(Ok ? "ok" : "FAILED: " + Error)} ({Seconds:0.0}s){(Result != null ? " -> " + Logic.OneLine(Result.ToJsonString()).Substring(0, Math.Min(300, Result.ToJsonString().Length)) : "")}";
}

public sealed class Windhawk
{
    readonly Func<string, string, string> _invoke;
    readonly Action<string> _log;

    public Windhawk(Func<string, string, string> invoke, Action<string> log)
    {
        _invoke = invoke;
        _log = log;
    }

    public WhResult Call(string function, Dictionary<string, object> args = null)
    {
        var sw = Stopwatch.StartNew();
        var r = new WhResult { Function = function };
        string argsJson = JsonSerializer.Serialize(args ?? new Dictionary<string, object>());
        _log($"windhawk: {function} {argsJson}");
        try
        {
            string raw = _invoke(function, argsJson);
            var node = JsonNode.Parse(raw ?? "null") as JsonObject;
            r.Ok = node?["ok"]?.GetValue<bool>() == true;
            r.Result = node?["result"];
            r.Error = r.Ok ? null : node?["error"]?.ToString() ?? "no answer";
        }
        catch (Exception e)
        {
            r.Ok = false;
            r.Error = e.InnerException?.Message ?? e.Message;
        }
        r.Seconds = sw.Elapsed.TotalSeconds;
        _log("windhawk: " + r);
        return r;
    }

    public WhResult InstallAlpha() => Call("Install-WindhawkAlpha");
    public WhResult RestoreStable() => Call("Restore-WindhawkStable");
    public WhResult State() => Call("Get-WindhawkState");
    public WhResult EnterSafeMode() => Call("Enter-SafeMode");
    public WhResult InstallLocalMod(string path, bool disabled) =>
        Call("Install-LocalMod", disabled ? new() { ["Path"] = path, ["Disabled"] = true } : new() { ["Path"] = path });
    public WhResult EnableMod(string id) => Call("Enable-Mod", new() { ["Id"] = id });
    public WhResult DisableMod(string id) => Call("Disable-Mod", new() { ["Id"] = id });
    public WhResult SetModSetting(string id, string key, object value) =>
        Call("Set-ModSetting", new() { ["Id"] = id, ["Key"] = key, ["Value"] = value });
    public WhResult SetModLogging(string id, bool enabled) => Call("Set-ModLogging", new() { ["Id"] = id, ["Enabled"] = enabled });
    public WhResult StartLogCapture(string file) => Call("Start-ModLogCapture", new() { ["OutFile"] = file });
    public WhResult StopLogCapture() => Call("Stop-ModLogCapture");

    /// <summary>Finds a version string anywhere in a state object (Version, AppVersion, EngineVersion...).</summary>
    public static string FindVersion(JsonNode state)
    {
        // Windhawk's own version, not a mod's: subtrees under keys mentioning "mod" are skipped.
        foreach (string name in new[] { "AppVersion", "WindhawkVersion", "EngineVersion", "Version" })
        {
            string v = Find(state, name, skipKeysContaining: "mod");
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>The first property with this name, depth first, as a string.</summary>
    public static string Find(JsonNode node, string name, string skipKeysContaining = null)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonValue jv)
                    {
                        return jv.ToString();
                    }
                }
                foreach (var kv in o)
                {
                    if (skipKeysContaining != null && kv.Key.Contains(skipKeysContaining, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string s = Find(kv.Value, name, skipKeysContaining);
                    if (s != null)
                    {
                        return s;
                    }
                }
                break;
            case JsonArray a:
                foreach (var e in a)
                {
                    string s = Find(e, name, skipKeysContaining);
                    if (s != null)
                    {
                        return s;
                    }
                }
                break;
        }
        return null;
    }

    /// <summary>The JSON object that describes a mod, found by its id anywhere in a state object.</summary>
    public static JsonNode FindMod(JsonNode state, string id)
    {
        switch (state)
        {
            case JsonObject o:
                foreach (var kv in o)
                {
                    if (kv.Key == id || kv.Key.EndsWith("@" + id, StringComparison.Ordinal) || kv.Key.EndsWith("/" + id, StringComparison.Ordinal))
                    {
                        return kv.Value;
                    }
                    if ((kv.Key.Equals("Id", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("ModId", StringComparison.OrdinalIgnoreCase)) &&
                        kv.Value is JsonValue v && (v.ToString() == id || v.ToString().EndsWith("@" + id, StringComparison.Ordinal)))
                    {
                        return o;
                    }
                }
                foreach (var kv in o)
                {
                    var m = FindMod(kv.Value, id);
                    if (m != null)
                    {
                        return m;
                    }
                }
                break;
            case JsonArray a:
                foreach (var e in a)
                {
                    var m = FindMod(e, id);
                    if (m != null)
                    {
                        return m;
                    }
                }
                break;
        }
        return null;
    }
}
