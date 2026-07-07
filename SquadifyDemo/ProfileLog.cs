using System.Globalization;

namespace SquadifyDemo;

/// <summary>
/// Deterministic, file-based timing sink used for cold-start / shared-client profiling.
///
/// <para>
/// The pool warm-up and per-run timings are emitted through <c>ILogger</c> so they show up
/// in the Aspire dashboard, but reading a child resource's structured logs from a script is
/// fragile (per-launch ports, dashboard tokens, OTLP querying). This sink ALSO appends the
/// same lines to a well-known temp file so profiling numbers can be read directly with
/// <c>Get-Content</c> — no browser or dashboard dependency.
/// </para>
///
/// <para>Path: <c>%TEMP%\squadify-timing.log</c> (override with <c>SQUAD_TIMING_LOG</c>).
/// This is profiling scaffolding — safe to delete when the investigation is done.</para>
/// </summary>
public static class ProfileLog
{
    private static readonly object Gate = new();

    public static string Path { get; } =
        Environment.GetEnvironmentVariable("SQUAD_TIMING_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "squadify-timing.log");

    /// <summary>Appends a single timestamped line to the timing log (thread-safe).</summary>
    public static void Write(string line)
    {
        var stamped = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:HH:mm:ss.fff}  {line}{Environment.NewLine}");
        try
        {
            lock (Gate)
                File.AppendAllText(Path, stamped);
        }
        catch
        {
            // Never let profiling I/O break a run.
        }
    }
}
