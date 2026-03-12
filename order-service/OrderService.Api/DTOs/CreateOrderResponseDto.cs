namespace OrderService.Api.DTOs;

public class CreateOrderResponseDto
{
    public string OrderId { get; set; } = null!;
    public string Status { get; set; } = null!;
}
