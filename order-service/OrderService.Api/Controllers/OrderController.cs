using Microsoft.AspNetCore.Mvc;
using Asp.Versioning;
using OrderService.Service.Interfaces;
using OrderService.Service.DTOs;
using OrderService.Api.DTOs;
using OrderService.Domain.Models;

namespace OrderService.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/orders")]
public class OrderController : ControllerBase
{
    private readonly IOrderService _service;
    private readonly ILogger<OrderController> _logger;

    public OrderController(IOrderService service, ILogger<OrderController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new order
    /// </summary>
    /// <param name="request">Order creation request with customer and item details</param>
    /// <returns>Created order with order ID and status</returns>
    /// <response code="200">Order created successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="429">Too many requests - rate limit exceeded</response>
    [HttpPost]
    [ProducesResponseType(typeof(CreateOrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CreateOrderResponseDto>> CreateOrder([FromBody] CreateOrderRequest request)
    {
        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid order request: {Errors}", 
                string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
            return BadRequest(ModelState);
        }

        var orderId = await _service.CreateOrderAsync(request);

        _logger.LogInformation("Wigit Order {OrderId} created for customer {CustomerId}", 
            orderId, request.CustomerId);

        return Ok(new CreateOrderResponseDto
        {
            OrderId = orderId,
            Status = "Created"
        });
    }

    /// <summary>
    /// Gets order details by ID
    /// </summary>
    /// <param name="orderId">The unique order identifier</param>
    /// <returns>Order details including status and fulfillment information</returns>
    /// <response code="200">Order found</response>
    /// <response code="404">Order not found</response>
    /// <response code="429">Too many requests - rate limit exceeded</response>
    [HttpGet("{orderId}")]
    [ProducesResponseType(typeof(OrderStatusResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OrderStatusResponseDto>> GetOrder(string orderId)
    {
        var order = await _service.GetOrderAsync(orderId);

        if (order == null)
        {
            _logger.LogWarning("Order {OrderId} not found", orderId);
            return NotFound();
        }

        return Ok(new OrderStatusResponseDto
        {
            OrderId = order.OrderId,
            Status = order.Status.ToString(),
            Fulfillment = order.Fulfillment == null
            ? null
            : new FulfillmentDetailsDto
            {
                TrackingNumber = order.Fulfillment.TrackingNumber,
                Carrier = order.Fulfillment.Carrier,
                ShippedAt = order.Fulfillment.ShippedAt,
                ErrorMessage = order.Fulfillment.ErrorMessage
            }
        });
    }

    /// <summary>
    /// Updates order status and fulfillment details
    /// </summary>
    /// <param name="orderId">The unique order identifier</param>
    /// <param name="requestDto">Status update request with optional fulfillment details</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Order updated successfully</response>
    /// <response code="400">Invalid status value</response>
    /// <response code="404">Order not found</response>
    /// <response code="429">Too many requests - rate limit exceeded</response>
    [HttpPatch("{orderId}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> UpdateOrderStatus(string orderId, [FromBody] UpdateOrderStatusRequestDto requestDto)
    {
        if (!Enum.TryParse<OrderStatus>(requestDto.Status, out var status))
            return BadRequest(new { Error = "Invalid status value" });

        FulfillmentDetails? fulfillment = null;
        if (!string.IsNullOrEmpty(requestDto.TrackingNumber) || !string.IsNullOrEmpty(requestDto.Carrier))
        {
            fulfillment = new FulfillmentDetails
            {
                TrackingNumber = requestDto.TrackingNumber,
                Carrier = requestDto.Carrier,
                ShippedAt = requestDto.ShippedAt,
                ErrorMessage = requestDto.ErrorMessage
            };
        }

        // Read optional X-Event-Id header to help with deduplication
        string? eventId = null;
        if (Request.Headers.TryGetValue("X-Event-Id", out var values))
        {
            eventId = values.FirstOrDefault();
        }

        var success = await _service.UpdateOrderStatusAsync(orderId, status, fulfillment, eventId);

        if (!success)
            return NotFound();

        return NoContent();
    }
}
