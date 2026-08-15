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
/// Author credit line. The AUTHOR is the public identity; the PUBLISHER is the suite, and that
/// split is deliberate - the assembly metadata, the installer's AppPublisher and the UAC prompt
/// all say "Just Useful Sound Tools", while this line points at the identity behind them.
/// </param>
/// <param name="Copyright">
/// Copyright line - see <see cref="CopyrightLine"/>. A third field, and a third rule: the suite
/// PUBLISHES, the contributors HOLD the copyright, and no person is named in either.
/// </param>
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
    string Copyright = AboutInfo.CopyrightLine,
    string SuiteLine = "Part of J.U.S.T. - Just Useful Sound Tools",
    string Motto = "FROM DJS TO DJS",
    string ByUrl = "https://sacrvm.dev")
{
    /// <summary>
    /// The one copyright line for every app built from this repo, matching the LICENSE and the
    /// assembly's Copyright field. It names the COLLECTIVE, never a person: rights arise with the
    /// actual people either way, and a repo that ships four tools still has one body of work - so
    /// this is repo-level, not per-app. One year, never a range (a range is upkeep nobody does).
    /// Keep the wording in step with LICENSE and Directory.Build.props; the "(c)" spelling is the
    /// UI form of the same notice.
    /// </summary>
    public const string CopyrightLine = "(c) 2026 JustPlay contributors";

    /// <summary>True when <see cref="By"/> should render as a link rather than as plain text.</summary>
    public bool HasByLink => !string.IsNullOrWhiteSpace(ByUrl);
}
