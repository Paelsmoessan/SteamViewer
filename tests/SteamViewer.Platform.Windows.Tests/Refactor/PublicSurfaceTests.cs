using SteamViewer.Platform.Windows.Elevation;
using SteamViewer.Platform.Windows.ScreenCapture;
using SteamViewer.Tests.Shared;

namespace SteamViewer.Platform.Windows.Tests.Refactor;

/// <summary>
/// Snapshot tests for the public surface of refactor-target types in
/// SteamViewer.Platform.Windows. First run creates baselines under
/// Refactor/baselines/*.surface.txt; subsequent runs fail if the surface drifts.
/// Set REGEN_BASELINES=1 to intentionally regenerate after an approved API change.
/// </summary>
public class PublicSurfaceTests
{
    [Fact]
    public void SystemHelperServer_PublicSurface_MatchesBaseline()
    {
        var actual = ApiSurface.Describe(typeof(SystemHelperServer));
        CharacterizationTestBase.AssertMatchesBaseline(
            actual,
            CharacterizationTestBase.FindBaselinesDir(),
            "SystemHelperServer.surface.txt");
    }

    [Fact]
    public void DxgiScreenCapture_PublicSurface_MatchesBaseline()
    {
        var actual = ApiSurface.Describe(typeof(DxgiScreenCapture));
        CharacterizationTestBase.AssertMatchesBaseline(
            actual,
            CharacterizationTestBase.FindBaselinesDir(),
            "DxgiScreenCapture.surface.txt");
    }
}
