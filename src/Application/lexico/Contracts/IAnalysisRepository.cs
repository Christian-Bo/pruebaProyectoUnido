using Lexico.Domain.Entities;

namespace Lexico.Application.Contracts;

public interface IAnalysisRepository
{
    Task<int> InsertAsync(Analisis analisis);
    Task<Analisis?> GetLatestByArchivoAsync(int archivoId);
}
