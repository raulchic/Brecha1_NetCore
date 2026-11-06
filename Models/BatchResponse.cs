namespace WebApi.Models;

/// <summary>
/// Respuesta devuelta al cliente cuando se acepta una solicitud de procesamiento de lote.
/// Se serializa a JSON y se devuelve con HTTP 202 Accepted.
/// Patrón aplicado: DTO (Data Transfer Object) — desacopla el modelo interno del contrato HTTP.
/// </summary>
public class BatchResponse
{
    /// <summary>
    /// Identificador único del lote generado. El cliente lo usa para consultar el estado
    /// mediante GET /api/batch/status/{batchId}.
    /// </summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>Estado inicial del lote al momento de aceptar la solicitud.</summary>
    public string Status { get; set; } = BatchStatusEnum.Pending.ToString();

    /// <summary>Mensaje descriptivo para el cliente.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Fecha y hora UTC en que se aceptó la solicitud.</summary>
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Indica si esta respuesta es el resultado de una solicitud idempotente previa.
    /// Permite al cliente saber que no se generó un nuevo trabajo.
    /// </summary>
    public bool IsIdempotentReplay { get; set; } = false;
}

/// <summary>
/// Respuesta devuelta por el endpoint GET /api/batch/status/{batchId}.
/// Incluye el estado actual y metadatos del ciclo de vida del lote.
/// </summary>
public class BatchStatusResponse
{
    /// <summary>Identificador único del lote consultado.</summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>Estado actual: Pending, Processing, Completed o Failed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Fecha y hora UTC en que se registró la solicitud original.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Fecha y hora UTC de la última actualización de estado.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Mensaje de error si el estado es Failed, nulo en caso contrario.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Respuesta estructurada para errores no controlados capturados por el middleware global.
/// Sigue el formato Problem Details (RFC 7807) extendido con traceId.
/// </summary>
public class ErrorResponse
{
    /// <summary>Código HTTP del error (ej: 400, 404, 500).</summary>
    public int StatusCode { get; set; }

    /// <summary>Mensaje descriptivo del error.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Identificador de trazabilidad de la solicitud. Corresponde al Activity.Current?.Id
    /// o al HttpContext.TraceIdentifier para correlacionar con logs del servidor.
    /// </summary>
    public string TraceId { get; set; } = string.Empty;
}
