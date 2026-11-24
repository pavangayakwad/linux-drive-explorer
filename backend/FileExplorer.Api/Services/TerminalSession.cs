using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FileExplorer.Api.Services;

/// <summary>
/// Bridges one browser WebSocket to an interactive shell running on a real pseudo-terminal.
/// .NET has no PTY API, so the shell is started under util-linux's `script`, which allocates the pty and
/// relays bytes over stdin/stdout. The shell itself is entered into the host's namespaces via
/// `nsenter --target 1` (same trick as HostMountService) so commands act on the real host, falling back
/// to a plain local shell when nsenter isn't available.
/// Protocol: frames from the client are raw keystrokes, except a text frame beginning with '{' that parses
/// as {"type":"resize","cols":N,"rows":N}. Server -> client frames are raw terminal output.
/// </summary>
public static class TerminalSession
{
    private const string ShellFallback = "exec bash -i 2>/dev/null || exec sh -i";

    public static async Task RunAsync(WebSocket socket, string physicalDir, string hostDir, int cols, int rows, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            await SendTextAsync(socket, "\r\nThe terminal is only available when the API runs on Linux.\r\n", ct);
            await CloseAsync(socket);
            return;
        }

        var ttyFile = Path.Combine(Path.GetTempPath(), $"fx-tty-{Guid.NewGuid():N}");
        var useHost = FindOnPath("nsenter");
        var shellCmd = useHost
            // --wd= would be opened in the container's filesystem (before entering the host mount namespace),
            // where the host path doesn't exist; so cd from inside the host namespaces instead.
            ? $"nsenter --target 1 --mount --uts --ipc --net --pid -- sh -c {Quote($"cd {Quote(hostDir)} 2>/dev/null; {ShellFallback}")}"
            : $"cd {Quote(physicalDir)} && exec sh -c {Quote(ShellFallback)}";
        // Record the pty slave path (for later resizes) and set the initial size before the shell starts.
        var inner = $"tty > {Quote(ttyFile)}; stty cols {cols} rows {rows}; export TERM=xterm-256color; {shellCmd}";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "script",
                ArgumentList = { "-qfc", inner, "/dev/null" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            await SendTextAsync(socket, "\r\nCould not start a terminal: the 'script' utility is missing.\r\n", ct);
            await CloseAsync(socket);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        async Task PumpAsync(Stream stream)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, CancellationToken.None)) > 0)
                {
                    if (socket.State != WebSocketState.Open) break;
                    await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
        }

        var outTask = PumpAsync(process.StandardOutput.BaseStream);
        var errTask = PumpAsync(process.StandardError.BaseStream);

        // Shell exited (e.g. the user typed `exit`): flush remaining output, then stop the receive loop so the
        // socket closes and the UI can dismiss itself.
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            linked.Cancel();
        });

        try
        {
            var buffer = new byte[8192];
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                var length = result.Count;
                var type = result.MessageType;
                while (!result.EndOfMessage)
                {
                    // Oversized frames are not expected (keystrokes / paste chunks); drain and ignore the rest.
                    result = await socket.ReceiveAsync(buffer, token);
                }

                if (type == WebSocketMessageType.Text && length > 0 && buffer[0] == (byte)'{'
                    && TryParseResize(buffer.AsSpan(0, length), out var c, out var r))
                {
                    await ResizeAsync(ttyFile, c, r);
                    continue;
                }

                await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, length), token);
                await process.StandardInput.BaseStream.FlushAsync(token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    // Closing stdin makes `script` hang up the shell's session; kill is the backstop.
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1500)) process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            try { File.Delete(ttyFile); } catch (IOException) { }
            await CloseAsync(socket);
        }
    }

    private static bool TryParseResize(ReadOnlySpan<byte> payload, out int cols, out int rows)
    {
        cols = rows = 0;
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "resize") return false;
            cols = Math.Clamp(root.GetProperty("cols").GetInt32(), 2, 1000);
            rows = Math.Clamp(root.GetProperty("rows").GetInt32(), 2, 1000);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static async Task ResizeAsync(string ttyFile, int cols, int rows)
    {
        try
        {
            var tty = (await File.ReadAllTextAsync(ttyFile)).Trim();
            if (!tty.StartsWith("/dev/")) return;
            using var stty = Process.Start(new ProcessStartInfo
            {
                FileName = "stty",
                ArgumentList = { "-F", tty, "cols", cols.ToString(), "rows", rows.ToString() },
                UseShellExecute = false,
            });
            if (stty is not null) await stty.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static bool FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, exe)));

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, ct);

    private static async Task CloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "exit", CancellationToken.None);
            }
        }
        catch (WebSocketException) { }
    }
}
