using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;
using PDFMaster.Security;

namespace PDFMaster.App.ViewModels;

/// <summary>Base das telas da Fase 6 que trabalham com uma lista de PDFs (ou um único PDF).</summary>
public abstract partial class FileListViewModel : ToolViewModelBase
{
    protected static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    protected FileListViewModel(ToolContext context, string title, string subtitle, bool single) : base(context, title, subtitle) => Single = single;

    /// <summary>A tela trabalha com um documento por vez.</summary>
    public bool Single { get; }

    public ObservableCollection<ConvertFileViewModel> Files { get; } = [];
    public override bool IsEmpty => Files.Count == 0;
    public bool HasFiles => Files.Count > 0;
    public string? FirstPath => Files.Count > 0 ? Files[0].Path : null;

    /// <summary>Texto mostrado na lista vazia.</summary>
    public abstract string EmptyHint { get; }

    protected override void OnStateChanged()
    {
        PickFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        StateChanged();
    }

    /// <summary>Atualiza os botões próprios da tela.</summary>
    protected virtual void StateChanged()
    {
    }

    private bool CanEdit() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFilesAsync()
    {
        var picked = PickPdfs(multiple: !Single);
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ClearAsync()
    {
        Files.Clear();
        await ChangedAsync();
    }

    public override async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return;
        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count < paths.Count)
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram adicionados", "Esta ferramenta aceita apenas PDFs.");
        if (pdfs.Count == 0) return;
        if (Single)
        {
            if (pdfs.Count > 1) Context.Notifications.Show(NotificationKind.Info, "Um documento por vez", $"Foi aberto \"{Path.GetFileName(pdfs[0])}\".");
            Files.Clear();
            pdfs = [pdfs[0]];
        }

        foreach (var path in pdfs)
        {
            var full = Path.GetFullPath(path);
            if (Files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new ConvertFileViewModel(full, Move, Remove));
        }

        SuggestOutputFolder(Files[0].Path);
        await ChangedAsync();
    }

    private void Move(ConvertFileViewModel file, int direction)
    {
        if (IsBusy) return;
        var index = Files.IndexOf(file);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count) return;
        Files.Move(index, target);
    }

    private async void Remove(ConvertFileViewModel file)
    {
        if (IsBusy) return;
        Files.Remove(file);
        await ChangedAsync();
    }

    private async Task ChangedAsync()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FirstPath));
        OnStateChanged();
        try
        {
            await OnFilesChangedAsync();
        }
        catch (Exception ex) when (ex is PdfEngineException or PDFMaster.Rendering.PdfRenderException or IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível ler o documento", ex.Message);
        }

        OnStateChanged();
    }

    /// <summary>Chamado depois que a lista mudou (para analisar os documentos novos).</summary>
    protected virtual Task OnFilesChangedAsync() => Task.CompletedTask;

    protected static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "• " + l));
}

/// <summary>Proteção por senha e permissões, em vários PDFs de uma vez.</summary>
public sealed partial class ProtectViewModel : FileListViewModel
{
    private string _userPassword = string.Empty;
    private string _userConfirm = string.Empty;
    private string _ownerPassword = string.Empty;
    private string _ownerConfirm = string.Empty;

    public ProtectViewModel(ToolContext context)
        : base(context, "Proteger com senha", "Senha de abertura e restrições de impressão, cópia e edição, com criptografia AES de 256 bits. Os originais não são alterados.", single: false) =>
        Refresh();

    public override string EmptyHint => "Arraste para cá os PDFs que serão protegidos";

    [ObservableProperty] private bool _requireOpenPassword = true;
    [ObservableProperty] private bool _allowPrint = true;
    [ObservableProperty] private bool _allowHighQualityPrint = true;
    [ObservableProperty] private bool _allowCopy = true;
    [ObservableProperty] private bool _allowModify = true;
    [ObservableProperty] private bool _allowAnnotate = true;
    [ObservableProperty] private bool _allowForms = true;
    [ObservableProperty] private bool _allowAssemble = true;
    [ObservableProperty] private bool _allowAccessibility = true;
    [ObservableProperty] private string? _problem;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    partial void OnRequireOpenPasswordChanged(bool value) => Refresh();
    partial void OnAllowPrintChanged(bool value) => Refresh();
    partial void OnAllowHighQualityPrintChanged(bool value) => Refresh();
    partial void OnAllowCopyChanged(bool value) => Refresh();
    partial void OnAllowModifyChanged(bool value) => Refresh();
    partial void OnAllowAnnotateChanged(bool value) => Refresh();
    partial void OnAllowFormsChanged(bool value) => Refresh();
    partial void OnAllowAssembleChanged(bool value) => Refresh();
    partial void OnAllowAccessibilityChanged(bool value) => Refresh();

