using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Vellum.Pages;

/// <summary>NFR-054 / SEC-904: users see a safe message with a correlation ID, never a stack trace.</summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }
    public int Code { get; private set; } = 500;

    public void OnGet(int? code) => Set(code);
    public void OnPost(int? code) => Set(code);

    private void Set(int? code)
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        Code = code ?? 500;
    }
}
