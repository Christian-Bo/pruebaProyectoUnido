using System.ComponentModel.DataAnnotations; //validaciones ejemplo [Key]....... etc
using Microsoft.EntityFrameworkCore;

namespace Auth.Domain.Entities;

[Index(nameof(UsuarioId), Name = "idx_usuario_facial")] //indice unico para que detecte el usuario con el reconocimineto_biometrico
[Index(nameof(Activo), Name = "idx_activo_facial")] //idice para visualizar si esta activo o no
public class AutenticacionFacial
{
    [Key] public int Id { get; set; }
    public int UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = default!;

    [Required] public string EncodingFacial { get; set; } = default!;  // TEXT
    public string? ImagenReferencia { get; set; } // TEXT (Base64)

    public bool Activo { get; set; } = true;
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
}
