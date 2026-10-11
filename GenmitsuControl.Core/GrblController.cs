using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GenmitsuControl.Core;

/// <summary>
/// A GRBL 1.1 connection with one acknowledged command in flight. No motion or
/// configuration writes are replayed after an error, reset, or lost connection.
/// </summary>
public sealed class GrblController : IAsyncDisposable
{
    private readonly IGrblTransport _transport;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _startupDelay;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly Dictionary<int, double> _settings = new();
    private readonly List<TaskCompletionSource<bool>> _statusWaiters = new();
    private CancellationTokenSource? _session;
    private Task? _pollTask;
    private TaskCompletionSource<bool>? _terminationCompletion;
    private PendingCommand? _pending;
    private bool _connected, _ready, _busy, _terminating, _disposed, _unitsKnown, _homing, _modeSwitchIncomplete;
    private long _generation;
    private long _jogGeneration;
    private JogIntent? _jog;
    private string _state = "Disconnected", _message = "Select a serial port and connect.", _activeSystem = "Unknown";
    private AxisPosition? _machine, _work, _g54, _wco;
    private DateTimeOffset? _lastStatus;
    private DateTimeOffset _lastStatusRequest = DateTimeOffset.MinValue;
    private const double ZeroTolerance = 0.000000001;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private enum JogPhase { Sending, Running, Pausing, Paused, Resuming }

    // This is deliberately session-only. A GRBL jog cancel flushes the planner;
    // resuming means issuing a NEW jog to this verified machine destination.
    private sealed class JogIntent(long generation, AxisPosition start, AxisPosition delta, double feed, double tolerance, AxisPosition stepResolution)
    {
        public long Generation { get; } = generation;
        public AxisPosition Start { get; } = start;
        public AxisPosition Delta { get; } = delta;
        public AxisPosition Target { get; } = Add(start, delta);
        public double Feed { get; } = feed;
        public double Tolerance { get; } = tolerance;
        public AxisPosition StepResolution { get; } = stepResolution;
        public JogPhase Phase { get; set; } = JogPhase.Sending;
        public AxisPosition? PausedAt { get; set; }
        public bool CommandDispatched { get; set; }
    }

    private sealed class JogIntentCancelledException() : InvalidOperationException("The jog was stopped or its saved destination was invalidated. No further movement was queued.");

