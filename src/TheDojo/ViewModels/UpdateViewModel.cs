using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TheDojo.Core.Updates;
using TheDojo.Services;

namespace TheDojo.ViewModels;

/// <summary>The "a new version is available" banner: checks GitHub once, then downloads, verifies and swaps the exe on request.</summary>
public sealed partial class UpdateViewModel(UpdateService? service, string? targetExe, Version current, IUiService ui) : ObservableObject
{
    private UpdateInfo? _update;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    private bool _isInstalling;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    private string? _error;

    public string Message => Error is { } e
        ? "The update failed: " + e
        : IsInstalling
            ? $"Downloading The Dojo {_update?.Version.ToString(3)}…"
            : _update is { } u ? $"The Dojo {u.Version.ToString(3)} is available (you have {current.ToString(3)})." : string.Empty;

    /// <summary>True when this is an installed single-file exe that can replace itself (not <c>dotnet run</c>).</summary>
    public bool CanSelfUpdate => service is not null && targetExe is not null;

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSelfUpdate)
        {
            return;
        }

        try
        {
            _update = await service!.CheckAsync(current, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        IsAvailable = _update is not null;
        OnPropertyChanged(nameof(Message));
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_update is null || IsInstalling)
        {
            return;
        }

        Error = null;
        IsInstalling = true;
        try
        {
            await service!.InstallAsync(_update, targetExe!, new Progress<double>(p => ui.Post(() => Progress = p)));
            ui.Restart(targetExe!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            Error = ex.Message;
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsAvailable = false;
        Error = null;
    }
}
