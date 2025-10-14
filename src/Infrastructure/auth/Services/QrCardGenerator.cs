using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;

namespace Auth.Infrastructure.Services
{
    public interface IQrCardGenerator
    {
        // Compatibilidad
        byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido);

        (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload);

        // Con foto opcional
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
        // Paleta basada en tu imagen
        private const string DARK  = "#00382F";  // verde oscuro
        private const string MID   = "#4E8F7E";  // verde medio
        private const string SAND  = "#D9CDA8";  // beige
        private const string IVORY = "#FFFAEE";  // marfil / off-white
        private const string INK   = "#0B1C18";  // tinta
        private const string MUTED = "#56706A";  // texto secundario

        // Tamaño pedido (igual que antes)
        private const float CARD_WIDTH  = 340f;  // pt
        private const float CARD_HEIGHT = 220f;  // pt

        // Layout
        private const float LEFT_BAR_WIDTH = 120f;  // ancho columna izquierda
        private const float QR_BOX_WIDTH   = 90f;   // ancho contenedor QR
        private const float PHOTO_BOX_W    = 96f;   // ancho foto

        public QrCardGenerator()
        {
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
            // Generar QR (PNG en memoria)
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
                        // ====== LADO IZQUIERDO (barra verde con foto) ======
                        row.ConstantItem(LEFT_BAR_WIDTH).Padding(8).Background(DARK).Column(left =>
                        {
                            // Logo / Título Universidad
                            left.Item().Border(1).BorderColor(SAND)
                                .PaddingVertical(4).PaddingHorizontal(6)
                                .AlignCenter()
                                .Text(txt =>
                                {
                                    txt.Line("TU").FontColor(IVORY).SemiBold().FontSize(9);
                                    txt.Line("LOGO").FontColor(IVORY).SemiBold().FontSize(9);
                                });

                            left.Item().PaddingTop(8).Text(t =>
                            {
                                t.AlignCenter();
                                t.Span("NOMBRE").FontColor(IVORY).SemiBold().FontSize(12);
                                t.Line("DE LA").FontColor(IVORY).SemiBold().FontSize(12);
                                t.Line("UNIVERSIDAD").FontColor(IVORY).SemiBold().FontSize(12);
                            });

                            // Foto (solo ancho fijo; altura fluye)
                            left.Item().PaddingTop(10).Column(fc =>
                            {
                                fc.Item().Width(PHOTO_BOX_W).Border(1).BorderColor(SAND).Padding(3)
                                  .Element(e =>
                                  {
                                      if (fotoBytes is { Length: > 0 })
                                      {
                                          e.Image(fotoBytes);
                                      }
                                      else
                                      {
                                          e.AlignCenter().AlignMiddle()
                                           .Text("FOTO").FontColor(SAND).FontSize(9);
                                      }
                                  });
                            });
                        });

                        // ====== LADO DERECHO (panel claro con datos + QR) ======
                        row.RelativeItem().Padding(10).Background(IVORY).Column(right =>
                        {
                            // Banda superior fina tipo acento
                            right.Item().Height(6).Background(MID);

                            // Datos
                            right.Item().PaddingTop(8).Column(info =>
                            {
                                info.Item().Text(t =>
                                {
                                    t.Span("Nombre: ").FontColor(MUTED);
                                    t.Span(nombreCompleto).SemiBold();
                                });

                                info.Item().Text(t =>
                                {
                                    t.Span("Usuario: ").FontColor(MUTED);
                                    t.Span(usuario);
                                });

                                info.Item().Text(t =>
                                {
                                    t.Span("Email: ").FontColor(MUTED);
                                    t.Span(email);
                                });
                            });

                            // Separador
                            right.Item().PaddingVertical(6).BorderBottom(1).BorderColor(SAND);

                            // QR + leyenda
                            right.Item().Row(qrRow =>
                            {
                                // Caja QR: solo ancho; altura fluye con la imagen
                                qrRow.ConstantItem(QR_BOX_WIDTH).Border(1).BorderColor(SAND).Padding(6)
                                     .Element(e => e.Image(qrPng));

                                // Texto de ayuda / leyenda (opcional)
                                qrRow.RelativeItem().PaddingLeft(8).Column(c =>
                                {
                                    c.Item().Text("Escanea para validar acceso")
                                           .FontColor(MUTED).FontSize(9);
                                    c.Item().Text("Acceso autorizado. Presente este carnet.")
                                           .FontColor(MUTED).Italic().FontSize(9);
                                });
                            });

                            // Pie con franja fina inferior
                            right.Item().AlignBottom().Height(6).Background(SAND);
                        });
                    });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
