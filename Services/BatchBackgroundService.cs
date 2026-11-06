using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WebApi.Infrastructure;
using WebApi.Models;

namespace WebApi.Services;

/// <summary>
/// Servicio en segundo plano responsable de consumir y procesar los lotes encolados.
/// 
/// Por qué BackgroundService:
/// - Ejecuta en el mismo proceso que la API sin necesidad de infraestructura externa.
/// - El host de ASP.NET Core gestiona su ciclo de vida automáticamente (start/stop).
/// - Se integra limpiamente con CancellationToken para apagado graceful.
/// 
/// Patrón aplicado: Consumer en el patrón Producer-Consumer con Channel[T].
/// El Channel actúa como buffer entre el thread HTTP (producer) y este worker (consumer).
/// 
/// Ciclo de vida DI: Singleton (registrado como IHostedService).
/// </summary>
public class BatchBackgroundService : BackgroundService
{
    private readonly Channel<BatchRequest> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BatchSettings _settings;
    private readonly ILogger<BatchBackgroundService> _logger;

    /// <summary>
    /// Constructor con inyección de dependencias.
    /// Al ser Singleton, recibe el mismo Channel e IdempotencyStore que BatchService.
    /// </summary>
    public BatchBackgroundService(
        Channel<BatchRequest> channel,
        IServiceScopeFactory scopeFactory,
        IOptions<BatchSettings> options,
        ILogger<BatchBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Punto de entrada del worker. Corre en loop hasta que se cancele el token.
    /// Lee del Channel de forma asíncrona: si no hay trabajo, espera sin consumir CPU.
    /// 
    /// Por qué ReadAllAsync:
    /// - Es la forma idiomática de consumir un Channel en .NET.
    /// - Se completa automáticamente cuando el Channel se cierra (apagado graceful).
    /// - No necesita polling ni sleep: el scheduler de .NET gestiona la espera.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BatchBackgroundService iniciado. Esperando trabajos...");

        // ReadAllAsync bloquea de forma asíncrona hasta que haya un elemento o se cancele.
        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            // Procesamos cada trabajo de forma independiente para no bloquear el loop
            await ProcessBatchAsync(request, stoppingToken);
        }

