using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

/// <summary>Extrair conteúdo: imagens, texto, anexos, fontes e metadados de PDFs, cada um numa pasta nova.</summary>
public sealed partial class ExtractContentViewModel : FileListViewModel
{
    private readonly ContentExtractor _extractor = new();

    public ExtractContentViewModel(ToolContext context)
        : base(context, ContentExtractor.OperationName,
            "Grava numa pasta nova (\"nome_conteudo\") as imagens, o texto, os anexos, as fontes e os metadados de cada PDF. O original não é alterado.",
            single: false)
    {
    }

    public override string EmptyHint => "Arraste para cá os PDFs";

    [ObservableProperty] private bool _images = true;
    [ObservableProperty] private bool _text = true;
    [ObservableProperty] private bool _attachments = true;
    [ObservableProperty] private bool _fonts;
    [ObservableProperty] private bool _metadata = true;
    [ObservableProperty] private string? _result;

    partial void OnImagesChanged(bool value) => StateChanged();
    partial void OnTextChanged(bool value) => StateChanged();
    partial void OnAttachmentsChanged(bool value) => StateChanged();
    partial void OnFontsChanged(bool value) => StateChanged();
    partial void OnMetadataChanged(bool value) => StateChanged();

    protected override void StateChanged() => ExtractCommand.NotifyCanExecuteChanged();

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(ExtractCommand);

    private bool CanExtract() => !IsBusy && HasFiles && HasOutputFolder && (Images || Text || Attachments || Fonts || Metadata);

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAsync()
    {
        var files = Files.Select(f => f.Path).ToList();
        var options = new ExtractOptions(Images, Text, Attachments, Fonts, Metadata);
        var folder = OutputFolder;
        var lines = new List<string>();
        Result = null;
        await RunAsync(Title, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} arquivos", "Extraindo…", async (progress, ct) =>
        {
            var produced = new List<ProducedFile>();
            for (var i = 0; i < files.Count; i++)
            {
                var index = i;
                var result = await _extractor.ExtractAsync(files[i], options, folder,
                    new Progress<double>(v => ((IProgress<double>)progress).Report((index + v) / files.Count)), ct);
                var written = Directory.EnumerateFiles(result.Folder, "*", SearchOption.AllDirectories)
                    .OrderBy(f => Path.GetDirectoryName(f)!.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (written.Count == 0)
                {
                    try
                    {
                        Directory.Delete(result.Folder, recursive: true);
                    }
                    catch (IOException)
                    {
                        // Pasta vazia; fica para a pessoa apagar.
                    }
                }

                produced.AddRange(written.Select(f => new ProducedFile(f, new FileInfo(f).Length, 0)));
                lines.Add($"{Path.GetFileName(files[i])} → {result.Folder}\n   {result.Images} imagem(ns), " +
                          $"{(result.TextPages > 0 ? $"texto de {result.TextPages} página(s)" : "sem texto")}, {result.Attachments} anexo(s), " +
                          $"{result.Fonts} fonte(s){(result.Metadata ? ", metadados" : string.Empty)}" +
                          (result.Notes.Count > 0 ? "\n   " + string.Join("\n   ", result.Notes) : string.Empty));
            }

            if (produced.Count == 0) throw new PdfEngineException("Nada foi extraído.\n" + string.Join("\n", lines));
            return produced;
        }, describe: f => FileSizeFormatter.Format(f.Bytes));
        if (lines.Count > 0) Result = string.Join("\n", lines);
    }
}
