// Data/DocumentoRepository.cs
using Dapper;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;

namespace Lexico.Infrastructure.Data;
public class DocumentoRepository : IDocumentoRepository
{
    private readonly DapperConnectionFactory _factory;
    public DocumentoRepository(DapperConnectionFactory factory) => _factory = factory;

    public async Task<int> InsertAsync(Documento d)
    {
        const string sql = @"
INSERT INTO documentos (usuario_id, nombre_archivo, contenido_original, idioma_id, tamaño_archivo, hash_documento, estado)
VALUES (@UsuarioId, @NombreArchivo, @ContenidoOriginal, @IdiomaId, @TamanoArchivo, @HashDocumento, @Estado);
SELECT LAST_INSERT_ID();";
        using var con = _factory.Create();
        return await con.ExecuteScalarAsync<int>(sql, d);
    }

    public async Task<Documento?> GetByIdAsync(int id)
    {
        const string sql = @"SELECT id AS Id, usuario_id AS UsuarioId, nombre_archivo AS NombreArchivo, contenido_original AS ContenidoOriginal,
                                    idioma_id AS IdiomaId, tamaño_archivo AS TamanoArchivo, hash_documento AS HashDocumento,
                                    fecha_carga AS FechaCarga, estado AS Estado
                             FROM documentos WHERE id = @id LIMIT 1;";
        using var con = _factory.Create();
        return await con.QueryFirstOrDefaultAsync<Documento>(sql, new { id });
    }

    public async Task UpdateEstadoAsync(int documentoId, string estado)
    {
        const string sql = @"UPDATE documentos SET estado = @estado WHERE id = @documentoId;";
        using var con = _factory.Create();
        await con.ExecuteAsync(sql, new { documentoId, estado });
    }
}
