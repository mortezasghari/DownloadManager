namespace DownloadManager.UI.ViewModels;

/// <summary>One parsed, importable URL in the import-review checklist. Ticked by default; only ticked
/// candidates are enqueued.</summary>
public sealed class ImportCandidate(Uri url) : ObservableObject
{
    private bool _isSelected = true;

    public Uri Url { get; } = url;

    public string Display => Url.ToString();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}