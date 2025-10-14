using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Auth.Application.Contracts;
using Auth.Application.DTOs;

namespace Auth.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    public AuthController(IAuthService auth) => _auth = auth;

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest dto)
    {
        var res = await _auth.RegisterAsync(dto);
        return Ok(res);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest dto)
    {
        var res = await _auth.LoginAsync(dto);
        return Ok(res);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var bearer = Request.Headers.Authorization.ToString();
        await _auth.LogoutAsync(bearer);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = new
        {
            Id = id,
            Usuario = User.Identity?.Name,
            Email = User.FindFirstValue(ClaimTypes.Email)
        };
        return Ok(user);
    }

    // ⚠️ Déjalo AllowAnonymous mientras pruebas. Luego puedes volver a [Authorize].
    [AllowAnonymous]
    [HttpPost("send-card-now")]
    public async Task<IActionResult> SendCardNow([FromBody] SendCardNowRequest dto)
    {
        if (dto is null || dto.UsuarioId <= 0)
        {
            Console.WriteLine("[SEND-CARD] BadRequest: usuarioId faltante");
            return BadRequest(new { message = "usuarioId requerido." });
        }

        Console.WriteLine($"[SEND-CARD] Recibido usuarioId={dto.UsuarioId}");
        try
        {
            // No debe lanzar: AuthService manejará errores internos
            await _auth.SendCardNowAsync(dto.UsuarioId);

            Console.WriteLine($"[SEND-CARD] OK usuarioId={dto.UsuarioId}");
            return Ok(new { message = "Carnet enviado (si hay correo configurado y QR válido)." });
        }
        catch (Exception ex)
        {
            // Devuelve detalle para depurar (puedes ocultarlo en prod si quieres)
            Console.WriteLine($"[SEND-CARD] ERROR usuarioId={dto.UsuarioId}: {ex}");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    public class SendCardNowRequest
    {
        public int UsuarioId { get; set; }
    }
}