    /// <summary>Recebe as senhas das caixas de senha (nunca são gravadas em disco).</summary>
    public void SetPassword(string field, string value)
    {
        switch (field)
        {
            case "user": _userPassword = value; break;
            case "userConfirm": _userConfirm = value; break;
            case "owner": _ownerPassword = value; break;
            case "ownerConfirm": _ownerConfirm = value; break;
        }

        Refresh();
    }

    private PdfPermissions Permissions => new(AllowPrint, AllowPrint && AllowHighQualityPrint, AllowCopy, AllowModify, AllowAnnotate, AllowForms, AllowAssemble, AllowAccessibility);

    private bool HasRestrictions => Permissions != PdfPermissions.All;

    protected override Task OnFilesChangedAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        Problem = Validate();
        OnStateChanged();
    }

    private string? Validate()
    {
        if (Files.Count == 0) return null;
        var min = PdfProtection.MinPasswordLength;
        if (RequireOpenPassword)
        {
            if (_userPassword.Length < min) return $"A senha de abertura precisa ter pelo menos {min} caracteres.";
            if (_userPassword != _userConfirm) return "A confirmação da senha de abertura não confere.";
        }

        if (HasRestrictions || !RequireOpenPassword)
        {
            if (!HasRestrictions) return "Sem senha de abertura, escolha ao menos uma restrição (senão a proteção não teria efeito).";
            if (_ownerPassword.Length < min) return $"Com restrições, defina a senha de proprietário (pelo menos {min} caracteres): é ela que permite tirar as restrições.";
            if (_ownerPassword != _ownerConfirm) return "A confirmação da senha de proprietário não confere.";
            if (RequireOpenPassword && _ownerPassword == _userPassword) return "A senha de proprietário precisa ser diferente da senha de abertura; senão quem abre o documento teria todas as permissões.";
        }

        return null;
    }

    protected override void StateChanged() => StartCommand.NotifyCanExecuteChanged();

    private bool CanRun() => !IsBusy && Files.Count > 0 && Problem is null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        Refresh();
        if (Problem is not null) return;
        var files = Files.ToList();
        foreach (var f in files)
        {
            f.Status = null;
            f.Failed = false;
        }

        var options = new ProtectOptions(RequireOpenPassword ? _userPassword : null, _ownerPassword.Length > 0 ? _ownerPassword : null, Permissions);
        var target = CreateTarget();
        var failures = 0;
        await RunAsync(PdfProtection.ProtectOperation, files.Count == 1 ? files[0].Name : $"{files.Count} documentos", "Protegendo…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = OutputPathResolver.CreateReservationSet();
            string? lastError = null;
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = $"Documento {i + 1} de {files.Count}: {files[i].Name}";
                try
                {
                    var result = await Context.Security.Protection.ProtectAsync(files[i].Path, options, target, ct, reserved);
                    produced.Add(result);
                    var report = PdfProtection.Inspect(result.Path, options.UserPassword);
                    files[i].Status = $"Protegido ({report.Encryption ?? "criptografado"}) → {Path.GetFileName(result.Path)}";
                }
                catch (PdfEngineException ex)
                {
                    files[i].Status = ex.Message;
                    files[i].Failed = true;
                    failures++;
                    lastError = ex.Message;
                }

                progress.Report((i + 1) / (double)files.Count);
            }

            if (produced.Count == 0) throw new PdfEngineException(lastError ?? "Nenhum documento pôde ser protegido.");
            return produced;
        }, () => (failures > 0 ? $"{failures} documento(s) com erro; veja a lista.\n" : string.Empty) + "Guarde as senhas: o programa não as grava.");
    }
}

/// <summary>Remoção de senha e restrições com a senha fornecida pela pessoa.</summary>
public sealed partial class UnlockViewModel : FileListViewModel
{
    private string _password = string.Empty;

    public UnlockViewModel(ToolContext context)
        : base(context, "Remover proteção", "Grava uma cópia sem senha e sem restrições, usando a senha que você informar. O original não é alterado.", single: true)
    {
    }

    public override string EmptyHint => "Arraste para cá o PDF protegido";

    [ObservableProperty] private string? _summary;
    [ObservableProperty] private bool _isProtected;
    [ObservableProperty] private bool _authorized;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    partial void OnAuthorizedChanged(bool value) => OnStateChanged();

    public void SetPassword(string value)
    {
        _password = value;
        OnStateChanged();
    }

