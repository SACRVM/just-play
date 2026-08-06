namespace JustPlay.UI.ViewModels;

/// <summary>
/// What a file's stored JUSTPLAY analysis is worth, as one of three states. The whole point is that it
/// is a REPORT, not an instruction: the suite trusts an old blob as-is and never re-analyses on its own
/// (memory <c>analysis-tag-persistence-design</c>), so "outdated" is a recommendation the user acts on.
/// </summary>
public enum AnalysisFreshness
{
    /// <summary>No analysis blob in the file at all. Grey.</summary>
    None,

    /// <summary>A blob from an older detector version — the values are real, just older than what we
    /// measure today. Yellow: worth re-running, nothing is broken.</summary>
    Outdated,

    /// <summary>Analysed with the current detector version. Green.</summary>
    Current,
}
