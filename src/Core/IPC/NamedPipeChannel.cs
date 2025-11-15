using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ClopWindows.Core.IPC;

public sealed class NamedPipeChannel : IAsyncDisposable, IDisposable
{
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public NamedPipeChannel(string pipeName)
    {
        _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
    }

    public void StartListening(Func<byte[], ValueTask<byte[]?>> handler, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenerTask = Task.Run(() => ListenAsync(handler, _cts.Token), CancellationToken.None);
    }

    private async Task ListenAsync(Func<byte[], ValueTask<byte[]?>> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                var request = await ReadMessageAsync(server, token).ConfigureAwait(false) ?? Array.Empty<byte>();
                var response = await handler(request).ConfigureAwait(false);
                if (response != null)
                {
                    await WriteMessageAsync(server, response, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // swallow errors to keep loop alive; callers can hook logging higher up
            }
        }
    }

    public async Task<byte[]?> SendAndWaitAsync(byte[]? payload, TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null, CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var timeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

        await WriteMessageAsync(client, payload ?? Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        if (responseTimeout == TimeSpan.Zero)
        {
            return null;
        }

        if (responseTimeout is { } waitTimeout)
        {
            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseCts.CancelAfter(waitTimeout);
            return await ReadMessageAsync(client, responseCts.Token).ConfigureAwait(false);
        }

        return await ReadMessageAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAndForgetAsync(byte[]? payload, TimeSpan? connectTimeout = null, CancellationToken cancellationToken = default)
        => SendAndWaitAsync(payload, connectTimeout, TimeSpan.Zero, cancellationToken);

    private static async Task<byte[]?> ReadMessageAsync(PipeStream stream, CancellationToken token)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await ReadExactAsync(stream, lengthBuffer, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteMessageAsync(PipeStream stream, byte[] payload, CancellationToken token)
    {
        var lengthBuffer = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthBuffer, 0, lengthBuffer.Length, token).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(PipeStream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("Pipe closed before message could be read.");
            }
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignored
            }
        }
        _cts?.Dispose();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_listenerTask != null)
        {
            try
            {
                _listenerTask.Wait();
            }
            catch (AggregateException)
            {
                // ignored
            }
        }
        _cts?.Dispose();
    }
}
