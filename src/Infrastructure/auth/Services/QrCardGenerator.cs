using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);

        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload);

        // con foto opcional
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes);
        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload,
            byte[]? fotoBytes);
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        // Paleta
        private const string PANEL  = "#111827"; // slate-900
        private const string BORDER = "#1f2937"; // slate-800
        private const string INK    = "#e5e7eb"; // slate-200
        private const string MUTED  = "#9ca3af"; // slate-400

        // Un poco más grande para dar “aire”
        private const float CARD_WIDTH  = 340f;   // pt
        private const float CARD_HEIGHT = 220f;   // pt

        public QrCardGenerator()
        {
            // Activa depuración si la variable está en true (útil en Railway para ver diagnosticos)
            // QUESTPDF_DEBUG=true
            var dbg = Environment.GetEnvironmentVariable("QUESTPDF_DEBUG");
            if (string.Equals(dbg, "true", StringComparison.OrdinalIgnoreCase))
                QuestPDF.Settings.EnableDebugging = true;

            QuestPDF.Settings.License = LicenseType.Community;
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

        private static byte[] RenderCard(string nombreCompleto, string usuario, string email, string qrContenido, byte[]? fotoBytes)
        {
            // QR como PNG en memoria
            using var qrGen = new QRCodeGenerator();
            var qrData = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var qrPng  = new PngByteQRCode(qrData).GetGraphic(9);

            // NOTA de diseño anti-conflictos:
            // - Evitamos Height fijo + Padding grande + contenido que no cabe.
            // - Para la foto: fijamos SOLO el ancho (96 pt) y un MinHeight suave; que la altura fluya.
            // - Para el QR: fijamos SOLO el ancho del contenedor (120 pt) y dejamos que la imagen se escale.
            // - Nada de .AlignRight() en ConstantItem; a veces provoca mediciones tensas según la versión.

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(CARD_WIDTH, CARD_HEIGHT);
                    page.Margin(10);

                    page.DefaultTextStyle(t => t.FontSize(10).FontColor(INK));

                    page.Content()
                        .Border(1).BorderColor(BORDER)
                        .Background(PANEL)
                        .Padding(12)
                        .Column(col =>
                        {
                            // Encabezado
                            col.Item().Row(row =>
                            {
                                row.RelativeItem().Text("Información usuarios").SemiBold().FontSize(12);

                                // Badge “UNIVERSIDAD” sin forzar alineaciones complejas
                                row.ConstantItem(112)
                                   .Border(1).BorderColor("#38bdf8")
                                   .Background("#0f172a")
                                   .PaddingVertical(3).PaddingHorizontal(8)
                                   .Text("UNIVERSIDAD").FontColor("#38bdf8").FontSize(9).SemiBold();
                            });

                            col.Item().LineHorizontal(0.75f).LineColor(BORDER);

                            // Cuerpo: Izquierda (foto+datos)  / Derecha (QR)
                            col.Item().Row(row =>
                            {
                                // IZQUIERDA
                                row.RelativeItem().Column(left =>
                                {
                                    left.Item().Row(r =>
                                    {
                                        // --- Foto
                                        r.ConstantItem(104).Column(fc =>
                                        {
                                            fc.Item().Text("Foto").FontColor(MUTED).FontSize(9);

                                            // Caja de foto: SOLO ancho fijo; altura fluye
                                            fc.Item()
                                              .Width(96)
                                              .MinHeight(96)
                                              .Border(1).BorderColor(BORDER)
                                              .Padding(3)
                                              .Element(e =>
                                              {
                                                  if (fotoBytes is { Length: > 0 })
                                                  {
                                                      // La imagen se escalará automáticamente al ancho disponible.
                                                      e.Image(fotoBytes);
                                                  }
                                                  else
                                                  {
                                                      e.AlignCenter().AlignMiddle()
                                                       .Text("Sin foto").FontColor(MUTED).FontSize(9);
                                                  }
                                              });
                                        });

                                        // --- Datos junto a la foto
                                        r.RelativeItem().Column(dc =>
                                            {
                                                dc.Item().Text(t =>
                                                {
                                                    t.Span("Usuario: ").FontColor(MUTED);
                                                });

                                                dc.Item().Text(t =>
                                                {
                                                    t.Span("Email: ").FontColor(MUTED);
                                                });

                                                dc.Item().PaddingTop(6)
                                                        .Text("Pequeña información")
                                                        .FontColor(MUTED).FontSize(10);
                                            });
                                    });
                                });

                                // DERECHA (QR)
                                row.ConstantItem(130).Column(qrCol =>
                                {
                                    // Caja del QR: SOLO ancho fijo; que la altura la decida el contenido
                                    qrCol.Item()
                                         .Width(120)
                                         .Border(1).BorderColor("#e2b857")
                                         .Padding(6)
                                         .Element(e =>
                                         {
                                             e.Image(qrPng);  // Se escalará al ancho disponible
                                         });

                                    qrCol.Item().Text("QR usuario").FontSize(9).FontColor(INK).AlignCenter();
                                });
                            });

                            // Pie
                            col.Item().PaddingTop(6)
                                      .Text("Acceso autorizado. Presente este carnet.")
                                      .Italic().FontColor(MUTED);
                        });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
