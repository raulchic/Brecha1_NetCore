using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using WebApi.Infrastructure;
using WebApi.Middleware;
using WebApi.Services;

// ============================================================
// PROGRAM.CS — Punto de entrada y composición de la aplicación
// Patrón aplicado: Composition Root — todos los servicios se
// registran en un único lugar al arrancar la aplicación.
// ============================================================

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("BatchDatabase");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configure ConnectionStrings__BatchDatabase para conectar con SQL Server.");

builder.Services.AddDbContext<BatchDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped<IBatchRepository, BatchRepository>();

// -------------------------------------------------------
// 1. CONFIGURACIÓN EXTERNALIZADA (Options Pattern)
// -------------------------------------------------------
// Enlazamos la sección "BatchSettings" del appsettings.json con la clase BatchSettings.
// IOptions<BatchSettings> estará disponible para inyección en cualquier servicio.
builder.Services.Configure<BatchSettings>(
    builder.Configuration.GetSection(BatchSettings.SectionName));

// -------------------------------------------------------
// 2. CHANNEL<BATCHREQUEST> — Primitiva de concurrencia
// -------------------------------------------------------
// Registramos el Channel como Singleton para que BatchService (Scoped) y
// BatchBackgroundService (Singleton) compartan la misma instancia.
//
// BoundedChannelOptions con capacidad limitada implementa backpressure:
// si el channel está lleno, WriteAsync espera hasta que haya espacio,
// evitando consumo de memoria ilimitado bajo carga alta.
//
// FullMode.Wait: el producer espera (backpressure) en lugar de descartar o lanzar excepción.
int channelCapacity = builder.Configuration
    .GetSection(BatchSettings.SectionName)
    .GetValue<int>("ChannelCapacity", 100);

builder.Services.AddSingleton(Channel.CreateBounded<WebApi.Models.BatchRequest>(
    new BoundedChannelOptions(channelCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,  // Solo el BackgroundService lee del channel
        SingleWriter = false  // Múltiples solicitudes HTTP pueden escribir concurrentemente
    }));

// -------------------------------------------------------
// 3. INFRAESTRUCTURA
// -------------------------------------------------------
// -------------------------------------------------------
// 4. SERVICIOS DE NEGOCIO
// -------------------------------------------------------
// BatchService: Scoped (una instancia por solicitud HTTP).
// Puede ser Scoped porque el Channel y el Store son Singleton y se inyectan correctamente.
builder.Services.AddScoped<IBatchService, BatchService>();

// -------------------------------------------------------
// 5. BACKGROUND SERVICE
// -------------------------------------------------------
// AddHostedService registra el servicio para que el host de ASP.NET Core
// lo inicie/detenga automáticamente con la aplicación.
// Se instancia una sola vez (Singleton implícito para IHostedService).
builder.Services.AddHostedService<BatchBackgroundService>();

// -------------------------------------------------------
// 6. CONTROLADORES
// -------------------------------------------------------
builder.Services.AddControllers();

// -------------------------------------------------------
// 7. SWAGGER / OPENAPI (Swashbuckle.AspNetCore)
// -------------------------------------------------------
// Generamos documentación OpenAPI automáticamente a partir de los comentarios XML
// y los atributos ProducesResponseType de los controladores.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Batch Processing API",
        Version = "v1",
        Description = "API para procesamiento asíncrono de archivos en lote. " +
                      "Implementa el patrón Producer-Consumer con Channel<T>, " +
                      "idempotencia con ConcurrentDictionary y BackgroundService para procesamiento en segundo plano.",
        Contact = new OpenApiContact
        {
            Name = "Equipo de Desarrollo",
            Email = "dev@empresa.com"
        }
    });

    // Incluir los comentarios XML generados para enriquecer la documentación Swagger
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
        options.IncludeXmlComments(xmlPath);

    // Agregar soporte para el header X-Idempotency-Key en la UI de Swagger
    options.AddSecurityDefinition("IdempotencyKey", new OpenApiSecurityScheme
    {
        Name = "X-Idempotency-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "Clave opcional para garantizar idempotencia en solicitudes repetidas."
    });
});

// -------------------------------------------------------
// 8. CONSTRUCCIÓN DE LA APLICACIÓN
// -------------------------------------------------------
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<BatchDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

// -------------------------------------------------------
// 9. PIPELINE DE MIDDLEWARE (orden importa)
// -------------------------------------------------------

// El middleware de errores DEBE ser el primero para capturar excepciones de todo el pipeline
app.UseGlobalErrorHandling();

// Swagger solo en ambientes no-producción (buena práctica de seguridad)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Batch Processing API v1");
        options.RoutePrefix = "swagger"; // Disponible en /swagger
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// -------------------------------------------------------
// 10. INICIO
// -------------------------------------------------------
app.Run();
