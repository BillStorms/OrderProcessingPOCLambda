using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderService.Service.Events;
using OrderService.Service.Interfaces;

namespace OrderService.Lambda.Infrastructure;

/// <summary>
/// Replaces KafkaEventPublisher for the Lambda deployment path.
/// Publishes OrderCreatedEvent as a JSON message to an SQS queue, which triggers
/// the FulfillmentService Lambda.
/// </summary>
public sealed class SqsEventPublisher : IEventPublisher
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly ILogger<SqsEventPublisher> _logger;

    public SqsEventPublisher(IAmazonSQS sqs, IConfiguration config, ILogger<SqsEventPublisher> logger)
    {
        _sqs      = sqs;
        _logger   = logger;
        _queueUrl = config["SQS:OrderEventsQueueUrl"]
            ?? throw new InvalidOperationException("SQS:OrderEventsQueueUrl config is missing");
    }

    public async Task PublishOrderCreatedAsync(OrderCreatedEvent evt)
    {
        // Guarantee an EventId for downstream idempotency
        if (string.IsNullOrWhiteSpace(evt.EventId))
            evt.EventId = Guid.NewGuid().ToString();

        var body = JsonSerializer.Serialize(evt);

        var request = new SendMessageRequest
        {
            QueueUrl    = _queueUrl,
            MessageBody = body,
            // MessageDeduplicationId + MessageGroupId are only needed for FIFO queues.
            // Using a standard queue here; idempotency is handled by the consumer.
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["EventId"] = new MessageAttributeValue
                {
                    DataType    = "String",
                    StringValue = evt.EventId
                },
                ["EventType"] = new MessageAttributeValue
                {
                    DataType    = "String",
                    StringValue = evt.EventType
                }
            }
        };

        var response = await _sqs.SendMessageAsync(request);
        _logger.LogInformation(
            "Published {EventType} for order {OrderId} → SQS MessageId={MessageId}",
            evt.EventType, evt.OrderId, response.MessageId);
    }
}

