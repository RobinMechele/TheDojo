using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TheDojo.Core;
using TheDojo.Core.Limits;
using TheDojo.Core.Reports;
using TheDojo.Services;

namespace TheDojo.ViewModels;

/// <summary>Exports: a Markdown report to share or keep, JSON for other tools, CSV of the raw data.</summary>
public sealed partial class ReportsViewModel(IUiService ui) : PageViewModel
{
    private DojoSnapshot? _snapshot;
    private IReadOnlyList<ProviderLimits> _limits = [];

    public override Page Page => Page.Reports;

    public override string Title => "Reports";

    public override string Glyph => "";

    [ObservableProperty]
    private bool _includePrompts;

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string? _status;

    partial void OnIncludePromptsChanged(bool value) => BuildPreview();

    public override void Update(DojoSnapshot snapshot, IReadOnlyList<ProviderLimits> limits, DateTimeOffset now)
    {
        _snapshot = snapshot;
        _limits = limits;
        BuildPreview();
    }

    private void BuildPreview()
    {
        if (_snapshot is not null)
        {
            Preview = MarkdownReport.Write(_snapshot.Report, _snapshot.Coach, _limits, IncludePrompts, Display.Zone);
        }
    }

    private string Stamp => _snapshot is null ? "report" : Display.Format(_snapshot.Report.To, "yyyy-MM-dd");

    [RelayCommand]
    private void SaveMarkdown() => Save("Save report", $"dojo-report-{Stamp}.md", "Markdown (*.md)|*.md", () => Preview);

    [RelayCommand]
    private void SaveJson() => Save("Save report as JSON", $"dojo-report-{Stamp}.json", "JSON (*.json)|*.json",
        () => JsonReport.Write(_snapshot!.Report, _snapshot.Coach, _limits, IncludePrompts));

    [RelayCommand]
    private void ExportCalls() => Save("Export model calls", $"dojo-calls-{Stamp}.csv", "CSV (*.csv)|*.csv",
        () => CsvExport.Calls(_snapshot!.Data, _snapshot.Costs, _snapshot.Report));

    [RelayCommand]
    private void ExportSessions() => Save("Export sessions", $"dojo-sessions-{Stamp}.csv", "CSV (*.csv)|*.csv",
        () => CsvExport.Sessions(_snapshot!.Report));

    [RelayCommand]
    private void ExportTools() => Save("Export tool calls", $"dojo-tools-{Stamp}.csv", "CSV (*.csv)|*.csv",
        () => CsvExport.Tools(_snapshot!.Data, _snapshot.Report));

    [RelayCommand]
    private void CopyMarkdown()
    {
        ui.SetClipboardText(Preview);
        Status = "Report copied to the clipboard.";
    }

    private void Save(string title, string name, string filter, Func<string> content)
    {
        if (_snapshot is null || ui.AskSavePath(title, name, filter) is not { } path)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, content());
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
        }
    }
}
