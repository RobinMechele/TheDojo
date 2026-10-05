namespace TheDojo.Services;

/// <summary>What view models ask of the window system, so they stay testable without one.</summary>
public interface IUiService
{
    /// <summary>A save-file dialog; null when cancelled.</summary>
    string? AskSavePath(string title, string suggestedName, string filter);

    void SetClipboardText(string text);

    /// <summary>Opens a file or folder with the shell (Explorer, the default viewer).</summary>
    void Open(string path);

    /// <summary>Starts the given exe and closes this app (after an update replaced it).</summary>
    void Restart(string exePath);

    /// <summary>Runs on the UI thread.</summary>
    void Post(Action action);
}
