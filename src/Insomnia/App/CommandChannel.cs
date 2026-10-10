using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Insomnia.App;

/// <summary>
/// A named pipe the running app listens on, so the command line (and any
/// script or agent running as you) can drive it:
/// <c>Insomnia.exe --start 4h</c>, <c>--extend 30m</c>, <c>--end</c>,
/// <c>--status</c>, <c>--quit</c>. Only the current user may connect.
/// </summary>
internal static class CommandChannel
{
    public static string PipeName =>
        "Insomnia-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    /// <summary>Sends one line, returns the reply, or null when no app is listening.</summary>
    public static string? Send(string line, TimeSpan timeout)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            c.Connect((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            c.Write(bytes);
            c.Flush();
            using var r = new StreamReader(c, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Serves until cancelled. Each line is handed to <paramref name="handle"/> on the UI thread.</summary>
    public static async Task Serve(Func<string, Task<string>> handle, SynchronizationContext ui, CancellationToken ct)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
            }
            catch (IOException) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
            catch (UnauthorizedAccessException) { return; }
            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var line = await ReadLine(server, ct).ConfigureAwait(false);
                    var tcs = new TaskCompletionSource<string>();
                    ui.Post(async _ =>
                    {
                        try { tcs.SetResult(await handle(line)); }
                        catch (Exception e) { tcs.SetResult("error: " + e.Message); }
                    }, null);
                    var reply = await tcs.Task.ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(reply.EndsWith('\n') ? reply : reply + "\n");
                    await server.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await server.FlushAsync(ct).ConfigureAwait(false);
                    server.WaitForPipeDrain();
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { }
            }
        }
    }

    static async Task<string> ReadLine(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 4096 && await s.ReadAsync(one, ct).ConfigureAwait(false) == 1)
        {
            if (one[0] == (byte)'\n') break;
            buf.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buf.ToArray()).Trim();
    }
}
