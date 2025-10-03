// Services/AnalysisService.cs
using System.Diagnostics;
using System.Text.RegularExpressions;
using Lexico.Application.Contracts;
using Lexico.Domain.Entities;

namespace Lexico.Application.Services;

/// <summary>
/// Orquesta el análisis léxico completo: inserta analisis_lexicos (iniciado),
/// calcula métricas/gramaticales/patrones, guarda resultados y cambia estado a completado.
/// </summary>
public class AnalysisService
{
    private readonly IAnalisisRepository _repoAnalisis;
    private readonly IDocumentoRepository _repoDoc;
    private readonly IIdiomaRepository _repoIdioma;
    private readonly IConfiguracionAnalisisRepository _repoCfg;
    private readonly ILogProcesamientoRepository _repoLog;

    public AnalysisService(
        IAnalisisRepository repoAnalisis,
        IDocumentoRepository repoDoc,
        IIdiomaRepository repoIdioma,
        IConfiguracionAnalisisRepository repoCfg,
        ILogProcesamientoRepository repoLog)
    {
        _repoAnalisis = repoAnalisis;
        _repoDoc = repoDoc;
        _repoIdioma = repoIdioma;
        _repoCfg = repoCfg;
        _repoLog = repoLog;
    }

    public async Task<AnalisisLexico> EjecutarAsync(int documentoId)
    {
        var doc = await _repoDoc.GetByIdAsync(documentoId)
                  ?? throw new ArgumentException("Documento no encontrado.");

        // obtener idioma
        var idioma = await _repoIdioma.GetByCodigoAsync(await InferCodigoIdioma(doc.IdiomaId));
        if (idioma is null) throw new ArgumentException("Idioma no soportado o inactivo.");

        // log: inicio análisis
        var logId = await _repoLog.InsertAsync(new LogProcesamiento {
            DocumentoId = doc.Id,
            Etapa = "tokenizacion",
            Estado = "iniciado",
            TiempoInicio = DateTime.UtcNow
        });

        var sw = Stopwatch.StartNew();

        // crear registro de análisis (iniciado)
        var analisis = new AnalisisLexico {
            DocumentoId = doc.Id,
            TotalPalabras = 0,
            PalabrasUnicas = 0,
            Estado = "iniciado",
            FechaProcesamiento = DateTime.UtcNow
        };
        analisis.Id = await _repoAnalisis.InsertAnalisisAsync(analisis);

        try
        {
            var stopwords = await _repoCfg.GetStopwordsAsync(idioma.Id);

            // 1) Tokenización / normalización
            var tokens = Tokenize(doc.ContenidoOriginal);

            // 2) Filtrado de stopwords para frecuencias “comunes”
            var tokensSinStop = tokens.Where(t => !stopwords.Contains(t)).ToList();

            // 3) Métricas
            analisis.TotalPalabras = tokens.Count;
            analisis.PalabrasUnicas = tokens.Distinct().Count();

            // 4) Frecuencias
            var topFrecuentes = tokens.GroupBy(x => x).Select(g => (pal: g.Key, f: g.Count()))
                                      .OrderByDescending(x => x.f).Take(10).ToList();
            var menosFrecuentes = tokens.GroupBy(x => x).Select(g => (pal: g.Key, f: g.Count()))
                                        .OrderBy(x => x.f).Take(10).ToList();

            var filasFreq = new List<FrecuenciaPalabra>();
            filasFreq.AddRange(topFrecuentes.Select(x => new FrecuenciaPalabra {
                AnalisisId = analisis.Id, Palabra = x.pal, Frecuencia = x.f, TipoPalabra = "mas_repetida"
            }));
            filasFreq.AddRange(menosFrecuentes.Select(x => new FrecuenciaPalabra {
                AnalisisId = analisis.Id, Palabra = x.pal, Frecuencia = x.f, TipoPalabra = "menos_repetida"
            }));
            // opcional: palabras comunes (sin stopwords), top N
            var comunesTop = tokensSinStop.GroupBy(x => x).Select(g => (pal: g.Key, f: g.Count()))
                                          .OrderByDescending(x => x.f).Take(30);
            filasFreq.AddRange(comunesTop.Select(x => new FrecuenciaPalabra {
                AnalisisId = analisis.Id, Palabra = x.pal, Frecuencia = x.f, TipoPalabra = "comun"
            }));

            // 5) Clasificación gramatical (heurístico por idioma)
            var clasif = Clasificar(tokens, idioma.CodigoIso);

            // 6) Patrones reconocidos
            var patrones = DetectarPatrones(doc.ContenidoOriginal);

            // Guardado masivo
            await _repoAnalisis.BulkInsertFrecuenciasAsync(analisis.Id, filasFreq);
            await _repoAnalisis.BulkInsertClasificacionAsync(analisis.Id, clasif);
            await _repoAnalisis.BulkInsertPatronesAsync(analisis.Id, patrones);

            // KPIs finales
            sw.Stop();
            analisis.TiempoProcesamiento = Math.Round((decimal)sw.Elapsed.TotalSeconds, 3);
            analisis.Estado = "completado";
            await _repoAnalisis.UpdateAnalisisAsync(analisis);

            await _repoLog.CloseAsync(logId, "completado", null, (int)sw.ElapsedMilliseconds);
            await _repoDoc.UpdateEstadoAsync(doc.Id, "procesado");

            return analisis;
        }
        catch (Exception ex)
        {
            sw.Stop();
            analisis.Estado = "error";
            analisis.MensajeError = ex.Message;
            await _repoAnalisis.UpdateAnalisisAsync(analisis);
            await _repoLog.CloseAsync(logId, "error", ex.Message, (int)sw.ElapsedMilliseconds);
            await _repoDoc.UpdateEstadoAsync(doc.Id, "error");
            throw;
        }
    }

