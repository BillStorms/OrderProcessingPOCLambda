using OrderService.Infrastructure.Kafka;

namespace OrderService.Lambda.Infrastructure;

/// <summary>
/// No-op implementation of ITopicInitializationService for the Lambda deployment.
/// Kafka topic initialization is not needed — SQS is used instead.
/// </summary>
public sealed class NoOpTopicInitializationService : ITopicInitializationService
{
    public Task InitializeTopicsAsync() => Task.CompletedTask;
}

