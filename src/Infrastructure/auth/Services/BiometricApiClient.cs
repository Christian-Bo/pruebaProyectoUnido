using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.auth.Services
{
    public class BiometricApiClient
    {
        private readonly HttpClient _http;

        public BiometricApiClient(HttpClient http, IConfiguration cfg)
        {
            // BaseAddress y Timeout ya vienen configurados desde Program.cs (AddHttpClient)
            _http = http;
        }

        // ===================== Verificar =====================
        private sealed class VerifyRequest
        {
            public string RostroA { get; set; } = string.Empty;
            public string RostroB { get; set; } = string.Empty;
        }

        private sealed class VerifyResponse
        {
            public bool? Success { get; set; }
            public bool? IsMatch { get; set; }
            public bool? exito { get; set; }
            public double? score { get; set; }
            public string? mensaje { get; set; }
            public string? message { get; set; }
        }

        public async Task<(bool Match, double? Score, string? Raw)>
            VerifyAsync(string rostroA, string rostroB, CancellationToken ct = default)
        {
            var req = new VerifyRequest { RostroA = rostroA, RostroB = rostroB };
            using var res = await _http.PostAsJsonAsync("Rostro/Verificar", req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, null, raw);

            var data = JsonSerializer.Deserialize<VerifyResponse>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var match = data?.IsMatch ?? data?.Success ?? data?.exito ?? false;
            return (match, data?.score, raw);
        }

        // ===================== Segmentar (AJUSTADO) =====================
        private sealed class SegmentRequest
        {
            public string RostroA { get; set; } = string.Empty;
            public string RostroB { get; set; } = string.Empty; // la colección usa vacío
        }

        /// <summary>
        /// La API externa devuelve: { resultado: bool, segmentado: bool, rostro: "<b64>" }
        /// </summary>
        public async Task<(bool Success, string? Base64, string? Raw)>
            SegmentAsync(string rostroBase64, CancellationToken ct = default)
        {
            var req = new SegmentRequest { RostroA = rostroBase64, RostroB = "" };

            using var res = await _http.PostAsJsonAsync("Rostro/Segmentar", req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, null, raw);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("resultado", out var r) && r.GetBoolean()
                     && root.TryGetProperty("segmentado", out var s) && s.GetBoolean();

            string? b64 = root.TryGetProperty("rostro", out var face) ? face.GetString() : null;

            return (ok && !string.IsNullOrWhiteSpace(b64), b64, raw);
        }
    }
}
