using System.IO;

namespace Sts2UndoMod.Sts2UndoModCode;

/// <summary>
/// Wraps the game logger but ALSO mirrors output to a probe file under
/// %APPDATA%/Sts2UndoMod/probe.log so we can grep discovered combat types
/// without scrubbing through full game logs.
/// </summary>
public static class UndoLogger
{
    /// <summary>
    /// Master switch for verbose INFO logging. When false, Info/Debug/Probe
    /// calls are no-ops — eliminates per-action disk writes that caused
    /// frame stutter on card play. WARN-level messages are still emitted
    /// (rare events, useful for diagnosing real problems).
    /// Flip to true when investigating a regression.
    /// </summary>
    public const bool EnableInfoLogging = false;

    private static readonly object FileLock = new();
    private static string? _probeLogPath;

    public static string ProbeLogPath
    {
        get
        {
            if (_probeLogPath != null) return _probeLogPath;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Sts2UndoMod");
            Directory.CreateDirectory(dir);
            _probeLogPath = Path.Combine(dir, "probe.log");
            return _probeLogPath;
        }
    }

    /// <summary>Truncate the probe log so each game launch starts fresh.</summary>
    public static void TruncateProbeLog()
    {
        try
        {
            lock (FileLock) File.WriteAllText(ProbeLogPath, string.Empty);
        }
        catch { /* ignore */ }
    }

    public static void Info(string msg)
    {
        if (!EnableInfoLogging) return;
        MainFile.Logger.Info(msg);
        AppendProbe("INFO", msg);
    }

    public static void Warn(string msg)
    {
        // Warn always emits — rare, signals real problems we want to diagnose.
        MainFile.Logger.Warn(msg);
        AppendProbe("WARN", msg);
    }

    public static void Debug(string msg)
    {
        if (!EnableInfoLogging) return;
        MainFile.Logger.Info(msg);
        AppendProbe("DEBUG", msg);
    }

    public static void Probe(string category, string msg)
    {
        if (!EnableInfoLogging) return;
        AppendProbe("PROBE/" + category, msg);
    }

    private static void AppendProbe(string level, string msg)
    {
        try
        {
            lock (FileLock)
            {
                File.AppendAllText(
                    ProbeLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // log file failures are not worth crashing the mod over
        }
    }
}
