using System.ComponentModel;

namespace SquadifyDemo.Tools;

/// <summary>
/// Tools for applying remediation actions.
/// These are the "code does data gathering" part of Squad's philosophy.
/// </summary>
public static class RemediationTools
{
    [Description("Kills a running database process by PID")]
    public static string KillDatabaseProcess(int processId)
    {
        Console.WriteLine($"  ⚡ Terminating database process {processId}...");
        Thread.Sleep(500); // Simulate
        return $"Process {processId} terminated successfully. Table lock released.";
    }

    [Description("Restarts the connection pool for a service")]
    public static string RestartConnectionPool(string serviceName)
    {
        Console.WriteLine($"  🔄 Recycling connection pool for {serviceName}...");
        Thread.Sleep(500);
        return $"Connection pool for {serviceName} recycled. 50 connections available.";
    }

    [Description("Sends an alert to the on-call team via PagerDuty")]
    public static string NotifyOnCall(string message, string severity = "high")
    {
        Console.WriteLine($"  📟 Notifying on-call team [{severity}]: {message}");
        return $"Alert sent to on-call engineer (severity: {severity})";
    }
}
