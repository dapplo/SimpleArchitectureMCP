using System.Net;
using DevToolsMcp.Server.Core.Telemetry;

namespace DevToolsMcp.Server.Core.Http;

/// <summary>
/// Records downstream response payload sizes per module so they show up in the Aspire dashboard. The body is not
/// buffered: with a Content-Length the size is recorded right away, otherwise (chunked or decompressed responses)
/// the bytes are counted while the caller streams them.
/// </summary>
public sealed class PayloadMetricsHandler(string module) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var method = request.Method.Method;
        var status = (int)response.StatusCode;

        if (response.Content.Headers.ContentLength is { } length)
        {
            DevToolsTelemetry.RecordResponseSize(module, method, status, length);
        }
        else
        {
            response.Content = new CountingContent(response.Content, bytes => DevToolsTelemetry.RecordResponseSize(module, method, status, bytes));
        }

        return response;
    }

    /// <summary>Passes the inner content through unchanged and reports the number of bytes read once.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action<long> _report;

        public CountingContent(HttpContent inner, Action<long> report)
        {
            _inner = inner;
            _report = report;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new CountingStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _report);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Read-only pass-through that reports the byte count at end of stream (or on dispose, if read partially).</summary>
    private sealed class CountingStream(Stream inner, Action<long> report) : Stream
    {
        private long _count;
        private bool _reported;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Count(int read)
        {
            _count += read;
            if (read == 0)
            {
                Report();
            }

            return read;
        }

        private void Report()
        {
            if (!_reported)
            {
                _reported = true;
                report(_count);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Report();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
