using System.Net;
using System.Text.Json;
using WebApi.Models;

namespace WebApi.Middleware;

/// <summary>
/// Middleware global de manejo de errores no controlados.
/// 
/// Por qué un middleware en lugar de ExceptionFilter:
/// - El middleware intercepta CUALQUIER excepción en el pipeline completo, incluyendo
///   excepciones lanzadas antes de llegar al controlador (routing, model binding, etc.).
/// - Un ExceptionFilter solo captura excepciones dentro del action filter pipeline.
/// - Garantizamos que TODAS las respuestas de error tengan el mismo formato estructurado.
/// 
/// Formato de respuesta:
///   { "statusCode": 500, "message": "...", "traceId": "..." }
/// 
/// Patrón aplicado: Chain of Responsibility (patrón base del middleware de ASP.NET Core).
/// Ciclo de vida: Singleton (instanciado una vez por el pipeline).
/// </summary>
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    // Opciones de serialización JSON: camelCase para consistencia con la API REST
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invocación del middleware. Envuelve el pipeline completo en un try/catch.
    /// Si no hay excepción, la solicitud fluye normalmente.
    /// Si hay excepción, la capturamos, la logueamos y devolvemos una respuesta estructurada.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // Continuar con el siguiente middleware en el pipeline
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    /// <summary>
    /// Mapea la excepción a un código HTTP apropiado y serializa la respuesta de error.
    /// 
    /// Por qué mapeo explícito de tipos de excepción:
    /// - Permite devolver 400 para errores de validación sin exponer stack traces al cliente.
    /// - Las excepciones de dominio (ArgumentException) → 400, las inesperadas → 500.
    /// - El detalle completo del error solo aparece en los logs del servidor (no en la respuesta).
    /// </summary>
    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Mapeo de tipo de excepción → código HTTP
        var (statusCode, message) = exception switch
        {
            ArgumentException or ArgumentNullException => (HttpStatusCode.BadRequest, exception.Message),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "No autorizado."),
            KeyNotFoundException => (HttpStatusCode.NotFound, exception.Message),
            OperationCanceledException => (HttpStatusCode.ServiceUnavailable, "La operación fue cancelada."),
            _ => (HttpStatusCode.InternalServerError, "Ocurrió un error interno. Consulte los logs del servidor.")
        };

        // TraceId: usamos Activity.Current?.Id si está disponible (distributed tracing),
        // con fallback a HttpContext.TraceIdentifier (único por solicitud en ASP.NET Core).
        string traceId = System.Diagnostics.Activity.Current?.Id
                         ?? context.TraceIdentifier;

        // Log con nivel apropiado según la gravedad
        if (statusCode == HttpStatusCode.InternalServerError)
        {
            _logger.LogError(
                exception,
                "Error no controlado. TraceId: {TraceId} | Path: {Path}",
                traceId, context.Request.Path);
        }
        else
        {
            _logger.LogWarning(
                "Error controlado {StatusCode}. TraceId: {TraceId} | Path: {Path} | Mensaje: {Message}",
                (int)statusCode, traceId, context.Request.Path, exception.Message);
        }

        // Construimos la respuesta estructurada
        var errorResponse = new ErrorResponse
        {
            StatusCode = (int)statusCode,
            Message = message,
            TraceId = traceId
        };

        // Forzamos Content-Type JSON y el código HTTP correcto
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(errorResponse, JsonOptions));
    }
}

/// <summary>
/// Métodos de extensión para registrar el middleware de forma fluida en Program.cs.
/// Patrón aplicado: Extension Methods — mejora la legibilidad del pipeline de configuración.
/// </summary>
public static class ErrorHandlingMiddlewareExtensions
{
    /// <summary>
    /// Agrega el middleware de manejo de errores global al pipeline de ASP.NET Core.
    /// Debe registrarse ANTES de cualquier otro middleware para capturar todas las excepciones.
    /// </summary>
    public static IApplicationBuilder UseGlobalErrorHandling(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorHandlingMiddleware>();
}
