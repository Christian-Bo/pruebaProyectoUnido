// Data/IdiomaRepository.cs
using Dapper;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;

namespace Lexico.Infrastructure.Data;
public class IdiomaRepository : IIdiomaRepository
{
    private readonly DapperConnectionFactory _factory;
    public IdiomaRepository(DapperConnectionFactory factory) => _factory = factory;

    public async Task<Idioma?> GetByCodigoAsync(string codigoIso)
    {
        const string sql = @"SELECT id AS Id, codigo_iso AS CodigoIso, nombre AS Nombre, nombre_original AS NombreOriginal,
                                    activo AS Activo, fecha_creacion AS FechaCreacion
                             FROM idiomas WHERE codigo_iso = @codigo AND activo = TRUE LIMIT 1;";
        using var con = _factory.Create();
        return await con.QueryFirstOrDefaultAsync<Idioma>(sql, new { codigo = codigoIso });
    }
}
