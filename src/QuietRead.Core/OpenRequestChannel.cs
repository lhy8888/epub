using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuietRead.Core;

// One bounded request at a time, restricted by the OS to the current user.
// This channel can only ask to activate a window or open an absolute EPUB path.
public sealed class OpenRequestChannel : IDisposable
{
    public const int MaxMessageBytes = 8192;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private readonly string _name;
    private readonly Action<string?> _receive;

    public OpenRequestChannel(string directory, Action<string?> receive)
    {
        _name = PipeName(directory); _receive = receive;
        _listener = ListenAsync();
    }

    public static string PipeName(string directory)
    {
        string scope = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) scope = scope.ToUpperInvariant();
        return "QuietRead-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + Environment.UserName)))[..32];
    }

    public static bool IsValidPath(string? path) => path is null || path.Length is > 0 and <= 2048 &&
        !path.Any(char.IsControl) && !path.StartsWith("\\\\", StringComparison.Ordinal) &&
        Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> TryForwardAsync(string directory, string? path, CancellationToken token = default)
    {
        if (!IsValidPath(path)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Request(1, path));
            if (bytes.Length > MaxMessageBytes) return false;
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            byte[] reply = new byte[1];
            await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            return reply[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
        { return false; }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxMessageBytes) continue;
                byte[] bytes = new byte[length];
                await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                Request? request = JsonSerializer.Deserialize<Request>(bytes, new JsonSerializerOptions { MaxDepth = 4 });
                bool valid = request is { Version: 1 } && IsValidPath(request.Path);
                if (valid) _receive(request!.Path);
                await pipe.WriteAsync(new byte[] { valid ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
            {
                if (!_stop.IsCancellationRequested) await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Observe shutdown without blocking the UI thread or disposing a token
        // source while an asynchronous stream still has a registered callback.
        _ = _listener.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record Request(int Version, string? Path);
}
