// Data/AnalisisRepository.cs
using Dapper;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;

namespace Lexico.Infrastructure.Data;
public class AnalisisRepository : IAnalisisRepository
{
    private readonly DapperConnectionFactory _factory;
    public AnalisisRepository(DapperConnectionFactory factory) => _factory = factory;

    public async Task<int> InsertAnalisisAsync(AnalisisLexico a)
    {
        const string sql = @"
INSERT INTO analisis_lexicos (documento_id, total_palabras, palabras_unicas, tiempo_procesamiento, fecha_procesamiento, estado, mensaje_error)
VALUES (@DocumentoId, @TotalPalabras, @PalabrasUnicas, @TiempoProcesamiento, @FechaProcesamiento, @Estado, @MensajeError);
SELECT LAST_INSERT_ID();";

        using var con = _factory.Create();
        con.Open(); // <- abrir explícitamente
        return await con.ExecuteScalarAsync<int>(sql, a);
    }

    public async Task UpdateAnalisisAsync(AnalisisLexico a)
    {
        const string sql = @"
UPDATE analisis_lexicos
SET total_palabras=@TotalPalabras, palabras_unicas=@PalabrasUnicas, tiempo_procesamiento=@TiempoProcesamiento,
    estado=@Estado, mensaje_error=@MensajeError
WHERE id=@Id;";

        using var con = _factory.Create();
        con.Open(); // <- abrir explícitamente
        await con.ExecuteAsync(sql, a);
    }

    public async Task<AnalisisLexico?> GetLatestByDocumentoAsync(int documentoId)
    {
        const string sql = @"SELECT id AS Id, documento_id AS DocumentoId, total_palabras AS TotalPalabras, palabras_unicas AS PalabrasUnicas,
                                    tiempo_procesamiento AS TiempoProcesamiento, fecha_procesamiento AS FechaProcesamiento,
                                    estado AS Estado, mensaje_error AS MensajeError
                             FROM analisis_lexicos
                             WHERE documento_id = @documentoId
                             ORDER BY fecha_procesamiento DESC
                             LIMIT 1;";

        using var con = _factory.Create();
        con.Open(); // <- abrir explícitamente
        return await con.QueryFirstOrDefaultAsync<AnalisisLexico>(sql, new { documentoId });
    }

    public async Task BulkInsertFrecuenciasAsync(int analisisId, IEnumerable<FrecuenciaPalabra> filas)
    {
        const string sql = @"
INSERT INTO frecuencia_palabras (analisis_id, palabra, frecuencia, tipo_palabra)
VALUES (@AnalisisId, @Palabra, @Frecuencia, @TipoPalabra);";

        using var con = _factory.Create();
        con.Open(); // <- abrir explícitamente ANTES de la transacción
        using var tx = con.BeginTransaction();

        foreach (var f in filas)
        {
            await con.ExecuteAsync(
                sql,
                new { AnalisisId = analisisId, f.Palabra, f.Frecuencia, f.TipoPalabra },
                tx
            );
        }

        tx.Commit();
    }

    public async Task BulkInsertClasificacionAsync(int analisisId, IEnumerable<ClasificacionGramatical> filas)
    {
        const string sql = @"
INSERT INTO clasificacion_gramatical (analisis_id, palabra, forma_raiz, categoria, frecuencia, confianza)
VALUES (@AnalisisId, @Palabra, @FormaRaiz, @Categoria, @Frecuencia, @Confianza);";

        using var con = _factory.Create();
        con.Open(); // <- abrir explícitamente
        using var tx = con.BeginTransaction();

        foreach (var f in filas)
        {
            await con.ExecuteAsync(
                sql,
                new { AnalisisId = analisisId, f.Palabra, f.FormaRaiz, f.Categoria, f.Frecuencia, f.Confianza },
                tx
            );
        }

        tx.Commit();
    }

    public async Task BulkInsertPatronesAsync(int analisisId, IEnumerable<PatronReconocido> filas)
    {
        const string sql = @"
INSERT INTO patrones_reconocidos (analisis_id, tipo_patron, patron_encontrado, contexto, posicion_inicio, posicion_fin, frecuencia)
VALUES (@AnalisisId, @TipoPatron, @PatronEncontrado, @Contexto, @PosicionInicio, @PosicionFin, @Frecuencia);";

        using var con = _factory.Create();
        con.Open(); // <- abrir explícitamente
        using var tx = con.BeginTransaction();

        foreach (var p in filas)
        {
            await con.ExecuteAsync(
                sql,
                new { AnalisisId = analisisId, p.TipoPatron, p.PatronEncontrado, p.Contexto, p.PosicionInicio, p.PosicionFin, p.Frecuencia },
                tx
            );
        }

        tx.Commit();
    }
}
