using PDFMaster.Core.Tools;
using PDFMaster.Domain;

namespace PDFMaster.App.ViewModels;

public sealed class ToolCardViewModel
{
    public ToolCardViewModel(ToolDefinition tool)
    {
        Tool = tool;
    }

    public ToolDefinition Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;
    public string Glyph => Tool.Glyph;
    public string CategoryName => ToolCatalog.CategoryName(Tool.Category);
    public bool IsAvailable => Tool.IsAvailable;
    public string StatusText => Tool.IsAvailable ? "Disponível" : $"Prevista para a Fase {Tool.PlannedPhase}";
}

/// <summary>Lista de ferramentas de uma categoria ou de um resultado de pesquisa.</summary>
public sealed class ToolsPageViewModel
{
    public ToolsPageViewModel(string title, string subtitle, IReadOnlyList<ToolDefinition> tools, bool showCategory)
    {
        Title = title;
        Subtitle = subtitle;
        ShowCategory = showCategory;
        Tools = tools.Select(t => new ToolCardViewModel(t)).ToList();
        HasUnavailable = Tools.Any(t => !t.IsAvailable);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool ShowCategory { get; }
    public IReadOnlyList<ToolCardViewModel> Tools { get; }
    public bool HasUnavailable { get; }
}
