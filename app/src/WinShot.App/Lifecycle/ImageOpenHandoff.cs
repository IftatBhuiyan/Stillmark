using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace WinShot.App.Lifecycle;

// Only the current Windows user can send file-open requests to this session's resident app.
internal static class ImageOpenHandoff
{
    private static string PipeName => $"Stillmark.Images.{WindowsIdentity.GetCurrent().User!.Value}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    internal static string? GetPath(IReadOnlyList<string> args) =>
        args.Count == 2 && args[0] == "--open" ? args[1] :
        args.Count == 1 && !args[0].StartsWith('-') ? args[0] : null;

    internal static async Task SendAsync(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        if (bytes.Length is 0 or > 32768) throw new IOException("The image path is too long.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        await client.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static async Task ListenAsync(Action<string> open, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                byte[] header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token);
                int length = BitConverter.ToInt32(header);
                if (length is <= 0 or > 32768) continue;
                byte[] data = new byte[length];
                await pipe.ReadExactlyAsync(data, timeout.Token);
                open(Encoding.UTF8.GetString(data));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { App.LogError(ex); }
        }
    }
}

