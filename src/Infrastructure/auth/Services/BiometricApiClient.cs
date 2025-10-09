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

        // ACEPTA diferentes formas de la API externa
        private sealed class VerifyResponse
        {
            public bool? Success { get; set; }          // algunas APIs
            public bool? IsMatch { get; set; }          // algunas APIs
            public bool? exito { get; set; }            // variantes
            public bool? coincide { get; set; }         // <- lo que recibimos ahora
            public double? score { get; set; }          // numérico (si lo mandan como number)
            public string? scoreStr { get; set; }       // soporte extra, por si viene como "score":"183"
            public string? mensaje { get; set; }
            public string? message { get; set; }
            public string? status { get; set; }         // "Ok" a veces
            public string? error { get; set; }
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
                // Hacemos tolerante a `score` string:
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                data = new VerifyResponse
                {
                    Success  = root.TryGetProperty("Success", out var x1) && x1.ValueKind == JsonValueKind.True ? true :
                               root.TryGetProperty("resultado", out var xr) && xr.ValueKind == JsonValueKind.True ? true :    // a veces "resultado": true
                               (root.TryGetProperty("Success", out x1) && x1.ValueKind == JsonValueKind.False ? false : (bool?)null),
                    IsMatch  = root.TryGetProperty("IsMatch", out var x2) && x2.ValueKind == JsonValueKind.True ? true :
                               (root.TryGetProperty("IsMatch", out x2) && x2.ValueKind == JsonValueKind.False ? false : (bool?)null),
                    exito    = root.TryGetProperty("exito", out var x3) && x3.ValueKind == JsonValueKind.True ? true :
                               (root.TryGetProperty("exito", out x3) && x3.ValueKind == JsonValueKind.False ? false : (bool?)null),
                    coincide = root.TryGetProperty("coincide", out var x4) && x4.ValueKind == JsonValueKind.True ? true :
                               (root.TryGetProperty("coincide", out x4) && x4.ValueKind == JsonValueKind.False ? false : (bool?)null),
                    status   = root.TryGetProperty("status", out var xs) && xs.ValueKind == JsonValueKind.String ? xs.GetString() : null,
                    error    = root.TryGetProperty("error", out var xe) && xe.ValueKind == JsonValueKind.String ? xe.GetString() : null,
                    mensaje  = root.TryGetProperty("mensaje", out var xm) && xm.ValueKind == JsonValueKind.String ? xm.GetString() : null,
                    message  = root.TryGetProperty("message", out var xme) && xme.ValueKind == JsonValueKind.String ? xme.GetString() : null
                };

                // score: puede venir como número o string
                if (root.TryGetProperty("score", out var sc))
                {
                    if (sc.ValueKind == JsonValueKind.Number)
                        data.score = sc.GetDouble();
                    else if (sc.ValueKind == JsonValueKind.String)
                    {
                        var s = sc.GetString();
                        data.scoreStr = s;
                        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                            data.score = d;
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, null, $"JSON deserialization error: {ex.Message}; RAW={head}");
            }

            // decimos que hay match si cualquiera de estas flags lo marca
            var match = data?.IsMatch
                        ?? data?.Success
                        ?? data?.exito
                        ?? data?.coincide
                        ?? false;

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
                    (root.TryGetProperty("resultado", out var r) && r.ValueKind == JsonValueKind.True) &&
                    (root.TryGetProperty("segmentado", out var s) && s.ValueKind == JsonValueKind.True);

                string? b64 = root.TryGetProperty("rostro", out var face) && face.ValueKind == JsonValueKind.String
                    ? face.GetString()
                    : null;

                var success = ok && !string.IsNullOrWhiteSpace(b64);
                return (success, b64, raw);
            }
            catch (Exception ex)
            {
                return (false, null, $"JSON parse error: {ex.Message}; RAW={head}");
            }
        }
    }
}
