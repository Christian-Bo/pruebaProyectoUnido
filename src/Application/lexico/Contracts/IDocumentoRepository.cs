// Contracts/IDocumentoRepository.cs
using Lexico.Domain.Entities;
namespace Lexico.Application.Contracts;
public interface IDocumentoRepository
{
    Task<int> InsertAsync(Documento d);
    Task<Documento?> GetByIdAsync(int id);
    Task UpdateEstadoAsync(int documentoId, string estado);
}
