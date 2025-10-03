// Entities/PatronReconocido.cs
namespace Lexico.Domain.Entities;
public class PatronReconocido
{
    public int Id { get; set; }
    public int AnalisisId { get; set; }
    public string TipoPatron { get; set; } = "otro";
    public string PatronEncontrado { get; set; } = "";
    public string? Contexto { get; set; }
    public int? PosicionInicio { get; set; }
    public int? PosicionFin { get; set; }
    public int Frecuencia { get; set; } = 1;
}
