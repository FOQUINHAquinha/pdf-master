using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Core.Files;
using PDFMaster.Core.Pdf;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

/// <summary>Base das telas que aplicam algo a vários PDFs inteiros (marca d'água, cabeçalho e rodapé).</summary>
public abstract partial class PdfBatchViewModel : ToolViewModelBase
{
    protected PdfBatchViewModel(ToolContext context, string title, string subtitle) : base(context, title, subtitle)
    {
    }

    public ObservableCollection<ConvertFileViewModel> Files { get; } = [];
    public override bool IsEmpty => Files.Count == 0;
    public bool HasFiles => Files.Count > 0;

    [ObservableProperty] private string? _problem;

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(StartCommand);

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        PickFilesCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanClear() => !IsBusy && Files.Count > 0;
    private bool CanRun() => !IsBusy && Files.Count > 0 && Problem is null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFilesAsync()
    {
        var picked = PickPdfs(multiple: true);
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        Files.Clear();
        FilesChanged();
    }

    public override Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy) return Task.CompletedTask;
        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count < paths.Count)
            Context.Notifications.Show(NotificationKind.Warning, "Alguns arquivos não foram adicionados", "Esta ferramenta aceita apenas PDFs.");
        foreach (var path in pdfs)
        {
            var full = Path.GetFullPath(path);
            if (Files.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            Files.Add(new ConvertFileViewModel(full, Move, Remove));
        }

        if (Files.Count > 0) SuggestOutputFolder(Files[0].Path);
        FilesChanged();
        return Task.CompletedTask;
    }

    private void Move(ConvertFileViewModel file, int direction)
    {
        if (IsBusy) return;
        var index = Files.IndexOf(file);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count) return;
        Files.Move(index, target);
    }

    private void Remove(ConvertFileViewModel file)
    {
        if (IsBusy) return;
        Files.Remove(file);
        FilesChanged();
    }

    private void FilesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFiles));
        Refresh();
    }

    protected void Refresh()
    {
        Problem = Validate();
        OnStateChanged();
    }

    protected abstract string? Validate();

    protected abstract string OperationName { get; }

    protected abstract Task<EditResult> ApplyAsync(string path, OutputTarget target, CancellationToken ct, ISet<string> reserved);

    protected static double Number(string text, double fallback) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StartAsync()
    {
        Refresh();
        if (Problem is not null) return;
        foreach (var file in Files)
        {
            file.Status = null;
            file.Failed = false;
        }

        var files = Files.ToList();
        var target = CreateTarget();
        var failures = 0;
        await RunAsync(OperationName, files.Count == 1 ? files[0].Name : $"{files.Count} documentos", "Aplicando…", async (progress, ct) =>
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
                    var result = await ApplyAsync(files[i].Path, target, ct, reserved);
                    produced.Add(result.File);
                    files[i].Status = $"{result.Applied} página(s) → {Path.GetFileName(result.File.Path)}";
                }
                catch (Exception ex) when (ex is PdfEngineException or PageRangeException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    files[i].Status = ex.Message;
                    files[i].Failed = true;
                    failures++;
                    lastError = ex.Message;
                }

                progress.Report((i + 1) / (double)files.Count);
            }

            if (produced.Count == 0) throw new PdfEngineException(files.Count == 1 && lastError is not null ? lastError : "Nenhum documento pôde ser processado; veja o motivo na lista.");
            return produced;
        }, () => failures > 0 ? $"{failures} documento(s) com erro; veja o motivo na lista." : null);
    }
}

/// <summary>Marca d'água de texto ou imagem em vários PDFs.</summary>
public sealed partial class WatermarkViewModel : PdfBatchViewModel
{
    public WatermarkViewModel(ToolContext context)
        : base(context, "Marca d'água", "Texto ou imagem em todas as páginas ou em páginas escolhidas, por cima ou por baixo do conteúdo.")
    {
        Positions =
        [
            new(MarkPosition.Center, "Centro"), new(MarkPosition.Top, "Em cima, no centro"), new(MarkPosition.Bottom, "Embaixo, no centro"),
            new(MarkPosition.TopLeft, "Canto superior esquerdo"), new(MarkPosition.TopRight, "Canto superior direito"),
            new(MarkPosition.BottomLeft, "Canto inferior esquerdo"), new(MarkPosition.BottomRight, "Canto inferior direito"),
            new(MarkPosition.Left, "Esquerda, no meio"), new(MarkPosition.Right, "Direita, no meio"),
        ];
        _selectedPosition = Positions[0];
        Refresh();
    }

