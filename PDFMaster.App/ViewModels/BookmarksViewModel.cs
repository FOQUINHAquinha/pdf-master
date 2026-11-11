using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.App.Services;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;

namespace PDFMaster.App.ViewModels;

public sealed partial class BookmarkRow : ObservableObject
{
    private readonly Action _changed;

    public BookmarkRow(PdfBookmark bookmark, Action changed)
    {
        _title = bookmark.Title;
        _pageText = (bookmark.PageIndex + 1).ToString(CultureInfo.InvariantCulture);
        _level = bookmark.Level;
        _changed = changed;
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _pageText;
    [ObservableProperty] private int _level;

    public Thickness Indent => new(Level * 22, 0, 0, 0);

    partial void OnTitleChanged(string value) => _changed();
    partial void OnPageTextChanged(string value) => _changed();

    partial void OnLevelChanged(int value)
    {
        OnPropertyChanged(nameof(Indent));
        _changed();
    }

    public PdfBookmark? ToBookmark() =>
        int.TryParse(PageText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? new PdfBookmark(Title, page - 1, Level) : null;
}

public sealed partial class LinkRow(PdfLinkInfo link) : ObservableObject
{
    public PdfLinkInfo Link { get; } = link;
    public string PageText => $"Página {Link.PageIndex + 1}";
    public string Description => Link.Description;
    [ObservableProperty] private bool _remove;
}

public sealed partial class AttachmentRow(string name, long bytes, string? sourcePath) : ObservableObject
{
    public string Name { get; } = name;
    public string Details => SourcePath is null ? FileSizeFormatter.Format(Bytes) : $"{FileSizeFormatter.Format(Bytes)} · novo";
    public long Bytes { get; } = bytes;

    /// <summary>Arquivo do computador a anexar (null = anexo que já está no PDF).</summary>
    public string? SourcePath { get; } = sourcePath;

    public bool IsExisting => SourcePath is null;
    [ObservableProperty] private bool _remove;
}

/// <summary>Marcadores (com níveis), sumário, links e anexos de um PDF.</summary>
public sealed partial class BookmarksViewModel : FileListViewModel
{
    private readonly DocumentNavigation _navigation;
    private int _pageCount;
    private bool _bookmarksEdited;

    public BookmarksViewModel(ToolContext context)
        : base(context, DocumentNavigation.OperationName,
            "Crie e organize marcadores (com subníveis), gere uma página de sumário, crie links para os endereços escritos no texto e anexe arquivos ao PDF.",
            single: true)
    {
        _navigation = new DocumentNavigation(context.Engine, new OutputPlanner(new OutputPathResolver()));
    }

    public override string EmptyHint => "Arraste para cá o PDF";

    public ObservableCollection<BookmarkRow> Bookmarks { get; } = [];
    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<AttachmentRow> Attachments { get; } = [];

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private BookmarkRow? _selectedBookmark;
    [ObservableProperty] private bool _linkUrls;
    [ObservableProperty] private bool _tableOfContents;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string? _result;

    public string PagesText => _pageCount > 0 ? $"O documento tem {_pageCount} página(s)." : string.Empty;
    public bool HasLinks => Links.Count > 0;
    public bool HasAttachments => Attachments.Count > 0;

    partial void OnSelectedBookmarkChanged(BookmarkRow? value) => StateChanged();

    protected override async Task OnFilesChangedAsync()
    {
        Loaded = false;
        Bookmarks.Clear();
        Links.Clear();
        Attachments.Clear();
        Result = null;
        if (FirstPath is not { } path) return;
        var info = await _navigation.ReadAsync(path);
        _pageCount = info.PageCount;
        foreach (var b in info.Bookmarks) Bookmarks.Add(new BookmarkRow(b, BookmarksChanged));
        foreach (var l in info.Links) Links.Add(new LinkRow(l));
        foreach (var a in info.Attachments) Attachments.Add(new AttachmentRow(a.Name, a.Bytes, null));
        _bookmarksEdited = false;
        Loaded = true;
        OnPropertyChanged(nameof(PagesText));
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasAttachments));
        Validate();
    }

    private void BookmarksChanged()
    {
        _bookmarksEdited = true;
        Validate();
    }

    private List<PdfBookmark>? CurrentBookmarks()
    {
        var list = new List<PdfBookmark>();
        foreach (var row in Bookmarks)
        {
            if (row.ToBookmark() is not { } bookmark) return null;
            list.Add(bookmark);
        }

        return list;
    }

    private void Validate()
    {
        var bookmarks = CurrentBookmarks();
        Problem = bookmarks is null ? "Informe a página de cada marcador com um número."
            : DocumentNavigation.Validate(bookmarks, _pageCount) is { } problem ? problem
            : TableOfContents && bookmarks.Count == 0 ? "O sumário é montado a partir dos marcadores: crie pelo menos um."
            : null;
        StateChanged();
    }

    partial void OnTableOfContentsChanged(bool value) => Validate();

