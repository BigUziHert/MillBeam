using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GenmitsuControl.Core;

/// <summary>Deterministic GRBL demonstration device. It never accesses hardware.</summary>
public sealed class SimulatedGrblTransport : IGrblTransport
{
    private readonly object _gate = new();
    private readonly StringBuilder _input = new();
    private readonly SortedDictionary<int, double> _settings = new()
    {
        [0] = 10, [1] = 25, [10] = 1, [13] = 0, [20] = 0, [21] = 1,
        [22] = 1, [23] = 3, [30] = 1000, [31] = 0, [32] = 0,
        [100] = 200, [101] = 200, [102] = 200,
        [110] = 6000, [111] = 6000, [112] = 2000,
        [120] = 300, [121] = 300, [122] = 300,
        [130] = 600, [131] = 500, [132] = 115
    };
    private readonly double[] _machine = [-200, -100, -15];
    private readonly double[] _g54 = [-210, -120, -20];
    private CancellationTokenSource? _motionCancellation;
    private Task? _motionTask;
    private long _motionVersion;
    private string _state = "Idle";
    private bool _open;
    private bool _disposed;
    private bool _inches;
    private double _feed;

    public event Action<string>? LineReceived;
    public event Action<Exception>? Faulted;
    public bool IsOpen { get { lock (_gate) return _open; } }

    /// <summary>Inject error:20 once for this command (case/whitespace insensitive).</summary>
    public string? FailNextCommand { get; set; }
    /// <summary>Ignore one matching command completely, to test acknowledgement timeout handling.</summary>
    public string? IgnoreNextCommand { get; set; }
    public TimeSpan MotionDuration { get; set; } = TimeSpan.FromMilliseconds(1000);

