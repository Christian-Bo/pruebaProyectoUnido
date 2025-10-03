// Contracts/ILogProcesamientoRepository.cs
using Lexico.Domain.Entities;
namespace Lexico.Application.Contracts;
public interface ILogProcesamientoRepository
{
    Task<int> InsertAsync(LogProcesamiento log);
    Task CloseAsync(int logId, string estado, string? mensaje, int duracionMs);
}
