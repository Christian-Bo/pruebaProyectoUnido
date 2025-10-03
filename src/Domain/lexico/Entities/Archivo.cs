namespace Lexico.Domain.Entities;

public class Archivo
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Contenido { get; set; } = "";
    public DateTime FechaSubida { get; set; } = DateTime.UtcNow;

    // Si manejas usuario luego (FK), deja el campo:
    public int? UsuarioId { get; set; }
}
