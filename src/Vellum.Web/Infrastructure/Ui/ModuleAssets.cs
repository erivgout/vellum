namespace Vellum.Infrastructure.Ui;

/// <summary>Module stylesheets and scripts in wwwroot/css/modules and wwwroot/js/modules are included by the layout automatically.</summary>
public static class ModuleAssets
{
    private static string[]? _css, _js;

    public static IEnumerable<string> Css(IWebHostEnvironment env) => _css ??= List(env, "css/modules", "*.css");
    public static IEnumerable<string> Js(IWebHostEnvironment env) => _js ??= List(env, "js/modules", "*.js");

    private static string[] List(IWebHostEnvironment env, string folder, string pattern)
    {
        var dir = Path.Combine(env.WebRootPath, folder.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern).Select(f => "/" + folder + "/" + Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray()
            : [];
    }
}
