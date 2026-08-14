using JustPlay.UI.Theming;

namespace JustPlay.UI.Views;

/// <summary>
/// The per-app content for the shared <see cref="AboutWindow"/>. Each JUST app passes its
/// own name, tagline, version and brand glyph; everything else (layout, theming, the
/// "Part of J.U.S.T." line, author/copyright) is shared so every app's About is identical
/// in look and structure. See CLAUDE.md "JUST suite UI philosophy".
/// </summary>
/// <param name="AppName">Display name shown as the gradient wordmark (e.g. "JustPlay", "JUST STREAM").</param>
/// <param name="Tagline">One-line description under the name.</param>
/// <param name="Version">Version line, already formatted (e.g. "Version 0.3.1").</param>
/// <param name="Glyph">The app's brand glyph, drawn white on the theme-gradient chip.</param>
/// <param name="By">
/// Author credit line. The AUTHOR is a person; the PUBLISHER is the suite, and that split is
/// deliberate - the assembly metadata, the installer's AppPublisher and the UAC prompt all say
/// "Just Useful Sound Tools", while this line and the LICENSE name who actually wrote it.
/// </param>
/// <param name="Copyright">Copyright line.</param>
/// <param name="SuiteLine">The shared J.U.S.T. suite line.</param>
/// <param name="Motto">The maker credo, shared by every JUST app - fixed wording, kept verbatim.</param>
/// <param name="ByUrl">
/// Where <paramref name="By"/> points. Set for every app in the suite by default, so the credit is
/// one click from the author's page in all three - pass an empty string to render it as plain text.
/// </param>
public sealed record AboutInfo(
    string AppName,
    string Tagline,
    string Version,
    BrandGlyph Glyph,
    string By = "by SACRVM",
    string Copyright = "(c) 2026 Just Useful Sound Tools",
    string SuiteLine = "Part of J.U.S.T. - Just Useful Sound Tools",
    string Motto = "FROM DJS TO DJS",
    string ByUrl = "https://sacrvm.dev")
{
    /// <summary>True when <see cref="By"/> should render as a link rather than as plain text.</summary>
    public bool HasByLink => !string.IsNullOrWhiteSpace(ByUrl);
}
