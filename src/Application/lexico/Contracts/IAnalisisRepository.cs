// Contracts/IAnalisisRepository.cs
using Lexico.Domain.Entities;
namespace Lexico.Application.Contracts;
public interface IAnalisisRepository
{
    Task<int> InsertAnalisisAsync(AnalisisLexico a);
    Task UpdateAnalisisAsync(AnalisisLexico a); // para estado, KPIs, error
    Task<AnalisisLexico?> GetLatestByDocumentoAsync(int documentoId);

    Task BulkInsertFrecuenciasAsync(int analisisId, IEnumerable<FrecuenciaPalabra> filas);
    Task BulkInsertClasificacionAsync(int analisisId, IEnumerable<ClasificacionGramatical> filas);
    Task BulkInsertPatronesAsync(int analisisId, IEnumerable<PatronReconocido> filas);
}
