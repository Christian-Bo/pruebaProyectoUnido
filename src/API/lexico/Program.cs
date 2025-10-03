using System;
using System.Reflection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Mvc; // ApiBehaviorOptions
using Lexico.Application.Contracts;
using Lexico.Application.Services;
using Lexico.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Puerto dinámico (Railway inyecta PORT). Local: 8080.
// -----------------------------------------------------------------------------
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

// -----------------------------------------------------------------------------
// Swagger
// -----------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Lexico.API", Version = "v1" });

    // Evita conflictos de esquemas y acciones
    c.CustomSchemaIds(t => t.FullName);
    c.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());

    // Incluir XML SOLO si existe (no requiere tocar el .csproj)
    var xmlName = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlName);
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});

// Ayuda a Swashbuckle con IFormFile sin exigir [Consumes] en cada acción (opcional)
builder.Services.Configure<ApiBehaviorOptions>(opt =>
{
    opt.SuppressConsumesConstraintForFormFileParameters = true;
});

// -----------------------------------------------------------------------------
// CORS (lee AllowedOrigins de appsettings; si viene "*" o vacío => AllowAnyOrigin)
// -----------------------------------------------------------------------------
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o =>
{
    o.AddPolicy("Default", p =>
    {
        if (allowedOrigins.Length == 0 || Array.Exists(allowedOrigins, x => x == "*"))
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        else
            p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    });
});

// -----------------------------------------------------------------------------
// Límite global de subida multipart (por si subes TXT/PDF grandes)
// -----------------------------------------------------------------------------
var maxMultipart = builder.Configuration.GetValue<long?>("Uploads:MaxMultipartBodyLength") ?? 10_000_000; // 10 MB
builder.Services.Configure<FormOptions>(opt =>
{
    opt.MultipartBodyLengthLimit = maxMultipart;
});

// Alinear también el límite de Kestrel (por si no usas formulario multipart)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxMultipart;
});

// -----------------------------------------------------------------------------
// Servicios (Dapper + repos + servicio de análisis)
// -----------------------------------------------------------------------------
builder.Services.AddSingleton<DapperConnectionFactory>();
builder.Services.AddScoped<IIdiomaRepository, IdiomaRepository>();
builder.Services.AddScoped<IDocumentoRepository, DocumentoRepository>();
builder.Services.AddScoped<IAnalisisRepository, AnalisisRepository>();
builder.Services.AddScoped<ILogProcesamientoRepository, LogProcesamientoRepository>();
builder.Services.AddScoped<IConfiguracionAnalisisRepository, ConfiguracionAnalisisRepository>();
builder.Services.AddScoped<AnalysisService>();

builder.Services.AddControllers();

var app = builder.Build();

// -----------------------------------------------------------------------------
// Cabeceras de proxy (Railway está detrás de reverse proxy)
// -----------------------------------------------------------------------------
var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
// Evita los warnings "Unknown proxy"
fwd.KnownNetworks.Clear();
fwd.KnownProxies.Clear();
app.UseForwardedHeaders(fwd);

// -----------------------------------------------------------------------------
// Swagger (mantener en prod para /swagger)
// -----------------------------------------------------------------------------
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Lexico.API v1");
});

// -----------------------------------------------------------------------------
// CORS
// -----------------------------------------------------------------------------
app.UseCors("Default");

// HTTPS redirection detrás de proxy puede dar warning; si quieres, déjalo desactivado
// app.UseHttpsRedirection();

app.MapControllers();



app.Run();
