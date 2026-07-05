// ═══════════════════════════════════════════════════════════════════════════
//  Squadify Your App — Demo Service
//
//  A web-hosted incident workflow demo matching the .NET Aspire community
//  toolkit pattern. Exposes HTTP endpoints for triggering demos and integrates
//  with the Aspire dashboard via OpenTelemetry and HTTP commands.
//
//  Endpoints:
//    GET  /health              — health check
//    GET  /status              — current workflow state
//    POST /incidents/simulate  — trigger the full squadified workflow
//    POST /demo/1              — run Demo 1 (single agent)
//    POST /demo/2              — run Demo 2 (sequential workflow)
//    POST /demo/3              — run Demo 3 (squadified pipeline)
//
//  Quick start (standalone):
//    $env:HTTP_PORTS = "5050"
//    dotnet run
//    # Then: curl -X POST http://localhost:5050/incidents/simulate
//
//  Under Aspire:
//    AppHost wires HTTP_PORTS and --team-root arguments automatically.
// ═══════════════════════════════════════════════════════════════════════════

using SquadifyDemo;

return await SquadifyWebProgram.RunAsync(args);
