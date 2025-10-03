using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Auth.Infrastructure.Data;                      // AppDbContext
using Auth.Infrastructure.Services;                 // QrService, QrCardGenerator
using Auth.Infrastructure.Services.Notifications;   // INotificationService
using Auth.Application.DTOs;

namespace Auth.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QrController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly QrService _qrService;                 // ← concreto
    private readonly QrCardGenerator _card;                // ← concreto
    private readonly INotificationService _notify;

    public QrController(
        AppDbContext db,
        QrService qrService,
        QrCardGenerator card,
        INotificationService notify)
    {
        _db = db;
        _qrService = qrService;
        _card = card;
        _notify = notify;
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
}
