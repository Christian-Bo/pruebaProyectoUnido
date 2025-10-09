using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.auth.Services
{
    public class BiometricApiClient
    {
        private readonly HttpClient _http;

        public BiometricApiClient(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            _http.BaseAddress = new Uri(cfg["ExternalBiometricApi:BaseUrl"]!);
            _http.Timeout = TimeSpan.FromSeconds(
                int.TryParse(cfg["ExternalBiometricApi:TimeoutSeconds"], out var t) ? t : 20);
        }

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

            var data = System.Text.Json.JsonSerializer.Deserialize<VerifyResponse>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var match = data?.IsMatch ?? data?.Success ?? data?.exito ?? false;
            return (match, data?.score, raw);
        }

        private sealed class SegmentRequest
        {
            public string Rostro { get; set; } = string.Empty;
        }
        private sealed class SegmentResponse
        {
            // tolerante a diferentes nombres que pueda devolver la API
            public string? RostroSegmentado { get; set; }
            public string? Imagen { get; set; }
            public string? Image { get; set; }
            public bool? Success { get; set; }
            public string? mensaje { get; set; }
            public string? message { get; set; }
        }

        public async Task<(bool Success, string? Base64, string? Raw)>
            SegmentAsync(string rostroBase64, CancellationToken ct = default)
        {
            var req = new SegmentRequest { Rostro = rostroBase64 };
            using var res = await _http.PostAsJsonAsync("Rostro/Segmentar", req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (false, null, raw);

            var data = System.Text.Json.JsonSerializer.Deserialize<SegmentResponse>(raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var ok = data?.Success ?? true; // varias APIs devuelven 200 sin flag
            var base64 = data?.RostroSegmentado ?? data?.Imagen ?? data?.Image;
            return (ok && !string.IsNullOrWhiteSpace(base64), base64, raw);
        }
    }
}
    

