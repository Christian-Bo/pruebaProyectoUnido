using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Auth.Application.Contracts;
using Auth.Application.DTOs;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Auth.API.Controllers;

/// <summary>
/// Controlador de autenticación con endpoints optimizados.
/// 
/// MEJORAS IMPLEMENTADAS:
/// - Logging estructurado en todos los endpoints
/// - Manejo consistente de errores
/// - Validación de entrada mejorada
/// - Rate limiting ready (agregar atributo cuando se implemente)
/// 
/// ENDPOINTS:
/// - POST /register: Registro rápido (email en background)
/// - POST /login: Login con usuario/email
/// - POST /logout: Revocación de sesión
/// - GET /me: Info del usuario autenticado
/// - POST /send-card-now: Reenvío manual de carnet
/// 
/// ESCALABILIDAD:
/// - Para API Gateway: Agregar versionado (v1, v2)
/// - Para multi-tenant: Inyectar tenant desde headers
/// - Para auditoría: Agregar middleware de logging de requests
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService auth, ILogger<AuthController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    /// <summary>
    /// Registra un nuevo usuario.
    /// RESPUESTA RÁPIDA: Email se envía en background.
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest dto)
    {
        try
        {
            // Validación básica (ModelState se valida automáticamente con [ApiController])
            if (!ModelState.IsValid)
            {
                _logger.LogWarning("[REGISTER] ModelState inválido: {Errors}", 
                    string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
                return BadRequest(ModelState);
            }

            _logger.LogInformation("[REGISTER] Intento de registro: {Usuario}/{Email}", 
                dto.Usuario, dto.Email);

            var res = await _auth.RegisterAsync(dto);

            _logger.LogInformation("[REGISTER] Registro exitoso: {Usuario}", dto.Usuario);
            
            return Ok(res);
        }
        catch (InvalidOperationException ex)
        {
            // Error de negocio (usuario duplicado, etc.)
            _logger.LogWarning(ex, "[REGISTER] Error de validación: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            // Error inesperado
            _logger.LogError(ex, "[REGISTER] Error inesperado para {Usuario}", dto.Usuario);
            return StatusCode(500, new { error = "Error interno del servidor" });
        }
    }

    /// <summary>
    /// Inicia sesión con usuario/email y contraseña.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest dto)
    {
        try
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            _logger.LogInformation("[LOGIN] Intento de login: {Credential}", dto.UsuarioOrEmail);

            var res = await _auth.LoginAsync(dto);

            return Ok(res);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("[LOGIN] Credenciales inválidas: {Credential}", dto.UsuarioOrEmail);
            return Unauthorized(new { error = "Credenciales inválidas" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LOGIN] Error inesperado para {Credential}", dto.UsuarioOrEmail);
            return StatusCode(500, new { error = "Error interno del servidor" });
        }
    }

    /// <summary>
    /// Cierra sesión (revoca el token actual).
    /// </summary>
    [Authorize]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var bearer = Request.Headers.Authorization.ToString();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            _logger.LogInformation("[LOGOUT] Usuario {UserId} cerrando sesión", userId);

            await _auth.LogoutAsync(bearer);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LOGOUT] Error inesperado");
            return StatusCode(500, new { error = "Error al cerrar sesión" });
        }
    }

    /// <summary>
    /// Obtiene información del usuario autenticado.
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        try
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userName = User.Identity?.Name;
            var email = User.FindFirstValue(ClaimTypes.Email);

            var user = new
            {
                Id = id,
                Usuario = userName,
                Email = email
            };

            _logger.LogDebug("[ME] Usuario {UserId} consultó su info", id);

            return Ok(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ME] Error inesperado");
            return StatusCode(500, new { error = "Error al obtener información" });
        }
    }

    /// <summary>
    /// Reenvía el carnet con QR por email.
    /// 
    /// IMPORTANTE:
    /// - Puede ser llamado desde frontend después del registro
    /// - También puede ser usado para reenviar carnets manualmente
    /// - NO bloquea la respuesta (email se envía en background)
    /// 
    /// SEGURIDAD:
    /// - Actualmente AllowAnonymous para compatibilidad con registro
    /// - MEJORA FUTURA: Requerir autorización o implementar rate limiting
    /// </summary>
    [AllowAnonymous] // TEMPORAL: Cambiar a [Authorize] cuando frontend esté adaptado
    [HttpPost("send-card-now")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SendCardNow([FromBody] SendCardNowRequest dto)
    {
        try
        {
            // Validación
            if (dto is null || dto.UsuarioId <= 0)
            {
                _logger.LogWarning("[SEND-CARD] Request inválido: {Dto}", dto);
                return BadRequest(new { error = "usuarioId es requerido y debe ser mayor a 0" });
            }

            _logger.LogInformation("[SEND-CARD] Solicitado para usuario {UserId}", dto.UsuarioId);

            byte[]? fotoOverride = null;
            if (!string.IsNullOrWhiteSpace(dto.FotoBase64))
            {
                var b64 = StripDataUrlPrefix(dto.FotoBase64);
                try
                {
                    fotoOverride = Convert.FromBase64String(NormalizeBase64(b64));
                    _logger.LogInformation("[SEND-CARD] FotoBase64 recibida: {KB}KB", fotoOverride.Length / 1024);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SEND-CARD] FotoBase64 inválida. Se ignorará y se usará la de BD si existe.");
                }
            }

            // NO debe lanzar excepciones (AuthService maneja errores internamente)
            await _auth.SendCardNowAsync(dto.UsuarioId, fotoOverride);

            return Ok(new 
            { 
                message = "Carnet encolado para envío", 
                usuarioId = dto.UsuarioId,
                conFotoOverride = fotoOverride != null,
                nota = "El email se enviará en los próximos segundos. Revisa tu bandeja de entrada y spam."
            });
        }
        catch (Exception ex)
        {
            // Solo en caso de error crítico (no debería ocurrir)
            _logger.LogError(ex, "[SEND-CARD] Error crítico para usuario {UserId}", dto?.UsuarioId);
            
            return StatusCode(500, new 
            { 
                error = "Error al procesar solicitud",
                detalle = ex.Message 
            });
        }
    }

    private static string StripDataUrlPrefix(string input)
    {
        const string marker = ";base64,";
        var idx = input.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? input[(idx + marker.Length)..] : input;
    }

    private static string NormalizeBase64(string b64)
    {
        b64 = b64.Trim().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "+", StringComparison.Ordinal);
        var mod = b64.Length % 4;
        if (mod != 0) b64 = b64.PadRight(b64.Length + (4 - mod), '=');
        return b64;
    }

    /// <summary>
    /// DTO para endpoint send-card-now.
    /// </summary>
    public class SendCardNowRequest
    {
        public int UsuarioId { get; set; }
        public string? FotoBase64 { get; set; }   // OPCIONAL: si se envía, se usa directamente para el PDF
    }
}
