using System.ComponentModel.DataAnnotations; //para validaciones [key]... etc
using Microsoft.EntityFrameworkCore; //para los indices index

namespace Auth.Domain.Entities;

[Index(nameof(UsuarioId), Name = "idx_usuario_qr")]  //indices en los usuariosID para realizar consultas
[Index(nameof(Codigo), IsUnique = true, Name = "idx_codigo_qr")] //indices unico no duplicados
[Index(nameof(Activo), Name = "idx_activo_qr")] //indice activo o inactivo
public class CodigoQr
{
    [Key] public int Id { get; set; }
    public int UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = default!;

    [Required, StringLength(555)]
    public string Codigo { get; set; } = default!; // mapea a 'codigo_qr'

    [Required, StringLength(555)]
    public string QrHash { get; set; } = default!;

    public bool Activo { get; set; } = true;
}
