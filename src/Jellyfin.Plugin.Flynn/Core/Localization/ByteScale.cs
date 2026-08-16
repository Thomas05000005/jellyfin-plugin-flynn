namespace Jellyfin.Plugin.Flynn.Core.Localization;

/// <summary>
/// Scales a byte count to the unit a person would read it in, without formatting it.
/// <para>
/// Returning a number and a unit key rather than a string is the whole point. Formatting here would
/// use the server process's culture, so a French admin would be shown "4.2 TB" with a decimal point
/// because that is the locale a background service happened to start under. Kept as a number, it
/// reaches the reader's culture intact and comes out "4,2".
/// </para>
/// </summary>
internal static class ByteScale
{
    /// <summary>
    /// Scales a byte count.
    /// <para>
    /// Base 1024 with the short units everyone actually uses. Insisting on TiB would be more
    /// correct and would make the figure disagree with the admin's own file manager.
    /// </para>
    /// </summary>
    /// <param name="bytes">The count.</param>
    /// <returns>
    /// The scaled value, already rounded for display, and the translation key for its unit. One
    /// decimal from GB upward and none below: "512.0 MB" reads worse than "512 MB", while "4 TB"
    /// hides hundreds of gigabytes.
    /// </returns>
    internal static (double Value, string UnitKey) Of(long bytes)
    {
        if (bytes <= 0)
        {
            return (0, StringKeys.UnitBytes);
        }

        string[] unitKeys =
        [
            StringKeys.UnitBytes, StringKeys.UnitKilobytes, StringKeys.UnitMegabytes,
            StringKeys.UnitGigabytes, StringKeys.UnitTerabytes, StringKeys.UnitPetabytes,
        ];
        var order = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), unitKeys.Length - 1);
        var size = bytes / Math.Pow(1024, order);

        return (Math.Round(size, order >= 3 ? 1 : 0), unitKeys[order]);
    }
}
