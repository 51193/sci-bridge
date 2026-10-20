namespace SciBridge;

/// <summary>
/// The fixed update source. The repository is part of the product, not a
/// version: the release tag remains the only version authority.
/// </summary>
public static class UpdateConfig
{
    public const string Repository = "51193/sci-bridge";
    public const string LatestReleaseApi =
        "https://api.github.com/repos/" + Repository + "/releases/latest";

    public const string ChecksumsAssetName = "SHA256SUMS.txt";
    public const string ExeAssetSuffix = "-win11.exe";

    /// <summary>A single self-contained exe is ~110 MB; reject anything absurd.</summary>
    public const long MaxExeBytes = 200L * 1024 * 1024;
    public const long MaxChecksumsBytes = 1024L * 1024;
}
