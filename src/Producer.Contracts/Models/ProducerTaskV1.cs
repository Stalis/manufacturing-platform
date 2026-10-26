namespace Producer.Contracts.Models;

public sealed record ProducerTaskV1(
    Guid ProductId, uint RevisionNumber, List<ProducerTaskV1.MaterialListItem> Materials, Guid? ReservationId = null)
{
    public record MaterialListItem(Guid MaterialId, uint Count);
    public Guid Id { get; init; } = Guid.CreateVersion7();
}
