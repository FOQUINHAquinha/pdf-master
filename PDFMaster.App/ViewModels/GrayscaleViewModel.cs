using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.Compression;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

/// <summary>Converter para preto e branco (tons de cinza), mantendo o texto como texto.</summary>
public sealed partial class GrayscaleViewModel : FileListViewModel
{
    private readonly GrayscaleConverter _converter;

    public GrayscaleViewModel(ToolContext context)
        : base(context, GrayscaleConverter.OperationName,
            "Converte textos, desenhos e imagens para tons de cinza sem transformar as páginas em imagem: o texto continua selecionável. O original não é alterado.",
            single: false)
    {
        _converter = new GrayscaleConverter(new OutputPlanner(new OutputPathResolver()), context.Renderer);
    }

    public override string EmptyHint => "Arraste para cá os PDFs";

    [ObservableProperty] private string? _result;

    protected override void StateChanged() => ConvertCommand.NotifyCanExecuteChanged();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(ConvertCommand);

    private bool CanConvert() => !IsBusy && HasFiles && HasOutputFolder;

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAsync()
    {
        var files = Files.Select(f => f.Path).ToList();
        var target = CreateTarget();
        var lines = new List<string>();
        Result = null;
        await RunAsync(Title, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} arquivos", "Convertendo para tons de cinza…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                var index = i;
                var result = await _converter.ConvertAsync(files[i], target,
                    new Progress<double>(v => ((IProgress<double>)progress).Report((index + v) / files.Count)), ct, reserved);
                produced.Add(result.File);
                var line = $"{Path.GetFileName(files[i])}: {result.ColorOperators} cor(es) de texto/desenho, {result.Images} imagem(ns) e {result.Shadings} degradê(s) convertidos.";
                line += result.PagesStillInColor.Count == 0
                    ? " Conferido: nenhuma página com cor."
                    : $" Atenção: ainda há cor na(s) página(s) {string.Join(", ", result.PagesStillInColor)}.";
                if (result.Notes.Count > 0) line += "\n   " + string.Join("\n   ", result.Notes);
                lines.Add(line);
            }

            return produced;
        });
        if (lines.Count > 0) Result = string.Join("\n", lines);
    }
}
