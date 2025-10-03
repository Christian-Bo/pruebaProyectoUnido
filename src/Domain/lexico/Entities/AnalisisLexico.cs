// Entities/AnalisisLexico.cs
namespace Lexico.Domain.Entities;
public class AnalisisLexico
{
    public int Id { get; set; }
    public int DocumentoId { get; set; }
    public int TotalPalabras { get; set; }
    public int PalabrasUnicas { get; set; }
    public decimal? TiempoProcesamiento { get; set; } // segundos
    public DateTime FechaProcesamiento { get; set; }
    public string Estado { get; set; } = "iniciado";
    public string? MensajeError { get; set; }
}
