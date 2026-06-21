namespace DownloadManager.UI.ViewModels;

/// <summary>
/// Which gate(s) currently assert the global queue pause (ADR-0023): the effective pause is the OR of the
/// manual user pause and the schedule gate. Exposed so the UI can show "Paused by you" vs "Outside scheduled
/// hours" (vs both) rather than an opaque "Paused".
/// </summary>
[Flags]
public enum QueuePauseReason
{
    None = 0,

    /// <summary>The user's manual Pause is asserting.</summary>
    Manual = 1,

    /// <summary>Scheduling is enabled and the current time is outside the active window.</summary>
    Schedule = 2,
}