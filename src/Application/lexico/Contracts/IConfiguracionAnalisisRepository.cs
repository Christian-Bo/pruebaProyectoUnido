// Contracts/IConfiguracionAnalisisRepository.cs
namespace Lexico.Application.Contracts;
public interface IConfiguracionAnalisisRepository
{
    Task<HashSet<string>> GetStopwordsAsync(int idiomaId);
}
