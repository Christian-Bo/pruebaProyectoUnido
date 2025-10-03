// Entities/LogProcesamiento.cs
namespace Lexico.Domain.Entities;
public class LogProcesamiento
{
    public int Id { get; set; }
    public int DocumentoId { get; set; }
    public string Etapa { get; set; } = "carga_documento";
    public string Estado { get; set; } = "iniciado";
    public string? Mensaje { get; set; }
    public DateTime TiempoInicio { get; set; }
    public DateTime? TiempoFin { get; set; }
    public int? DuracionMs { get; set; }
}
