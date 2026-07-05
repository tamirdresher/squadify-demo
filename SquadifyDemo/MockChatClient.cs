using Microsoft.Extensions.AI;

/// <summary>
/// A mock chat client for testing without Azure OpenAI credentials.
/// Returns realistic-looking responses based on the last user message.
/// </summary>
public class MockChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("MockLLM");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var response = GenerateResponse(lastUserMessage);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var response = GenerateResponse(lastUserMessage);

        // Simulate streaming by yielding one chunk
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(response)]
        };

        await Task.CompletedTask;
    }

    public void Dispose() { }

    public TService? GetService<TService>(object? key = null) where TService : class
        => this as TService;

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsAssignableFrom(GetType()) ? this : null;

    private static string GenerateResponse(string input)
    {
        if (input.Contains("Investigate") || input.Contains("Analyze") || input.Contains("log"))
            return "Analysis: PaymentService has 523 errors in the last 5 minutes. " +
                   "Root cause: Connection pool exhaustion on checkout-db-primary. " +
                   "The connection limit of 100 was reached due to slow queries from " +
                   "a recent deployment (v2.4.1) that added an unindexed JOIN.";

        if (input.Contains("fix") || input.Contains("Fix") || input.Contains("remediat"))
            return "Remediation applied:\n" +
                   "1. ✅ Scaled connection pool from 100 → 250\n" +
                   "2. ✅ Added index on orders.customer_id (migration #847)\n" +
                   "3. ✅ Restarted PaymentService pods (3/3 healthy)\n" +
                   "Error rate now: 0.2% (within SLA)";

        if (input.Contains("Squad") || input.Contains("review") || input.Contains("decision"))
            return "Squad Decision: Approve remediation plan. " +
                   "The connection pool scaling + index fix addresses the root cause. " +
                   "Action: Proceed with deployment. Post-mortem scheduled.";

        return $"Processed: {input[..Math.Min(50, input.Length)]}...";
    }
}
