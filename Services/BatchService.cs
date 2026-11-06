using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WebApi.Infrastructure;
using WebApi.Models;

namespace WebApi.Services;

/// <summary>
/// Configuración externalizada del sistema de lotes.
/// Se enlaza con la sección "BatchSettings" de appsettings.json vía IOptions[T].
/// Patrón aplicado: Options Pattern — permite configuración fuertemente tipada y validable.
/// </summary>
public class BatchSettings
{
    public const string SectionName = "BatchSettings";

    /// <summary>Tamaño máximo del archivo en bytes. Por defecto: 10 MB.</summary>
    public long MaxFileSizeBytes { get; set; } = 10_485_760;

    /// <summary>Extensiones de archivo permitidas (incluir el punto, ej: ".csv").</summary>
    public string[] AllowedExtensions { get; set; } = [".csv", ".txt"];

    /// <summary>Número máximo de reintentos por línea fallida.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Capacidad máxima del Channel[BatchRequest].</summary>
    public int ChannelCapacity { get; set; } = 100;
}

/// <summary>
/// Implementación del servicio de lotes.
/// Responsabilidades únicas (SRP):
///   1. Verificar idempotencia antes de encolar.
///   2. Registrar el BatchId en el IdempotencyStore.
///   3. Escribir el trabajo en el Channel para que el BackgroundService lo consuma.
///   4. Consultar el estado de un lote.
/// 
/// No realiza el procesamiento real del archivo (eso es responsabilidad del BackgroundService).
/// 
/// Patrón aplicado: Service Layer + Producer en el patrón Producer-Consumer con Channel[T].
/// Ciclo de vida DI: Scoped (una instancia por solicitud HTTP).
/// </summary>
public class BatchService : IBatchService
{
    private readonly IBatchRepository _batchRepository;
    private readonly Channel<BatchRequest> _channel;
    private readonly ILogger<BatchService> _logger;

    /// <summary>
    /// Constructor con inyección de dependencias.
    /// El Channel se registra como Singleton en DI, lo que garantiza que el controlador
    /// y el BackgroundService compartan la misma instancia del canal.
    /// </summary>
    public BatchService(
        IBatchRepository batchRepository,
        Channel<BatchRequest> channel,
        ILogger<BatchService> logger)
    {
        _batchRepository = batchRepository;
        _channel = channel;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<BatchResponse> EnqueueBatchAsync(
        string? idempotencyKey,
        string fileName,
        string fileContent)
    {
        // Generamos un BatchId único para este trabajo
        string batchId = Guid.NewGuid().ToString();

        // La clave de idempotencia es el header X-Idempotency-Key si viene, o el BatchId nuevo.
        // Si el cliente no envía el header, cada solicitud es única por diseño.
        string effectiveKey = idempotencyKey ?? batchId;

        // Verificamos idempotencia: ¿ya fue procesada esta clave antes?
        var now = DateTimeOffset.UtcNow;
        var candidate = new BatchFileRecord
        {
            BatchId = batchId,
            IdempotencyKey = effectiveKey,
            FileName = Path.GetFileName(fileName),
            FileContent = fileContent,
            Status = BatchStatusEnum.Pending.ToString(),
            CreatedAt = now,
            UpdatedAt = now
        };
        var (existingRecord, isNew) = await _batchRepository.CreateAsync(candidate);

        if (!isNew)
        {
            // Solicitud duplicada detectada → devolvemos la respuesta original
            _logger.LogInformation(
                "Solicitud idempotente detectada. BatchId: {BatchId}, Clave: {Key}, Estado actual: {Status}",
                existingRecord.BatchId, effectiveKey, existingRecord.Status);

            return new BatchResponse
            {
                BatchId = existingRecord.BatchId,
                Status = existingRecord.Status,
                Message = "Solicitud duplicada. Se devuelve el resultado previo sin reprocesar.",
                AcceptedAt = existingRecord.CreatedAt,
                IsIdempotentReplay = true
            };
        }

        // Primera vez que se recibe esta clave → registramos y encolamos
        var request = new BatchRequest
        {
            BatchId = batchId,
            IdempotencyKey = effectiveKey,
            FileName = fileName,
            FileContent = fileContent,
            RequestedAt = DateTimeOffset.UtcNow
        };

        // Escribimos en el Channel. TryWrite es no-bloqueante: si el channel está lleno,
        // retorna false. Usamos WriteAsync para esperar espacio si está lleno (backpressure).
        // Esto evita pérdida de trabajos bajo carga alta.
        try
        {
            await _channel.Writer.WriteAsync(request);
            _logger.LogInformation(
                "BatchId: {BatchId} | Archivo '{FileName}' encolado exitosamente.",
                batchId, fileName);
        }
        catch (ChannelClosedException ex)
        {
            // El channel fue cerrado (apagado de la aplicación) → actualizamos estado
            await _batchRepository.UpdateStatusAsync(batchId, BatchStatusEnum.Failed, "El sistema se está apagando.");
            _logger.LogError(ex, "BatchId: {BatchId} | No se pudo encolar: channel cerrado.", batchId);
            throw;
        }

        return new BatchResponse
        {
            BatchId = batchId,
            Status = BatchStatusEnum.Pending.ToString(),
            Message = $"Archivo '{fileName}' aceptado y encolado para procesamiento.",
            AcceptedAt = now,
            IsIdempotentReplay = false
        };
    }

    /// <inheritdoc/>
    public async Task<BatchStatusResponse?> GetBatchStatusAsync(string batchId)
    {
        // Buscamos por BatchId en el store. Nota: el store usa la effectiveKey como clave,
        // que puede ser el BatchId si no se usó X-Idempotency-Key.
        // Para soportar búsqueda por BatchId cuando se usó header, almacenamos también por BatchId.
        var record = await _batchRepository.GetByBatchIdAsync(batchId);

        if (record is null)
        {
            _logger.LogWarning("Consulta de estado: BatchId {BatchId} no encontrado.", batchId);
            return null;
        }

        return new BatchStatusResponse
        {
            BatchId = record.BatchId,
            Status = record.Status,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ErrorMessage = record.ErrorMessage
        };
    }
}
