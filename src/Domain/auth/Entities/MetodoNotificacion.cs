using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Auth.Domain.Entities;

[Index(nameof(UsuarioId), Name = "idx_usuario_notif")] //Buscar metodo para el usuario
[Index(nameof(Tipo), Name = "idx_tipo_notif")] //filtra el tipo de notificación E-mail / Whatsapp
public class MetodoNotificacion
{
    [Key] public int Id { get; set; }
    public int UsuarioId { get; set; }
    public Usuario Usuario { get; set; } = default!;

    [Required]
    public TipoNotificacion Tipo { get; set; } // enum → string

    [Required, StringLength(150)]
    public string Destino { get; set; } = default!;

    public bool Activo { get; set; } = true;
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow; //al momento de crearlo se coloca la fecha y hora de creación
}
