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
    }

    public class QrCardGenerator : IQrCardGenerator
    {
        private const string PANEL   = "#111827";
        private const string BORDER  = "#1f2937";
        private const string INK     = "#e5e7eb";
        private const string MUTED   = "#9ca3af";
        private const string ACCENT  = "#38bdf8";
        private const string ACCENT2 = "#e2b857";

        private const float CARD_WIDTH  = 300f;
        private const float CARD_HEIGHT = 190f;
        private const float RADIUS      = 14f;

        private static readonly byte[] LogoPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAGAAAABgCAYAAABxLjZfAAAACXBIWXMAAAsTAAALEwEAmpwYAAABbUlEQVR4nO3bwW3CMBQF4Y2C"
          + "eM/1b4Wm8o9wE0x0m7F9xj2iS1x9v0Wg6i8O8xQ3mV5m0GxYVQ6c7mBwhs9+N3m6m9r8w8CkqkUQkS6rVYvZf9k0b5xX3rGkQwYk"
          + "aZrZcI0y3aR9VQm3x0m7oZyQ0bC7vO1hQmJ4v4s7v9dVfQe3w6wq0Cw1o9l2k0bA3JwqgQwqgQwqgQwqgQwqgQwqgQwqgQwqgQwqg"
          + "Q0o7Y6Yw0bUo6pYcZr2xKdn9V1cPp1y8f7o0g8Wb2g2s4w7p4pQzqXQmVgk5m2ZtH2YpC1jC9mZl2l9x2m9q1b0tVb5b9l8XK8w8"
          + "S+qYg5aYw2bIw3rIw2bIw3rIw2bIw3rIw2bIw3rI4H5f8cQmF9z8c6cZKpVKpVKpVKpVKpVKpVKpVKpVKpVKrV/6eU3eQ3Jrjv1f7"
          + "2w8s1k5x0S9w9xB1zqg4p9g7b7cZb2x0jU2V4sQ2iQ7gq5YwWmQ2cQ3cQ2cQ3cQ2cQ3cQ2cQ3cQ2cQ3cY2/8E7y6wO1gL3Gk7c0g/"
          + "Y6oR5c3o8wAAAAASUVORK5CYII="
        );

        public byte[] CreateCardPdf(string nombreCompleto, string usuario, string email, string qrContenido)
        {
            return RenderCard(nombreCompleto, usuario, email, qrContenido);
        }

        public (string FileName, byte[] Content, string ContentType) GenerateRegistrationPdf(
            string fullName,
            string userName,
            string email,
            string qrPayload)
        {
            var bytes = RenderCard(fullName, userName, email, qrPayload);
            return ($"QR-{userName}.pdf", bytes, "application/pdf");
        }

        private static byte[] RenderCard(string nombreCompleto, string usuario, string email, string qrContenido)
        {
            using var qrGen  = new QRCodeGenerator();
            var qrData       = qrGen.CreateQrCode(qrContenido, QRCodeGenerator.ECCLevel.M);
            var qrPng        = new PngByteQRCode(qrData).GetGraphic(
                                    pixelsPerModule: 9,
                                    darkColor: System.Drawing.Color.Black,
                                    lightColor: System.Drawing.Color.White,
                                    drawQuietZones: true
                               );

            QuestPDF.Settings.License = LicenseType.Community;

            var pdf = Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(CARD_WIDTH, CARD_HEIGHT);
                    page.Margin(8);

                    page.Content()
                        .Padding(10)
                        .Border(1).BorderColor(BORDER)
                        .Background(PANEL)
                        // Si tu versión soporta esquinas redondeadas:
                        .CornerRadius(RADIUS) // <-- si no compila, elimina esta línea
                        .Row(row =>
                        {
                            // ===== Izquierda (info) =====
                            row.RelativeItem(2).Column(col =>
                            {
                                col.Spacing(6);

                                col.Item().Row(rh =>
                                {
                                    rh.RelativeItem().Text("Información usuarios")
                                        .FontColor(INK).SemiBold().FontSize(13);

                                    rh.ConstantItem(96).AlignRight()
                                      .Border(1).BorderColor(ACCENT)
                                      .Background("#0f172a").PaddingVertical(4).PaddingHorizontal(8)
                                      .CornerRadius(999) // si no compila, quitar
                                      .Text("UNIVERSIDAD").FontColor(ACCENT).FontSize(9).SemiBold();
                                });

                                col.Item().LineHorizontal(0.8f).LineColor(BORDER);

                                col.Item().Text(nombreCompleto)
                                    .FontSize(18).SemiBold().FontColor(INK);

                                col.Item().Text(t =>
                                {
                                    t.Span("Usuario: ").FontColor(MUTED);
                                    t.Span(usuario).FontColor(INK);
                                });

                                col.Item().Text(t =>
                                {
                                    t.Span("Email: ").FontColor(MUTED);
                                    t.Span(email).FontColor(INK);
                                });

                                col.Item().PaddingTop(8).Text("Pequeña información")
                                    .FontColor(MUTED).FontSize(10);
                            });

                            // ===== Derecha (logo + QR) =====
                            row.RelativeItem(1).Column(col =>
                            {
                                col.Spacing(8);

                                col.Item().Container()
                                    .Border(1).BorderColor(BORDER)
                                    .Background("#0b1324")
                                    .CornerRadius(12) // si no compila, quitar
                                    .Height(80)
                                    .Padding(6)
                                    .Row(r2 =>
                                    {
                                        r2.RelativeItem()
                                          .AlignCenter().AlignMiddle()
                                          .Image(LogoPng).FitArea(); // <-- API correcta
                                    });

                                col.Item().Container()
                                    .Border(1).BorderColor(ACCENT2)
                                    .Background("#0b1324")
                                    .CornerRadius(12) // si no compila, quitar
                                    .Height(90)
                                    .Padding(6)
                                    .Row(r3 =>
                                    {
                                        r3.RelativeItem()
                                          .AlignCenter().AlignMiddle()
                                          .Image(qrPng).FitArea(); // <-- API correcta
                                    });

                                col.Item().AlignCenter().Text("QR usuario")
                                    .FontColor(INK).FontSize(9);
                            });
                        });
                });
            }).GeneratePdf();

            return pdf;
        }
    }
}
