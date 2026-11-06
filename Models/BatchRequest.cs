namespace WebApi.Models;

/// <summary>
/// Representa el trabajo de procesamiento de un lote que se encola en el Channel.
/// Esta clase viaja desde el controlador hasta el BackgroundService a través del Channel[T].
/// Patrón aplicado: Message — encapsula todo lo necesario para procesar el trabajo de forma desacoplada.
/// </summary>
public class BatchRequest
{
    /// <summary>
    /// Identificador único del lote generado por el controlador.
    /// Se usa como clave de trazabilidad en todos los logs.
    /// </summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>
    /// Clave de idempotencia recibida opcionalmente en el header X-Idempotency-Key.
    /// Si está presente, se usa para evitar el reprocesamiento de solicitudes duplicadas.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Nombre original del archivo subido, usado para logging y trazabilidad.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Contenido completo del archivo leído de forma asíncrona en el controlador.
    /// Se lee antes de encolar para no mantener el stream de la solicitud HTTP abierto.
    /// </summary>
    public string FileContent { get; set; } = string.Empty;

    /// <summary>Fecha y hora UTC en que se creó la solicitud.</summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}
