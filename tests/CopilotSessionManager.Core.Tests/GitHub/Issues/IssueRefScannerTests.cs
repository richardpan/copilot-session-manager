using System.Linq;
using CopilotSessionManager.Core.GitHub.Issues;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Issues;

public class IssueRefScannerTests
{
    private const string DefaultRepo = "octo/widgets";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    public void Scan_NullOrBlank_ReturnsEmpty(string? input)
    {
        IssueRefScanner.Scan(input, DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_BareNumberWithDefault_ResolvesToDefaultRepo()
    {
        var refs = IssueRefScanner.Scan("Closes #42 in this PR.", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("octo/widgets");
        refs[0].Number.Should().Be(42);
    }

    [Fact]
    public void Scan_BareNumberWithoutDefault_ReturnsEmpty()
    {
        var refs = IssueRefScanner.Scan("Closes #42 in this PR.", defaultOwnerRepo: null);

        refs.Should().BeEmpty();
    }

    [Fact]
    public void Scan_BareNumberWithBlankDefault_ReturnsEmpty()
    {
        var refs = IssueRefScanner.Scan("Closes #42.", "   ");

        refs.Should().BeEmpty();
    }

    [Fact]
    public void Scan_OwnerRepoQualified_ReturnsCrossRepoRef()
    {
        var refs = IssueRefScanner.Scan("See acme/tools#7 for context.", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("acme/tools");
        refs[0].Number.Should().Be(7);
    }

    [Fact]
    public void Scan_OwnerRepoQualified_NoDefault_StillResolves()
    {
        var refs = IssueRefScanner.Scan("See acme/tools#7.", defaultOwnerRepo: null);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("acme/tools");
        refs[0].Number.Should().Be(7);
    }

    [Fact]
    public void Scan_MultipleRefs_AreReturnedInDocumentOrder()
    {
        var md = "First #1, then acme/tools#2, then #3.";

        var refs = IssueRefScanner.Scan(md, DefaultRepo);

        refs.Should().HaveCount(3);
        refs[0].Number.Should().Be(1);
        refs[1].OwnerRepo.Should().Be("acme/tools");
        refs[1].Number.Should().Be(2);
        refs[2].Number.Should().Be(3);
    }

    [Fact]
    public void Scan_DuplicateRefs_AreCollapsed()
    {
        var md = "First #1 then #1 then octo/widgets#1.";

        var refs = IssueRefScanner.Scan(md, DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].Number.Should().Be(1);
    }

    [Theory]
    [InlineData("# Heading #42")]
    [InlineData("## Section #42")]
    [InlineData("### Subsection #42")]
    [InlineData("#### Heading 4 #42")]
    [InlineData("##### Heading 5 #42")]
    [InlineData("###### Heading 6 #42")]
    public void Scan_HeadingMarkers_AreIgnored(string md)
    {
        // The bare-#NN regex disallows '#' immediately preceding another '#',
        // so '##' and longer markers can't form a ref. Trailing '#42' on the
        // same line, when separated by whitespace, IS a valid ref.
        var refs = IssueRefScanner.Scan(md, DefaultRepo);
        refs.Should().ContainSingle().Which.Number.Should().Be(42);
    }

    [Fact]
    public void Scan_HeadingOnlyNoTrailingRef_ReturnsEmpty()
    {
        IssueRefScanner.Scan("## Section header", DefaultRepo).Should().BeEmpty();
        IssueRefScanner.Scan("### Another", DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_UrlFragment_IsIgnored()
    {
        var md = "See https://example.com/page#42 for context.";

        IssueRefScanner.Scan(md, DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_GitHubIssueUrl_ResolvesViaUrl()
    {
        var md = "[#42](https://github.com/acme/tools/issues/42)";

        var refs = IssueRefScanner.Scan(md, DefaultRepo);

        // The URL form takes precedence over the bare #42 on the same span,
        // and dedup collapses the two readings into one ref.
        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("acme/tools");
        refs[0].Number.Should().Be(42);
    }

    [Fact]
    public void Scan_GitHubIssueUrl_OnItsOwn_ResolvesToUrlRepo()
    {
        var md = "Tracking https://github.com/acme/tools/issues/9 here.";

        var refs = IssueRefScanner.Scan(md, DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("acme/tools");
        refs[0].Number.Should().Be(9);
    }

    [Fact]
    public void Scan_InlineCode_IsIgnored()
    {
        var md = "Run `gh pr list #42` to see them.";

        IssueRefScanner.Scan(md, DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_FencedCodeBlock_IsIgnored()
    {
        var md = "Open issues:\n```\n# TODO #42\nfix #99\n```\nback to prose.";

        IssueRefScanner.Scan(md, DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_FencedCodeBlockWithLanguage_IsIgnored()
    {
        var md = "Snippet:\n```bash\ngh issue view #42\n```\n";

        IssueRefScanner.Scan(md, DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_MixesValidAndIgnored_ReturnsOnlyValid()
    {
        var md = """
            ## Status

            Closes #1 and acme/tools#2.

            ```
            #999 ignored
            ```

            See `#88` (also ignored). Final: https://example.com/x#10 fragment, not a ref.

            But the issue URL https://github.com/octo/widgets/issues/3 IS one.
            """;

        var refs = IssueRefScanner.Scan(md, DefaultRepo);

        refs.Select(r => r.ToString()).Should().Equal(
            "octo/widgets#1",
            "acme/tools#2",
            "octo/widgets#3");
    }

    [Fact]
    public void Scan_CapsAt50Refs()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 100; i++)
        {
            sb.Append("Ref #").Append(i).Append(' ');
        }

        var refs = IssueRefScanner.Scan(sb.ToString(), DefaultRepo);

        refs.Should().HaveCount(IssueRefScanner.MaxRefs);
        refs.Should().HaveCount(50);
        refs[0].Number.Should().Be(1);
        refs[49].Number.Should().Be(50);
    }

    [Fact]
    public void Scan_InvalidOwnerRepo_IsIgnored()
    {
        // Segment pattern requires a slash for owner/repo, and disallows
        // '-' at position 0 of either segment.
        var md = "/repo#42 and -bad/repo#7 and not_a_ref#1"; // not_a_ref has no slash

        var refs = IssueRefScanner.Scan(md, defaultOwnerRepo: null);

        refs.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ZeroNumber_IsIgnored()
    {
        var refs = IssueRefScanner.Scan("Bug #0 reported.", DefaultRepo);

        refs.Should().BeEmpty();
    }

    [Fact]
    public void Scan_NumberAfterLetter_IsNotMatched()
    {
        // Bare-ref regex disallows preceding letters/digits to avoid catching
        // version markers like 'v#42' or 'fix#42 (without space)'.
        IssueRefScanner.Scan("v#42 fix#7 abc#1", DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_NumberInsideWordBoundary_LeadingHashIsRequired()
    {
        // A literal "42" alone is not a ref.
        IssueRefScanner.Scan("Issue 42 reported", DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_OwnerRepoLowercased()
    {
        var refs = IssueRefScanner.Scan("see Acme/Tools#3", defaultOwnerRepo: null);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("acme/tools");
    }

    [Fact]
    public void Scan_BareRefAtStartOfString_Matches()
    {
        var refs = IssueRefScanner.Scan("#5 is the first ref", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].Number.Should().Be(5);
    }

    [Fact]
    public void Scan_BareRefAtEndOfString_Matches()
    {
        var refs = IssueRefScanner.Scan("Closes the long-running #5", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].Number.Should().Be(5);
    }

    [Fact]
    public void Scan_RefInsideParentheses_Matches()
    {
        var refs = IssueRefScanner.Scan("(see #5)", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].Number.Should().Be(5);
    }

    [Fact]
    public void Scan_PullRequestUrl_DoesNotMatchAsIssue()
    {
        // /pull/NN URLs are not issues; the URL regex is strict on /issues/
        // so the URL fragment scrubber blanks the # in the URL but #42 is
        // not present in raw form here, so we expect zero refs.
        var md = "PR https://github.com/acme/tools/pull/42 references nothing.";

        IssueRefScanner.Scan(md, DefaultRepo).Should().BeEmpty();
    }

    [Fact]
    public void Scan_BackticksAcrossLines_DoNotEatProse()
    {
        // An unclosed inline backtick on one line should not consume the
        // following line's refs.
        var md = "An unclosed `tick on one line\nbut closes #5 on the next.";

        var refs = IssueRefScanner.Scan(md, DefaultRepo);
        refs.Should().ContainSingle().Which.Number.Should().Be(5);
    }

    [Fact]
    public void Scan_RefImmediatelyBeforePunctuation_Matches()
    {
        var md = "Issues: #1, #2, and #3.";

        var refs = IssueRefScanner.Scan(md, DefaultRepo);

        refs.Should().HaveCount(3);
        refs.Select(r => r.Number).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void Scan_LargeNumber_ParsesCorrectly()
    {
        var refs = IssueRefScanner.Scan("Closes #2147483647.", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].Number.Should().Be(int.MaxValue);
    }

    [Fact]
    public void Scan_NumberOverflowsInt_IsIgnored()
    {
        var refs = IssueRefScanner.Scan("Closes #99999999999999.", DefaultRepo);

        refs.Should().BeEmpty();
    }

    [Fact]
    public void Scan_OwnerRepoWithDotsAndDashes_IsAccepted()
    {
        var md = "see octo-cat/my.repo-1#7";

        var refs = IssueRefScanner.Scan(md, defaultOwnerRepo: null);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("octo-cat/my.repo-1");
    }

    [Fact]
    public void Scan_OwnerRepoOverridesBareDuplicate()
    {
        // Bare and qualified refs to the same canonical issue collapse into one badge.
        var refs = IssueRefScanner.Scan("First #5, then octo/widgets#5 again.", DefaultRepo);

        refs.Should().ContainSingle();
        refs[0].OwnerRepo.Should().Be("octo/widgets");
        refs[0].Number.Should().Be(5);
    }
}
