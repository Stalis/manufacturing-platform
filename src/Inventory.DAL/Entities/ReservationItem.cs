namespace Inventory.DAL.Entities;

public class ReservationItem
{
    public Reservation? Reservation { get; set; }
    public Guid ReservationId { get; set; }
    public Guid MaterialId { get; set; }
    public uint Quantity { get; set; }
    public uint ConsumedQuantity { get; set; } = 0;
}