    public IReadOnlyList<Choice<MarkPosition>> Positions { get; }
    public IReadOnlyList<string> Families => WindowsFonts.EditorFamilies;

    [ObservableProperty] private bool _useImage;
    [ObservableProperty] private string _text = "CONFIDENCIAL";
    [ObservableProperty] private string _fontFamily = "Arial";
    [ObservableProperty] private string _fontSizeText = "60";
    [ObservableProperty] private string _colorHex = "#C02020";
    [ObservableProperty] private bool _bold = true;
    [ObservableProperty] private double _opacity = 30;
    [ObservableProperty] private double _rotation = 45;
    [ObservableProperty] private Choice<MarkPosition> _selectedPosition;
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private double _imageWidth = 50;
    [ObservableProperty] private string _pages = string.Empty;
    [ObservableProperty] private bool _behind;

    public bool UseText => !UseImage;

    partial void OnUseImageChanged(bool value)
    {
        OnPropertyChanged(nameof(UseText));
        Refresh();
    }

    partial void OnTextChanged(string value) => Refresh();
    partial void OnFontSizeTextChanged(string value) => Refresh();
    partial void OnColorHexChanged(string value) => Refresh();
    partial void OnImagePathChanged(string? value) => Refresh();

    protected override string OperationName => PdfEditor.WatermarkOperation;

    [RelayCommand]
    private void PickImage()
    {
        var images = string.Join(";", ImageTools.Extensions.Select(e => "*" + e));
        var picked = Context.Dialogs.PickFiles("Imagem da marca d'água", $"Imagens|{images}", false, StartFolder());
        if (picked.Count > 0) ImagePath = picked[0];
    }

    protected override string? Validate()
    {
        if (UseImage) return string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath) ? "Escolha a imagem da marca d'água." : null;
        if (string.IsNullOrWhiteSpace(Text)) return "Digite o texto da marca d'água.";
        if (Number(FontSizeText, -1) is < TextStyle.MinSize or > TextStyle.MaxSize) return $"Tamanho da fonte entre {TextStyle.MinSize} e {TextStyle.MaxSize}.";
        return Rgb.TryParse(ColorHex, out _) ? null : "Cor no formato #RRGGBB.";
    }

    protected override async Task<EditResult> ApplyAsync(string path, OutputTarget target, CancellationToken ct, ISet<string> reserved)
    {
        byte[]? image = UseImage ? await File.ReadAllBytesAsync(ImagePath!, ct) : null;
        if (image is not null) ImageTools.Size(image);
        Rgb.TryParse(ColorHex, out var color);
        var options = new WatermarkOptions(
            UseImage ? null : Text,
            image,
            new TextStyle(FontFamily, Number(FontSizeText, 60), color, Bold),
            Opacity / 100,
            Rotation,
            SelectedPosition.Value,
            ImageWidth,
            string.IsNullOrWhiteSpace(Pages) ? null : Pages,
            Behind);
        return await Context.Editor.Editor.WatermarkAsync(path, options, target, ct, reserved);
    }
}

/// <summary>Cabeçalho e rodapé com campos (arquivo, data, hora, página, total) em vários PDFs.</summary>
public sealed partial class HeaderFooterViewModel : PdfBatchViewModel
{
    public HeaderFooterViewModel(ToolContext context)
        : base(context, "Cabeçalhos e rodapés", "Nome do documento, texto, data, hora e numeração de páginas, com margens personalizadas.")
    {
        Refresh();
    }

    public IReadOnlyList<string> Families => WindowsFonts.EditorFamilies;

    [ObservableProperty] private string _headerLeft = string.Empty;
    [ObservableProperty] private string _headerCenter = string.Empty;
    [ObservableProperty] private string _headerRight = string.Empty;
    [ObservableProperty] private string _footerLeft = string.Empty;
    [ObservableProperty] private string _footerCenter = string.Empty;
    [ObservableProperty] private string _footerRight = "Página {pagina} de {total}";
    [ObservableProperty] private string _fontFamily = "Arial";
    [ObservableProperty] private string _fontSizeText = "9";
    [ObservableProperty] private string _colorHex = "#303030";
    [ObservableProperty] private string _edgeMarginText = "10";
    [ObservableProperty] private string _sideMarginText = "15";
    [ObservableProperty] private string _pages = string.Empty;
    [ObservableProperty] private string _firstNumberText = "1";

