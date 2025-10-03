// Lexico.API/Controllers/AnalisisController.cs
using Microsoft.AspNetCore.Mvc;
using Lexico.Application.Services;
using Lexico.Application.Contracts;

namespace Lexico.API.Controllers;

[ApiController]
[Route("api/analisis")]
public class AnalisisController : ControllerBase
{
    private readonly AnalysisService _svc;
    private readonly IAnalisisRepository _repoAnalisis;

    public AnalisisController(AnalysisService svc, IAnalisisRepository repoAnalisis)
    {
        _svc = svc;
        _repoAnalisis = repoAnalisis;
    }

    /// <summary>
    /// Ejecuta el análisis completo del documento y guarda resultados en todas las tablas.
    /// </summary>
    [HttpPost("{documentoId}")]
    public async Task<IActionResult> Ejecutar(int documentoId)
    {
        var a = await _svc.EjecutarAsync(documentoId);
        return Ok(new {
            Mensaje = "Análisis completado",
            a.Id,
            a.DocumentoId,
            a.TotalPalabras,
            a.PalabrasUnicas,
            a.TiempoProcesamiento,
            a.Estado
        });
    }

    /// <summary>
    /// Obtiene el último análisis del documento.
    /// </summary>
    [HttpGet("{documentoId}")]
    public async Task<IActionResult> Ultimo(int documentoId)
    {
        var a = await _repoAnalisis.GetLatestByDocumentoAsync(documentoId);
        return a is null ? NotFound("Sin análisis") : Ok(a);
    }
}
