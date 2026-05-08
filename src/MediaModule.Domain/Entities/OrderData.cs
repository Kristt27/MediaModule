namespace MediaModule.Domain.Entities;

public sealed record OrderData(string OrderId, string ClientName, string ProductType)
{
    public string Status { get; init; } = "Completed";

    public DateTime? CompletedAtUtc { get; init; }
}
