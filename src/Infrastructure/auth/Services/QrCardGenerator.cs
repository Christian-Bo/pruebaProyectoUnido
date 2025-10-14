using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using System;
using System.IO;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);

        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload);

        // con foto opcional
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes);

        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes);
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        // ===== Paleta UMG =====
        private const string UMG_RED   = "#B91C1C";
        private const string UMG_BLUE  = "#0B63A6";
        private const string UMG_GOLD  = "#D9B24C";
        private const string UMG_IVORY = "#FFF8E6";
        private const string INK       = "#102A43";
        private const string MUTED     = "#5B7083";

        // ===== Tamaño tarjeta (ligeramente más grande para evitar overflow) =====
        private const float CARD_WIDTH  = 400f;  // antes 370
        private const float CARD_HEIGHT = 260f;  // antes 250

        // ===== Layout =====
        private const float LEFT_BAR_WIDTH = 138f; // antes 124
        private const float LOGO_BOX_W     = 78f;
        private const float LOGO_BOX_H     = 78f;  // altura fija -> evita crecer
        private const float PHOTO_BOX_W    = 100f;
        private const float PHOTO_BOX_H    = 132f; // altura fija -> evita crecer
        private const float QR_BOX_W       = 112f;

        // Ruta del logo (opcional). Coloca tu imagen en Auth.Infrastructure/branding/umg-logo.png
        private static readonly string LogoPath =
            Path.Combine(AppContext.BaseDirectory, "branding", "umg-logo.png");

        private static readonly byte[]? DefaultLogoBytes =
            File.Exists(LogoPath) ? File.ReadAllBytes(LogoPath) : null;

        public QrCardGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            if (string.Equals(Environment.GetEnvironmentVariable("QUESTPDF_DEBUG"), "true", StringComparison.OrdinalIgnoreCase))
                QuestPDF.Settings.EnableDebugging = true;
        }

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, null);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, null);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
            => RenderCard(nombreCompleto, usuario, email, qrContenido, fotoBytes);

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName, string userName, string email, string qrPayload, byte[]? fotoBytes)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload, fotoBytes);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        private static byte[] RenderCard(
            string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
        {
            // QR en PNG
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
                        // ===== IZQUIERDA =====
                        row.ConstantItem(LEFT_BAR_WIDTH).Padding(8).Background(UMG_BLUE).Column(left =>
                        {
                            // Bloque superior (logo + nombre UMG) con alturas capadas
                            left.Item().Border(2).BorderColor(UMG_RED).Background(UMG_BLUE)
                                .Padding(4).Column(c =>
                                {
                                    // Logo (máx 78x78)
                                    c.Item().AlignCenter().Width(LOGO_BOX_W).Height(LOGO_BOX_H).Element(e =>
                                    {
                                        if (DefaultLogoBytes is { Length: > 0 })
                                            e.Image(DefaultLogoBytes);   // se adapta al contenedor
                                        else
                                            e.Border(1).BorderColor(UMG_GOLD).Padding(4)
                                             .Text("UMG").FontColor(UMG_IVORY).SemiBold().AlignCenter();
                                    });

                                    c.Item().PaddingTop(6).Text("UNIVERSIDAD\nMARIANO GÁLVEZ")
                                        .FontColor(UMG_IVORY).AlignCenter().SemiBold().FontSize(10);
                                });

                            // Foto (máx 100x132) con doble borde dorado/rojo
                            left.Item().PaddingTop(8).PaddingBottom(2).Column(fc =>
                            {
                                fc.Item().Padding(2).Border(2).BorderColor(UMG_GOLD).Padding(2)
                                  .Border(1).BorderColor(UMG_RED)
                                  .Width(PHOTO_BOX_W).Height(PHOTO_BOX_H)
                                  .Element(e =>
                                  {
                                      if (fotoBytes is { Length: > 0 })
                                          e.Image(fotoBytes); // se adapta al contenedor
                                      else
                                          e.AlignCenter().AlignMiddle()
                                           .Text("FOTO").FontColor(UMG_IVORY).FontSize(9);
                                  });
                            });
                        });

                        // ===== DERECHA =====
                        row.RelativeItem().Background(UMG_IVORY).Padding(10).Column(right =>
                        {
                            right.Item().Height(6).Background(UMG_RED);

                            right.Item().PaddingTop(6).Column(info =>
                            {
                                info.Item().Text(t => { t.Span("Nombre: ").FontColor(MUTED);  t.Span(nombreCompleto).SemiBold(); });
                                info.Item().Text(t => { t.Span("Usuario: ").FontColor(MUTED); t.Span(usuario); });
                                info.Item().Text(t => { t.Span("Email: ").FontColor(MUTED);   t.Span(email); });
                            });

                            right.Item().PaddingVertical(6).BorderBottom(1).BorderColor(UMG_GOLD);

                            right.Item().Row(qrRow =>
                            {
                                qrRow.ConstantItem(QR_BOX_W)
                                     .Border(1).BorderColor(UMG_GOLD).Padding(6)
                                     .Element(e => e.Image(qrPng));

                                qrRow.RelativeItem().PaddingLeft(8).Column(c =>
                                {
                                    c.Item().Text("Escanea para validar acceso").FontColor(MUTED).FontSize(9);
                                    c.Item().Text("Acceso autorizado.\nPresente este carnet.").FontColor(MUTED).Italic().FontSize(9);
                                });
                            });

                            right.Item().PaddingTop(6).Height(4).Background(UMG_BLUE);
                        });
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
