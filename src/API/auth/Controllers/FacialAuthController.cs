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

        // 1) Capturar -> Segmentar (front usa esto primero)
        [HttpPost("segment")]
        public async Task<ActionResult<SegmentResponseDto>> Segment([FromBody] SegmentRequestDto req)
        {
            if (string.IsNullOrWhiteSpace(req.RostroBase64))
                return BadRequest(new SegmentResponseDto { Success = false, Mensaje = "RostroBase64 requerido." });

            var (ok, seg, msg) = await _service.SegmentAsync(req.RostroBase64);
            return ok
                ? Ok(new SegmentResponseDto { Success = true, RostroSegmentado = seg })
                : BadRequest(new SegmentResponseDto { Success = false, Mensaje = msg });
        }

        // 2) Botón "Guardar foto" (guarda en autenticacion_facial)
        [HttpPost("save")]
        public async Task<ActionResult<SaveFaceResponseDto>> Save([FromBody] SaveFaceRequestDto req)
        {
            if (req.UsuarioId <= 0 || string.IsNullOrWhiteSpace(req.RostroBase64))
                return BadRequest(new SaveFaceResponseDto { Success = false, Mensaje = "UsuarioId y RostroBase64 requeridos." });

            var (ok, id, msg) = await _service.SaveFaceAsync(req.UsuarioId, req.RostroBase64);
            return ok
                ? Ok(new SaveFaceResponseDto { Success = true, FacialId = id, Mensaje = msg })
                : BadRequest(new SaveFaceResponseDto { Success = false, Mensaje = msg });
        }

        // 3) Login con rostro (segmenta adentro y compara)
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

