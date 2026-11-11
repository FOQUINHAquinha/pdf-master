using System.Reflection;

namespace PDFMaster.App.Services;

/// <summary>Identificação do programa e do autor.</summary>
public static class AppInfo
{
    public const string Author = "Gustavo Farias Medeiros";

    public const string Credit = "Criado por " + Author;

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
