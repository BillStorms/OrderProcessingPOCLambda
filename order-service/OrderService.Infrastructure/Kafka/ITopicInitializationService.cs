using System.Threading.Tasks;

namespace OrderService.Infrastructure.Kafka;

/// <summary>
/// Service to initialize and manage Kafka topics on application startup.
/// </summary>
public interface ITopicInitializationService
{
    /// <summary>
    /// Ensures the required Kafka topic exists. Creates it if it doesn't.
    /// Idempotent - safe to call multiple times.
    /// </summary>
    /// <returns>Task representing the asynchronous operation</returns>
    Task InitializeTopicsAsync();
}

