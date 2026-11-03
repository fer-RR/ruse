using System.IO;
using System.Reflection;

namespace Ruse.App;

/// <summary>
/// Loads satellite resource assemblies from Localization/&lt;culture&gt; in the
/// packaged layout. Calls from the .NET resource manager otherwise probe only
/// the culture folder next to the executable.
/// </summary>
internal static class LocalizationResolver
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        AssemblyName name;
        try
        {
            name = new AssemblyName(args.Name);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name.CultureName) ||
            string.IsNullOrWhiteSpace(name.Name))
        {
            return null;
        }

        string fileName = name.Name + ".dll";
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Localization",
            name.CultureName,
            fileName);
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