    // Helpers -------------------------------------------------------------

    private static List<string> Tokenize(string texto)
        => texto.Split(new[] {' ','\n','\r','\t',',','.',';','!','?','\"','\'','(',')','[',']','{','}','/','\\',':'},
                       StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToLower().Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

    private static List<ClasificacionGramatical> Clasificar(List<string> tokens, string codigoIso)
    {
        // muy básico (puedes mejorar reglas por idioma)
        var result = new List<ClasificacionGramatical>();

        // pronombres
        var pronRE = codigoIso switch {
            "es" => @"\b(yo|tú|vos|usted|él|ella|nosotros|nosotras|vosotros|ustedes|ellos|ellas)\b",
            "en" => @"\b(i|you|he|she|it|we|they)\b",
            "ru" => @"\b(я|ты|он|она|оно|мы|вы|они)\b",
            _    => @"$\b$"
        };
        var pron = tokens.Where(t => Regex.IsMatch(t, pronRE, RegexOptions.IgnoreCase))
                         .GroupBy(t => t).Select(g => new ClasificacionGramatical {
                             Palabra = g.Key, Categoria = "pronombre_personal", Frecuencia = g.Count(), Confianza = 0.9m
                         });
        result.AddRange(pron);

        // verbos (forma raíz heurística)
        var verbRE = codigoIso switch {
            "es" => @"\b\w+(ar|er|ir)\b",
            "en" => @"\b\w+(ing|ed)\b",
            "ru" => @"\b\w+(ть|л|ют|ем)\b",
            _    => @"$\b$"
        };
        var verbs = tokens.Where(t => Regex.IsMatch(t, verbRE, RegexOptions.IgnoreCase))
                          .GroupBy(t => t).Select(g => new ClasificacionGramatical {
                              Palabra = g.Key, Categoria = "verbo", Frecuencia = g.Count(), Confianza = 0.6m
                          });
        result.AddRange(verbs);

        // sustantivos (heurístico simple)
        IEnumerable<string> susts = codigoIso switch {
            "es" => tokens.Where(t => t.EndsWith("o") || t.EndsWith("a")),
            "en" => tokens.Where(t => t.EndsWith("tion") || t.EndsWith("ness")),
            "ru" => tokens.Where(t => t.EndsWith("ия") || t.EndsWith("ость")),
            _    => Enumerable.Empty<string>()
        };
        result.AddRange(susts.GroupBy(s => s).Select(g => new ClasificacionGramatical{
            Palabra = g.Key, Categoria = "sustantivo", Frecuencia = g.Count(), Confianza = 0.5m
        }));

        return result.Select(c => { c.FormaRaiz = c.Palabra; return c; }).ToList();
    }

    private static List<PatronReconocido> DetectarPatrones(string texto)
    {
        var res = new List<PatronReconocido>();
        void add(string tipo, Match m)
        {
            res.Add(new PatronReconocido {
                TipoPatron = tipo,
                PatronEncontrado = m.Value,
                Contexto = ExtractContext(texto, m.Index, 30),
                PosicionInicio = m.Index,
                PosicionFin = m.Index + m.Length,
                Frecuencia = 1
            });
        }

        var email = Regex.Matches(texto, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
        foreach (Match m in email) add("email", m);

        var url = Regex.Matches(texto, @"https?://[^\s]+");
        foreach (Match m in url) add("url", m);

        var phone = Regex.Matches(texto, @"\+?\d[\d\s\-]{7,}\d");
        foreach (Match m in phone) add("telefono", m);

        var numero = Regex.Matches(texto, @"\b\d+([.,]\d+)?\b");
        foreach (Match m in numero) add("numero", m);

        var fecha = Regex.Matches(texto, @"\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})\b");
        foreach (Match m in fecha) add("fecha", m);

        return res;
    }

    private static string ExtractContext(string texto, int index, int radius)
    {
        int start = Math.Max(0, index - radius);
        int end = Math.Min(texto.Length, index + radius);
        return texto.Substring(start, end - start);
    }

    // Mapa simple (puedes resolver por join si prefieres)
    private async Task<string> InferCodigoIdioma(int idiomaId)
    {
        // Lo ideal: un repositorio que obtenga por Id. Para simplificar,
        // aprovechamos GetByCodigoAsync con un map in-memory si lo necesitas.
        return await Task.FromResult("es"); // si tu flujo ya trae idioma_id, puedes no usarlo.
    }
}
