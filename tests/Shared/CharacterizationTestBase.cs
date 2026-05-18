using System.Runtime.CompilerServices;

namespace SteamViewer.Tests.Shared;

/// <summary>
/// Helpers for the characterization-test pattern used to gate refactor commits.
/// A characterization test asserts that the observable behavior of a method/code
/// path matches a recorded baseline, both BEFORE and AFTER a refactor. Used as
/// the auto-mode gate's behavior-preservation check for R2/R3 method extracts.
/// </summary>
public static class CharacterizationTestBase
{
    /// <summary>
    /// Resolves the directory containing baseline assets for the calling test file.
    /// Tests place baselines alongside their .cs file under a 'baselines' subfolder;
    /// this helper finds that folder by source-file path at compile time.
    /// </summary>
    public static string FindBaselinesDir([CallerFilePath] string? callerFile = null)
    {
        if (callerFile is null) throw new InvalidOperationException("CallerFilePath not provided");
        var dir = Path.GetDirectoryName(callerFile);
        if (dir is null) throw new InvalidOperationException($"Cannot resolve directory for {callerFile}");
        return Path.Combine(dir, "baselines");
    }

    /// <summary>
    /// Compares actual content against a baseline file. On first run (or when
    /// REGEN_BASELINES=1 is set), writes the baseline and fails the test with a
    /// review-and-commit message. On subsequent runs, asserts exact match.
    /// </summary>
    public static void AssertMatchesBaseline(string actualContent, string baselineDir, string baselineFile)
    {
        Directory.CreateDirectory(baselineDir);
        var path = Path.Combine(baselineDir, baselineFile);
        var regen = Environment.GetEnvironmentVariable("REGEN_BASELINES") == "1";

        if (regen || !File.Exists(path))
        {
            File.WriteAllText(path, actualContent);
            var action = File.Exists(path) && !regen ? "created" : "regenerated";
            Xunit.Assert.Fail($"Baseline {action} at {path}.{Environment.NewLine}" +
                              $"Review the file, commit it, then re-run tests without REGEN_BASELINES.");
        }

        var expected = File.ReadAllText(path);
        Xunit.Assert.Equal(expected, actualContent);
    }
}
