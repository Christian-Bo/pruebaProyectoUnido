namespace Lexico.Domain.Entities;

public class Analisis
{
    public int Id { get; set; }
    public int ArchivoId { get; set; }
    public int TotalPalabras { get; set; }
    public string PalabrasFrecuentes { get; set; } = "";
    public string PalabrasRaras { get; set; } = "";
    public string Pronombres { get; set; } = "";
    public string Sustantivos { get; set; } = "";
    public string Verbos { get; set; } = "";
    public DateTime FechaAnalisis { get; set; } = DateTime.UtcNow;
}
