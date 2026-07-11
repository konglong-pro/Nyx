using System.IO;

namespace Nyx.App;

internal static class PathInput
{
    internal static string NormalizeOrOriginal(string? path)
    {
        if (path is null)
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return path;
        }
    }
}
