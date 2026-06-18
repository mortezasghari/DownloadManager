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