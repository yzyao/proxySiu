using System.Buffers;
using System.Text;

namespace ProxySiu.Api.Services;

public static class LimitedHttpContent
{
    public const int MaximumLookupBytes = 64 * 1024;

    // Enforce the limit on streamed/decompressed bytes as Content-Length may be absent or compressed.
    public static async Task<string> ReadStringAsync(HttpContent content, int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidOperationException("响应内容超过大小限制。");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(16 * 1024, (long)maximumBytes + 1));
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, maximumBytes - target.Length + 1)), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (target.Length + read > maximumBytes)
                {
                    throw new InvalidOperationException("响应内容超过大小限制。");
                }

                target.Write(buffer, 0, read);
            }

            return Decode(content, target);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Decode(HttpContent content, MemoryStream target)
    {
        var bytes = target.GetBuffer().AsSpan(0, (int)target.Length);
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException("响应字符编码无效。", exception);
            }
        }
        else if (bytes.StartsWith(Encoding.UTF8.Preamble)) encoding = Encoding.UTF8;
        else if (bytes.StartsWith(Encoding.UTF32.Preamble)) encoding = Encoding.UTF32;
        else if (bytes.StartsWith(Encoding.Unicode.Preamble)) encoding = Encoding.Unicode;
        else if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble)) encoding = Encoding.BigEndianUnicode;
        else encoding = Encoding.UTF8;

        if (bytes.StartsWith(encoding.Preamble)) bytes = bytes[encoding.Preamble.Length..];
        return encoding.GetString(bytes);
    }
}