    protected override async Task OnFilesChangedAsync()
    {
        Summary = null;
        IsProtected = false;
        if (FirstPath is not { } path) return;
        var report = await Task.Run(() => PdfProtection.Inspect(path));
        IsProtected = report.Encrypted;
        Summary = SecurityText.Describe(report);
    }

    protected override void StateChanged() => StartCommand.NotifyCanExecuteChanged();

    private bool CanRun() => !IsBusy && IsProtected && Authorized && _password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        if (FirstPath is not { } path) return;
        var target = CreateTarget();
        var password = _password;
        if (await RunAsync(PdfProtection.UnlockOperation, Path.GetFileName(path), "Removendo proteção…",
                async (_, ct) => [await Context.Security.Protection.UnlockAsync(path, password, target, ct)]))
        {
            Authorized = false;
        }
    }
}

/// <summary>Análise de segurança e integridade de vários PDFs (não gera arquivos).</summary>
public sealed partial class SecurityInfoViewModel : FileListViewModel
{
    public SecurityInfoViewModel(ToolContext context)
        : base(context, "Propriedades de segurança", "Mostra quais documentos são protegidos, as permissões, a criptografia, as assinaturas digitais e se a estrutura está íntegra.", single: false)
    {
    }

    public override string EmptyHint => "Arraste para cá os PDFs a analisar";

    protected override async Task OnFilesChangedAsync()
    {
        var pending = Files.Where(f => f.Status is null).ToList();
        if (pending.Count == 0) return;
        await AnalyzeAsync("Analisando…", async (progress, ct) =>
        {
            for (var i = 0; i < pending.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = $"Documento {i + 1} de {pending.Count}: {pending[i].Name}";
                var (text, problem) = await Task.Run(() => Analyze(pending[i].Path, ct), ct);
                pending[i].Status = text;
                pending[i].Failed = problem;
                progress.Report((i + 1) / (double)pending.Count);
            }
        });
    }

    [RelayCommand]
    private async Task AnalyzeAgainAsync()
    {
        foreach (var f in Files) f.Status = null;
        await OnFilesChangedAsync();
    }

    private static (string Text, bool Problem) Analyze(string path, CancellationToken ct)
    {
        var lines = new List<string>();
        var problem = false;
        try
        {
            var report = PdfProtection.Inspect(path);
            lines.Add(SecurityText.Describe(report).Replace("\n", " · ", StringComparison.Ordinal));
            var structure = PdfRepair.Diagnose(path, null, ct);
            lines.Add(structure.Level switch
            {
                DamageLevel.None => "Estrutura íntegra.",
                DamageLevel.PasswordRequired => "Estrutura: precisa da senha para analisar.",
                _ => "Estrutura com problemas: " + string.Join(" ", structure.Problems.Take(3)) + " Use \"Reparar PDF\".",
            });
            problem = structure.Damaged;
            if (report.Opened && !report.PasswordRequired && !structure.StrictReaderOpens)
                lines.Add("Assinaturas e metadados: analise a cópia gerada por \"Reparar PDF\".");
            else if (report.Opened && !report.PasswordRequired)
            {
                var signatures = SignatureVerifier.Count(path);
                lines.Add(signatures == 0 ? "Sem assinaturas digitais." : $"{signatures} assinatura(s) digital(is): confira em \"Verificar assinaturas digitais\".");
                var properties = PdfMetadata.Read(path);
                if (properties.PdfAPart > 0) lines.Add($"Declara PDF/A-{properties.PdfAPart}{properties.PdfAConformance.ToLowerInvariant()} (declaração, não validação).");
            }
        }
        catch (Exception ex) when (ex is PdfEngineException or PDFMaster.Rendering.PdfRenderException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            lines.Add("Não foi possível analisar: " + ex.Message);
            problem = true;
        }

        return (string.Join("\n", lines), problem);
    }
}

/// <summary>Textos comuns das telas de segurança.</summary>
internal static class SecurityText
{
    public static string Describe(SecurityReport report)
    {
        if (!report.Opened)
            return report.PasswordRequired ? "Protegido: precisa de senha para abrir." : report.Problem ?? "O documento não pôde ser aberto.";
        var lines = new List<string>();
        if (!report.Encrypted)
        {
            lines.Add("Sem proteção: abre sem senha e sem restrições.");
        }
        else
        {
            lines.Add(report.HasRestrictions ? "Protegido com restrições (abre sem senha)." : "Criptografado, sem restrições.");
            if (report.Encryption is { } encryption) lines.Add("Criptografia: " + encryption + ".");
            if (report.Permissions?.Denied().ToList() is { Count: > 0 } denied) lines.Add("Não permite: " + string.Join(", ", denied) + ".");
        }

        lines.Add($"{report.PageCount} página(s)" + (report.Version is { } v ? $", PDF {v}." : "."));
        return string.Join("\n", lines);
    }
}

