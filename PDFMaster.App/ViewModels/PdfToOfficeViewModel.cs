using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

/// <summary>PDF para Excel (texto em linhas e colunas) e PowerPoint (um slide por página).</summary>
public sealed partial class PdfToOfficeViewModel : FileListViewModel
{
    private readonly PdfToOfficeConverter _converter;

    public PdfToOfficeViewModel(ToolContext context)
        : base(context, PdfToOfficeConverter.OperationName,
            "Excel: uma aba por página, com o texto separado em linhas e colunas. PowerPoint: um slide por página com a imagem da página.",
            single: false)
    {
        _converter = new PdfToOfficeConverter(context.Renderer, new OutputPlanner(new OutputPathResolver()));
        Resolutions = [new(96, "96 DPI (arquivo menor)"), new(150, "150 DPI (recomendado)"), new(220, "220 DPI (mais nítido)")];
        _selectedResolution = Resolutions[1];
    }

    public override string EmptyHint => "Arraste para cá os PDFs";
    public IReadOnlyList<Choice<int>> Resolutions { get; }

    [ObservableProperty] private bool _toExcel = true;
    [ObservableProperty] private bool _toPowerPoint;
    [ObservableProperty] private Choice<int> _selectedResolution;

    public string ActionText => ToExcel ? "Converter para Excel" : "Converter para PowerPoint";

    partial void OnToExcelChanged(bool value)
    {
        if (value == ToPowerPoint) ToPowerPoint = !value;
        OnPropertyChanged(nameof(ActionText));
    }

    partial void OnToPowerPointChanged(bool value)
    {
        if (value == ToExcel) ToExcel = !value;
        OnPropertyChanged(nameof(ActionText));
    }

    protected override void StateChanged() => ConvertCommand.NotifyCanExecuteChanged();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(ConvertCommand);

    private bool CanConvert() => !IsBusy && HasFiles && HasOutputFolder;

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        var files = Files.Select(f => f.Path).ToList();
        var target = CreateTarget();
        var format = ToExcel ? OfficeFormat.Excel : OfficeFormat.PowerPoint;
        var dpi = SelectedResolution.Value;
        await RunAsync(Title, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} arquivos",
            format == OfficeFormat.Excel ? "Montando as planilhas…" : "Montando os slides…", async (progress, ct) =>
            {
                var produced = new List<ProducedFile>();
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < files.Count; i++)
                {
                    var index = i;
                    produced.Add(await _converter.ConvertAsync(files[i], format, target, dpi,
                        new Progress<double>(v => ((IProgress<double>)progress).Report((index + v) / files.Count)), ct, reserved));
                }

                return produced;
            }, describe: f => $"{f.PageCount} {(format == OfficeFormat.Excel ? "aba(s)" : "slide(s)")}, {FileSizeFormatter.Format(f.Bytes)}");
    }
}
