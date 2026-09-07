using System.Net.Http;
using System.Text;

namespace NinjaPricer;

internal static class BoundedHttp
{
    internal static async Task<string> GetStringAsync(HttpClient client, string url, int maxBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException($"HTTP response exceeds {maxBytes} bytes.");

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 81920));
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"HTTP response exceeds {maxBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}
