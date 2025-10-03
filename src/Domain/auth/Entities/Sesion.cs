using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Auth.Domain.Entities;

[Index(nameof(SessionTokenHash), Name = "idx_session_token")] //validaciones rapidas para los tokens
[Index(nameof(UsuarioId), Name = "idx_usuario_sesion")] //busca todas las sesiones del usuario activo
[Index(nameof(Activa), Name = "idx_activa")] //activo o inactivo
public class Sesion
{
    [Key] public int Id { get; set; }
    public int UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = default!;

    [Required, StringLength(255)]
    public string SessionTokenHash { get; set; } = default!; // guardamos HASH del JWT

    [Required]
    public MetodoLogin MetodoLogin { get; set; }

    public DateTime FechaLogin { get; set; } = DateTime.UtcNow;
    public bool Activa { get; set; } = true;
}
