using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using PDFMaster.Core.Tools;
using PDFMaster.Domain;

namespace PDFMaster.App.ViewModels;

public sealed class ToolCardViewModel
{
    public ToolCardViewModel(ToolDefinition tool, Action<ToolDefinition> open)
    {
        Tool = tool;
        OpenCommand = new RelayCommand(() => open(tool), () => tool.IsAvailable);
    }

    public ToolDefinition Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;
    public string Glyph => Tool.Glyph;
    public string CategoryName => ToolCatalog.CategoryName(Tool.Category);
    public bool IsAvailable => Tool.IsAvailable;
    public string StatusText => Tool.IsAvailable ? "Disponível" : $"Prevista para a Fase {Tool.PlannedPhase}";
    public string Tooltip => Tool.IsAvailable ? "Abrir " + Tool.Name : $"Ainda não implementada (Fase {Tool.PlannedPhase})";

    /// <summary>Só executa para ferramentas disponíveis; as demais ficam desabilitadas.</summary>
    public ICommand OpenCommand { get; }
}

/// <summary>Lista de ferramentas de uma categoria ou de um resultado de pesquisa.</summary>
public sealed class ToolsPageViewModel
{
    public ToolsPageViewModel(string title, string subtitle, IReadOnlyList<ToolDefinition> tools, bool showCategory,
        Action<ToolDefinition> open)
    {
        Title = title;
        Subtitle = subtitle;
        ShowCategory = showCategory;
        // Disponíveis primeiro, mantendo a ordem original dentro de cada grupo.
        Tools = tools.OrderByDescending(t => t.IsAvailable).Select(t => new ToolCardViewModel(t, open)).ToList();
        HasUnavailable = Tools.Any(t => !t.IsAvailable);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool ShowCategory { get; }
    public IReadOnlyList<ToolCardViewModel> Tools { get; }
    public bool HasUnavailable { get; }
}
