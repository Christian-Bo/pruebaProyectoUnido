// Entities/FrecuenciaPalabra.cs
namespace Lexico.Domain.Entities;
public class FrecuenciaPalabra
{
    public int Id { get; set; }
    public int AnalisisId { get; set; }
    public string Palabra { get; set; } = "";
    public int Frecuencia { get; set; }
    public string TipoPalabra { get; set; } = "comun"; // comun|mas_repetida|menos_repetida
}
