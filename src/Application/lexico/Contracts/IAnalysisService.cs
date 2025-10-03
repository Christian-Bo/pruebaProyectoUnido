using Lexico.Domain.Entities;

namespace Lexico.Application.Contracts;

public interface IAnalysisService
{
    Analisis Analizar(Archivo archivo, string idioma);
}
