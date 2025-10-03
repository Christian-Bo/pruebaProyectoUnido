using System.Globalization;
using System.Text.Json;

namespace Auth.Infrastructure.Services;

public static class FaceEncodingUtils
{
    // Acepta JSON array o CSV, devuelve double[128]
    public static double[] ParseEncoding128(string encodingRaw)
    {
        if (string.IsNullOrWhiteSpace(encodingRaw))
            throw new ArgumentException("Encoding vacío.");

        encodingRaw = encodingRaw.Trim();

        // ¿JSON array?
        if (encodingRaw.StartsWith("[") && encodingRaw.EndsWith("]"))
        {
            var arr = JsonSerializer.Deserialize<double[]>(encodingRaw);
            if (arr is null || arr.Length == 0)
                throw new ArgumentException("Encoding JSON inválido.");
            return Validate128(arr);
        }

        // ¿CSV?
        var parts = encodingRaw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<double>(parts.Length);
        foreach (var p in parts)
        {
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
            else
                throw new ArgumentException("Encoding CSV contiene valores no numéricos.");
        }
        return Validate128(list.ToArray());
    }

    private static double[] Validate128(double[] v)
    {
        if (v.Length != 128)
            throw new ArgumentException($"Se esperaba un vector de 128 dimensiones, recibido: {v.Length}.");
        return v;
    }

    public static double EuclideanDistance(double[] a, double[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Dimensiones incompatibles.");
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }
}
