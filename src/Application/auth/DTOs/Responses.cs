namespace Auth.Application.DTOs;

//objetos de respuestas tipicos para el login/inicio de sesion

public class AuthResponse
{
    public string AccessToken { get; set; } = default!;
    public int ExpiresInSeconds { get; set; }
    public UsuarioDto Usuario { get; set; } = default!;
}


//Vista segura de la información del usuario
public class UsuarioDto
{
    public int Id { get; set; }
    public string Usuario { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string NombreCompleto { get; set; } = default!;
    public string? Telefono { get; set; }
}

public class FacialLoginResponse
    {
        public bool Success { get; set; }
        public string? Token { get; set; }
        public string? Mensaje { get; set; }
    }
public class SegmentResponseDto
    {
        public bool Success { get; set; }
        public string? RostroSegmentado { get; set; } // base64 resultante
        public string? Mensaje { get; set; }
    }

    public class SaveFaceResponseDto
    {
        public bool Success { get; set; }
        public int? FacialId { get; set; }
        public string? Mensaje { get; set; }
    }    