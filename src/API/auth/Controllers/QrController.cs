using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Auth.Application.Contracts;
using Auth.Infrastructure.Data;                      // AppDbContext
using Auth.Infrastructure.Services;                 // IQrService, IQrCardGenerator
using Auth.Infrastructure.Services.Notifications;   // INotificationService
using Auth.Application.DTOs;

namespace Auth.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QrController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IQrService _qrService;                 // ← interfaz (antes concreto)
    private readonly IQrCardGenerator _card;                // ← interfaz (antes concreto)
    private readonly INotificationService _notify;
    private readonly IAuthService _auth;                    // para hacer login con QR del carnet

    public QrController(
        AppDbContext db,
        IQrService qrService,
        IQrCardGenerator card,
        INotificationService notify,
        IAuthService auth)
    {
        _db = db;
        _qrService = qrService;
        _card = card;
        _notify = notify;
        _auth = auth;
    }

    // Envía el carnet con QR al email del usuario
    [HttpPost("send-card")]
    public async Task<IActionResult> SendCard([FromBody] QrSendRequest dto)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u =>
            u.UsuarioNombre == dto.UsuarioOrEmail || u.Email == dto.UsuarioOrEmail);

        if (user is null || !user.Activo)
            return NotFound("Usuario no encontrado o inactivo.");

        // 1) Obtener/crear código QR
        var qr = await _qrService.GetOrCreateUserQrAsync(user.Id);

        // 2) Generar PDF del carnet con el QR
        //    Usa el generador que dejamos en Infrastructure/Services/QrCardGenerator.cs
        var pdf = _card.GenerateRegistrationPdf(
            fullName: user.NombreCompleto,
            userName: user.UsuarioNombre,
            email: user.Email,
            qrPayload: qr.Codigo
        );

        // 3) Enviar correo
        var subject = "Tu carnet de acceso con código QR";
        var body = $@"
            <p>Hola <b>{user.NombreCompleto}</b>,</p>
            <p>Adjuntamos tu carnet de acceso con código QR.</p>
            <p>Si no solicitaste este correo, por favor contacta a soporte.</p>
        ";

        await _notify.SendEmailAsync(
            toEmail: user.Email,
            subject: subject,
            htmlBody: body,
            attachment: (pdf.FileName, pdf.Content, pdf.ContentType) // adjunta el PDF
        );

        return Ok(new { message = "Carnet enviado al correo del usuario." });
    }

    // ============ NUEVO: Login usando el QR del carnet ============
    [HttpPost("login-qr/carnet")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> LoginConCarnet([FromBody] QrCarnetLoginRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto?.CodigoQr))
            return BadRequest("Código QR requerido.");

        var resp = await _auth.LoginByCarnetQrAsync(dto.CodigoQr);
        return Ok(resp);
    }

    // DTO local para el login por QR del carnet (si ya tienes uno global, puedes usarlo)
    public record QrCarnetLoginRequest(string CodigoQr);
}
