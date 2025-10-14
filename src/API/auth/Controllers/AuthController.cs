using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

using Auth.Infrastructure;
using Auth.Application.Contracts;     // IAuthService
using Auth.Application.DTOs;

namespace Auth.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    // Si aún te da ambigüedad, cambia la firma por:
    // public AuthController(Auth.Application.Contracts.IAuthService auth) => _auth = auth;

    public AuthController(IAuthService auth) => _auth = auth;

    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest dto)
    {
        var res = await _auth.RegisterAsync(dto);
        return Ok(res);
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
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

    [HttpPost("send-card-now")]
    [Authorize] // opcional; quítalo si quieres permitirlo sin token
    public async Task<IActionResult> SendCardNow([FromBody] SendCardNowRequest dto)
    {
        if (dto is null || dto.UsuarioId <= 0)
            return BadRequest("usuarioId requerido.");

        await _auth.SendCardNowAsync(dto.UsuarioId);
        return Ok(new { message = "Carnet enviado (si hay correo configurado y QR válido)." });
    }

    public class SendCardNowRequest
    {
        public int UsuarioId { get; set; }
    }
}