/// <summary>Visualização, edição e remoção de metadados.</summary>
public sealed partial class MetadataViewModel : FileListViewModel
{
    private const string DateFormat = "dd/MM/yyyy HH:mm:ss";

    public MetadataViewModel(ToolContext context)
        : base(context, "Metadados", "Veja, edite ou remova título, autor, assunto, palavras-chave, criador, produtor e datas. É gravada uma cópia.", single: true)
    {
    }

    public override string EmptyHint => "Arraste para cá o PDF";

    [ObservableProperty] private string _docTitle = string.Empty;
    [ObservableProperty] private string _author = string.Empty;
    [ObservableProperty] private string _subject = string.Empty;
    [ObservableProperty] private string _keywords = string.Empty;
    [ObservableProperty] private string _creator = string.Empty;
    [ObservableProperty] private string _producer = string.Empty;
    [ObservableProperty] private string _created = string.Empty;
    [ObservableProperty] private string _modified = string.Empty;
    [ObservableProperty] private bool _updateModified = true;
    [ObservableProperty] private string? _extra;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _loaded;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(SaveCommand);

    partial void OnCreatedChanged(string value) => Refresh();
    partial void OnModifiedChanged(string value) => Refresh();
    partial void OnUpdateModifiedChanged(bool value) => Refresh();

    protected override async Task OnFilesChangedAsync()
    {
        Loaded = false;
        Extra = null;
        if (FirstPath is not { } path) return;
        var p = await Task.Run(() => PdfMetadata.Read(path));
        DocTitle = p.Title;
        Author = p.Author;
        Subject = p.Subject;
        Keywords = p.Keywords;
        Creator = p.Creator;
        Producer = p.Producer;
        Created = p.Created?.ToString(DateFormat, PtBr) ?? string.Empty;
        Modified = p.Modified?.ToString(DateFormat, PtBr) ?? string.Empty;
        var extra = new List<string> { p.HasXmp ? "Tem bloco de metadados XMP (será regravado de acordo com estes campos)." : "Sem bloco de metadados XMP." };
        if (p.PdfAPart > 0) extra.Add($"Declara PDF/A-{p.PdfAPart}{p.PdfAConformance.ToLowerInvariant()}: depois de editar, valide de novo em \"Converter e validar PDF/A\".");
        if (SignatureVerifier.Count(path) > 0) extra.Add("O documento tem assinatura digital: a cópia com metadados alterados não terá assinatura válida.");
        Extra = string.Join("\n", extra);
        Loaded = true;
        Refresh();
    }

