using Aspire.Hosting;
using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

// ── Model selection: Azure OpenAI (cloud) or Foundry Local ───────────────
// If AZURE_OPENAI_ENDPOINT is set (user-secrets or env), use cloud models.
// Otherwise, fall back to Foundry Local with Phi-4 (slower, no cloud needed).
var azureEndpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var azureKey = builder.Configuration["AZURE_OPENAI_KEY"]
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY");

var useAzure = !string.IsNullOrWhiteSpace(azureEndpoint);

IResourceBuilder<IResourceWithConnectionString>? chatResource = null;

if (!useAzure)
{
    // Foundry Local — no cloud dependency, runs Phi-4 on-device
    var foundry = builder.AddFoundry("foundry")
        .RunAsFoundryLocal();
    chatResource = foundry.AddDeployment("chat", FoundryModel.Local.Phi4);
}

// Add the Squad AI-agent team resource.
var squad = builder.AddSquad("incident-response-squad",
    teamRoot: Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..")));

// Add the SquadifyDemo workflow as a project resource.
var workflow = builder.AddProject<Projects.SquadifyDemo>("squadify-workflow")
    .WithReference(squad)
    .WithEnvironment("USE_AZURE_OPENAI", useAzure ? "true" : "false")
    .WithHttpEndpoint(name: "http", env: "HTTP_PORTS")
    .WithHttpCommand("/incidents/simulate", "Trigger Incident",
        commandOptions: new()
        {
            Method = HttpMethod.Post,
            IconName = "Flash",
            IsHighlighted = true
        })
    .WithHttpCommand("/trace", "View Traces",
        commandOptions: new() { IconName = "TextBulletListSquare" });

if (useAzure)
{
    workflow.WithEnvironment("AZURE_OPENAI_ENDPOINT", azureEndpoint!)
           .WithEnvironment("AZURE_OPENAI_KEY", azureKey ?? "");
}
else
{
    workflow.WithReference(chatResource!);
}

builder.Build().Run();
