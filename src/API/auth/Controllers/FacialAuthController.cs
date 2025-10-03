using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Auth.Infrastructure.Services;
using Auth.Application.DTOs;
using System.Security.Claims;

namespace Auth.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FacialAuthController : ControllerBase
{
    private readonly IFacialAuthService _facial;

    public FacialAuthController(IFacialAuthService facial)
    {
        _facial = facial;
    }

 
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> LoginFacial([FromBody] LoginFacialRequest dto)
    {
        var res = await _facial.LoginFacialAsync(dto);
        return Ok(res);
    }

    /// <summary>
    /// Registra una codificación facial para el usuario autenticado.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> RegisterFacial([FromBody] RegisterFacialRequest dto)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
        var userId = int.Parse(userIdStr);

        var id = await _facial.RegisterFacialAsync(userId, dto);
        return CreatedAtAction(nameof(RegisterFacial), new { id }, new { id });
    }
}
