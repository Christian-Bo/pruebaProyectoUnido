namespace Auth.Application.DTOs;

public class LoginFacialRequest
{
    public string? UsuarioOrEmail { get; set; }

    
    public string Encoding { get; set; } = default!;

    public string? ImagenBase64 { get; set; }
}

public class RegisterFacialRequest
{
    public string Encoding { get; set; } = default!;
    public string? ImagenBase64 { get; set; }
    public bool Activo { get; set; } = true;
}
