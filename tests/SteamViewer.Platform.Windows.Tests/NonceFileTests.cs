using SteamViewer.Platform.Windows.Elevation;

namespace SteamViewer.Platform.Windows.Tests;

public class NonceFileTests
{
    [Fact]
    public void Write_ThenReadAndDelete_RoundTripsNonceAndCleansFile()
    {
        var adminPid = (uint)Random.Shared.Next(100000, 999999); // avoid colliding with real session
        var nonce = "k6eysXqTw4Q5YI5_JS76mEnHXBtsc6Q1NXLZX0ooYpI";
        var path = NonceFile.PathFor(adminPid);
        if (File.Exists(path)) File.Delete(path);

        NonceFile.Write(adminPid, nonce);
        Assert.True(File.Exists(path), "nonce file should exist after Write");

        var read = NonceFile.ReadAndDelete(adminPid);
        Assert.Equal(nonce, read);
        Assert.False(File.Exists(path), "nonce file should be deleted after ReadAndDelete");
    }

    [Fact]
    public void ReadAndDelete_MissingFile_ReturnsNull()
    {
        var adminPid = (uint)Random.Shared.Next(100000, 999999);
        var path = NonceFile.PathFor(adminPid);
        if (File.Exists(path)) File.Delete(path);

        Assert.Null(NonceFile.ReadAndDelete(adminPid));
    }

    [Fact]
    public void PathFor_DifferentPids_ProducesDistinctPaths()
    {
        Assert.NotEqual(NonceFile.PathFor(1234), NonceFile.PathFor(5678));
    }

    [Fact]
    public void Write_OverwritesPriorFileForSameAdminPid()
    {
        var adminPid = (uint)Random.Shared.Next(100000, 999999);
        NonceFile.Write(adminPid, "first");
        NonceFile.Write(adminPid, "second");

        var read = NonceFile.ReadAndDelete(adminPid);
        Assert.Equal("second", read);
    }
}