    public string Preview => DocumentStamps.Expand(
        string.Join("   |   ", new[] { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }.Where(t => !string.IsNullOrWhiteSpace(t))),
        Files.Count > 0 ? Path.GetFileNameWithoutExtension(Files[0].Name) : "documento", (int)Number(FirstNumberText, 1), 12, DateTime.Now);

    partial void OnHeaderLeftChanged(string value) => Changed();
    partial void OnHeaderCenterChanged(string value) => Changed();
    partial void OnHeaderRightChanged(string value) => Changed();
    partial void OnFooterLeftChanged(string value) => Changed();
    partial void OnFooterCenterChanged(string value) => Changed();
    partial void OnFooterRightChanged(string value) => Changed();
    partial void OnFontSizeTextChanged(string value) => Changed();
    partial void OnEdgeMarginTextChanged(string value) => Changed();
    partial void OnSideMarginTextChanged(string value) => Changed();
    partial void OnFirstNumberTextChanged(string value) => Changed();
    partial void OnColorHexChanged(string value) => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(Preview));
        Refresh();
    }

    protected override string OperationName => PdfEditor.HeaderFooterOperation;

    private HeaderFooterOptions Options()
    {
        Rgb.TryParse(ColorHex, out var color);
        static string? Text(string t) => string.IsNullOrWhiteSpace(t) ? null : t;
        return new HeaderFooterOptions(Text(HeaderLeft), Text(HeaderCenter), Text(HeaderRight), Text(FooterLeft), Text(FooterCenter), Text(FooterRight),
            new TextStyle(FontFamily, Number(FontSizeText, 9), color), Number(EdgeMarginText, 10), Number(SideMarginText, 15),
            string.IsNullOrWhiteSpace(Pages) ? null : Pages, (int)Number(FirstNumberText, 1));
    }

    protected override string? Validate()
    {
        if (Options().IsEmpty) return "Preencha pelo menos um texto de cabeçalho ou rodapé.";
        if (Number(FontSizeText, -1) is < TextStyle.MinSize or > 72) return "Tamanho da fonte entre 4 e 72.";
        if (Number(EdgeMarginText, -1) is < 0 or > 100 || Number(SideMarginText, -1) is < 0 or > 100) return "Margens entre 0 e 100 mm.";
        if (!int.TryParse(FirstNumberText, out var first) || first < 0) return "O primeiro número deve ser um inteiro positivo.";
        return Rgb.TryParse(ColorHex, out _) ? null : "Cor no formato #RRGGBB.";
    }

    protected override Task<EditResult> ApplyAsync(string path, OutputTarget target, CancellationToken ct, ISet<string> reserved) =>
        Context.Editor.Editor.HeaderFooterAsync(path, Options(), target, ct, reserved);
}

/// <summary>Um campo do formulário na tela de preenchimento.</summary>
public sealed partial class FormFieldEntry : ObservableObject
{
    public FormFieldEntry(FormFieldInfo info)
    {
        Info = info;
        _text = info.Type is FormFieldType.Text ? info.Value ?? string.Empty : string.Empty;
        _isChecked = info.Type == FormFieldType.CheckBox && info.Value is { Length: > 0 } v && v != "Off";
        _selectedOption = info.Type is FormFieldType.Radio or FormFieldType.Choice ? info.Value : null;
    }

    public FormFieldInfo Info { get; }
    public string Name => Info.Name;
    public string TypeLabel => Info.Type switch
    {
        FormFieldType.Text => Info.Multiline ? "Texto (várias linhas)" : "Texto",
        FormFieldType.CheckBox => "Caixa de seleção",
        FormFieldType.Radio => "Botões de opção",
        FormFieldType.Choice => "Lista",
        FormFieldType.Button => "Botão (não preenchível)",
        _ => "Assinatura digital (não preenchível aqui)",
    } + (Info.ReadOnly && Info.Type is not (FormFieldType.Button or FormFieldType.Signature) ? " · somente leitura" : string.Empty)
      + (Info.PageIndex >= 0 ? $" · página {Info.PageIndex + 1}" : string.Empty);

