// Entities/ClasificacionGramatical.cs
namespace Lexico.Domain.Entities;
public class ClasificacionGramatical
{
    public int Id { get; set; }
    public int AnalisisId { get; set; }
    public string Palabra { get; set; } = "";
    public string? FormaRaiz { get; set; }
    public string Categoria { get; set; } = "otro";
    public int Frecuencia { get; set; } = 1;
    public decimal? Confianza { get; set; }
}
