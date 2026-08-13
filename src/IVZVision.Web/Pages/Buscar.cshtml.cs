using IVZVision.Data.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IVZVision.Web.Pages;

public class BuscarModel : PageModel
{
    private readonly SearchService _search;

    public BuscarModel(SearchService search) => _search = search;

    [BindProperty(SupportsGet = true)] public string? Prompt { get; set; }

    public IReadOnlyList<SearchHit> Hits { get; private set; } = Array.Empty<SearchHit>();
    public string? Interpretation { get; private set; }
    public int Total { get; private set; }
    public string? DatabaseError { get; private set; }

    /// <summary>Ejemplos que se ofrecen como atajos y que documentan lo que entiende el buscador.</summary>
    public static readonly string[] Examples =
    {
        "personas desconocidas de hoy",
        "matrículas de las últimas 2 horas",
        "alertas de las últimas 24 horas",
        "animales de esta semana",
        "rostros no autorizados",
        "objetos sin identificar",
    };

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Prompt)) return;

        try
        {
            var result = await _search.SearchAsync(Prompt, take: 100, includePending: true, ct);

            Hits = result.Hits;
            Total = result.Total;
            Interpretation = result.Interpretation;
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }
}
