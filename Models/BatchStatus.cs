namespace WebApi.Models;

/// <summary>
/// Enumeración de los posibles estados del ciclo de vida de un lote (batch).
/// Se usa como valor de estado en el IdempotencyStore y en las respuestas de la API.
/// Patrón aplicado: State Pattern (implícito vía enum + switch en el servicio).
/// </summary>
public enum BatchStatusEnum
{
    /// <summary>El lote fue encolado pero aún no comenzó a procesarse.</summary>
    Pending,

    /// <summary>El BackgroundService está procesando activamente este lote.</summary>
    Processing,

    /// <summary>El lote fue procesado exitosamente en su totalidad.</summary>
    Completed,

    /// <summary>El procesamiento del lote falló después de agotar los reintentos.</summary>
    Failed
}

/// <summary>
/// Registro de estado persistido en el IdempotencyStore para cada BatchId.
/// Contiene el estado actual, la respuesta original y metadatos de auditoría.
/// Patrón aplicado: Value Object — inmutable luego de completarse.
/// </summary>
public class BatchStatusRecord
{
    /// <summary>Identificador único del lote.</summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>Estado actual del lote en su ciclo de vida.</summary>
    public BatchStatusEnum Status { get; set; } = BatchStatusEnum.Pending;

    /// <summary>Fecha y hora UTC en que se registró la solicitud original.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Fecha y hora UTC de la última actualización de estado.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Respuesta original serializada. Se usa para devolver exactamente la misma respuesta
    /// ante una segunda solicitud con la misma clave de idempotencia.
    /// </summary>
    public object? OriginalResponse { get; set; }

    /// <summary>Mensaje de error en caso de fallo, nulo si fue exitoso.</summary>
    public string? ErrorMessage { get; set; }
}
