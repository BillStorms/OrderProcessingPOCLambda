namespace OrderService.Api.DTOs;

public class OrderStatusResponseDto
{
    public string OrderId { get; set; } = null!;
    public string Status { get; set; } = null!;
    public FulfillmentDetailsDto? Fulfillment { get; set; }
}
