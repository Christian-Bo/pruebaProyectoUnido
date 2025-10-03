namespace Auth.Application.DTOs;


//requisistos necesario para el registro del nuevo usuario 
public class RegisterRequest
{
    public string Usuario { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string NombreCompleto { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string? Telefono { get; set; }
}

//requisitos necesario para el inicio de sesión
public class LoginRequest
{
    public string UsuarioOrEmail { get; set; } = default!;
    public string Password { get; set; } = default!;
}
