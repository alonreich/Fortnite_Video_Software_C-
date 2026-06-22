using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.Core.Media;

public class MpvIpcClient : IDisposable
{
    private Process? _mpvProcess;
    private NamedPipeClientStream? _pipeClient;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource _cts = new();
    
    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsEof { get; private set; }

    public event Action<double>? TimePosChanged;
    public event Action? SeekCompleted;

    public async Task StartAsync(nint hwnd, string mpvPath)
    {
        string pipeName = $"mpv-avalonia-pipe-{Guid.NewGuid():N}";
        string pipePath = $@"\\.\pipe\{pipeName}";

        _mpvProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = mpvPath,
                Arguments = $"--idle --wid={hwnd.ToInt64()} --input-ipc-server={pipePath} --keep-open=yes --hwdec=auto --input-default-bindings=no",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _mpvProcess.Start();

        // Connect to pipe
        _pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        
        // Retry connection
        for (int i = 0; i < 20; i++)
        {
            try
            {
                await _pipeClient.ConnectAsync(200);
                break;
            }
            catch (TimeoutException)
            {
                if (i == 19) throw new Exception("Could not connect to MPV IPC pipe.");
            }
        }

        _writer = new StreamWriter(_pipeClient, new UTF8Encoding(false)) { AutoFlush = true };
        _reader = new StreamReader(_pipeClient, new UTF8Encoding(false));

        _ = Task.Run(ListenLoop, _cts.Token);

        // Observe properties
        await SendCommandAsync("observe_property", 1, "time-pos");
        await SendCommandAsync("observe_property", 2, "duration");
        await SendCommandAsync("observe_property", 3, "pause");
        await SendCommandAsync("observe_property", 4, "eof-reached");
    }

    private async Task ListenLoop()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested && _reader != null)
            {
                string? line = await _reader.ReadLineAsync(_cts.Token);
                if (string.IsNullOrEmpty(line)) break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("event", out var eventProp))
                    {
                        string eventName = eventProp.GetString() ?? "";
                        if (eventName == "property-change")
                        {
                            string name = root.GetProperty("name").GetString() ?? "";
                            if (root.TryGetProperty("data", out var dataProp))
                            {
                                if (name == "time-pos" && dataProp.ValueKind == JsonValueKind.Number)
                                {
                                    CurrentTime = dataProp.GetDouble();
                                    TimePosChanged?.Invoke(CurrentTime);
                                }
                                else if (name == "duration" && dataProp.ValueKind == JsonValueKind.Number)
                                {
                                    Duration = dataProp.GetDouble();
                                }
                                else if (name == "pause" && (dataProp.ValueKind == JsonValueKind.True || dataProp.ValueKind == JsonValueKind.False))
                                {
                                    IsPaused = dataProp.GetBoolean();
                                }
                                else if (name == "eof-reached" && (dataProp.ValueKind == JsonValueKind.True || dataProp.ValueKind == JsonValueKind.False))
                                {
                                    IsEof = dataProp.GetBoolean();
                                }
                            }
                        }
                        else if (eventName == "seek")
                        {
                            // MPV just started seeking, do nothing or track
                        }
                        else if (eventName == "playback-restart")
                        {
                            // Seek usually completes with playback-restart
                            SeekCompleted?.Invoke();
                        }
                    }
                }
                catch { } // Ignore parse errors
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CoreLogger.Fail("IPC", $"MPV Listen loop failed: {ex.Message}");
        }
    }

    public async Task SendCommandAsync(params object[] args)
    {
        if (_writer == null) return;
        
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"command\":[");
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is string s)
            {
                sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            else if (a is double d)
            {
                sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (a is int num)
            {
                sb.Append(num);
            }
            if (i < args.Length - 1) sb.Append(',');
        }
        sb.Append("]}");
        
        string json = sb.ToString();
        
        try
        {
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();
        }
        catch { }
    }

    public async Task LoadFileAsync(string path)
    {
        await SendCommandAsync("loadfile", path.Replace("\\", "\\\\"));
        await SendCommandAsync("set", "pause", "no");
    }

    public async Task SetPropertyAsync(string name, string value)
    {
        await SendCommandAsync("set", name, value);
    }

    public async Task SetPropertyDoubleAsync(string name, double value)
    {
        await SendCommandAsync("set", name, value);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _pipeClient?.Dispose();
        try
        {
            if (_mpvProcess != null && !_mpvProcess.HasExited)
            {
                _mpvProcess.Kill(true);
            }
        }
        catch { }
        _mpvProcess?.Dispose();
    }
}
