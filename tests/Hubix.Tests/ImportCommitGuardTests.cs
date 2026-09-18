using Hubix.Web.Api;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// The promise that a commit applies the workbook somebody actually read.
/// </summary>
/// <remarks>
/// The file is not stashed between preview and commit — it is uploaded again, re-read and
/// re-planned, which is the safer half of the design. What ties the two together is the hash the
/// preview handed out, and the check used to be skipped entirely when the field was missing: a
/// caller that simply left it off could commit a workbook nobody had reviewed (issue #38).
/// <para>
/// A unit test rather than an integration one because this lives in <c>Hubix.Web</c>, which
/// <c>Hubix.IntegrationTests</c> deliberately does not reference.
/// </para>
/// </remarks>
public class ImportCommitGuardTests
{
    private const string Previewed = "0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    [Fact]
    public void The_file_that_was_previewed_may_be_committed()
    {
        ImportApi.RefuseUnreviewed(Previewed, Previewed).ShouldBeNull();
    }

    [Fact]
    public void A_different_file_is_refused()
    {
        ImportApi.RefuseUnreviewed(Previewed, "f9e8d7c6b5a4039281706f5e4d3c2b1a" + "0a1b2c3d4e5f6071")
            .ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_commit_that_names_no_preview_is_refused(string? claimed)
    {
        // The whole of this issue. Omitting the field used to skip the comparison, so the
        // guarantee held only for clients that chose to ask for it.
        ImportApi.RefuseUnreviewed(claimed, Previewed).ShouldNotBeNull();
    }

    [Fact]
    public void A_refusal_does_not_say_which_way_it_failed()
    {
        // One answer for both, because there is one thing to do about it: upload the file
        // again and read the changes.
        ImportApi.RefuseUnreviewed(null, Previewed)
            .ShouldBe(ImportApi.RefuseUnreviewed("something else", Previewed));
    }
}
