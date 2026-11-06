using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebApi.Models;
using WebApi.Services;

namespace WebApi.Controllers;

/// <summary>
/// Controlador de operaciones de lotes (Batch).
/// 
/// Responsabilidades (SRP):
///   - Recibir y validar la solicitud HTTP.
///   - Leer el contenido del archivo de forma asíncrona.
///   - Delegar la lógica de negocio al BatchService.
///   - Devolver la respuesta HTTP apropiada.
/// 
/// Lo que NO hace este controlador (separación de responsabilidades):
///   - No procesa el archivo directamente.
///   - No gestiona el Channel ni el store de idempotencia.
///   - No conoce los detalles de implementación del BackgroundService.
/// 
/// Patrón aplicado: Thin Controller (delega en el servicio, no tiene lógica de negocio).
/// </summary>
[ApiController]
[Route("api/batch")]
[Produces("application/json")]
public class BatchController : ControllerBase
{
    private readonly IBatchService _batchService;
    private readonly BatchSettings _settings;
    private readonly ILogger<BatchController> _logger;

    // Extensiones permitidas como HashSet para búsqueda O(1)
    private readonly HashSet<string> _allowedExtensions;

    public BatchController(
        IBatchService batchService,
        IOptions<BatchSettings> settings,
        ILogger<BatchController> logger)
    {
        _batchService = batchService;
        _settings = settings.Value;
        _logger = logger;
        _allowedExtensions = new HashSet<string>(
            _settings.AllowedExtensions,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sube un archivo para procesamiento en lote asíncrono.
    /// </summary>
    /// <remarks>
    /// Acepta archivos .csv o .txt de hasta 10 MB. El procesamiento ocurre en segundo plano.
    /// 
    /// **Idempotencia:** si se incluye el header `X-Idempotency-Key` con la misma clave
    /// en múltiples solicitudes, solo la primera encola el trabajo. Las siguientes devuelven
    /// el mismo resultado con `isIdempotentReplay: true`.
    /// 
    /// **Ejemplo de uso:**
    /// ```
    /// POST /api/batch/upload
    /// Headers: X-Idempotency-Key: mi-clave-unica-123
    /// Body: form-data con campo "file" conteniendo el archivo .csv
    /// ```
    /// </remarks>
    /// <param name="file">Archivo a procesar. Debe ser .csv o .txt, máximo 10 MB.</param>
    /// <response code="202">Archivo aceptado y encolado para procesamiento en segundo plano.</response>
    /// <response code="400">Validación fallida: archivo nulo, vacío, extensión no permitida o tamaño excedido.</response>
    /// <response code="500">Error interno del servidor.</response>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(BatchResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    [RequestSizeLimit(10_485_760)] // 10 MB — límite a nivel de IIS/Kestrel también
    public async Task<IActionResult> UploadBatchFile(IFormFile? file)
    {
        // --- VALIDACIÓN ESTRICTA DEL ARCHIVO ---

        // 1. El archivo no debe ser nulo ni estar vacío
        if (file is null || file.Length == 0)
        {
            _logger.LogWarning("Intento de subida con archivo nulo o vacío.");
            return BadRequest(new ErrorResponse
            {
                StatusCode = 400,
                Message = "El archivo es requerido y no puede estar vacío.",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        // 2. Validar extensión del archivo
        string extension = Path.GetExtension(file.FileName);
        if (!_allowedExtensions.Contains(extension))
        {
            _logger.LogWarning(
                "Extensión no permitida: '{Extension}'. Permitidas: {Allowed}",
                extension, string.Join(", ", _settings.AllowedExtensions));

            return BadRequest(new ErrorResponse
            {
                StatusCode = 400,
                Message = $"Extensión '{extension}' no permitida. Se aceptan: {string.Join(", ", _settings.AllowedExtensions)}.",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        // 3. Validar tamaño máximo del archivo
        if (file.Length > _settings.MaxFileSizeBytes)
        {
            long maxMb = _settings.MaxFileSizeBytes / 1_048_576;
            _logger.LogWarning(
                "Archivo demasiado grande: {Size} bytes. Máximo: {Max} bytes.",
                file.Length, _settings.MaxFileSizeBytes);

            return BadRequest(new ErrorResponse
            {
                StatusCode = 400,
                Message = $"El archivo excede el tamaño máximo permitido de {maxMb} MB.",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        // --- LECTURA ASÍNCRONA DEL CONTENIDO ---
        // Leemos el contenido ANTES de encolar para liberar el stream HTTP.
        // Si encolaramos el IFormFile directamente, el stream podría ser disposed
        // antes de que el BackgroundService lo procese.
        string fileContent;
        using (var streamReader = new StreamReader(file.OpenReadStream()))
        {
            fileContent = await streamReader.ReadToEndAsync();
        }

        // Extraemos la clave de idempotencia del header opcional
        string? idempotencyKey = Request.Headers["X-Idempotency-Key"].FirstOrDefault();

        _logger.LogInformation(
            "Archivo '{FileName}' recibido ({Size} bytes). IdempotencyKey: {Key}",
            file.FileName, file.Length, idempotencyKey ?? "(no proporcionada)");

        // Delegamos al servicio la verificación de idempotencia y el encolado
        var response = await _batchService.EnqueueBatchAsync(idempotencyKey, file.FileName, fileContent);

        // HTTP 202 Accepted: la solicitud fue aceptada pero el procesamiento no ha concluido
        return Accepted(response);
    }

    /// <summary>
    /// Consulta el estado actual de un lote por su identificador único.
    /// </summary>
    /// <remarks>
    /// Permite al cliente hacer polling para conocer si el procesamiento finalizó.
    /// 
    /// **Estados posibles:**
    /// - `Pending`: encolado, aún no inició procesamiento.
    /// - `Processing`: el BackgroundService está procesando el archivo.
    /// - `Completed`: procesamiento finalizado exitosamente.
    /// - `Failed`: el procesamiento falló (ver campo `errorMessage`).
    /// 
    /// **Ejemplo de uso:**
    /// ```
    /// GET /api/batch/status/3fa85f64-5717-4562-b3fc-2c963f66afa6
    /// ```
    /// </remarks>
    /// <param name="batchId">Identificador único del lote obtenido al subir el archivo.</param>
    /// <response code="200">Estado actual del lote.</response>
    /// <response code="404">El BatchId no existe o no fue registrado en este servidor.</response>
    [HttpGet("status/{batchId}")]
    [ProducesResponseType(typeof(BatchStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatchStatus([FromRoute] string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return BadRequest(new ErrorResponse
            {
                StatusCode = 400,
                Message = "El BatchId es requerido.",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        var status = await _batchService.GetBatchStatusAsync(batchId);

        if (status is null)
        {
            _logger.LogWarning("BatchId {BatchId} no encontrado.", batchId);
            return NotFound(new ErrorResponse
            {
                StatusCode = 404,
                Message = $"El BatchId '{batchId}' no existe.",
                TraceId = HttpContext.TraceIdentifier
            });
        }

        return Ok(status);
    }
}
