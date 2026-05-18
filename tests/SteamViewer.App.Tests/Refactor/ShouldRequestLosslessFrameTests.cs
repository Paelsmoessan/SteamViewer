using SteamViewer.App.Services.Models;

namespace SteamViewer.App.Tests.Refactor;

/// <summary>
/// Characterization test for the Stage 2 R2 extract
/// <see cref="ViewerSession.ShouldRequestLosslessFrame"/>.
/// Truth table covers every input branch + the 150ms threshold boundary.
/// </summary>
public class ShouldRequestLosslessFrameTests
{
    [Theory]
    // Happy path: idle past threshold, no lossless in flight, not on SD -> request.
    [InlineData(false, false, false, 151L, true)]
    [InlineData(false, false, false, 1000L, true)]
    // Idle long enough but lossless already active -> skip.
    [InlineData(true,  false, false, 1000L, false)]
    // Idle long enough but a request is already pending -> skip.
    [InlineData(false, true,  false, 1000L, false)]
    // Idle long enough but Secure Desktop active -> skip (SD has its own path).
    [InlineData(false, false, true,  1000L, false)]
    // Boundary: 150ms is NOT past threshold (strict > 150).
    [InlineData(false, false, false, 150L,  false)]
    // Boundary: 149ms below threshold.
    [InlineData(false, false, false, 149L,  false)]
    // Edge: 0ms (input just happened) below threshold.
    [InlineData(false, false, false, 0L,    false)]
    // All blockers set -> skip.
    [InlineData(true,  true,  true,  1000L, false)]
    public void Logic_MatchesPreExtractInlineBehavior(
        bool losslessActive, bool requestPending, bool secureDeskActive, long elapsedMs, bool expected)
    {
        var actual = ViewerSession.ShouldRequestLosslessFrame(
            losslessActive, requestPending, secureDeskActive, elapsedMs);
        Assert.Equal(expected, actual);
    }
}
