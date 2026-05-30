namespace Erda.Channels;

/// <summary>
/// Drains <see cref="WhatsAppInboundQueue"/> and processes each message with
/// <see cref="WhatsAppChannelService"/>. One message at a time; a failure in one never stops the loop.
/// </summary>
public sealed class WhatsAppInboundWorker(
    WhatsAppInboundQueue queue,
    WhatsAppChannelService channel,
    ILogger<WhatsAppInboundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WhatsApp inbound worker started.");
        try
        {
            await foreach (var message in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await channel.ProcessAsync(message, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error processing an inbound WhatsApp message.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
