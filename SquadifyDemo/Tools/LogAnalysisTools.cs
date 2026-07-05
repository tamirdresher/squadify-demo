using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace SquadifyDemo.Tools;

/// <summary>
/// Tools that an agent can use to gather data.
/// The agent decides WHEN to call these — we just provide the capabilities.
/// </summary>
public static class LogAnalysisTools
{
    [Description("Retrieves recent error logs from the specified service")]
    public static string GetServiceLogs(string serviceName, int minutesBack = 30)
    {
        // In production: query Application Insights, ELK, etc.
        return $"""
            [{DateTime.UtcNow.AddMinutes(-5):HH:mm:ss}] ERROR PaymentService: Connection timeout to database (attempt 3/3)
            [{DateTime.UtcNow.AddMinutes(-4):HH:mm:ss}] ERROR PaymentService: Circuit breaker opened for DB connection pool
            [{DateTime.UtcNow.AddMinutes(-3):HH:mm:ss}] WARN  OrderService: PaymentService returning 503
            [{DateTime.UtcNow.AddMinutes(-2):HH:mm:ss}] ERROR OrderService: 847 orders stuck in 'pending_payment' state
            [{DateTime.UtcNow.AddMinutes(-1):HH:mm:ss}] ALERT Monitoring: Error rate > 50% on /api/checkout
            """;
    }

    [Description("Gets current health metrics for a service")]
    public static string GetServiceHealth(string serviceName)
    {
        return $"""
            Service: {serviceName}
            Status: DEGRADED
            CPU: 23%  |  Memory: 67%  |  DB Connections: 50/50 (EXHAUSTED)
            Active requests: 1,247  |  Error rate: 52%
            Last healthy: {DateTime.UtcNow.AddMinutes(-6):HH:mm:ss} UTC
            """;
    }

    [Description("Queries database connection pool status")]
    public static string GetConnectionPoolStatus(string serviceName)
    {
        return $"""
            Pool: {serviceName}-primary
            Max connections: 50  |  Active: 50  |  Idle: 0  |  Waiting: 234
            Avg query time: 12.4s (normal: 45ms)
            Longest running query: SELECT * FROM orders WHERE status='pending' — running for 847s
            Blocked by: Table lock on [orders] held by maintenance job (PID: 4521)
            """;
    }
}
