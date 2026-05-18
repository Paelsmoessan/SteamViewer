using SteamViewer.App.Services.Models;
using SteamViewer.Tests.Shared;

namespace SteamViewer.App.Tests.Refactor;

/// <summary>
/// Snapshot tests for the public surface of refactor-target types in SteamViewer.App.
/// First run creates baselines under Refactor/baselines/*.surface.txt; subsequent runs
/// fail if the surface drifts. Set REGEN_BASELINES=1 to intentionally regenerate after
/// an approved API change.
/// </summary>
public class PublicSurfaceTests
{
    [Fact]
    public void HostSession_PublicSurface_MatchesBaseline()
    {
        var actual = ApiSurface.Describe(typeof(HostSession));
        CharacterizationTestBase.AssertMatchesBaseline(
            actual,
            CharacterizationTestBase.FindBaselinesDir(),
            "HostSession.surface.txt");
    }

    [Fact]
    public void ViewerSession_PublicSurface_MatchesBaseline()
    {
        var actual = ApiSurface.Describe(typeof(ViewerSession));
        CharacterizationTestBase.AssertMatchesBaseline(
            actual,
            CharacterizationTestBase.FindBaselinesDir(),
            "ViewerSession.surface.txt");
    }
}
