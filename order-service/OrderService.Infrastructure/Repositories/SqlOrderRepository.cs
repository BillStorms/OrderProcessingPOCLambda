using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Models;
using OrderService.Service.Interfaces;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure.Repositories;

public class SqlOrderRepository : IOrderRepository
{
    private readonly OrderDbContext _context;

    public SqlOrderRepository(OrderDbContext context)
    {
        _context = context;
    }

    public async Task InsertAsync(Order order)
    {
        await _context.Orders.AddAsync(order);
        await _context.SaveChangesAsync();
    }

    public async Task<Order?> GetAsync(string orderId)
    {
        return await _context.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.OrderId == orderId);
    }

    public async Task UpdateAsync(Order order)
    {
        _context.Orders.Update(order);
        await _context.SaveChangesAsync();
    }

    public async Task<bool> TryMarkEventProcessedAsync(string eventId)
    {
        // Attempt to insert a new ProcessedEvent. If the EventId already exists, return false.
        var pe = new ProcessedEvent
        {
            EventId = eventId,
            ProcessedAt = DateTime.UtcNow
        };

        try
        {
            _context.ProcessedEvents.Add(pe);
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Likely a unique key violation - event already processed
            return false;
        }
    }
}
