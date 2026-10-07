using System.IO;
using System.Text.Json;

namespace Nyx.App;

internal static class LaunchMessageCodec
{
    internal const int MaximumMessageBytes = 1024 * 1024;
    internal const int MaximumPathCount = 4096;
    private const int MaximumPathLength = 32767;

    internal static byte[] Encode(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Validate(paths);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ForwardedLaunch(paths));
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The launch selection is too large to forward.");
        }

        return payload;
    }

    internal static async Task<ForwardedLaunch?> ReadAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var payload = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (payload.Length + count > MaximumMessageBytes)
            {
                throw new InvalidDataException("The launch message exceeded its size limit.");
            }

            payload.Write(buffer, 0, count);
        }

        var launch = JsonSerializer.Deserialize<ForwardedLaunch>(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        if (launch is not null)
        {
            Validate(launch.Paths);
        }

        return launch;
    }

    private static void Validate(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count > MaximumPathCount)
        {
            throw new InvalidDataException("The launch message contained an invalid path list.");
        }

        long characters = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)
                || path.Length > MaximumPathLength
                || path.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The launch message contained an invalid path.");
            }

            characters += path.Length;
            if (characters > MaximumMessageBytes)
            {
                throw new InvalidDataException("The launch selection is too large to forward.");
            }
        }
    }
}
