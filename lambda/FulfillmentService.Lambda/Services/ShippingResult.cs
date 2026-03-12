namespace FulfillmentService.Lambda.Services;

public class ShippingResult
{
    public bool     Success        { get; set; }
    public string?  TrackingNumber { get; set; }
    public string?  Carrier        { get; set; }
    public DateTime? ShippedAt     { get; set; }
    public string?  ErrorMessage   { get; set; }
}

