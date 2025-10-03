// Data/LogProcesamientoRepository.cs
using Dapper;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;

namespace Lexico.Infrastructure.Data;
public class LogProcesamientoRepository : ILogProcesamientoRepository
{
    private readonly DapperConnectionFactory _factory;
    public LogProcesamientoRepository(DapperConnectionFactory factory) => _factory = factory;

    public async Task<int> InsertAsync(LogProcesamiento log)
    {
        const string sql = @"
INSERT INTO log_procesamiento (documento_id, etapa, estado, mensaje, tiempo_inicio)
VALUES (@DocumentoId, @Etapa, @Estado, @Mensaje, @TiempoInicio);
SELECT LAST_INSERT_ID();";
        using var con = _factory.Create();
        return await con.ExecuteScalarAsync<int>(sql, log);
    }

    public async Task CloseAsync(int logId, string estado, string? mensaje, int duracionMs)
    {
        const string sql = @"
UPDATE log_procesamiento
SET estado=@Estado, mensaje=@Mensaje, tiempo_fin=NOW(), duracion_ms=@DuracionMs
WHERE id=@Id;";
        using var con = _factory.Create();
        await con.ExecuteAsync(sql, new { Id = logId, Estado = estado, Mensaje = mensaje, DuracionMs = duracionMs });
    }
}
