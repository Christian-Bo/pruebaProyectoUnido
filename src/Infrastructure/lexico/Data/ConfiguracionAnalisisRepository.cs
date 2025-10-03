// Data/ConfiguracionAnalisisRepository.cs
using System.Text.Json;
using Dapper;
using Lexico.Application.Contracts;

namespace Lexico.Infrastructure.Data;
public class ConfiguracionAnalisisRepository : IConfiguracionAnalisisRepository
{
    private readonly DapperConnectionFactory _factory;
    public ConfiguracionAnalisisRepository(DapperConnectionFactory factory) => _factory = factory;

    public async Task<HashSet<string>> GetStopwordsAsync(int idiomaId)
    {
        const string sql = @"
SELECT configuracion
FROM configuracion_analisis
WHERE idioma_id=@idiomaId AND tipo_configuracion='stopwords' AND activo=TRUE
ORDER BY fecha_actualizacion DESC LIMIT 1;";
        using var con = _factory.Create();
        var json = await con.ExecuteScalarAsync<string?>(sql, new { idiomaId });
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>();

        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return new HashSet<string>(arr.Select(x => x.ToLowerInvariant()));
        }
        catch { return new HashSet<string>(); }
    }
}
