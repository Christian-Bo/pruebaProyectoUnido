using Lexico.Domain.Entities;

namespace Lexico.Application.Contracts;

public interface IFileRepository
{
    Task<int> InsertAsync(Archivo archivo);
    Task<Archivo?> GetByIdAsync(int id);
}