    private sealed class PendingCommand(string command)
    {
        public string Command { get; } = command;
        public List<string> Lines { get; } = new();
        public TaskCompletionSource<string[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public GrblController(IGrblTransport transport, TimeSpan? commandTimeout = null, TimeSpan? startupDelay = null)
    {
        _transport = transport;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(3);
        _startupDelay = startupDelay ?? TimeSpan.FromMilliseconds(1800);
        if (_commandTimeout <= TimeSpan.Zero || _startupDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        _transport.LineReceived += OnLine;
        _transport.Faulted += OnFault;
    }

    public event Action? StateChanged;
    public event Action<string>? Log;

    public ControllerSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                bool fresh = _lastStatus.HasValue && DateTimeOffset.UtcNow - _lastStatus.Value < TimeSpan.FromSeconds(2);
                bool idle = _connected && _ready && fresh && _state == "Idle";
                string mode = _settings.TryGetValue(32, out var laser) ? laser == 1 ? "Laser" : laser == 0 ? "Milling" : "Unknown" : "Unknown";
                bool verified = !_modeSwitchIncomplete && mode != "Unknown" && ProfileMatchesLocked(mode == "Laser");
                string state = _connected && _ready && !fresh && !_homing ? $"{_state} (status stale)" : _state;
                return new(_connected, _busy || _terminating, idle, state, mode, _message, _machine, _work, _g54,
                    new ReadOnlyDictionary<int, double>(new Dictionary<int, double>(_settings)), _activeSystem,
                    _settings.TryGetValue(13, out var units) && units == 1, idle && !_busy && !_terminating && !JogIsHeldLocked(),
                    _lastStatus, verified)
                {
                    IsJogPaused = _jog?.Phase == JogPhase.Paused,
                    JogPausing = _jog?.Phase == JogPhase.Pausing,
                    CanPauseJog = _connected && _ready && fresh && !_busy && !_terminating && _state == "Jog" && _jog?.Phase == JogPhase.Running,
                    CanResumeJog = idle && !_busy && !_terminating && _jog?.Phase == JogPhase.Paused && _machine is not null && _unitsKnown
                };
            }
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        long generation;
        CancellationToken token;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy || _terminating) throw new InvalidOperationException("Another operation is in progress.");
            if (_connected) throw new InvalidOperationException("The controller is already connected.");
            ClearControllerStateLocked();
            _session?.Dispose();
            _session = new CancellationTokenSource();
            token = _session.Token;
            generation = ++_generation;
            _busy = true;
            _message = "Opening serial port and reading controller state…";
            _state = "Connecting";
        }
        Changed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
        try
        {
            await _transport.OpenAsync(linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation) throw new OperationCanceledException(linked.Token);
                _connected = true;
            }
            Changed();
            // USB serial opening can reset an Arduino. Let its bootloader finish;
            // never put configuration or motion in a startup queue.
            await Task.Delay(_startupDelay, linked.Token).ConfigureAwait(false);
            await ReadControllerAsync(linked.Token).ConfigureAwait(false);
            await ReadCoordinatesAsync(linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation) throw new OperationCanceledException(linked.Token);
                _ready = true;
                _busy = false;
                _message = "Connected. Controller settings and offsets read back.";
            }
            WriteLog("Connected; no mode settings or datum were changed.");
            Changed();
            _pollTask = PollAsync(token);
        }
        catch (Exception ex)
        {
            if (IsGeneration(generation)) await TerminateAsync($"Connection failed: {ex.Message}", false).ConfigureAwait(false);
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default) =>
        TerminateAsync("Disconnected. Serial port released for LightBurn.", false);

    public Task RefreshAsync(CancellationToken ct = default) => RunOperationAsync("Refresh", async token =>
    {
        await ReadControllerAsync(token).ConfigureAwait(false);
        await ReadCoordinatesAsync(token).ConfigureAwait(false);
        SetMessage("Controller settings, offsets, and coordinates refreshed.");
    }, ct, false, preserveJog: true);

    public Task SwitchModeAsync(bool laser, CancellationToken ct = default) => RunOperationAsync(laser ? "Laser mode switch" : "Milling mode switch", async token =>
    {
        lock (_sync) _modeSwitchIncomplete = true;
        Changed();
        await MutateAsync("M5", token).ConfigureAwait(false);
        await MutateAsync(laser ? "$32=1" : "$32=0", token).ConfigureAwait(false);
        await MutateAsync(laser ? "$120=1000" : "$120=300", token).ConfigureAwait(false);
        await MutateAsync(laser ? "$121=1000" : "$121=300", token).ConfigureAwait(false);
        await MutateAsync("$122=300", token).ConfigureAwait(false);
        if (laser)
        {
            await MutateAsync("G10 L2 P1 X0 Y0 Z0", token).ConfigureAwait(false);
            InvalidateCoordinates();
        }
        await ReadControllerAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            if (!ProfileMatchesLocked(laser)) throw new InvalidOperationException("Mode setting readback does not match the requested profile.");
            if (laser && (_g54 is null || !_g54.IsZero(ZeroTolerance)))
                throw new InvalidOperationException("G54 readback is not zero; laser mode switch is incomplete.");
        }
        await ReadCoordinatesAsync(token).ConfigureAwait(false);
        lock (_sync) _modeSwitchIncomplete = false;
        SetMessage(laser ? "Laser mode verified: X/Y 1,000, Z 300 mm/s²; G54 is zero." : "Milling mode verified: X/Y/Z 300 mm/s². No previous datum was restored.");
    }, ct);

    public Task SetWorkDatumAsync(CancellationToken ct = default) => RunOperationAsync("Set work datum", async token =>
    {
        await MutateAsync("G54", token).ConfigureAwait(false);
        InvalidateCoordinates();
        await MutateAsync("G10 L20 P1 X0 Y0 Z0", token).ConfigureAwait(false);
        InvalidateCoordinates();
        await ReadParametersAsync(token).ConfigureAwait(false);
        await ReadModalAsync(token).ConfigureAwait(false);
        await ReadCoordinatesAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            // Inch reporting has 0.00254 mm precision; allow one reporting quantum.
            double tolerance = _settings.GetValueOrDefault(13) == 1 ? 0.003 : 0.001;
            if (_activeSystem != "G54" || _work is null || !_work.IsZero(tolerance))
                throw new InvalidOperationException("The controller did not report work zero in G54.");
        }
        SetMessage("G54 work datum set to the current position. No movement commanded.");
    }, ct);

    public Task ClearWorkDatumAsync(CancellationToken ct = default) => RunOperationAsync("Clear work datum", async token =>
    {
        await MutateAsync("G10 L2 P1 X0 Y0 Z0", token).ConfigureAwait(false);
        InvalidateCoordinates();
        await ReadParametersAsync(token).ConfigureAwait(false);
        lock (_sync)
            if (_g54 is null || !_g54.IsZero(ZeroTolerance)) throw new InvalidOperationException("G54 readback is not zero.");
        await ReadCoordinatesAsync(token).ConfigureAwait(false);
        SetMessage("Stored G54 offset cleared and verified. No movement commanded.");
    }, ct);

    public Task HomeAsync(CancellationToken ct = default) => RunOperationAsync("Homing", async token =>
    {
        await RequireStateAsync(token, allowAlarm: true).ConfigureAwait(false);
        InvalidateCoordinates();
        lock (_sync)
        {
            _homing = true;
            _state = "Home";
            _lastStatus = null;
        }
        Changed();
        try { await SendCommandAsync("$H", token, TimeSpan.FromMinutes(3)).ConfigureAwait(false); }
        finally { lock (_sync) _homing = false; }
        InvalidateCoordinates();
        await ReadCoordinatesAsync(token).ConfigureAwait(false);
        if (CurrentState() != "Idle") throw new InvalidOperationException($"Homing returned but controller state is {CurrentState()}.");
        SetMessage("Homing complete; controller is Idle. Work datum was not changed.");
    }, ct);

    /// <summary>Move all axes together to G54 work zero at the requested feed. Does not alter offsets.</summary>
    public Task MoveToWorkZeroAsync(double feed, CancellationToken ct = default)
    {
        if (!double.IsFinite(feed) || feed <= 0 || feed > 10000)
            throw new ArgumentOutOfRangeException(nameof(feed), "Work return feed must be positive and no more than 10,000 mm/min.");
        string command = string.Create(Invariant, $"G21 G90 G54 G1 X0 Y0 Z0 F{feed:0.###}");
        // Reject values that would round down to a zero feed on the controller.
        if (feed < 0.001)
            throw new ArgumentOutOfRangeException(nameof(feed), "Work return feed must be at least 0.001 mm/min.");
        return RunOperationAsync("Move to work zero", async token =>
        {
            await MutateAsync("M5", token).ConfigureAwait(false);
            await MutateAsync(command, token).ConfigureAwait(false);
            // G54 selection can change WCO. Discard the pre-move coordinate cache
            // even if the controller acknowledged the command before starting motion.
            InvalidateCoordinates();
            await ReadModalAsync(token).ConfigureAwait(false);
            lock (_sync)
                if (_activeSystem != "G54") throw new InvalidOperationException("Work return did not select G54.");
            SetMessage("Moving to G54 work zero. All axes may move together; wait for Idle.");
            await WaitForWorkZeroAsync(token).ConfigureAwait(false);
            SetMessage("At G54 work zero; controller is Idle. Work datum was not changed.");
        }, ct);
    }

    private async Task WaitForWorkZeroAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        int idleAwayReports = 0, idleWithoutCoordinates = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Only real-time status requests are sent while waiting. Never replay
            // the move, and never equate its early 'ok' with physical completion.
            await RequestStatusAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                if (_state == "Idle")
                {
                    if (_wco is null || _machine is null || _work is null)
                    {
                        if (++idleWithoutCoordinates >= 12)
                            throw new InvalidOperationException("Work return is Idle, but fresh coordinates and WCO were not received to verify work zero.");
                    }
                    else
                    {
                        double tolerance = _settings.GetValueOrDefault(13) == 1 ? 0.003 : 0.001;
                        if (_work.IsZero(tolerance)) return;
                        if (++idleAwayReports >= 3)
                            throw new InvalidOperationException("Work return is Idle but the controller did not report G54 work zero.");
                    }
                }
                else if (_state == "Run")
                {
                    idleAwayReports = idleWithoutCoordinates = 0;
                }
                else
                    throw new InvalidOperationException($"Work return interrupted; controller state is {_state}.");
            }
        }
        throw new TimeoutException("Work return did not finish within 30 minutes. Check controller state; use Software Stop to abort movement if needed.");
    }

    public Task JogAsync(char axis, double distance, double feed, CancellationToken ct = default)
    {
        axis = char.ToUpperInvariant(axis);
        if (axis is not ('X' or 'Y' or 'Z')) throw new ArgumentOutOfRangeException(nameof(axis));
        if (!double.IsFinite(distance) || distance == 0 || Math.Abs(distance) > 1000) throw new ArgumentOutOfRangeException(nameof(distance), "Jog distance must be nonzero and at most 1,000 mm.");
        return JogAsync(axis == 'X' ? distance : 0, axis == 'Y' ? distance : 0, axis == 'Z' ? distance : 0, feed, ct);
    }

    /// <summary>
    /// Move the supplied axes together in one coordinated incremental jog.
    /// Distances are independent axis displacements in millimetres (a diagonal
    /// X/Y step applies the full step to each axis); feed is path speed in mm/min.
    /// Zero-distance axes are omitted. This does not change GRBL's modal units.
    /// </summary>
    public Task JogAsync(double xDistance, double yDistance, double zDistance, double feed, CancellationToken ct = default)
    {
        static double RoundDistance(double value, string parameter)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > 1000)
                throw new ArgumentOutOfRangeException(parameter, "Each jog distance must be finite and at most 1,000 mm.");
            double rounded = double.Parse(value.ToString("0.####", Invariant), Invariant);
            if (value != 0 && rounded == 0)
                throw new ArgumentOutOfRangeException(parameter, "Jog distance rounds to zero; use at least 0.0001 mm.");
            return rounded;
        }

        AxisPosition delta = new(RoundDistance(xDistance, nameof(xDistance)), RoundDistance(yDistance, nameof(yDistance)), RoundDistance(zDistance, nameof(zDistance)));
        if (delta.X == 0 && delta.Y == 0 && delta.Z == 0)
            throw new ArgumentException("At least one jog axis must have a nonzero distance.");
        if (!double.IsFinite(feed) || feed <= 0 || feed > 100000) throw new ArgumentOutOfRangeException(nameof(feed), "Jog feed must be positive, in mm/min.");
        // Track exactly the values sent to GRBL, including their wire precision.
        double sentFeed = double.Parse(feed.ToString("0.###", Invariant), Invariant);
        if (sentFeed == 0) throw new ArgumentOutOfRangeException(nameof(feed), "Jog feed rounds to zero; use at least 0.001 mm/min.");
        string command = string.Create(Invariant, $"$J=G91 G21 {JogAxisWords(delta, delta)} F{sentFeed:0.###}");
        long jogGeneration = 0;
        return RunOperationAsync("Jog", async token =>
        {
            await RequireStateAsync(token).ConfigureAwait(false);
            JogIntent jog;
            lock (_sync)
            {
                if (jogGeneration != _jogGeneration) throw new JogIntentCancelledException();
                if (!_unitsKnown || _machine is null)
                    throw new InvalidOperationException("Jogging requires a fresh machine position in known units. Refresh controller state first.");
                jog = new JogIntent(_jogGeneration, _machine, delta, sentFeed, PositionToleranceLocked(),
                    new AxisPosition(StepResolutionLocked('X'), StepResolutionLocked('Y'), StepResolutionLocked('Z')));
                if (!double.IsFinite(jog.Target.X) || !double.IsFinite(jog.Target.Y) || !double.IsFinite(jog.Target.Z))
                    throw new InvalidOperationException("The requested jog has an invalid machine destination.");
                _jog = jog;
            }
            await SendCommandAsync(command, token, jog: jog).ConfigureAwait(false);
            lock (_sync)
            {
                RequireJogIntentLocked(jog);
                jog.Phase = JogPhase.Running;
            }
            await RequestStatusAsync(token).ConfigureAwait(false);
            lock (_sync)
                _message = ReferenceEquals(_jog, jog) ? "Jog acknowledged. Pause retains the destination; Stop discards it." : "Jog finished or stopped. No remaining jog is saved.";
            Changed();
        }, ct, startedLocked: () => jogGeneration = _jogGeneration);
    }

    /// <summary>Cancel this app's current jog, then retain its destination only after fresh Idle and position verification.</summary>
    public Task PauseJogAsync(CancellationToken ct = default) => RunOperationAsync("Pause", async token =>
    {
        JogIntent jog;
        lock (_sync)
        {
            jog = _jog ?? throw new InvalidOperationException("There is no active app jog to pause.");
            if (jog.Phase != JogPhase.Running) throw new InvalidOperationException("The jog is not available to pause.");
        }
        await RequestStatusAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            // A short jog may have completed while the user pressed Pause.
            if (!ReferenceEquals(_jog, jog))
            {
                if (_state != "Idle")
                    throw new InvalidOperationException("The tracked jog was invalidated before pausing. No destination was saved; check controller state and use Stop if motion continues.");
                _message = "Jog already finished or stopped. There is no remaining jog to resume.";
                return;
            }
            if (_state != "Jog" || _machine is null || !_unitsKnown || !IsOnJogPathLocked(jog, _machine))
                throw new InvalidOperationException("Pause requires this app's active jog and a verified machine position.");
            jog.Phase = JogPhase.Pausing;
            _message = "Pausing jog; waiting for the controller to stop…";
        }
        Changed();
        WriteLog("> [Pause jog / jog cancel 0x85]");
        await WriteBytesAsync([0x85], token, jog).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + _commandTimeout + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await RequestStatusAsync(token).ConfigureAwait(false);
            lock (_sync)
            {
                RequireJogIntentLocked(jog);
                if (_state == "Idle")
                {
                    if (_machine is null || !_unitsKnown || !IsOnJogPathLocked(jog, _machine))
                        throw new InvalidOperationException("The jog stopped, but its machine position could not be verified. The saved destination was discarded.");
                    if (IsAtJogTargetLocked(jog, _machine))
                    {
                        ClearJogLocked();
                        _message = "Jog reached its destination before pausing. There is nothing to resume.";
                    }
                    else
                    {
                        jog.PausedAt = _machine;
                        jog.Phase = JogPhase.Paused;
                        _message = "Jog paused. Resume continues to its original destination at its original feed; Stop discards it.";
                    }
                    return;
                }
                if (_state != "Jog") throw new InvalidOperationException($"Jog pause interrupted; controller state is {_state}.");
            }
        }
        await TerminateAsync("Jog pause did not reach Idle. Saved jog discarded; connection closed. Check the machine before reconnecting.", false).ConfigureAwait(false);
        throw new TimeoutException("Jog cancellation did not reach Idle before the deadline.");
    }, ct, preserveJog: true);

    /// <summary>Explicitly issue a new machine-coordinate jog to the original destination, after verifying the paused machine position.</summary>
    public Task ResumeJogAsync(CancellationToken ct = default) => RunOperationAsync("Resume", async token =>
    {
        JogIntent jog;
        lock (_sync)
        {
            jog = _jog ?? throw new InvalidOperationException("There is no paused jog to resume.");
            if (jog.Phase != JogPhase.Paused || jog.PausedAt is null)
                throw new InvalidOperationException("The jog has not finished pausing.");
        }
        await RequestStatusAsync(token).ConfigureAwait(false);
        lock (_sync)
        {
            RequireJogIntentLocked(jog);
            if (_state != "Idle" || !_unitsKnown || _machine is null || jog.PausedAt is null ||
                !SamePosition(_machine, jog.PausedAt, JogToleranceLocked(jog)) || !IsOnJogPathLocked(jog, _machine))
                throw new InvalidOperationException("Machine position or state changed since pausing. The saved jog was discarded; no resume movement was sent.");
            jog.Phase = JogPhase.Resuming;
            jog.CommandDispatched = false;
        }
        // G53 bypasses all current work offsets. Do not reconstruct another
        // incremental distance from rounded status reports on each resume.
        string command = string.Create(Invariant, $"$J=G21 G90 G53 {JogAxisWords(jog.Target, jog.Delta)} F{jog.Feed:0.###}");
        await SendCommandAsync(command, token, jog: jog).ConfigureAwait(false);
        lock (_sync)
        {
            RequireJogIntentLocked(jog);
            jog.Phase = JogPhase.Running;
            jog.PausedAt = null;
        }
        await RequestStatusAsync(token).ConfigureAwait(false);
        lock (_sync)
            _message = ReferenceEquals(_jog, jog) ? "Jog resumed toward its original machine destination at its original feed." : "Jog finished or stopped. No remaining jog is saved.";
        Changed();
    }, ct, preserveJog: true);

    /// <summary>GRBL real-time jog cancel (0x85). This bypasses the command/operation queue.</summary>
    public async Task CancelJogAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_connected) throw new InvalidOperationException("Connect to the controller first.");
            // Invalidate before waiting for the write lock so an in-flight
            // pause/resume cannot enqueue movement after Stop was pressed.
            ClearJogLocked();
        }
        Changed();
        WriteLog("> [Jog cancel 0x85]");
        try
        {
            await WriteBytesAsync([0x85], ct).ConfigureAwait(false);
            SetMessage("Jog cancel sent; any saved jog was discarded. Wait for Idle before another movement.");
        }
        catch (Exception ex)
        {
            await TerminateAsync($"Jog cancel could not be delivered: {ex.Message}", false).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Real-time GRBL soft reset. Not a physical emergency stop.</summary>
    public Task StopAsync(CancellationToken ct = default) =>
        TerminateAsync("Software stop sent; connection closed. Reconnect and check machine position before continuing. Use the physical emergency stop for an emergency.", true);

    public Task SendConsoleAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Enter a command.", nameof(command));
        command = command.Trim();
        if (command.Length > 120 || command.Any(c => c < 32 || c > 126))
            throw new ArgumentException("Enter one printable ASCII command, at most 120 characters. Control characters and multiple lines are not allowed.", nameof(command));
        string normalized = Regex.Replace(command, @"\s+", "").ToUpperInvariant();
        if (normalized.Contains("$RST", StringComparison.Ordinal))
            throw new InvalidOperationException("Factory-reset commands are disabled. Individual controller settings may be entered deliberately.");
        if (command.Contains('!') || command.Contains('~') || (command.Contains('?') && command != "?"))
            throw new InvalidOperationException("Use the dedicated stop and jog controls for real-time control characters.");
        if (normalized == "$H") return HomeAsync(ct);
        bool query = normalized is "$$" or "$#" or "$G" or "$I" or "$N" or "$" or "?";
        return RunOperationAsync("Console command", async token =>
        {
            if (command == "?")
            {
                await RequestStatusAsync(token).ConfigureAwait(false);
                SetMessage("Status received.");
                return;
            }
            if (!query) await RequireStateAsync(token).ConfigureAwait(false);
            await SendCommandAsync(command, token).ConfigureAwait(false);
            if (!query) InvalidateCoordinates();
            await RequestStatusAsync(token).ConfigureAwait(false);
            if (query || CurrentState() == "Idle") await ReadControllerAsync(token).ConfigureAwait(false);
            await ReadCoordinatesAsync(token).ConfigureAwait(false);
            SetMessage($"Command acknowledged. Controller state: {CurrentState()}.");
        }, ct, preserveJog: query);
    }

    private async Task RunOperationAsync(string name, Func<CancellationToken, Task> action, CancellationToken ct, bool recover = true, bool preserveJog = false, Action? startedLocked = null)
    {
        long generation;
        CancellationToken sessionToken;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connected || !_ready || _session is null) throw new InvalidOperationException("Connect and read the controller state first.");
            if (_busy || _terminating) throw new InvalidOperationException("Another operation is in progress; the request was not queued.");
            if (JogIsHeldLocked() && !preserveJog)
                throw new InvalidOperationException("Resume or Stop before changing modes, datums, or commanding other movement.");
            if (!preserveJog) ClearJogLocked();
            startedLocked?.Invoke();
            _busy = true;
            generation = _generation;
            sessionToken = _session.Token;
            _message = $"{name} in progress…";
        }
        Changed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, ct);
        try { await action(linked.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (_sync) if (generation == _generation) ClearJogLocked();
            string failure = $"{name} incomplete: {ex.Message}";
            WriteLog(failure);
            if (IsCurrent(generation))
            {
                SetMessage(failure);
                if (recover && !linked.IsCancellationRequested)
                {
                    try
                    {
                        await ReadControllerAsync(linked.Token).ConfigureAwait(false);
                        await ReadCoordinatesAsync(linked.Token).ConfigureAwait(false);
                        if (IsCurrent(generation)) SetMessage(failure + " Actual controller state refreshed; no rollback was attempted.");
                    }
                    catch (Exception refreshError) { WriteLog($"Controller refresh failed: {refreshError.Message}"); }
                }
            }
            throw;
        }
        finally
        {
            lock (_sync) if (generation == _generation) _busy = false;
            Changed();
        }
    }

    private async Task MutateAsync(string command, CancellationToken ct)
    {
        await RequireStateAsync(ct).ConfigureAwait(false);
        await SendCommandAsync(command, ct).ConfigureAwait(false);
    }

    private async Task RequireStateAsync(CancellationToken ct, bool allowAlarm = false)
    {
        await RequestStatusAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (!_connected || (_state != "Idle" && !(allowAlarm && _state == "Alarm")))
                throw new InvalidOperationException(allowAlarm ? $"Homing requires Idle or Alarm; controller is {_state}." : $"This action requires Idle; controller is {_state}.");
        }
    }

    private async Task ReadControllerAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            _settings.Clear();
            _unitsKnown = false;
            InvalidateCoordinatesLocked();
        }
        var settings = await SendCommandAsync("$$", ct).ConfigureAwait(false);
        var returned = new HashSet<int>();
        foreach (string line in settings)
        {
            var match = Regex.Match(line, @"^\$(\d+)=");
            if (match.Success) returned.Add(int.Parse(match.Groups[1].Value, Invariant));
        }
        foreach (int number in new[] { 13, 32, 120, 121, 122 })
            if (!returned.Contains(number)) throw new InvalidOperationException($"Settings readback did not include ${number}. This application requires GRBL 1.1-compatible reports.");
        await ReadParametersAsync(ct).ConfigureAwait(false);
        await ReadModalAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadParametersAsync(CancellationToken ct)
    {
        lock (_sync) _g54 = null;
        string[] lines = await SendCommandAsync("$#", ct).ConfigureAwait(false);
        lock (_sync)
            if (_g54 is null || !lines.Any(l => l.StartsWith("[G54:", StringComparison.Ordinal)))
                throw new InvalidOperationException("Offset readback did not include a valid G54 offset.");
    }

    private async Task ReadModalAsync(CancellationToken ct)
    {
        lock (_sync) _activeSystem = "Unknown";
        var lines = await SendCommandAsync("$G", ct).ConfigureAwait(false);
        lock (_sync)
            if (_activeSystem == "Unknown" || !lines.Any(l => l.StartsWith("[GC:", StringComparison.Ordinal)))
                throw new InvalidOperationException("Modal readback did not identify the active work-coordinate system.");
    }

    private async Task ReadCoordinatesAsync(CancellationToken ct)
    {
        // GRBL sends WCO intermittently (up to 30 reports while moving).
        // Coordinates remain blank until the controller provides an actual WCO;
        // G54 alone is insufficient because G92 and tool length may also apply.
        for (int i = 0; i < 12; i++)
        {
            await RequestStatusAsync(ct).ConfigureAwait(false);
            lock (_sync) if (_machine is not null && _work is not null) return;
        }
        WriteLog("Position report received; waiting for the controller's WCO before showing both coordinate systems.");
    }

    private async Task<string[]> SendCommandAsync(string command, CancellationToken ct, TimeSpan? timeout = null, JogIntent? jog = null)
    {
        PendingCommand pending;
        long generation;
        lock (_sync)
        {
            if (!_connected || !_transport.IsOpen) throw new InvalidOperationException("The serial connection is closed.");
            if (_pending is not null) throw new InvalidOperationException("An acknowledged command is already in flight.");
            pending = new PendingCommand(command);
            _pending = pending;
            generation = _generation;
        }
        WriteLog($"> {command}");
        try
        {
            await WriteBytesAsync(Encoding.ASCII.GetBytes(command + "\n"), ct, jog).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(timeout ?? _commandTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A late 'ok' has no command ID. Closing is the only safe boundary;
            // never let that acknowledgement complete a subsequent command.
            if (IsCurrent(generation)) await TerminateAsync($"Timed out waiting for '{command}'. Operation incomplete; reconnect to re-establish command synchronization.", false).ConfigureAwait(false);
            throw new TimeoutException($"No acknowledgement for '{command}'. Connection closed; reconnect before continuing.");
        }
        catch (OperationCanceledException)
        {
            bool stillCurrent;
            lock (_sync) stillCurrent = ReferenceEquals(_pending, pending) && _connected;
            if (stillCurrent) await TerminateAsync($"'{command}' was interrupted; reconnect to re-establish command synchronization.", false).ConfigureAwait(false);
            throw;
        }
        catch (GrblCommandException) { throw; }
        catch (JogIntentCancelledException) { throw; }
        catch (Exception ex)
        {
            if (IsCurrent(generation)) await TerminateAsync($"Serial command failed: {ex.Message}. Connection closed.", false).ConfigureAwait(false);
            throw;
        }
        finally { lock (_sync) if (ReferenceEquals(_pending, pending)) _pending = null; }
    }

    private async Task RequestStatusAsync(CancellationToken ct, bool tolerateHoming = false)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _statusGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var delay = TimeSpan.FromMilliseconds(200) - (DateTimeOffset.UtcNow - _lastStatusRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
            lock (_sync)
            {
                if (!_connected) throw new InvalidOperationException("The serial connection is closed.");
                _statusWaiters.Add(waiter);
            }
            _lastStatusRequest = DateTimeOffset.UtcNow;
            await WriteBytesAsync([(byte)'?'], ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync) _statusWaiters.Remove(waiter);
            throw;
        }
        finally { _statusGate.Release(); }
        try { await waiter.Task.WaitAsync(_commandTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // GRBL's homing routine can suppress '?' responses. The dedicated
            // $H acknowledgement deadline remains active during this interval.
            lock (_sync) if (tolerateHoming && _homing) return;
            await TerminateAsync("Controller status timed out. Operation incomplete; connection closed. Check the cable and reconnect.", false).ConfigureAwait(false);
            throw new TimeoutException("The controller did not return a fresh status report.");
        }
        finally { lock (_sync) _statusWaiters.Remove(waiter); }
    }

    private async Task WriteBytesAsync(byte[] bytes, CancellationToken ct, JogIntent? jog = null)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task write;
            lock (_sync)
            {
                // Linearize motion dispatch with Stop. If Stop wins this
                // lock, no new jog bytes can be sent; if dispatch wins, the
                // real-time cancel follows that write and stops the movement.
                if (jog is not null)
                {
                    RequireJogIntentLocked(jog);
                    if (bytes.Length > 1 && bytes[0] == (byte)'$')
                    {
                        AxisPosition? expected = jog.Phase == JogPhase.Resuming ? jog.PausedAt : jog.Start;
                        if (_state != "Idle" || !_unitsKnown || _machine is null || expected is null || !SamePosition(_machine, expected, JogToleranceLocked(jog)))
                        {
                            ClearJogLocked();
                            throw new JogIntentCancelledException();
                        }
                        jog.CommandDispatched = true;
                    }
                }
                ct.ThrowIfCancellationRequested();
                write = _transport.WriteAsync(bytes, ct);
            }
            await write.ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                lock (_sync) if (_homing) continue;
                await RequestStatusAsync(ct, tolerateHoming: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) await TerminateAsync($"Connection lost: {ex.Message}", false).ConfigureAwait(false);
        }
    }

    private void OnLine(string raw)
    {
        string line = raw.Trim();
        if (line.Length == 0) return;
        bool startupReset = false;
        lock (_sync)
        {
            if (!_connected) return;
            if (line.StartsWith('<') && line.EndsWith('>'))
            {
                ParseStatusLocked(line);
                foreach (var waiter in _statusWaiters) waiter.TrySetResult(true);
                _statusWaiters.Clear();
            }
            else
            {
                if (line.StartsWith("Grbl ", StringComparison.OrdinalIgnoreCase))
                {
                    startupReset = _ready || _pending is not null;
                    ClearControllerStateLocked();
                }
                ParseDataLocked(line);
                if (line == "ok") _pending?.Completion.TrySetResult(_pending.Lines.ToArray());
                else if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                {
                    ClearJogLocked();
                    _pending?.Completion.TrySetException(new GrblCommandException(_pending.Command, line));
                }
                else if (line.StartsWith("ALARM:", StringComparison.OrdinalIgnoreCase))
                {
                    ClearJogLocked();
                    _state = "Alarm";
                    _lastStatus = null;
                    _pending?.Completion.TrySetException(new GrblCommandException(_pending.Command, line));
                    _message = $"Controller {line}. Inspect the machine before continuing.";
                }
                else _pending?.Lines.Add(line);
            }
        }
        WriteLog($"< {line}");
        Changed();
        if (startupReset) _ = TerminateAsync("Unexpected controller reset. Operation incomplete; reconnect and check machine position.", false);
    }

    private void ParseDataLocked(string line)
    {
        var setting = Regex.Match(line, @"^\$(\d+)=([-+0-9.eE]+)");
        if (setting.Success && int.TryParse(setting.Groups[1].Value, out int number) && double.TryParse(setting.Groups[2].Value, NumberStyles.Float, Invariant, out double value) && double.IsFinite(value))
        {
            if (number == 13)
            {
                if (!_unitsKnown || _settings.GetValueOrDefault(13) != value) InvalidateCoordinatesLocked();
                _unitsKnown = true;
            }
            _settings[number] = value;
        }
        if (line.StartsWith("[G54:", StringComparison.Ordinal) && line.EndsWith(']') && _unitsKnown)
            _g54 = ParseAxesLocked(line[5..^1]);
        if (line.StartsWith("[GC:", StringComparison.Ordinal) && line.EndsWith(']'))
            foreach (string mode in line[4..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (mode is "G54" or "G55" or "G56" or "G57" or "G58" or "G59") _activeSystem = mode;
    }

    private void ParseStatusLocked(string line)
    {
        string[] fields = line[1..^1].Split('|');
        _state = fields[0];
        _lastStatus = DateTimeOffset.UtcNow;
        AxisPosition? machine = null, work = null;
        if (_unitsKnown)
        {
            foreach (string field in fields.Skip(1))
            {
                if (field.StartsWith("WCO:", StringComparison.Ordinal)) _wco = ParseAxesLocked(field[4..]);
                else if (field.StartsWith("MPos:", StringComparison.Ordinal)) machine = ParseAxesLocked(field[5..]);
                else if (field.StartsWith("WPos:", StringComparison.Ordinal)) work = ParseAxesLocked(field[5..]);
            }
        }
        _machine = machine ?? (work is not null && _wco is not null ? Add(work, _wco) : null);
        _work = work ?? (machine is not null && _wco is not null ? Subtract(machine, _wco) : null);
        ValidateJogStatusLocked();
    }

    private AxisPosition? ParseAxesLocked(string value)
    {
        string[] axes = value.Split(',');
        if (axes.Length < 3) return null;
        if (!double.TryParse(axes[0], NumberStyles.Float, Invariant, out var x) || !double.TryParse(axes[1], NumberStyles.Float, Invariant, out var y) || !double.TryParse(axes[2], NumberStyles.Float, Invariant, out var z) || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return null;
        double factor = _settings.GetValueOrDefault(13) == 1 ? 25.4 : 1;
        return new(x * factor, y * factor, z * factor);
    }

    private bool ProfileMatchesLocked(bool laser) =>
        SettingEqualsLocked(32, laser ? 1 : 0) && SettingEqualsLocked(120, laser ? 1000 : 300) && SettingEqualsLocked(121, laser ? 1000 : 300) && SettingEqualsLocked(122, 300);

    private bool SettingEqualsLocked(int number, double expected) => _settings.TryGetValue(number, out var value) && Math.Abs(value - expected) < 0.00001;
    private static AxisPosition Add(AxisPosition a, AxisPosition b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static AxisPosition Subtract(AxisPosition a, AxisPosition b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double AxisValue(AxisPosition position, char axis) => axis switch { 'X' => position.X, 'Y' => position.Y, 'Z' => position.Z, _ => throw new ArgumentOutOfRangeException(nameof(axis)) };
    private static bool SamePosition(AxisPosition a, AxisPosition b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance && Math.Abs(a.Z - b.Z) <= tolerance;
    private double PositionToleranceLocked() => _settings.GetValueOrDefault(13) == 1 ? 0.00255 : 0.00101;
    private double JogToleranceLocked(JogIntent jog) => Math.Max(jog.Tolerance, PositionToleranceLocked());
    private double StepResolutionLocked(char axis)
    {
        int setting = axis switch { 'X' => 100, 'Y' => 101, 'Z' => 102, _ => throw new ArgumentOutOfRangeException(nameof(axis)) };
        if (!_settings.TryGetValue(setting, out double stepsPerMm) || !double.IsFinite(stepsPerMm) || stepsPerMm <= 0) return 0;
        double resolution = 1 / stepsPerMm;
        return double.IsFinite(resolution) ? resolution : 0;
    }
    private double JogPathToleranceLocked(JogIntent jog, char axis) => JogToleranceLocked(jog) +
        (AxisValue(jog.Delta, axis) != 0 ? AxisValue(jog.StepResolution, axis) : 0);
    private static string JogAxisWords(AxisPosition values, AxisPosition delta) => string.Join(" ",
        "XYZ".Where(axis => AxisValue(delta, axis) != 0)
            .Select(axis => string.Create(Invariant, $"{axis}{AxisValue(values, axis):0.####}")));
    private bool IsAtJogTargetLocked(JogIntent jog, AxisPosition position) => "XYZ".All(axis =>
        Math.Abs(AxisValue(position, axis) - AxisValue(jog.Target, axis)) <= JogPathToleranceLocked(jog, axis));
    private bool IsOnJogPathLocked(JogIntent jog, AxisPosition position)
    {
        double minimumProgress = 0, maximumProgress = 1;
        foreach (char axis in "XYZ")
        {
            double tolerance = JogPathToleranceLocked(jog, axis);
            double current = AxisValue(position, axis), start = AxisValue(jog.Start, axis), delta = AxisValue(jog.Delta, axis);
            if (delta == 0)
            {
                if (Math.Abs(current - start) > tolerance) return false;
            }
            else
            {
                // All active axes must describe the SAME progress along the
                // original line, allowing each reported coordinate its own
                // report precision and one physical step of quantization. A
                // bounding box would accept a different diagonal and could
                // resume from an unexpected machine path.
                double first = (current - start - tolerance) / delta;
                double second = (current - start + tolerance) / delta;
                minimumProgress = Math.Max(minimumProgress, Math.Min(first, second));
                maximumProgress = Math.Min(maximumProgress, Math.Max(first, second));
                if (minimumProgress > maximumProgress) return false;
            }
        }
        return true;
    }
    private bool JogIsHeldLocked() => _jog?.Phase is JogPhase.Pausing or JogPhase.Paused or JogPhase.Resuming;
    private void RequireJogIntentLocked(JogIntent jog)
    {
        if (!_connected || !_ready || !ReferenceEquals(_jog, jog) || jog.Generation != _jogGeneration)
            throw new JogIntentCancelledException();
    }
    private void ClearJogLocked() { _jog = null; ++_jogGeneration; }
    private void ValidateJogStatusLocked()
    {
        if (_jog is not { } jog) return;
        bool awaitingDispatch = jog.Phase is JogPhase.Sending or JogPhase.Resuming && !jog.CommandDispatched;
        bool changedState = jog.Phase == JogPhase.Paused || awaitingDispatch ? _state != "Idle" : _state is not ("Idle" or "Jog");
        bool changedPosition = _machine is not null && (!IsOnJogPathLocked(jog, _machine) ||
            ((jog.Phase == JogPhase.Paused || awaitingDispatch) &&
             !SamePosition(_machine, jog.PausedAt ?? jog.Start, JogToleranceLocked(jog))));
        if (changedState || changedPosition)
        {
            ClearJogLocked();
            _message = "Controller state or machine position changed unexpectedly. Saved jog discarded; no resume movement was sent.";
        }
        else if (jog.Phase == JogPhase.Running && _state == "Idle")
        {
            bool reachedTarget = _machine is not null && IsAtJogTargetLocked(jog, _machine);
            ClearJogLocked();
            _message = reachedTarget ? "Jog complete." : "Jog stopped. No remaining jog is saved.";
        }
    }
    private string CurrentState() { lock (_sync) return _state; }
    private bool IsCurrent(long generation) { lock (_sync) return generation == _generation && _connected; }
    private bool IsGeneration(long generation) { lock (_sync) return generation == _generation; }
    private void InvalidateCoordinates() { lock (_sync) InvalidateCoordinatesLocked(); Changed(); }
    private void InvalidateCoordinatesLocked() { _wco = _machine = _work = null; }

    private void ClearControllerStateLocked()
    {
        ClearJogLocked();
        _settings.Clear();
        _unitsKnown = false;
        _modeSwitchIncomplete = false;
        _g54 = null;
        _activeSystem = "Unknown";
        _lastStatus = null;
        InvalidateCoordinatesLocked();
    }

    private void OnFault(Exception ex) => _ = TerminateAsync($"Serial connection lost: {ex.Message}. Reconnect before continuing.", false);

    private async Task TerminateAsync(string message, bool sendReset)
    {
        CancellationTokenSource? session = null;
        Task? previousTermination;
        TaskCompletionSource<bool>? completion = null;
        lock (_sync)
        {
            previousTermination = _terminating ? _terminationCompletion!.Task : null;
            if (previousTermination is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _terminationCompletion = completion;
                _terminating = true;
                _busy = true;
                _connected = _ready = _homing = false;
                ++_generation;
                session = _session;
                _pending?.Completion.TrySetCanceled();
                _pending = null;
                foreach (var waiter in _statusWaiters) waiter.TrySetCanceled();
                _statusWaiters.Clear();
                ClearControllerStateLocked();
                _state = "Disconnected";
                _message = message;
            }
        }
        if (previousTermination is not null)
        {
            await previousTermination.ConfigureAwait(false);
            return;
        }
        session?.Cancel();
        Changed();
        try
        {
            if (sendReset && _transport.IsOpen)
            {
                WriteLog("> [Software stop / Ctrl-X reset]");
                // Bypass both the acknowledged-command queue and the operation
                // gate. Cancelled session writes release the short write lock.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await WriteBytesAsync([0x18], deadline.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    message = $"Software stop delivery could not be confirmed: {ex.Message}. Use the physical emergency stop. Connection closed.";
                }
            }
            else if (sendReset) message = "No active serial connection; software stop could not be sent. Use the physical emergency stop if the machine is moving.";
            await _transport.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { message += $" Serial-port close failed: {ex.Message}"; }
        finally
        {
            lock (_sync)
            {
                _message = message;
                _busy = _terminating = false;
            }
            WriteLog(message);
            Changed();
            completion!.TrySetResult(true);
        }
    }

    private void SetMessage(string message) { lock (_sync) _message = message; Changed(); }
    private void WriteLog(string text) { try { Log?.Invoke(text); } catch { /* UI subscribers must not break the transport. */ } }
    private void Changed() { try { StateChanged?.Invoke(); } catch { /* UI subscribers must not break the transport. */ } }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        _transport.LineReceived -= OnLine;
        _transport.Faulted -= OnFault;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _session?.Dispose();
        _disposed = true;
    }
}
