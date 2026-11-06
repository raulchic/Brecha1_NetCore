using System.Collections.Concurrent;
using WebApi.Models;

namespace WebApi.Infrastructure;

/// <summary>
/// Contrato para el almacén de idempotencia.
/// Abstraer el store detrás de una interfaz permite reemplazarlo por una implementación
/// distribuida (Redis, base de datos) sin cambiar el controlador ni el servicio.
/// Patrón aplicado: Repository Pattern (simplificado para estado en memoria).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Intenta registrar una nueva entrada para la clave de idempotencia dada.
    /// Si ya existe, retorna false y el registro existente; si no, registra y retorna true.
    /// La operación es atómica (thread-safe).
    /// </summary>
    /// <param name="idempotencyKey">Clave única de idempotencia (BatchId o X-Idempotency-Key).</param>
    /// <param name="record">
    /// Al retornar true: el nuevo registro recién creado.
    /// Al retornar false: el registro previo ya almacenado.
    /// </param>
    /// <returns>true si se registró por primera vez; false si ya existía.</returns>
    bool TryRegister(string idempotencyKey, out BatchStatusRecord record);

    /// <summary>
    /// Actualiza el estado de un registro existente de forma thread-safe.
    /// Se llama desde el BackgroundService al cambiar el estado del lote.
    /// </summary>
    /// <param name="idempotencyKey">Clave del registro a actualizar.</param>
    /// <param name="status">Nuevo estado a asignar.</param>
    /// <param name="errorMessage">Mensaje de error opcional (solo para estado Failed).</param>
    void UpdateStatus(string idempotencyKey, BatchStatusEnum status, string? errorMessage = null);

    /// <summary>
    /// Recupera el registro de estado para una clave dada.
    /// Retorna null si la clave no existe.
    /// </summary>
    /// <param name="idempotencyKey">Clave del registro a consultar.</param>
    BatchStatusRecord? GetRecord(string idempotencyKey);
}

/// <summary>
/// Implementación en memoria del almacén de idempotencia usando ConcurrentDictionary.
/// 
/// Por qué ConcurrentDictionary:
/// - Es thread-safe por diseño: múltiples hilos (HTTP threads + BackgroundService) pueden
///   leer y escribir simultáneamente sin locks explícitos.
/// - GetOrAdd es atómico: garantiza que la misma clave no se registre dos veces
///   aunque dos solicitudes concurrentes lleguen al mismo tiempo.
/// - Para producción real, reemplazar por Redis o SQL para persistencia entre reinicios.
/// 
/// Patrón aplicado: Singleton (registrado como tal en DI) + Repository simplificado.
/// </summary>
public class IdempotencyStore : IIdempotencyStore
{
    // Diccionario thread-safe. Clave = idempotency key, Valor = registro de estado.
    private readonly ConcurrentDictionary<string, BatchStatusRecord> _store = new();

    /// <inheritdoc/>
    public bool TryRegister(string idempotencyKey, out BatchStatusRecord record)
    {
        // Creamos el registro candidato con estado inicial Pending
        var newRecord = new BatchStatusRecord
        {
            BatchId = idempotencyKey,
            Status = BatchStatusEnum.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // TryAdd es atómico: solo uno de los hilos concurrentes ganará el registro.
        // Si retorna false, la clave ya existía → solicitud duplicada detectada.
        bool added = _store.TryAdd(idempotencyKey, newRecord);

        // Recuperamos el registro real (sea nuevo o el ya existente)
        record = _store[idempotencyKey];
        return added;
    }

    /// <inheritdoc/>
    public void UpdateStatus(string idempotencyKey, BatchStatusEnum status, string? errorMessage = null)
    {
        // AddOrUpdate con función de actualización: modifica el registro existente in-place.
        // Usamos un nuevo objeto para evitar condiciones de carrera en la lectura del estado anterior.
        _store.AddOrUpdate(
            idempotencyKey,
            // Función de creación (no debería ocurrir aquí, pero manejo defensivo)
            _ => new BatchStatusRecord
            {
                BatchId = idempotencyKey,
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            },
            // Función de actualización: modifica el registro existente
            (_, existing) =>
            {
                existing.Status = status;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.ErrorMessage = errorMessage;
                return existing;
            });
    }

    /// <inheritdoc/>
    public BatchStatusRecord? GetRecord(string idempotencyKey)
    {
        _store.TryGetValue(idempotencyKey, out var record);
        return record;
    }
}