    public Task OpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_open) throw new InvalidOperationException("The simulator is already connected.");
            _open = true;
            _input.Clear();
        }
        Emit("Grbl 1.1h ['$' for help]");
        Emit("[MSG:SIMULATOR - no physical machine connected]");
        return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ct.ThrowIfCancellationRequested();
        var lines = new List<string>();
        lock (_gate)
        {
            if (!_open) throw new IOException("The simulator is disconnected.");
            foreach (var value in data)
            {
                if (value == (byte)'?') lines.Add(Status());
                else if (value == 0x18) Reset(lines);
                else if (value == 0x85)
                {
                    if (_state == "Jog") { CancelMotion(); _state = "Idle"; }
                }
                else if (value == (byte)'!')
                {
                    if (_state is "Run" or "Jog") { CancelMotion(); _state = "Hold:0"; }
                }
                else if (value == (byte)'~')
                {
                    if (_state.StartsWith("Hold", StringComparison.Ordinal)) _state = "Idle";
                }
                else if (value is (byte)'\r' or (byte)'\n')
                {
                    if (_input.Length == 0) continue;
                    var command = Normalize(_input.ToString());
                    _input.Clear();
                    ProcessCommand(command, lines);
                }
                else if (value < 0x80 && value != 0)
                {
                    _input.Append((char)value);
                    if (_input.Length > 256)
                    {
                        _input.Clear();
                        lines.Add("error:11");
                    }
                }
            }
        }
        foreach (var line in lines) Emit(line);
        return Task.CompletedTask;
    }

    private void ProcessCommand(string command, List<string> lines)
    {
        if (IgnoreNextCommand is { } ignored && Normalize(ignored) == command)
        {
            IgnoreNextCommand = null;
            return;
        }
        if (FailNextCommand is { } failed && Normalize(failed) == command)
        {
            FailNextCommand = null;
            lines.Add("error:20");
            return;
        }
        if (command == "$$")
        {
            foreach (var entry in _settings) lines.Add($"${entry.Key}={Number(entry.Value)}");
            lines.Add("ok");
            return;
        }
        if (command == "$#")
        {
            lines.Add($"[G54:{Coordinates(_g54)}]");
            foreach (var index in new[] { 55, 56, 57, 58, 59, 28, 30, 92 })
                lines.Add($"[G{index}:0.000,0.000,0.000]");
            lines.Add("[TLO:0.000]");
            lines.Add("[PRB:0.000,0.000,0.000:0]");
            lines.Add("ok");
            return;
        }
        if (command == "$G")
        {
            lines.Add($"[GC:G0 G54 {(_inches ? "G20" : "G21")} G90 G94 M5 M9 T0 F0 S0]");
            lines.Add("ok");
            return;
        }
        if (command == "$I")
        {
            lines.Add("[VER:1.1h.20190825:MillBeam Simulator]");
            lines.Add("[OPT:V,15,128]");
            lines.Add("ok");
            return;
        }
        if (command == "$X")
        {
            if (_state == "Alarm") _state = "Idle";
            if (_state != "Idle") { lines.Add("error:8"); return; }
            lines.Add("[MSG:Caution: Unlocked]");
            lines.Add("ok");
            return;
        }
        if (command == "$H")
        {
            if (_settings[22] == 0) { lines.Add("error:5"); return; }
            if (_state is not ("Idle" or "Alarm")) { lines.Add("error:8"); return; }
            StartMotion([-1, -1, -1], "Home", acknowledgeAtEnd: true);
            return;
        }
        if (_state != "Idle")
        {
            lines.Add(_state == "Alarm" ? "error:9" : "error:8");
            return;
        }
        var setting = Regex.Match(command, @"^\$(\d+)=([-+]?(?:\d+(?:\.\d*)?|\.\d+))$");
        if (setting.Success)
        {
            var key = int.Parse(setting.Groups[1].Value, CultureInfo.InvariantCulture);
            var value = double.Parse(setting.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!_settings.ContainsKey(key)) { lines.Add("error:3"); return; }
            if (!double.IsFinite(value) || value < 0 || (key is 13 or 20 or 21 or 22 or 32 && value is not (0 or 1)))
            { lines.Add("error:3"); return; }
            _settings[key] = value;
            lines.Add("ok");
            return;
        }
        if (command.StartsWith("G10", StringComparison.Ordinal))
        {
            var datum = Regex.Match(command, @"^G10L(2|20)P1X([-+]?(?:\d+(?:\.\d*)?|\.\d+))Y([-+]?(?:\d+(?:\.\d*)?|\.\d+))Z([-+]?(?:\d+(?:\.\d*)?|\.\d+))$");
            if (!datum.Success) { lines.Add("error:20"); return; }
            var setFromPosition = datum.Groups[1].Value == "20";
            for (var axis = 0; axis < 3; axis++)
            {
                var desired = double.Parse(datum.Groups[axis + 2].Value, CultureInfo.InvariantCulture) * (_inches ? 25.4 : 1);
                _g54[axis] = setFromPosition ? _machine[axis] - desired : desired;
            }
            lines.Add("ok");
            return;
        }
        var workReturn = Regex.Match(command, @"^G21G90G54G1X0Y0Z0F(\d+(?:\.\d*)?)$");
        if (workReturn.Success)
        {
            var feed = double.Parse(workReturn.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(feed) || feed <= 0) { lines.Add("error:22"); return; }
            _inches = false;
            StartMotion((double[])_g54.Clone(), "Run", acknowledgeAtEnd: false);
            _feed = feed;
            lines.Add("ok");
            return;
        }
        if (command.StartsWith("$J=", StringComparison.Ordinal))
        {
            // Jogging is modal-state independent. Resume uses an absolute machine target
            // so a cancelled move's remaining travel cannot be confused with its full step.
            var words = Regex.Matches(command[3..], @"([A-Z])([-+]?(?:\d+(?:\.\d*)?|\.\d+))");
            if (string.Concat(words.Select(word => word.Value)) != command[3..]) { lines.Add("error:20"); return; }
            var target = (double[])_machine.Clone();
            bool? incremental = null;
            var machineCoordinates = false;
            double? scale = null;
            double? feed = null;
            var axes = new HashSet<int>();
            foreach (Match word in words)
            {
                var letter = word.Groups[1].Value[0];
                var value = double.Parse(word.Groups[2].Value, CultureInfo.InvariantCulture);
                if (letter == 'G' && value is 90 or 91 && incremental is null) incremental = value == 91;
                else if (letter == 'G' && value == 53 && !machineCoordinates) machineCoordinates = true;
                else if (letter == 'G' && value is 20 or 21 && scale is null) scale = value == 20 ? 25.4 : 1;
                else if (letter == 'F' && feed is null) feed = value;
                else if (letter is 'X' or 'Y' or 'Z')
                {
                    var axis = letter == 'X' ? 0 : letter == 'Y' ? 1 : 2;
                    if (!axes.Add(axis)) { lines.Add("error:25"); return; }
                    target[axis] = value; // Convert to absolute millimetres after units are known.
                }
                else { lines.Add("error:20"); return; }
            }
            if (incremental is null || scale is null || feed is null || !double.IsFinite(feed.Value) || feed <= 0 || axes.Count == 0 || (machineCoordinates && incremental.Value))
            { lines.Add("error:22"); return; }
            foreach (var axis in axes)
                target[axis] = target[axis] * scale.Value + (incremental.Value ? _machine[axis] : machineCoordinates ? 0 : _g54[axis]);
            StartMotion(target, "Jog", acknowledgeAtEnd: false);
            _feed = feed.Value * scale.Value;
            lines.Add("ok");
            return;
        }
        if (command is "G54" or "G90" or "G94" or "M5" or "M9") { lines.Add("ok"); return; }
        if (command is "G20" or "G21") { _inches = command == "G20"; lines.Add("ok"); return; }
        // Unsupported console commands must never appear to have executed successfully.
        lines.Add("error:20");
    }

    private void StartMotion(double[] target, string state, bool acknowledgeAtEnd)
    {
        CancelMotion();
        _state = state;
        var cancellation = new CancellationTokenSource();
        _motionCancellation = cancellation;
        var version = _motionVersion;
        var start = (double[])_machine.Clone();
        var duration = MotionDuration > TimeSpan.Zero ? MotionDuration : TimeSpan.FromMilliseconds(20);
        _motionTask = Task.Run(async () =>
        {
            try
            {
                for (var step = 1; step <= 20; step++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, duration.TotalMilliseconds / 20)), cancellation.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_open || version != _motionVersion) return;
                        for (var axis = 0; axis < 3; axis++)
                            _machine[axis] = start[axis] + (target[axis] - start[axis]) * step / 20;
                    }
                }
                lock (_gate)
                {
                    if (!_open || version != _motionVersion) return;
                    _state = "Idle";
                    _feed = 0;
                }
                if (acknowledgeAtEnd) Emit("ok");
            }
            catch (OperationCanceledException) { }
            finally { cancellation.Dispose(); }
        });
    }

    private void CancelMotion()
    {
        _motionVersion++;
        try { _motionCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _motionCancellation = null;
        _feed = 0;
    }

    private void Reset(List<string> lines)
    {
        var wasMoving = _state is "Jog" or "Run" or "Home" || _state.StartsWith("Hold", StringComparison.Ordinal);
        CancelMotion();
        _input.Clear();
        _inches = false;
        _state = wasMoving || _state == "Alarm" ? "Alarm" : "Idle";
        lines.Add("Grbl 1.1h ['$' for help]");
        if (_state == "Alarm") lines.Add("[MSG:'$H'|'$X' to unlock]");
    }

    private string Status() => $"<{_state}|MPos:{Coordinates(_machine)}|FS:{Number(_feed)},0|WCO:{Coordinates(_g54)}>";
    private string Coordinates(double[] values) => string.Join(",", values.Select(value => (value / (_settings[13] == 1 ? 25.4 : 1)).ToString(_settings[13] == 1 ? "0.0000" : "0.000", CultureInfo.InvariantCulture)));
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Normalize(string command) => string.Concat(command.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private void Emit(string line)
    {
        if (LineReceived is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(line); }
            catch { /* The simulator receive loop follows serial transport semantics. */ }
        }
    }

    /// <summary>Force a controller state for tests without transmitting a command.</summary>
    public void SetStatusForTest(string state)
    {
        lock (_gate)
        {
            CancelMotion();
            _state = state;
        }
    }

    /// <summary>Simulate an unplugged serial device.</summary>
    public void InjectFault(Exception? error = null)
    {
        lock (_gate)
        {
            CancelMotion();
            _open = false;
            _input.Clear();
        }
        Faulted?.Invoke(error ?? new IOException("Simulated serial device disconnected."));
    }

    public async Task CloseAsync()
    {
        Task? motion;
        lock (_gate)
        {
            var wasMoving = _state is "Jog" or "Run" or "Home";
            _open = false;
            _input.Clear();
            CancelMotion();
            if (wasMoving) _state = "Alarm";
            motion = _motionTask;
            _motionTask = null;
        }
        if (motion is not null) await motion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
    }
}
