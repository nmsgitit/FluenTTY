using System.IO.Pipes;
using System.Text.Json;

namespace FluenTTY.Services;

/// <summary>
/// One FluenTTY window per user (when enabled): a second launch hands its command line to the
/// running instance over a named pipe, which opens the requested sessions as new tabs.
/// Installed and portable copies use separate names so they don't capture each other.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static string Suffix => AppInfo.IsPortable ? ".Portable" : "";
    private static string MutexName => @"Local\FluenTTY.Instance" + Suffix;
    // Named pipes are machine-wide, so on a shared server another user could create this name first
    // and receive our command lines (including -pw passwords). Both ends use CurrentUserOnly: the
    // server's pipe only admits this user, and the client refuses a pipe owned by anyone else.
    // Security review M5 — see CLAUDE.md "Security invariants".
    public static string PipeName =>$"FluenTTY.Args{Suffix}.{Environment.UserName}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private const PipeOptions Secure = PipeOptions.CurrentUserOnly;

    private readonly Mutex _mutex;
    private readonly bool _owned;
    private CancellationTokenSource? _cts;

    public bool IsFirstInstance => _owned;

    public event Action<string[]>? ArgsReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out _owned);
    }

    public static bool SendToExisting(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, Secure);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(JsonSerializer.Serialize(args));
            writer.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"single-instance send failed: {ex.Message}");
            return false;
        }
    }

    public void StartListening()
    {
        if (!_owned) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | Secure);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(ct);
                    var args = string.IsNullOrEmpty(line) ? Array.Empty<string>() : JsonSerializer.Deserialize<string[]>(line) ?? Array.Empty<string>();
                    ArgsReceived?.Invoke(args);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Diagnostics.Log($"single-instance server error: {ex.Message}");
                    try { await Task.Delay(250, ct); } catch { break; }
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_owned) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