    protected override void StateChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        AddBookmarkCommand.NotifyCanExecuteChanged();
        RemoveBookmarkCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        IndentCommand.NotifyCanExecuteChanged();
        OutdentCommand.NotifyCanExecuteChanged();
        AddAttachmentsCommand.NotifyCanExecuteChanged();
    }

    public override Task<bool> RunPrimaryAsync() => ExecuteIfPossibleAsync(SaveCommand);

    private bool CanEditDocument() => Loaded && !IsBusy;
    private bool HasSelection() => CanEditDocument() && SelectedBookmark is not null;

    [RelayCommand(CanExecute = nameof(CanEditDocument))]
    private void AddBookmark()
    {
        var index = SelectedBookmark is null ? Bookmarks.Count : Bookmarks.IndexOf(SelectedBookmark) + 1;
        var level = SelectedBookmark?.Level ?? 0;
        var page = SelectedBookmark?.ToBookmark() is { } b ? Math.Min(b.PageIndex + 1, _pageCount - 1) : 0;
        var row = new BookmarkRow(new PdfBookmark($"Marcador {Bookmarks.Count + 1}", page, level), BookmarksChanged);
        Bookmarks.Insert(index, row);
        SelectedBookmark = row;
        BookmarksChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveBookmark()
    {
        var index = Bookmarks.IndexOf(SelectedBookmark!);
        var level = SelectedBookmark!.Level;
        Bookmarks.RemoveAt(index);
        // Os filhos sobem um nível para a estrutura continuar válida.
        for (var i = index; i < Bookmarks.Count && Bookmarks[i].Level > level; i++) Bookmarks[i].Level--;
        SelectedBookmark = Bookmarks.Count == 0 ? null : Bookmarks[Math.Min(index, Bookmarks.Count - 1)];
        BookmarksChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown() => Move(1);

    private void Move(int direction)
    {
        var row = SelectedBookmark!;
        var index = Bookmarks.IndexOf(row);
        var target = index + direction;
        if (target < 0 || target >= Bookmarks.Count) return;
        Bookmarks.Move(index, target);
        SelectedBookmark = row;
        BookmarksChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Indent()
    {
        var index = Bookmarks.IndexOf(SelectedBookmark!);
        if (index > 0 && SelectedBookmark!.Level <= Bookmarks[index - 1].Level) SelectedBookmark.Level++;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Outdent()
    {
        if (SelectedBookmark!.Level > 0) SelectedBookmark.Level--;
    }

    [RelayCommand(CanExecute = nameof(CanEditDocument))]
    private void AddAttachments()
    {
        foreach (var file in Context.Dialogs.PickFiles("Arquivos para anexar ao PDF", "Todos os arquivos|*.*", multiple: true, StartFolder()))
        {
            var info = new FileInfo(file);
            Attachments.Add(new AttachmentRow(info.Name, info.Length, info.FullName));
        }

        OnPropertyChanged(nameof(HasAttachments));
    }

    [RelayCommand]
    private void SaveAttachment(AttachmentRow? row)
    {
        if (row is null || FirstPath is not { } path) return;
        var folder = Context.Dialogs.PickFolder("Salvar o anexo em…", StartFolder());
        if (folder is null) return;
        try
        {
            var data = row.SourcePath is { } source ? File.ReadAllBytes(source) : DocumentNavigation.ReadAttachment(path, row.Name);
            var target = Path.Combine(folder, ContentExtractor.SafeName(row.Name));
            for (var i = 2; File.Exists(target); i++)
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(ContentExtractor.SafeName(row.Name))} ({i}){Path.GetExtension(row.Name)}");
            File.WriteAllBytes(target, data);
            Context.Notifications.Show(NotificationKind.Success, "Anexo salvo", target,
                new NotificationAction("Abrir pasta", () => Context.Shell.OpenFolder(target)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PdfEngineException)
        {
            Context.Notifications.Show(NotificationKind.Error, "Não foi possível salvar o anexo", ex.Message);
        }
    }

    private bool CanSave() => CanEditDocument() && HasOutputFolder && Problem is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var source = FirstPath!;
        var changes = new NavigationChanges(
            _bookmarksEdited ? CurrentBookmarks() : null,
            Links.Where(l => l.Remove).Select(l => (l.Link.PageIndex, l.Link.AnnotationIndex)).ToList(),
            LinkUrls,
            Attachments.Where(a => !a.IsExisting && !a.Remove).Select(a => a.SourcePath!).ToList(),
            Attachments.Where(a => a.IsExisting && a.Remove).Select(a => a.Name).ToList(),
            TableOfContents);
        var target = CreateTarget();
        NavigationResult? done = null;
        Result = null;
        await RunAsync(Title, Path.GetFileName(source), "Gravando…", async (_, ct) =>
        {
            done = await _navigation.SaveAsync(source, changes, target, ct);
            return [done.File];
        });
        if (done is not null)
        {
            Result = $"{done.Bookmarks} marcador(es); {done.LinksCreated} link(s) criado(s), {done.LinksRemoved} removido(s); " +
                     $"{done.AttachmentsAdded} anexo(s) adicionado(s), {done.AttachmentsRemoved} removido(s)" +
                     (done.TableOfContents ? "; página de sumário inserida no início." : ".");
        }
    }
}
