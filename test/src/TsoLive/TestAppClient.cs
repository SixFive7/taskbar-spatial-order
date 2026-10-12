// Starts the test app and talks to it over its named pipe.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using TsoShared;

namespace TsoLive;

public sealed class TestAppException : Exception
{
    public TestAppException(string message) : base(message) { }
}

public sealed class TestAppClient : IDisposable
{
    public Process Process { get; private set; }
    public string PipeName { get; private set; }
    public bool Hidden { get; private set; }
    public Action<string> Log = _ => { };

    NamedPipeClientStream _pipe;
    StreamReader _reader;
    StreamWriter _writer;
    readonly object _lock = new();
    Thread _pinger;
    volatile bool _stopPinger;

    public static TestAppClient Start(string exe, string pipeName, int ownerPid, string journal, string log, bool hidden,
        int deadmanSec, int idleSec, string aumid, string prefix)
    {
        var psi = new ProcessStartInfo(Path.GetFullPath(exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
        };
        psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--owner-pid"); psi.ArgumentList.Add(ownerPid.ToString());
        psi.ArgumentList.Add("--deadman-sec"); psi.ArgumentList.Add(deadmanSec.ToString());
        psi.ArgumentList.Add("--idle-sec"); psi.ArgumentList.Add(idleSec.ToString());
        psi.ArgumentList.Add("--aumid"); psi.ArgumentList.Add(aumid);
        psi.ArgumentList.Add("--prefix"); psi.ArgumentList.Add(prefix);
        if (journal != null) { psi.ArgumentList.Add("--journal"); psi.ArgumentList.Add(journal); }
        if (log != null) { psi.ArgumentList.Add("--log"); psi.ArgumentList.Add(log); }
        if (hidden) psi.ArgumentList.Add("--hidden");

        var c = new TestAppClient { PipeName = pipeName, Hidden = hidden, Process = Process.Start(psi) };
        try
        {
            c.Connect(10000);
        }
        catch
        {
            c.Kill();
            throw;
        }
        return c;
    }

    void Connect(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (Process.HasExited)
            {
                throw new TestAppException($"test app exited with code {Process.ExitCode} before its pipe was ready");
            }
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(500);
                _pipe = pipe;
                var utf8 = new UTF8Encoding(false);
                _reader = new StreamReader(_pipe, utf8, false);
                _writer = new StreamWriter(_pipe, utf8) { AutoFlush = true, NewLine = "\n" };
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TestAppException("could not connect to the test app's pipe");
            }
        }
    }

    public bool Alive => Process != null && !Process.HasExited && _pipe != null && _pipe.IsConnected;

    /// <summary>Sends one command; returns the parsed response (ok or not).</summary>
    public JsonObject TrySend(string line, int timeoutMs = 10000)
    {
        lock (_lock)
        {
            if (_pipe == null)
            {
                throw new TestAppException("not connected");
            }
            _writer.WriteLine(line);
            using var cts = new CancellationTokenSource(timeoutMs);
            string response;
            try
            {
                response = _reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TestAppException($"no answer to '{line}' within {timeoutMs} ms");
            }
            if (response == null)
            {
                throw new TestAppException($"pipe closed while sending '{line}'");
            }
            return JsonNode.Parse(response).AsObject();
        }
    }

    /// <summary>Sends one command and throws if the answer is not ok.</summary>
    public JsonObject Send(string line, int timeoutMs = 10000)
    {
        var r = TrySend(line, timeoutMs);
        if (r["ok"]?.GetValue<bool>() != true)
        {
            throw new TestAppException($"'{line}': {r["error"]}");
        }
        if (r["dry"]?.GetValue<bool>() == true)
        {
            Log($"test app dry: {r["skipped"]}");
        }
        return r;
    }

    /// <summary>Builds and sends a command: Cmd("move", "A", x, y, w, h).</summary>
    public JsonObject Cmd(string verb, params object[] parts) => Send(Command.Build(verb, parts));

    /// <summary>Pings every few seconds from a background thread, so the app's idle timer only
    /// fires when this process is gone or wedged.</summary>
    public void StartPinger(int intervalMs = 5000)
    {
        _pinger = new Thread(() =>
        {
            while (!_stopPinger)
            {
                Thread.Sleep(intervalMs);
                if (_stopPinger)
                {
                    break;
                }
                try
                {
                    if (Monitor.TryEnter(_lock, 2000))
                    {
                        Monitor.Exit(_lock);
                        TrySend("ping", 3000);
                    }
                }
                catch
                {
                }
            }
        })
        { IsBackground = true, Name = "test app pinger" };
        _pinger.Start();
    }

    /// <summary>Asks the app to close its windows and exit; kills it if it does not.</summary>
    public bool Quit(int waitMs = 5000)
    {
        _stopPinger = true;
        try
        {
            if (Alive)
            {
                TrySend("quit", 3000);
            }
        }
        catch
        {
        }
        bool exited = Process?.WaitForExit(waitMs) ?? true;
        if (!exited)
        {
            Kill();
        }
        Close();
        return exited;
    }

    public void Kill()
    {
        try
        {
            if (Process != null && !Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(3000);
            }
        }
        catch
        {
        }
    }

    void Close()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        _stopPinger = true;
        Quit(2000);
        Process?.Dispose();
    }
}
