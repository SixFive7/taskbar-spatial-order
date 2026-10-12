// Reads the mod's captured log (the file Start-ModLogCapture writes) from a mark onwards.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TsoLive;

public sealed class ModLog
{
    public readonly string Path;
    public readonly string ModId;

    public ModLog(string path, string modId)
    {
        Path = path;
        ModId = modId;
    }

    public bool Exists => File.Exists(Path);

    /// <summary>The current length of the log, as a mark.</summary>
    public long Mark()
    {
        try
        {
            return File.Exists(Path) ? new FileInfo(Path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Lines written after the mark. If the capture prefixes lines with the mod id, only
    /// this mod's lines are returned.</summary>
    public List<string> Since(long mark)
    {
        if (!File.Exists(Path))
        {
            return new List<string>();
        }
        byte[] bytes;
        using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long start = Math.Min(mark, fs.Length);
            fs.Seek(start, SeekOrigin.Begin);
            bytes = new byte[fs.Length - start];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = fs.Read(bytes, read, bytes.Length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
        }
        string text = Decode(bytes);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (ModId != null && lines.Any(l => l.Contains(ModId, StringComparison.Ordinal)))
        {
            lines = lines.Where(l => l.Contains(ModId, StringComparison.Ordinal)).ToList();
        }
        return lines;
    }

    static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        // UTF-16 without BOM (mid-file reads): many zero bytes at odd positions.
        int zeros = 0;
        for (int i = 1; i < Math.Min(bytes.Length, 400); i += 2)
        {
            if (bytes[i] == 0)
            {
                zeros++;
            }
        }
        if (bytes.Length > 8 && zeros > Math.Min(bytes.Length, 400) / 4)
        {
            return Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public ModLogFacts Facts(long mark) => Logic.AnalyzeModLog(Since(mark));
}
