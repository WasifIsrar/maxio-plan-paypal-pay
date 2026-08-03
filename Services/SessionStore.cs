using System.Collections.Concurrent;

namespace MaxioPaypalBilling.Services;

public sealed class CheckoutSession
{
    public required string Id { get; init; }
    public bool Demo { get; init; }
    public int? CustomerId { get; init; }
    public int? ProductId { get; init; }
    public string? ProductFamilyId { get; init; }
    public int? SubscriptionId { get; init; }
    public string? InvoiceUid { get; init; }
    public string? InvoiceNumber { get; init; }
    public string? InvoiceFormat { get; init; }
    public required string Amount { get; init; }
    public required string Currency { get; init; }
    public required string PlanName { get; init; }
    public string? PaypalOrderId { get; set; }
    public required string CustomerEmail { get; init; }
    public required string CustomerName { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, CheckoutSession> _sessions = new();

    public void Save(CheckoutSession session) => _sessions[session.Id] = session;

    public CheckoutSession? Get(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public CheckoutSession Update(string id, Action<CheckoutSession> patch)
    {
        if (!_sessions.TryGetValue(id, out var existing))
        {
            throw new InvalidOperationException($"Unknown checkout session: {id}");
        }

        patch(existing);
        return existing;
    }
}
