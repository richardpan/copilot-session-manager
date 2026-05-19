using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.5 (#198): in-process plug-in point for contributing extra sections to
/// every generated <c>SESSION-DOCS.html</c>.
/// </summary>
/// <remarks>
/// Providers are resolved from DI and called in parallel by
/// <see cref="SessionDocsService"/> during HTML generation. Each provider may
/// return zero or more <see cref="DocsSection"/> objects; sections are slotted
/// into the page by their <see cref="DocsSection.Placement"/>. Failures and
/// timeouts (default 2 s per provider) are logged and skipped — they never
/// block the render.
/// <para>
/// Use this when the same section makes sense for every session (e.g. git
/// status, cost rollup, recent files). Use <c>SESSION-DOCS.&lt;name&gt;.{md|html}</c>
/// fragments (#196) for per-session content.
/// </para>
/// </remarks>
public interface ISessionDocsSectionProvider
{
    /// <summary>Stable identifier. Used in log messages and as the tie-break in section ordering.</summary>
    string Name { get; }

    /// <summary>
    /// Sort order within a single <see cref="SectionPlacement"/>; lower values
    /// render first. Sections from the same provider keep their returned order.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Produce zero or more sections for the given session. Implementations
    /// must not throw; if they do, the failure is logged and the provider's
    /// contribution is skipped for this render.
    /// </summary>
    ValueTask<IReadOnlyList<DocsSection>> GetSectionsAsync(Session session, CancellationToken cancellationToken);
}

/// <summary>
/// V1.5 (#198): a single section contributed by an <see cref="ISessionDocsSectionProvider"/>.
/// </summary>
/// <param name="Anchor">
/// Stable id used for the <c>&lt;section id=…&gt;</c> attribute and the TOC link.
/// Must be unique within the rendered page; the host sanitises the value with
/// <see cref="SessionDocsService.SlugifyAnchor"/> before use.
/// </param>
/// <param name="Title">Heading text shown above the section and in the TOC.</param>
/// <param name="HtmlBody">
/// Already-rendered HTML for the section body (everything inside the
/// <c>&lt;section&gt;</c> wrapper, after the &lt;h2&gt;). The host does not escape
/// this value — providers are responsible for safe encoding.
/// </param>
/// <param name="Placement">Slot in the page where the section is inserted. Defaults to <see cref="SectionPlacement.End"/>.</param>
/// <param name="Subtitle">Optional muted badge rendered next to the heading (e.g. provider name).</param>
public sealed record DocsSection(
    string Anchor,
    string Title,
    string HtmlBody,
    SectionPlacement Placement = SectionPlacement.End,
    string? Subtitle = null);

/// <summary>
/// V1.5 (#198): named slots in the generated <c>SESSION-DOCS.html</c> where
/// provider sections can be inserted. Built-in sections occupy fixed
/// positions: Overview → Fragments → Mockups → Files → Plan → Checkpoints.
/// </summary>
public enum SectionPlacement
{
    /// <summary>Between Overview and Fragments.</summary>
    AfterOverview,

    /// <summary>Between Fragments and Mockups.</summary>
    AfterFragments,

    /// <summary>Between Mockups and Files.</summary>
    AfterMockups,

    /// <summary>Between Files and Plan.</summary>
    AfterFiles,

    /// <summary>Between Plan and Checkpoints.</summary>
    AfterPlan,

    /// <summary>After all built-in sections (the bottom of the page).</summary>
    End,
}
