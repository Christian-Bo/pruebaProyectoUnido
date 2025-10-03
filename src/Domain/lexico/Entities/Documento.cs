// Entities/Documento.cs
namespace Lexico.Domain.Entities;
public class Documento
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }     // viene de tu login externo
    public string NombreArchivo { get; set; } = "";
    public string ContenidoOriginal { get; set; } = "";
    public int IdiomaId { get; set; }
    public int TamanoArchivo { get; set; } // bytes
    public string HashDocumento { get; set; } = ""; // sha256
    public DateTime FechaCarga { get; set; }
    public string Estado { get; set; } = "cargado"; // ENUM en BD
}
