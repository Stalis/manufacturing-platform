namespace Inventory.DAL.Entities;

public class Reservation
{
    public Guid Id { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Opened;
    public List<ReservationItem> Items { get; set; } = [];
}