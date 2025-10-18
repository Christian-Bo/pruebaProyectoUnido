using System.Threading.Channels;

namespace Auth.Infrastructure.Services.Notifications;

/// <summary>
/// Representa un trabajo de envío de email con adjuntos opcionales.
/// MEJORA FUTURA: Agregar prioridad, reintentos configurables, y persistencia en DB.
/// </summary>
public record EmailJob(
    string To,
    string Subject,
    string HtmlBody,
    string? AttachmentName,
    byte[]? AttachmentBytes,
    string? AttachmentContentType
);

/// <summary>
/// Cola thread-safe para trabajos de email.
/// MEJORA FUTURA: Implementar con Redis o RabbitMQ para distribución multi-instancia.
/// </summary>
public interface IEmailJobQueue
{
    ValueTask EnqueueAsync(EmailJob job, CancellationToken ct = default);
    IAsyncEnumerable<EmailJob> DequeueAsync(CancellationToken ct);
}

/// <summary>
/// Implementación en memoria con Channel.
/// LIMITACIÓN: Se pierden trabajos si el proceso se detiene.
/// MEJORA FUTURA: Persistir en base de datos o cola externa.
/// </summary>
public class InMemoryEmailJobQueue : IEmailJobQueue
{
    // Capacidad limitada para evitar consumo excesivo de memoria
    private readonly Channel<EmailJob> _channel = Channel.CreateBounded<EmailJob>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait, // Espera si la cola está llena
            SingleReader = true,  // Solo el background service lee
            SingleWriter = false  // Múltiples threads pueden escribir
        });

    public ValueTask EnqueueAsync(EmailJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public async IAsyncEnumerable<EmailJob> DequeueAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
        {
            while (_channel.Reader.TryRead(out var job))
                yield return job;
        }
    }
}