using System.ComponentModel.DataAnnotations; //atributo para validación
using System.ComponentModel.DataAnnotations.Schema; //atributo para mapeo en DB
using Microsoft.EntityFrameworkCore; //atributo de funciones

namespace Auth.Domain.Entities;
//capa del nombre/carpeta donde se encuentra el archivo

[Index(nameof(UsuarioNombre), IsUnique = true, Name = "idx_usuario")] //creacion unica de indice de usuario
[Index(nameof(Email), IsUnique = true, Name = "idx_email")] //indice unico de email
[Index(nameof(Activo), Name = "idx_activo")] //para poder filtrar rapido si es activo o inactivo
public class Usuario
{
    [Key] public int Id { get; set; } //clave primaria ID

    [Required, StringLength(50)]
    public string UsuarioNombre { get; set; } = default!; // mapea a 'usuario'

    [Required, StringLength(100)]
    public string Email { get; set; } = default!;

    [Required, StringLength(150)]
    public string NombreCompleto { get; set; } = default!;

    [Required, StringLength(255)]
    public string PasswordHash { get; set; } = default!;

    [StringLength(20)]
    public string? Telefono { get; set; }

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public bool Activo { get; set; } = true;

    public ICollection<AutenticacionFacial> AutenticacionesFaciales { get; set; } = new List<AutenticacionFacial>(); //navegación de 1/n de reconocimiento facial
    public ICollection<CodigoQr> CodigosQr { get; set; } = new List<CodigoQr>();//Navegación de codigosQR para usuarios
    public ICollection<MetodoNotificacion> MetodosNotificacion { get; set; } = new List<MetodoNotificacion>(); //navegación a metodos de notificación
    public ICollection<Sesion> Sesiones { get; set; } = new List<Sesion>(); //navegación de 1/n tokens de sesión


    //Nota en este caso no se agrega los mimso nombres de la DB ya que existe un .cs el cual mapea todo eso para mas practicidad
}
