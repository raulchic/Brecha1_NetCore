using WebApi.Models;

namespace WebApi.Services;

/// <summary>
/// Contrato del servicio de lotes (Batch).
/// Define las operaciones de negocio disponibles independientemente de la implementación.
/// 
/// Por qué una interfaz:
/// - Principio de Inversión de Dependencias (SOLID-D): el controlador depende de la
///   abstracción, no de la implementación concreta.
/// - Facilita pruebas unitarias mediante mocking (ej: Moq, NSubstitute).
/// - Permite swappear la implementación sin modificar el controlador.
/// 
/// Patrón aplicado: Interface Segregation + Dependency Inversion (SOLID).
/// </summary>
public interface IBatchService
{
    /// <summary>
    /// Encola un nuevo trabajo de procesamiento de lote.
    /// Verifica idempotencia antes de encolar: si la clave ya fue procesada,
    /// retorna la respuesta previa sin crear un nuevo trabajo.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Clave de idempotencia opcional. Si se provee, evita el reprocesamiento duplicado.
    /// Si es null, se usa el BatchId generado como clave.
    /// </param>
    /// <param name="fileName">Nombre original del archivo subido.</param>
    /// <param name="fileContent">Contenido del archivo leído de forma asíncrona.</param>
    /// <returns>
    /// Tupla con la respuesta a devolver al cliente y un flag indicando si es replay idempotente.
    /// </returns>
    Task<BatchResponse> EnqueueBatchAsync(string? idempotencyKey, string fileName, string fileContent);

    /// <summary>
    /// Consulta el estado actual de un lote por su BatchId.
    /// </summary>
    /// <param name="batchId">Identificador único del lote.</param>
    /// <returns>El estado del lote, o null si el BatchId no existe.</returns>
    Task<BatchStatusResponse?> GetBatchStatusAsync(string batchId);
}
