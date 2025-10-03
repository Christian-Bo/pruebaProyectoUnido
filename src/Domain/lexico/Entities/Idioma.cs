// Entities/Idioma.cs
namespace Lexico.Domain.Entities;
public class Idioma
{
    public int Id { get; set; }
    public string CodigoIso { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string NombreOriginal { get; set; } = "";
    public bool Activo { get; set; } = true;
    public DateTime FechaCreacion { get; set; }
}