        _logger.LogInformation("BatchBackgroundService detenido.");
    }

    /// <summary>
    /// Procesa un lote completo: lee línea por línea con reintentos exponenciales.
    /// 
    /// Flujo:
    ///   1. Marcar como Processing en el store.
    ///   2. Leer el contenido línea por línea con StreamReader (streaming).
    ///   3. Por cada línea: procesar con hasta N reintentos con espera exponencial.
    ///   4. Al final: marcar como Completed o Failed.
    /// </summary>
    private async Task ProcessBatchAsync(BatchRequest request, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BatchId: {BatchId} | Iniciando procesamiento del archivo '{FileName}'.",
            request.BatchId, request.FileName);

        // Paso 1: marcar como Processing
        await using var scope = _scopeFactory.CreateAsyncScope();
        var batchRepository = scope.ServiceProvider.GetRequiredService<IBatchRepository>();

        int totalLines = 0;
        int failedLines = 0;

        try
        {
            await batchRepository.UpdateStatusAsync(request.BatchId, BatchStatusEnum.Processing, cancellationToken: stoppingToken);
            // Paso 2: leer línea por línea usando StringReader (simula streaming de grandes archivos).
            // En un escenario real con archivos persistidos en disco/blob, se usaría FileStream.
            // Usamos StringReader porque el contenido ya fue leído en el controlador para liberar
            // el stream HTTP antes de encolar el trabajo.
            using var reader = new StringReader(request.FileContent);

            string? line;
            int lineNumber = 0;

            while ((line = await ReadLineAsync(reader)) is not null)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "BatchId: {BatchId} | Procesamiento interrumpido por señal de apagado en línea {Line}.",
                        request.BatchId, lineNumber);
                    break;
                }

                lineNumber++;
                totalLines++;

                // Paso 3: procesar cada línea con reintentos exponenciales
                bool lineSuccess = await ProcessLineWithRetriesAsync(
                    request.BatchId, lineNumber, line, stoppingToken);
                await batchRepository.SaveLineAsync(
                    request.BatchId,
                    lineNumber,
                    line,
                    lineSuccess,
                    lineSuccess ? null : "La línea falló tras agotar los reintentos.",
                    stoppingToken);

                if (!lineSuccess)
                    failedLines++;
            }

            // Paso 4: determinar el estado final del lote
            if (failedLines == 0)
            {
                await batchRepository.UpdateStatusAsync(
                    request.BatchId, BatchStatusEnum.Completed, cancellationToken: stoppingToken);

                _logger.LogInformation(
                    "BatchId: {BatchId} | Procesamiento COMPLETADO. Total líneas: {Total}.",
                    request.BatchId, totalLines);
            }
            else
            {
                string errorMsg = $"Fallaron {failedLines} de {totalLines} líneas tras agotar reintentos.";
                await batchRepository.UpdateStatusAsync(
                    request.BatchId, BatchStatusEnum.Failed, errorMsg, stoppingToken);

                _logger.LogWarning(
                    "BatchId: {BatchId} | Procesamiento FALLIDO parcialmente. {Failed}/{Total} líneas fallidas.",
                    request.BatchId, failedLines, totalLines);
            }
        }
        catch (Exception ex)
        {
            string errorMsg = $"Error inesperado: {ex.Message}";
            await batchRepository.UpdateStatusAsync(
                request.BatchId, BatchStatusEnum.Failed, errorMsg, CancellationToken.None);

            _logger.LogError(
                ex,
                "BatchId: {BatchId} | Error inesperado durante el procesamiento.",
                request.BatchId);
        }
    }

    /// <summary>
    /// Procesa una línea individual con lógica de reintentos idempotentes.
    /// 
    /// Estrategia de backoff exponencial:
    ///   - Intento 1: espera 0 ms (inmediato)
    ///   - Intento 2: espera 2^1 * 100ms = 200ms
    ///   - Intento 3: espera 2^2 * 100ms = 400ms
    /// 
    /// Por qué exponencial:
    /// - Reduce la presión sobre el recurso fallante (ej: I/O, red).
    /// - Evita thundering herd en escenarios con múltiples workers.
    /// </summary>
    /// <returns>true si la línea fue procesada exitosamente; false si agotó reintentos.</returns>
    private async Task<bool> ProcessLineWithRetriesAsync(
        string batchId,
        int lineNumber,
        string line,
        CancellationToken stoppingToken)
    {
        int maxAttempts = _settings.MaxRetryAttempts;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Simulamos el procesamiento de la línea
                // En un caso real: parsear CSV, validar, insertar en BD, etc.
                await SimulateLineProcessingAsync(batchId, lineNumber, line);

                // Log de trazabilidad con el formato requerido
                _logger.LogInformation(
                    "[BatchId: {BatchId}] Línea {LineNumber}: {Content}",
                    batchId, lineNumber, line);

                Console.WriteLine($"[BatchId: {batchId}] Línea {lineNumber}: {line}");

                return true; // Éxito → salir del loop de reintentos
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                // Aún quedan intentos → espera exponencial y reintenta
                int delayMs = (int)Math.Pow(2, attempt) * 100;

                _logger.LogWarning(
                    "BatchId: {BatchId} | Línea {LineNumber} falló en intento {Attempt}/{Max}. " +
                    "Reintentando en {Delay}ms. Error: {Error}",
                    batchId, lineNumber, attempt, maxAttempts, delayMs, ex.Message);

                await Task.Delay(delayMs, stoppingToken);
            }
            catch (Exception ex)
            {
                // Último intento agotado → línea marcada como fallida
                _logger.LogError(
                    ex,
                    "BatchId: {BatchId} | Línea {LineNumber} FALLIDA tras {Max} intentos.",
                    batchId, lineNumber, maxAttempts);

                return false;
            }
        }

        return false; // Por diseño no se llega aquí, pero el compilador lo requiere
    }

    /// <summary>
    /// Simula el procesamiento de una línea de forma asíncrona.
    /// Reemplazar por lógica real: parseo, validación, persistencia, etc.
    /// 
    /// Por qué async Task:
    /// - Permite que el caller use await y no bloquee el thread pool.
    /// - Facilita el testing al poder inyectar delays o excepciones simuladas.
    /// </summary>
    private static async Task SimulateLineProcessingAsync(string batchId, int lineNumber, string line)
    {
        // Simulamos una pequeña latencia de procesamiento (ej: escritura a BD)
        await Task.Delay(10);

        // Línea vacía no se considera error, solo se omite
        if (string.IsNullOrWhiteSpace(line))
            return;

        // Para demostración: las líneas que contienen "ERROR_TEST" simulan un fallo
        // Esto permite probar el mecanismo de reintentos sin infraestructura externa
        if (line.Contains("ERROR_TEST", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Simulación de error en línea {lineNumber}");
    }

    /// <summary>
    /// Wrapper asíncrono para StringReader.ReadLine().
    /// StringReader no tiene ReadLineAsync verdaderamente asíncrono (opera en memoria),
    /// pero wrapearlo en Task permite mantener el contrato async del pipeline completo
    /// y facilita el reemplazo futuro por StreamReader real (disco/red).
    /// </summary>
    private static Task<string?> ReadLineAsync(StringReader reader)
        => Task.FromResult(reader.ReadLine());
}