    public bool IsText => Info.Type == FormFieldType.Text && Info.IsFillable;
    public bool IsCheck => Info.Type == FormFieldType.CheckBox && Info.IsFillable;
    public bool IsChoice => Info.Type is FormFieldType.Radio or FormFieldType.Choice && Info.IsFillable;
    public bool IsLocked => !Info.IsFillable;
    public string LockedValue => Info.Value ?? "—";

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string? _selectedOption;

    public string? Value => Info.Type switch
    {
        FormFieldType.Text => Text,
        FormFieldType.CheckBox => IsChecked ? Info.Options.FirstOrDefault() ?? "Yes" : "Off",
        _ => SelectedOption,
    };
}

/// <summary>Preenche formulários PDF existentes e grava um novo arquivo.</summary>
public sealed partial class FormFillViewModel : ToolViewModelBase
{
    private string? _path;

    public FormFillViewModel(ToolContext context)
        : base(context, "Formulários", "Preencha os campos de um formulário PDF e grave uma cópia preenchida. Para criar campos, use o editor.")
    {
    }

    /// <summary>Definido pela janela principal: abre o documento em outra ferramenta.</summary>
    public Action<string, string>? OpenToolRequested { get; set; }

    public ObservableCollection<FormFieldEntry> Fields { get; } = [];
    public override bool IsEmpty => _path is null;
    public bool HasDocument => _path is not null;
    public bool HasFields => Fields.Count > 0;
    public string DocumentName => _path is null ? string.Empty : Path.GetFileName(_path);

    [ObservableProperty] private string _summary = "Abra um PDF com formulário.";

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(SaveCommand);

    protected override void OnStateChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        PickFileCommand.NotifyCanExecuteChanged();
        CreateFieldsCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit() => !IsBusy;
    private bool CanSave() => !IsBusy && Fields.Any(f => f.Info.IsFillable);
    private bool CanCreate() => !IsBusy && _path is not null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PickFileAsync()
    {
        var picked = PickPdfs(multiple: false);
        if (picked.Count > 0) await AddFilesAsync(picked);
    }

    public override async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        var pdfs = OnlyPdfs(paths);
        if (pdfs.Count == 0)
        {
            if (paths.Count > 0) Context.Notifications.Show(NotificationKind.Warning, "Nenhum PDF na seleção", "Abra um arquivo PDF.");
            return;
        }

        IReadOnlyList<FormFieldInfo> fields = [];
        var path = Path.GetFullPath(pdfs[0]);
        var ok = false;
        await WhileBusyAsync("Lendo os campos…", async () =>
        {
            try
            {
                fields = await Task.Run(() => FormFields.Read(path));
                ok = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Context.Notifications.Show(NotificationKind.Error, "Não foi possível ler o formulário",
                    "O arquivo pode estar protegido por senha ou danificado. " + ex.Message);
            }
        });
        if (!ok) return;

        _path = path;
        Fields.Clear();
        foreach (var field in fields) Fields.Add(new FormFieldEntry(field));
        SuggestOutputFolder(path);
        var fillable = fields.Count(f => f.IsFillable);
        Summary = fields.Count == 0
            ? "Este PDF não tem campos de formulário. Use \"Criar campos no editor\" para adicioná-los."
            : $"{fields.Count} campo(s), {fillable} preenchível(is). O resultado é gravado como \"nome_preenchido.pdf\"; o original não muda.";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(DocumentName));
        OnStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateFields()
    {
        if (_path is not null) OpenToolRequested?.Invoke("form-fields", _path);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_path is null) return;
        var values = Fields.Where(f => f.Info.IsFillable).ToDictionary(f => f.Name, f => f.Value);
        var source = _path;
        EditResult? result = null;
        await RunAsync(PdfEditor.FormOperation, Path.GetFileName(source), "Gravando o formulário preenchido…", async (progress, ct) =>
        {
            result = await Context.Editor.Editor.FillFormAsync(source, values, CreateTarget(), ct);
            progress.Report(1);
            return [result.File];
        }, () => result is { Notes.Count: > 0 } r ? string.Join("\n", r.Notes) : null);
    }
}
