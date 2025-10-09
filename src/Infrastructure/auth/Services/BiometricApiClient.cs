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
            Console.WriteLine($"[BiometricApiClient] BaseAddress: {_http.BaseAddress}");
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

            // ---- LOG de diagnóstico ----
            var url = "Rostro/Verificar";
            Console.WriteLine($"[BiometricApiClient] POST {url} (BaseAddress={_http.BaseAddress})");

            using var res = await _http.PostAsJsonAsync(url, req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            var head = raw is null ? "(null)" : (raw.Length > 200 ? raw[..200] : raw);
            Console.WriteLine($"[BiometricApiClient] Verify Status={(int)res.StatusCode} RawLen={(raw?.Length ?? 0)} RawHead={head}");

            if (!res.IsSuccessStatusCode)
                return (false, null, raw);

            if (string.IsNullOrWhiteSpace(raw))
                return (false, null, "(empty response)");

            VerifyResponse? data = null;
            try
            {
                data = JsonSerializer.Deserialize<VerifyResponse>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                return (false, null, $"JSON deserialization error: {ex.Message}; RAW={head}");
            }

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

            // ---- LOG de diagnóstico ----
            var url = "Rostro/Segmentar";
            Console.WriteLine($"[BiometricApiClient] POST {url} (BaseAddress={_http.BaseAddress})");

            using var res = await _http.PostAsJsonAsync(url, req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            var head = raw is null ? "(null)" : (raw.Length > 200 ? raw[..200] : raw);
            Console.WriteLine($"[BiometricApiClient] Segment Status={(int)res.StatusCode} RawLen={(raw?.Length ?? 0)} RawHead={head}");

            if (!res.IsSuccessStatusCode)
                return (false, null, raw);

            if (string.IsNullOrWhiteSpace(raw))
                return (false, null, "(empty response)");

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                var ok =
                    root.TryGetProperty("resultado", out var r) && r.ValueKind == JsonValueKind.True ||
                    (root.TryGetProperty("resultado", out r) && r.ValueKind == JsonValueKind.False && r.GetBoolean()); // robustez por si cambia tipo

                var segOk =
                    root.TryGetProperty("segmentado", out var s) && s.ValueKind == JsonValueKind.True ||
                    (root.TryGetProperty("segmentado", out s) && s.ValueKind == JsonValueKind.False && s.GetBoolean());

                // Si ambas flags están presentes las usamos; si no, seguimos con la imagen.
                var flagsOk = ok && segOk;

                string? b64 = root.TryGetProperty("rostro", out var face) && face.ValueKind == JsonValueKind.String
                    ? face.GetString()
                    : null;

                var success = (!string.IsNullOrWhiteSpace(b64)) && (flagsOk || !flagsOk); // si hay imagen, la damos por buena.

                return (success, b64, raw);
            }
            catch (Exception ex)
            {
                return (false, null, $"JSON parse error: {ex.Message}; RAW={head}");
            }
        }
    }
}
