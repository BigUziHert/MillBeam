using System.IO.Ports;
using System.Text;

namespace GenmitsuControl.Core;

/// <summary>A byte-oriented GRBL serial connection with bounded blocking reads.</summary>
public sealed class SerialGrblTransport : IGrblTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _connectionState = new();
    private SerialPort? _port;
    private CancellationTokenSource? _readerCancellation;
    private Task? _reader;
    private int _faulted;
    private bool _disposed;

    public SerialGrblTransport(string portName, int baudRate = 115200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        _portName = portName;
        _baudRate = baudRate;
    }

    public event Action<string>? LineReceived;
    public event Action<Exception>? Faulted;
    public bool IsOpen
    {
        get { lock (_connectionState) return !_disposed && _faulted == 0 && _port?.IsOpen == true; }
    }

    public static string[] GetPortNames() => SerialPort.GetPortNames()
        .OrderBy(name => name.Length).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task OpenAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_port is not null)
                throw new InvalidOperationException("The serial transport is already open. Close it before reconnecting.");

            var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                ReadTimeout = 100,
                WriteTimeout = 1500,
                Encoding = Encoding.ASCII,
                NewLine = "\n"
            };
            try
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(port.Open, CancellationToken.None).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_connectionState)
                {
                    _readerCancellation = new CancellationTokenSource();
                    _port = port;
                    _faulted = 0;
                    var token = _readerCancellation.Token;
                    _reader = Task.Run(() => ReadLoop(port, token), CancellationToken.None);
                }
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        SerialPort? port = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_connectionState)
            {
                port = _port;
                if (_disposed || _faulted != 0 || port?.IsOpen != true)
                    throw new IOException("The serial port is disconnected.");
            }

            // SerialPort's byte overload preserves realtime bytes such as 0x85.
            // A bounded synchronous write avoids uncancellable BaseStream async reads/writes.
            await Task.Run(() => port.Write(data, 0, data.Length), CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (port is not null) ReportFault(ex, port);
            throw;
        }
        finally { _writes.Release(); }
    }

    public async Task CloseAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            SerialPort? port;
            CancellationTokenSource? cancellation;
            Task? reader;
            lock (_connectionState)
            {
                port = _port;
                cancellation = _readerCancellation;
                reader = _reader;
                _port = null;
                _readerCancellation = null;
                _reader = null;
            }
            cancellation?.Cancel();

            // Dispose releases the Windows handle and unblocks any outstanding read.
            // No write semaphore is acquired here: closing also interrupts a stalled write.
            try
            {
                if (port is not null)
                    await Task.Run(port.Dispose).ConfigureAwait(false);
            }
            finally
            {
                if (reader is not null)
                    await reader.ConfigureAwait(false);
                cancellation?.Dispose();
            }
        }
        finally { _lifecycle.Release(); }
    }

    private void ReadLoop(SerialPort port, CancellationToken ct)
    {
        var bytes = new byte[256];
        var line = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count;
                try { count = port.Read(bytes, 0, bytes.Length); }
                catch (TimeoutException) { continue; }

                for (var index = 0; index < count && !ct.IsCancellationRequested; index++)
                {
                    var value = bytes[index];
                    if (value is (byte)'\r' or (byte)'\n')
                    {
                        if (line.Length > 0)
                        {
                            var completeLine = line.ToString();
                            line.Clear();
                            RaiseLine(completeLine);
                        }
                    }
                    else if (value != 0)
                    {
                        line.Append((char)value);
                        if (line.Length > 4096)
                            throw new IOException("Controller returned an overlong response without a line terminator.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                ReportFault(ex, port);
        }
    }

    private void RaiseLine(string line)
    {
        // A UI subscriber must not terminate the serial receive worker.
        if (LineReceived is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(line); }
            catch { /* Subscribers own their error handling. */ }
        }
    }

    private void ReportFault(Exception error, SerialPort source)
    {
        lock (_connectionState)
        {
            // A write can fail while CloseAsync is replacing a connection. Its failure
            // must not invalidate a newly opened session on this transport instance.
            if (!ReferenceEquals(_port, source) || _faulted != 0) return;
            _faulted = 1;
        }
        // Dispatch independently so a subscriber may disconnect and join the reader.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_connectionState)
                if (!ReferenceEquals(_port, source)) return;
            if (Faulted is not { } handlers) return;
            foreach (Action<Exception> handler in handlers.GetInvocationList())
            {
                try { handler(error); }
                catch { /* Subscribers own their error handling. */ }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
    }
}
