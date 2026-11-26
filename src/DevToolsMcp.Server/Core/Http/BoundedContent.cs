namespace DevToolsMcp.Server.Core.Http;

public static class BoundedContent
{
    /// <summary>
    /// Reads a response body of at most <paramref name="maxBytes"/>. Content-Length is checked first, but a chunked or
    /// compressed response has none, so the limit is also enforced while reading.
    /// </summary>
    public static async Task<byte[]> ReadAsBytesAsync(HttpContent content, long maxBytes, Func<Exception> tooLarge, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw tooLarge();
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw tooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
