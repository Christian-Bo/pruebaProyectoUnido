// Contracts/IIdiomaRepository.cs
using Lexico.Domain.Entities;
namespace Lexico.Application.Contracts;
public interface IIdiomaRepository
{
    Task<Idioma?> GetByCodigoAsync(string codigoIso);
}
