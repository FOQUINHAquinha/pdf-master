using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PDFMaster.Batch;

/// <summary>Opção do menu de contexto do Explorador para arquivos PDF.</summary>
/// <param name="Id">Ação passada ao programa (--context id) e ferramenta aberta.</param>
public sealed record ContextAction(string Id, string Label, string ToolId);

/// <summary>
/// Integração opcional com o Windows, só para o usuário atual (HKEY_CURRENT_USER, sem pedir
/// administrador): opções no menu de contexto dos PDFs e registro do programa em "Abrir com" e nos
/// Aplicativos padrão. O Windows não permite que um programa se defina sozinho como padrão: a pessoa
/// confirma nas Configurações. Tudo o que é criado aqui é removido por <see cref="RemoveAll"/>.
/// </summary>
public sealed class ShellIntegration
{
    public const string ProgId = "PDFMaster.Document";
    public const string ApplicationName = "PDF Master";
    private const string VerbPrefix = "PDFMaster.";

    public static readonly IReadOnlyList<ContextAction> Actions =
    [
        new("view", "Abrir com o PDF Master", "view"),
        new("merge", "Mesclar PDFs selecionados", "merge"),
        new("compress", "Comprimir PDF", "compress"),
        new("pdf-to-image", "Converter para imagem", "pdf-to-image"),
        new("extract-pages", "Extrair páginas", "extract-pages"),
        new("protect", "Proteger PDF", "protect"),
    ];

    private readonly string _executable;
    private readonly string _classes;
    private readonly string _software;

    /// <param name="softwareRoot">Raiz em HKCU (padrão "Software"; os testes usam outra para não mexer no sistema).</param>
    public ShellIntegration(string executable, string softwareRoot = "Software")
    {
        _executable = Path.GetFullPath(executable);
        _software = softwareRoot.TrimEnd('\\');
        _classes = _software + @"\Classes";
    }

    private string VerbsKey => _classes + @"\SystemFileAssociations\.pdf\shell";
    private string CapabilitiesKey => _software + @"\PDFMaster\Capabilities";

    public static ContextAction? Find(string id) => Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Menu de contexto instalado (e apontando para este executável).</summary>
    public bool IsContextMenuInstalled
    {
        get
        {
            using var command = Registry.CurrentUser.OpenSubKey($@"{VerbsKey}\{VerbPrefix}{Actions[0].Id}\command");
            return command?.GetValue(null) is string value && value.Contains(_executable, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsOpenWithRegistered
    {
        get
        {
            using var command = Registry.CurrentUser.OpenSubKey($@"{_classes}\{ProgId}\shell\open\command");
            return command?.GetValue(null) is string value && value.Contains(_executable, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void InstallContextMenu()
    {
        foreach (var action in Actions)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{VerbsKey}\{VerbPrefix}{action.Id}");
            verb.SetValue("MUIVerb", $"PDF Master: {action.Label}");
            verb.SetValue("Icon", $"\"{_executable}\",0");
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue(null, $"\"{_executable}\" --context {action.Id} \"%1\"");
        }

        NotifyShell();
    }

    public void RemoveContextMenu()
    {
        using (var verbs = Registry.CurrentUser.OpenSubKey(VerbsKey, writable: true))
        {
            if (verbs is not null)
            {
                foreach (var name in verbs.GetSubKeyNames().Where(n => n.StartsWith(VerbPrefix, StringComparison.Ordinal)))
                    verbs.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }
        }

        NotifyShell();
    }

    /// <summary>Registra o programa como opção para abrir PDFs ("Abrir com" e Aplicativos padrão).</summary>
    public void RegisterOpenWith()
    {
        using (var prog = Registry.CurrentUser.CreateSubKey($@"{_classes}\{ProgId}"))
        {
            prog.SetValue(null, "Documento PDF");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{_executable}\",0");
            using var command = prog.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{_executable}\" --tool view \"%1\"");
        }

        using (var openWith = Registry.CurrentUser.CreateSubKey($@"{_classes}\.pdf\OpenWithProgids")) openWith.SetValue(ProgId, string.Empty);
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", ApplicationName);
            capabilities.SetValue("ApplicationDescription", "Suíte de ferramentas PDF local, gratuita e sem conta.");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", ProgId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey($@"{_software}\RegisteredApplications"))
            registered.SetValue(ApplicationName, CapabilitiesKey);
        NotifyShell();
    }

    public void UnregisterOpenWith()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{_classes}\{ProgId}", throwOnMissingSubKey: false);
        using (var openWith = Registry.CurrentUser.OpenSubKey($@"{_classes}\.pdf\OpenWithProgids", writable: true))
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(_software + @"\PDFMaster", throwOnMissingSubKey: false);
        using (var registered = Registry.CurrentUser.OpenSubKey($@"{_software}\RegisteredApplications", writable: true))
            registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);
        NotifyShell();
    }

    /// <summary>Remove tudo o que o programa criou no registro.</summary>
    public void RemoveAll()
    {
        RemoveContextMenu();
        UnregisterOpenWith();
    }

    /// <summary>Endereço da tela de Aplicativos padrão do Windows já filtrada para o programa.</summary>
    public static string DefaultAppsUri => "ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(ApplicationName);

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sem o shell (ex.: Windows Server Core): o Explorador atualiza sozinho mais tarde.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