    private static bool TryDate(string text, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var format in new[] { DateFormat, "dd/MM/yyyy HH:mm", "dd/MM/yyyy" })
        {
            if (DateTime.TryParseExact(text.Trim(), format, PtBr, DateTimeStyles.AssumeLocal, out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private void Refresh()
    {
        Problem = !TryDate(Created, out _) ? "Data de criação inválida (use dd/mm/aaaa hh:mm:ss)."
            : !UpdateModified && !TryDate(Modified, out _) ? "Data de modificação inválida (use dd/mm/aaaa hh:mm:ss)."
            : null;
        OnStateChanged();
    }

    protected override void StateChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        RemoveAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => !IsBusy && Loaded && Problem is null;
    private bool CanRemove() => !IsBusy && Loaded;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (FirstPath is not { } path) return;
        TryDate(Created, out var created);
        DateTime? modified = DateTime.Now;
        if (!UpdateModified) TryDate(Modified, out modified);
        var properties = new DocumentProperties(DocTitle, Author, Subject, Keywords, Creator, Producer, created, modified);
        var target = CreateTarget();
        await RunAsync(PdfMetadata.OperationName, Path.GetFileName(path), "Gravando metadados…",
            async (_, ct) => [await Context.Security.Metadata.WriteAsync(path, properties, target, ct)]);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAllAsync()
    {
        if (FirstPath is not { } path) return;
        var target = CreateTarget();
        await RunAsync(PdfMetadata.OperationName, Path.GetFileName(path), "Removendo metadados…",
            async (_, ct) => [await Context.Security.Metadata.RemoveAllAsync(path, target, ct)],
            () => "Título, autor, assunto, palavras-chave, criador, produtor, datas e o bloco XMP foram removidos da cópia.");
    }
}

/// <summary>Uma assinatura verificada, pronta para exibir.</summary>
public sealed record SignatureItem(string Header, string Verdict, string Level, IReadOnlyList<string> Details, string Notes);

/// <summary>Verificação criptográfica das assinaturas digitais de um PDF.</summary>
public sealed partial class SignatureViewModel : FileListViewModel
{
    public SignatureViewModel(ToolContext context)
        : base(context, "Verificar assinaturas digitais",
            "Confere criptograficamente cada assinatura: integridade, alterações posteriores, certificado, validade e cadeia de certificação.", single: true) =>
        RootsInfo = RootsText(0);

    public override string EmptyHint => "Arraste para cá o PDF assinado";

    public ObservableCollection<SignatureItem> Signatures { get; } = [];

    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string _rootsInfo;

    private string RootsFolder => Context.Security.TrustedRootsFolder;

    private string RootsText(int count) =>
        $"Raízes adicionais confiáveis: {count} certificado(s) em \"{RootsFolder}\". Coloque ali certificados raiz (.cer, .crt) em que você confia, como os da ICP-Brasil.";

    protected override async Task OnFilesChangedAsync()
    {
        Signatures.Clear();
        Summary = null;
        if (FirstPath is not { } path) return;
        await AnalyzeAsync("Verificando assinaturas…", async (_, _) =>
        {
            var roots = LoadRoots();
            RootsInfo = RootsText(roots.Count);
            try
            {
                var reports = await Task.Run(() => SignatureVerifier.Verify(path, roots));
                foreach (var r in reports) Signatures.Add(ToItem(r));
                Summary = reports.Count == 0
                    ? "Nenhuma assinatura digital neste documento. (Assinaturas desenhadas ou imagens de assinatura não são assinaturas digitais.)"
                    : $"{reports.Count} assinatura(s) digital(is). A revogação dos certificados não é consultada (o programa não acessa a internet).";
            }
            finally
            {
                foreach (var root in roots) root.Dispose();
            }
        });
    }

    private List<X509Certificate2> LoadRoots()
    {
        var roots = new List<X509Certificate2>();
        try
        {
            Directory.CreateDirectory(RootsFolder);
            foreach (var file in Directory.EnumerateFiles(RootsFolder))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is not (".cer" or ".crt" or ".der" or ".pem")) continue;
                try
                {
                    roots.Add(X509CertificateLoader.LoadCertificateFromFile(file));
                }
                catch (CryptographicException)
                {
                    Context.Notifications.Show(NotificationKind.Warning, "Certificado ignorado", $"\"{Path.GetFileName(file)}\" não é um certificado válido.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Warning, "Pasta de certificados indisponível", ex.Message);
        }

        return roots;
    }

    private static SignatureItem ToItem(SignatureReport r)
    {
        string D(DateTimeOffset? d) => d is { } v ? v.LocalDateTime.ToString("dd/MM/yyyy HH:mm:ss", PtBr) : "—";
        var details = new List<string>
        {
            $"Signatário declarado: {r.SignerName ?? "—"}",
            $"Certificado: {r.SignerSubject ?? "—"}",
            $"Emissor: {r.Issuer ?? "—"}",
            $"Número de série: {r.SerialNumber ?? "—"}",
            $"Validade do certificado: {D(r.NotBefore)} a {D(r.NotAfter)}" + (r.NotBefore is null ? string.Empty : r.CertificateValidAtSigningTime ? " (válido na data da assinatura)" : " (NÃO válido na data da assinatura)"),
            $"Data da assinatura: {D(r.SigningTime)}" + (r.HasTimestamp ? r.TimestampValid ? " (carimbo do tempo válido)" : " (carimbo do tempo NÃO validado)" : " (declarada pelo signatário)"),
            $"Integridade: " + r.Integrity switch
            {
                SignatureIntegrity.Intact => "o conteúdo assinado não mudou e a assinatura cobre o documento inteiro",
                SignatureIntegrity.IntactButModifiedLater => $"o conteúdo assinado não mudou, mas há {r.BytesAfterSignature} bytes acrescentados depois",
                SignatureIntegrity.Broken => "o conteúdo assinado foi ALTERADO",
                _ => "não verificável",
            },
            $"Cadeia de certificação: " + (r.Chain.Count == 0 ? "—" : string.Join(" → ", r.Chain)) + r.Trust switch
            {
                CertificateTrust.Trusted => " (confiável)",
                CertificateTrust.UntrustedRoot => " (raiz não confiável neste computador)",
                CertificateTrust.Invalid => " (com problemas)",
                _ => string.Empty,
            },
        };
        if (r.Reason is { } reason) details.Add("Motivo: " + reason);
        if (r.Location is { } location) details.Add("Local: " + location);
        if (r.DigestAlgorithm is { } digest) details.Add($"Resumo criptográfico: {digest} · formato {r.SubFilter}");
        var level = r.Integrity == SignatureIntegrity.Broken || r.Integrity == SignatureIntegrity.Unverifiable || r.Trust == CertificateTrust.Invalid || !r.CertificateValidAtSigningTime
            ? "bad"
            : r.Integrity == SignatureIntegrity.Intact && r.Trust == CertificateTrust.Trusted ? "good" : "warning";
        var header = r.FieldName + (r.PageIndex >= 0 ? $" — página {r.PageIndex + 1}" : string.Empty);
        return new SignatureItem(header, r.Verdict, level, details, Bullets(r.Notes));
    }

    [RelayCommand]
    private void OpenRootsFolder()
    {
        try
        {
            Directory.CreateDirectory(RootsFolder);
            Context.Shell.OpenFolder(RootsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível abrir a pasta", ex.Message);
        }
    }

    [RelayCommand]
    private async Task VerifyAgainAsync() => await OnFilesChangedAsync();

    private bool CanSaveReport() => !IsBusy && Signatures.Count > 0;

    protected override void StateChanged() => SaveReportCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    private async Task SaveReportAsync()
    {
        if (FirstPath is not { } path) return;
        var text = new StringBuilder();
        text.AppendLine("Verificação de assinaturas digitais — PDF Master");
        text.AppendLine("Documento: " + path);
        text.AppendLine("Data da verificação: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", PtBr));
        text.AppendLine();
        foreach (var s in Signatures)
        {
            text.AppendLine(s.Header);
            text.AppendLine(s.Verdict);
            foreach (var d in s.Details) text.AppendLine("  " + d);
            if (s.Notes.Length > 0) text.AppendLine(s.Notes);
            text.AppendLine();
        }

        await SaveTextAsync(path, "_assinaturas.txt", "Verificar assinaturas", text.ToString());
    }

    private Task<bool> SaveTextAsync(string source, string suffix, string operation, string content)
    {
        var planner = new OutputPlanner(new OutputPathResolver());
        var target = CreateTarget();
        return RunAsync(operation, Path.GetFileName(source), "Gravando relatório…", (_, _) =>
        {
            var (output, overwrite) = planner.Resolve(Path.GetFileNameWithoutExtension(source) + suffix, target, operation, [source], null);
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.WriteAllText(temp, content, new UTF8Encoding(true)));
            return Task.FromResult<IReadOnlyList<ProducedFile>>([new ProducedFile(output, bytes, 0)]);
        }, describe: f => FileSizeFormatter.Format(f.Bytes));
    }
}

/// <summary>Diagnóstico e reparo de PDFs danificados.</summary>
public sealed partial class RepairViewModel : FileListViewModel
{
    private string _password = string.Empty;

    public RepairViewModel(ToolContext context)
        : base(context, "Reparar PDF", "Analisa a estrutura do arquivo, reconstrói a tabela de referências e grava uma cópia com o que puder ser recuperado.", single: true)
    {
    }

    public override string EmptyHint => "Arraste para cá o PDF danificado (ou que não abre)";

    [ObservableProperty] private string? _diagnosis;
    [ObservableProperty] private bool _needsPassword;
    [ObservableProperty] private bool _canRepairFile;
    [ObservableProperty] private string? _result;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(RepairCommand);

    public void SetPassword(string value) => _password = value;

    protected override async Task OnFilesChangedAsync()
    {
        Diagnosis = null;
        Result = null;
        NeedsPassword = false;
        CanRepairFile = false;
        if (FirstPath is not { } path) return;
        var password = _password.Length > 0 ? _password : null;
        await AnalyzeAsync("Analisando a estrutura…", async (_, ct) =>
        {
            var report = await Task.Run(() => PdfRepair.Diagnose(path, password, ct), ct);
            NeedsPassword = report.Level == DamageLevel.PasswordRequired;
            CanRepairFile = report.Level is not (DamageLevel.PasswordRequired or DamageLevel.Unrecoverable);
            var head = report.Level switch
            {
                DamageLevel.None => "Nenhum dano encontrado.",
                DamageLevel.Recoverable => "Arquivo danificado, mas legível: a cópia reparada deve recuperar todas as páginas.",
                DamageLevel.PartiallyRecoverable => "Arquivo danificado: parte das páginas não pode ser lida.",
                DamageLevel.Unrecoverable => "Arquivo irrecuperável: nenhuma página pôde ser lida.",
                _ => "O documento é protegido por senha.",
            };
            var lines = new List<string> { head, $"Tamanho: {FileSizeFormatter.Format(report.FileSize)} · páginas legíveis: {report.PageCount - report.BrokenPages.Count} de {report.PageCount}" };
            if (report.XrefEntries > 0) lines.Add($"Tabela de referências: {report.XrefEntries} entradas, {report.XrefWrongOffsets} apontando para o lugar errado.");
            if (report.ObjectsInFile is { } objects) lines.Add($"Objetos encontrados no arquivo: {objects}" + (report.MissingObjects is > 0 ? $"; objetos referenciados que faltam: {report.MissingObjects}." : "."));
            if (report.Problems.Count > 0) lines.Add(Bullets(report.Problems));
            Diagnosis = string.Join("\n", lines);
        });
    }

    [RelayCommand]
    private async Task AnalyzeAgainAsync() => await OnFilesChangedAsync();

    protected override void StateChanged() => RepairCommand.NotifyCanExecuteChanged();

    partial void OnCanRepairFileChanged(bool value) => OnStateChanged();

    private bool CanRepair() => !IsBusy && CanRepairFile;

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        if (FirstPath is not { } path) return;
        var target = CreateTarget();
        var password = _password.Length > 0 ? _password : null;
        RepairResult? outcome = null;
        await RunAsync(PdfRepair.OperationName, Path.GetFileName(path), "Reconstruindo…", async (_, ct) =>
        {
            outcome = await Context.Security.Repair.RepairAsync(path, password, target, ct);
            return [outcome.File];
        }, () => outcome is null ? null : $"{outcome.RecoveredPages} página(s) recuperada(s)" + (outcome.LostPages.Count > 0 ? $", {outcome.LostPages.Count} perdida(s)." : "."));
        if (outcome is not null)
        {
            var lines = new List<string>
            {
                $"Cópia recuperada: {Path.GetFileName(outcome.File.Path)}",
                $"Páginas recuperadas: {outcome.RecoveredPages}" + (outcome.LostPages.Count > 0 ? $" · perdidas: {string.Join(", ", outcome.LostPages)}" : " · nenhuma perdida"),
                $"Objetos gravados na cópia: {outcome.ObjectsWritten}",
            };
            if (outcome.Notes.Count > 0) lines.Add(Bullets(outcome.Notes));
            Result = string.Join("\n", lines);
        }
    }
}

/// <summary>Conversão para PDF/A e validação, em vários PDFs.</summary>
public sealed partial class PdfAViewModel : FileListViewModel
{
    public PdfAViewModel(ToolContext context)
        : base(context, "Converter e validar PDF/A", "Converte para PDF/A-1b, 2b ou 3b (arquivamento de longo prazo) ou só valida, com relatório do que foi verificado.", single: false)
    {
        Levels =
        [
            new(PdfALevel.A2b, "PDF/A-2b — recomendado (aceita transparência e camadas)"),
            new(PdfALevel.A1b, "PDF/A-1b — o mais antigo e restrito"),
            new(PdfALevel.A3b, "PDF/A-3b — permite anexos de qualquer tipo"),
        ];
        _selectedLevel = Levels[0];
        var tool = VeraPdf.Find();
        ValidatorInfo = tool is null
            ? "Validador completo (veraPDF) não encontrado: os arquivos passam pela verificação interna dos principais requisitos, mas a conformidade não é confirmada. Instale o veraPDF (gratuito) para a validação completa."
            : $"veraPDF encontrado ({tool}): cada arquivo também é validado por ele.";
    }

    public override string EmptyHint => "Arraste para cá os PDFs";

    public IReadOnlyList<Choice<PdfALevel>> Levels { get; }
    public string ValidatorInfo { get; }

    [ObservableProperty] private Choice<PdfALevel> _selectedLevel;
    [ObservableProperty] private bool _validateOnly;

    public string StartLabel => ValidateOnly ? "Validar" : "Converter";

    partial void OnValidateOnlyChanged(bool value) => OnPropertyChanged(nameof(StartLabel));

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void StateChanged() => StartCommand.NotifyCanExecuteChanged();

    private bool CanRun() => !IsBusy && Files.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        var files = Files.ToList();
        foreach (var f in files)
        {
            f.Status = null;
            f.Failed = false;
        }

        var level = SelectedLevel.Value;
        var target = CreateTarget();
        if (ValidateOnly)
        {
            await ValidateAsync(files, level, target);
            return;
        }

        var failures = 0;
        await RunAsync(PdfAConverter.OperationName, files.Count == 1 ? files[0].Name : $"{files.Count} documentos", "Convertendo…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = OutputPathResolver.CreateReservationSet();
            string? lastError = null;
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = $"Documento {i + 1} de {files.Count}: {files[i].Name}";
                try
                {
                    var result = await Context.Security.PdfA.ConvertAsync(files[i].Path, level, target, true, ct, reserved);
                    produced.Add(result.File);
                    var lines = new List<string> { $"{result.Validation.Verdict} → {Path.GetFileName(result.File.Path)}" };
                    if (result.Changes.Count > 0) lines.Add(Bullets(result.Changes));
                    files[i].Status = string.Join("\n", lines);
                }
                catch (PdfAConversionException ex)
                {
                    files[i].Status = ex.Message + "\n" + Bullets(ex.Problems.Take(10));
                    files[i].Failed = true;
                    failures++;
                    lastError = ex.Message;
                }
                catch (PdfEngineException ex)
                {
                    files[i].Status = ex.Message;
                    files[i].Failed = true;
                    failures++;
                    lastError = ex.Message;
                }

                progress.Report((i + 1) / (double)files.Count);
            }

            if (produced.Count == 0) throw new PdfEngineException((lastError ?? "Nenhum documento pôde ser convertido.") + " Veja os motivos na lista.");
            return produced;
        }, () => failures > 0 ? $"{failures} documento(s) não convertido(s); veja os motivos na lista." : null);
    }

    private async Task ValidateAsync(List<ConvertFileViewModel> files, PdfALevel level, OutputTarget target)
    {
        var report = new StringBuilder();
        report.AppendLine($"Relatório de validação {PdfA.Name(level)} — PDF Master");
        report.AppendLine("Data: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", PtBr));
        report.AppendLine("A verificação interna cobre os principais requisitos; só um validador completo (veraPDF) confirma a conformidade.");
        report.AppendLine();
        await RunAsync("Validar PDF/A", files.Count == 1 ? files[0].Name : $"{files.Count} documentos", "Validando…", async (progress, ct) =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                StatusText = $"Documento {i + 1} de {files.Count}: {files[i].Name}";
                var path = files[i].Path;
                try
                {
                    var validation = await Task.Run(() =>
                    {
                        var v = PdfAValidator.Validate(path, level, ct);
                        return VeraPdf.Validate(path, level, ct) is { } external ? v with { External = external } : v;
                    }, ct);
                    var lines = new List<string> { validation.Verdict };
                    if (validation.DeclaredPart > 0) lines.Add($"O arquivo declara PDF/A-{validation.DeclaredPart}{validation.DeclaredConformance.ToLowerInvariant()}.");
                    foreach (var check in validation.Checks.Where(c => c.Status != CheckStatus.Passed))
                        lines.Add($"{check.Description}:\n" + Bullets(check.Problems));
                    if (validation.External is { } e)
                    {
                        lines.Add(e.Summary);
                        if (e.FailedRules.Count > 0) lines.Add(Bullets(e.FailedRules.Take(10)));
                    }

                    files[i].Status = string.Join("\n", lines);
                    files[i].Failed = !validation.InternalPassed || validation.External?.Compliant == false;
                    report.AppendLine(path);
                    report.AppendLine(string.Join("\n", lines));
                    report.AppendLine("Requisitos verificados:");
                    foreach (var check in validation.Checks)
                        report.AppendLine($"  [{(check.Status == CheckStatus.Passed ? "ok" : check.Status == CheckStatus.Failed ? "FALHOU" : "não verificado")}] {check.Description}");
                    report.AppendLine();
                }
                catch (PdfEngineException ex)
                {
                    files[i].Status = ex.Message;
                    files[i].Failed = true;
                    report.AppendLine(path).AppendLine("Não validado: " + ex.Message).AppendLine();
                }

                progress.Report((i + 1) / (double)files.Count);
            }

            var planner = new OutputPlanner(new OutputPathResolver());
            var first = files[0].Path;
            var name = (files.Count == 1 ? Path.GetFileNameWithoutExtension(first) + "_" : string.Empty) + $"validacao_PDFA-{PdfA.Flavour(level)}.txt";
            var (output, overwrite) = planner.Resolve(name, target, "Validar PDF/A", files.Select(f => f.Path).ToList(), null);
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp => File.WriteAllText(temp, report.ToString(), new UTF8Encoding(true)));
            return [new ProducedFile(output, bytes, 0)];
        }, describe: _ => "Relatório de validação");
    }
}
