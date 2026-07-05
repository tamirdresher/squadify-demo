using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using SquadifyDemo.Tools;

namespace SquadifyDemo.Agents;

/// <summary>
/// Builds the incident analysis workflow using MAF primitives.
/// Shows: ChatClientAgent, AIFunction tools, and DAG workflows.
/// </summary>
public static class IncidentWorkflow
{
    /// <summary>
    /// Creates a simple agent with tools — the core MAF building block.
    /// </summary>
    public static ChatClientAgent CreateLogAnalyzer(IChatClient chatClient)
    {
        // Create tools from static methods — MAF discovers parameters automatically
        var tools = new[]
        {
            AIFunctionFactory.Create(LogAnalysisTools.GetServiceLogs),
            AIFunctionFactory.Create(LogAnalysisTools.GetServiceHealth),
            AIFunctionFactory.Create(LogAnalysisTools.GetConnectionPoolStatus)
        };

        return new ChatClientAgent(
            chatClient,
            name: "LogAnalyzer",
            instructions: """
                You are an expert at analyzing production logs and service health.
                When given an incident, use your tools to gather data, then provide
                a clear root cause analysis with evidence.
                Be concise — bullet points, not essays.
                """,
            tools: tools);
    }

    /// <summary>
    /// Creates the remediation agent that can take action.
    /// </summary>
    public static ChatClientAgent CreateRemediator(IChatClient chatClient)
    {
        var tools = new[]
        {
            AIFunctionFactory.Create(RemediationTools.KillDatabaseProcess),
            AIFunctionFactory.Create(RemediationTools.RestartConnectionPool),
            AIFunctionFactory.Create(RemediationTools.NotifyOnCall)
        };

        return new ChatClientAgent(
            chatClient,
            name: "Remediator",
            instructions: """
                You are an incident remediator. Given a root cause analysis,
                execute the appropriate fix using your tools.
                Always notify the on-call team after taking action.
                Report what you did and the current status.
                """,
            tools: tools);
    }

    /// <summary>
    /// Builds a sequential workflow: Analyze → Remediate.
    /// The output of the analyzer feeds into the remediator.
    /// </summary>
    public static AIAgent BuildSequentialPipeline(IChatClient chatClient)
    {
        var analyzer = CreateLogAnalyzer(chatClient);
        var remediator = CreateRemediator(chatClient);

        // Sequential: analyzer's output becomes remediator's input
        // .AsAIAgent() wraps the workflow so it can be run like any agent
        return AgentWorkflowBuilder.BuildSequential([analyzer, remediator]).AsAIAgent();
    }
}
