using DownloadManager.Core.Domain;

namespace DownloadManager.UI.Services;

/// <summary>
/// Picks a URL-list file to import. Abstracted so the view-model stays Avalonia-free and headless-testable;
/// the real implementation wraps the platform file picker, tests supply a fake.
/// </summary>
public interface IFilePicker
{
    Task<string?> PickListFileAsync();
}

/// <summary>
/// Prompts the user to (re-)supply per-download credentials. Returns the new credentials, or <c>null</c>
/// if cancelled. Credentials stay session-memory only — never persisted (ADR-0011).
/// </summary>
public interface ICredentialPrompt
{
    Task<DownloadCredentials?> PromptAsync(string downloadName);
}

/// <summary>
/// Reads plain text from the system clipboard (Phase 6 import auto-paste). Behind a seam so the import
/// view-model stays Avalonia-free; the real implementation wraps <c>TopLevel.Clipboard</c>. Returns
/// <c>null</c> when the clipboard is empty or holds non-text content.
/// </summary>
public interface IClipboardTextSource
{
    Task<string?> GetTextAsync();
}

/// <summary>
/// Shows the import-review dialog and enqueues the ticked URLs via <paramref name="addToQueue"/> (the
/// normal add-path). Avalonia-backed; injected so the root view-model stays headless-testable.
/// </summary>
public interface IImportDialog
{
    Task ShowAsync(Func<IReadOnlyList<Uri>, Task> addToQueue);
}