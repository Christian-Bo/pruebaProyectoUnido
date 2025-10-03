using Microsoft.AspNetCore.Mvc;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;
using System.Text;

namespace Lexico.API.Controllers;

[ApiController]
[Route("api/upload")]
public class UploadController : ControllerBase
{
    private readonly IFileRepository _files;

    public UploadController(IFileRepository files) => _files = files;

    /// <summary>
    /// Sube un archivo .txt (máx 5 MB) y lo guarda en la base de datos.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]          // 👈 importante para Swashbuckle
    [RequestSizeLimit(5_000_000)]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(
        IFormFile file                           // 👈 SIN [FromForm]
        // Si luego necesitas más campos, añádelos como [FromForm] string x, etc.
    )
    {
        if (file == null || file.Length == 0)
            return BadRequest("Debe enviar un archivo .txt válido.");

        if (!file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Solo se permiten archivos .txt.");

        string contenido;
        // Lee como UTF-8 (detecta BOM si existiera)
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            contenido = await reader.ReadToEndAsync();

        var arch = new Archivo
        {
            Nombre = file.FileName,
            Contenido = contenido,
            FechaSubida = DateTime.UtcNow,
            UsuarioId = null // (pendiente de integrar con autenticación)
        };

        var id = await _files.InsertAsync(arch);

        return Ok(new
        {
            Mensaje = "Archivo cargado",
            ArchivoId = id,
            Nombre = arch.Nombre,
            Tamano = file.Length
        });
    }
}
