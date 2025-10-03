using System.Data;
using Dapper;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;

namespace Lexico.Infrastructure.Data;

/// <summary>
/// Repo de Archivos con Dapper (SQL parametrizado).
/// Asume que la tabla ya existe en MySQL (sin migraciones EF).
/// </summary>
public class FileRepository : IFileRepository
{
    private readonly DapperConnectionFactory _factory;
    public FileRepository(DapperConnectionFactory factory) => _factory = factory;

    public async Task<int> InsertAsync(Archivo archivo)
    {
        const string sql = @"
INSERT INTO Archivos (Nombre, Contenido, FechaSubida, UsuarioId)
VALUES (@Nombre, @Contenido, @FechaSubida, @UsuarioId);
SELECT LAST_INSERT_ID();";
        using var con = _factory.Create();
        var id = await con.ExecuteScalarAsync<int>(sql, archivo);
        return id;
    }

    public async Task<Archivo?> GetByIdAsync(int id)
    {
        const string sql = @"SELECT Id, Nombre, Contenido, FechaSubida, UsuarioId
                             FROM Archivos WHERE Id = @Id LIMIT 1;";
        using var con = _factory.Create();
        return await con.QueryFirstOrDefaultAsync<Archivo>(sql, new { Id = id });
    }
}
