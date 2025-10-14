using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        // Compatibilidad (sin foto / sin logo)
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload);

        // Con foto (compat)
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes);
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes);

        // NUEVO: con branding (nombre y logo opcional)
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes,
            string universityName, byte[]? logoBytes);
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        // Paleta (tu referencia)
        private const string DARK  = "#00382F";  // verde oscuro
        private const string MID   = "#4E8F7E";  // verde medio
        private const string SAND  = "#D9CDA8";  // beige
        private const string IVORY = "#FFFAEE";  // marfil
        private const string INK   = "#0B1C18";  // texto
        private const string MUTED = "#56706A";  // texto secundario

        // Tamaño MÁS AMPLIO para que todo quepa en 1 página
        private const float CARD_WIDTH  = 360f;  // pt
        private const float CARD_HEIGHT = 240f;  // pt

        // Layout
        private const float LEFT_BAR_WIDTH = 120f;
        private const float PHOTO_BOX_W    = 96f;
        private const float QR_BOX_WIDTH   = 100f;

        public QrCardGenerator()
        {
            var dbg = Environment.GetEnvironmentVariable("QUESTPDF_DEBUG");
            if (string.Equals(dbg, "true", StringComparison.OrdinalIgnoreCase))
                QuestPDF.Settings.EnableDebugging = true;

            QuestPDF.Settings.License = LicenseType.Community;
        }

        // ===== Firmas de compatibilidad =====
        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, null, "UMG", null);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, null, "UMG", null);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, fotoBytes, "UMG", null);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, fotoBytes, "UMG", null);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        // ===== Nueva firma (con nombre y logo) =====
        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes,
            string universityName, byte[]? logoBytes)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, fotoBytes, universityName, logoBytes);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        // Render principal
        private static byte[] RenderCard(
            string nombreCompleto, string usuario, string email, string qrContenido,
            byte[]? fotoBytes, string universityName, byte[]? logoBytes)
        {
            // Generar QR
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var qrPng  = new PngByteQRCode(qrData).GetGraphic(9);

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(CARD_WIDTH, CARD_HEIGHT);
                    page.Margin(6);
                    page.DefaultTextStyle(t => t.FontSize(10).FontColor(INK));

                    page.Content().Row(row =>
                    {
                        // -------- Columna izquierda (barra + logo + título + foto) --------
                        row.ConstantItem(LEFT_BAR_WIDTH).Padding(8).Background(DARK).Column(left =>
                        {
                            // Logo (si viene)
                            left.Item().AlignCenter().Element(e =>
                            {
                                if (logoBytes is { Length: > 0 })
                                    e.Width(64).Image(logoBytes);
                                else
                                    e.Border(1).BorderColor(SAND).Padding(4)
                                     .Text("UMG").FontColor(IVORY).SemiBold().AlignCenter();
                            });

                            // Nombre de la universidad
                            left.Item().PaddingTop(6).Text(t =>
                            {
                                t.AlignCenter();
                                t.Span(universityName?.ToUpperInvariant() ?? "UMG")
                                 .FontColor(IVORY).SemiBold().FontSize(12);
                            });

                            // Foto (solo ancho; altura fluye. Borde doble estilo credencial)
                            left.Item().PaddingTop(8).Column(fc =>
                            {
                                fc.Item().Padding(2).Border(2).BorderColor(SAND).Padding(2)
                                  .Border(1).BorderColor(IVORY)
                                  .Width(PHOTO_BOX_W)
                                  .Element(e =>
                                  {
                                      if (fotoBytes is { Length: > 0 })
                                          e.Image(fotoBytes);
                                      else
                                          e.AlignCenter().AlignMiddle()
                                           .Text("FOTO").FontColor(SAND).FontSize(9);
                                  });
                            });
                        });

                        // -------- Columna derecha (datos + QR) --------
                        row.RelativeItem().Background(IVORY).Padding(10).Column(right =>
                        {
                            // Banda superior
                            right.Item().Height(6).Background(MID);

                            // Datos del usuario
                            right.Item().PaddingTop(6).Column(info =>
                            {
                                info.Item().Text(t => { t.Span("Nombre: ").FontColor(MUTED);  t.Span(nombreCompleto).SemiBold(); });
                                info.Item().Text(t => { t.Span("Usuario: ").FontColor(MUTED); t.Span(usuario); });
                                info.Item().Text(t => { t.Span("Email: ").FontColor(MUTED);   t.Span(email); });
                            });

                            // Separador
                            right.Item().PaddingVertical(6).BorderBottom(1).BorderColor(SAND);

                            // QR + leyenda
                            right.Item().Row(qrRow =>
                            {
                                qrRow.ConstantItem(QR_BOX_WIDTH)
                                     .Border(1).BorderColor(SAND).Padding(6)
                                     .Element(e => e.Image(qrPng));

                                qrRow.RelativeItem().PaddingLeft(8).Column(c =>
                                {
                                    c.Item().Text("Escanea para validar acceso").FontColor(MUTED).FontSize(9);
                                    c.Item().Text("Acceso autorizado.\nPresente este carnet.").FontColor(MUTED).Italic().FontSize(9);
                                });
                            });

                            // (sin barra inferior fija para no empujar contenido)
                        });
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
