using Auth.Application.Contracts;
using Auth.Application.DTOs;
using Application.auth.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace API.auth.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FacialAuthController : ControllerBase
    {
        private readonly IFacialAuthService _service;

        public FacialAuthController(IFacialAuthService service)
        {
            _service = service;
        }

        [HttpPost("login")]
        public async Task<ActionResult<FacialLoginResponse>> Login([FromBody] FacialLoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.RostroBase64))
                return BadRequest(new FacialLoginResponse { Success = false, Mensaje = "RostroBase64 requerido." });

            var (ok, userId, tokenOrMsg) = await _service.LoginWithFaceAsync(req.RostroBase64);

            if (!ok)
                return Unauthorized(new FacialLoginResponse { Success = false, Mensaje = tokenOrMsg });

            return Ok(new FacialLoginResponse { Success = true, Token = tokenOrMsg, Mensaje = "Inicio de sesión exitoso." });
        }
    }
}
