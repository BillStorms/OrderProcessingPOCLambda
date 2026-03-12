using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Models;

namespace OrderService.Infrastructure.Data;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders { get; set; } = null!;
    public DbSet<ProcessedEvent> ProcessedEvents { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.OrderId);
            entity.Property(e => e.OrderId).IsRequired();
            entity.Property(e => e.CustomerId).IsRequired();
            entity.Property(e => e.CustomerName).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            // Configure Items as owned entity (stored in same table)
            entity.OwnsMany(e => e.Items, item =>
            {
                item.Property(i => i.ProductId).IsRequired();
                item.Property(i => i.Quantity).IsRequired();
            });

            // Configure Fulfillment as owned entity (stored in same table)
            entity.OwnsOne(e => e.Fulfillment, fulfillment =>
            {
                fulfillment.Property(f => f.TrackingNumber);
                fulfillment.Property(f => f.Carrier);
                fulfillment.Property(f => f.ShippedAt);
                fulfillment.Property(f => f.ErrorMessage);
            });
        });

        modelBuilder.Entity<ProcessedEvent>(pe =>
        {
            pe.HasKey(p => p.EventId);
            pe.Property(p => p.EventId).IsRequired();
            pe.Property(p => p.ProcessedAt).IsRequired();
        });
    }
}
