using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GenmitsuControl.Core;

namespace GenmitsuControl.Tests;

/// <summary>A deterministic, independently implemented GRBL wire-level test double.</summary>
internal sealed class FakeTransport : IGrblTransport
{
    public event Action<string>? LineReceived;
    public event Action<Exception>? Faulted;
    public bool IsOpen { get; private set; }
    public int CloseCount { get; private set; }
    public int OpenFailuresRemaining { get; set; }
    public TimeSpan CloseDelay { get; set; }
    public ConcurrentQueue<string> Commands { get; } = new();
    public ConcurrentQueue<byte> Realtime { get; } = new();
    public Dictionary<int, double> Settings { get; } = new()
    {
        [13] = 0, [20] = 1, [21] = 1, [22] = 1, [23] = 3, [27] = 2,
        [30] = 1000, [31] = 0, [32] = 0, [100] = 200, [101] = 200,
        [102] = 200, [110] = 4000, [111] = 4000, [112] = 2000,
        [120] = 300, [121] = 300, [122] = 300, [130] = 600, [131] = 500, [132] = 110,
    };
    public AxisPosition Machine { get; set; } = new(50, 60, 10);
    public AxisPosition G54 { get; set; } = new(12, 24, 3);
    public AxisPosition G92 { get; set; } = new(0, 0, 0);
    public double ToolLength { get; set; }
    public string State { get; set; } = "Idle";
    public string ActiveWcs { get; set; } = "G54";
    public bool ParserInches { get; set; }
    public bool SendWorkPosition { get; set; }
    public bool IncludeWco { get; set; } = true;
    public bool SuppressStatus { get; set; }
    public bool SuppressPosition { get; set; }
    public bool HoldJogCancel { get; set; }
    public AxisPosition? JogTarget { get; private set; }
    public double JogFeed { get; private set; }
    public Action<byte>? BeforeRealtime { get; set; }
    public string? FailNextCommand { get; set; }
    public string? DropNextCommand { get; set; }
    public int? IgnoreSettingWrite { get; set; }
    public bool IgnoreClearG54 { get; set; }
    public int OmitWcoAfterDatumForQueries { get; set; }
    public int HomingStatusQueries { get; private set; }
    public TimeSpan HomeDelay { get; set; }
    public bool HoldWorkMove { get; set; }
    public bool IgnoreWorkMove { get; set; }
    public int OmitWcoAfterWorkForQueries { get; set; }
    private AxisPosition? _workMoveTarget;
    private int _pendingWcoOmissions;
    private bool _homing;
    public Action<string>? BeforeCommand { get; set; }
    public Task OpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (OpenFailuresRemaining > 0)
        {
            OpenFailuresRemaining--;
            throw new IOException("Port is already in use by another application.");
        }
        IsOpen = true;
        return Task.CompletedTask;
    }
    public async Task CloseAsync()
    {
        if (CloseDelay > TimeSpan.Zero) await Task.Delay(CloseDelay);
        IsOpen = false;
        _workMoveTarget = null;
        JogTarget = null;
        CloseCount++;
    }
    public async ValueTask DisposeAsync() => await CloseAsync();
    public Task WriteAsync(byte[] data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsOpen) throw new IOException("Test port is closed.");
        if (data.Length == 1 && (data[0] == '?' || data[0] == '!' || data[0] == '~' || data[0] >= 0x80 || data[0] == 0x18))
        {
            var b = data[0];
            Realtime.Enqueue(b);
            BeforeRealtime?.Invoke(b);
            if (b == '?' && _homing) HomingStatusQueries++;
            if (b == '?' && !SuppressStatus && !_homing) SendStatus();
            if (b == 0x18) { _workMoveTarget = null; JogTarget = null; State = "Idle"; Emit("Grbl 1.1h ['$' for help]"); }
            if (b == 0x85 && State == "Jog") { JogTarget = null; if (!HoldJogCancel) State = "Idle"; }
            if (b == '!') State = "Hold:0";
            return Task.CompletedTask;
        }
        foreach (var command in Encoding.ASCII.GetString(data).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Commands.Enqueue(command);
            BeforeCommand?.Invoke(command);
            if (Matches(FailNextCommand, command)) { FailNextCommand = null; Emit("error:9"); continue; }
            if (Matches(DropNextCommand, command)) { DropNextCommand = null; continue; }
            Execute(command);
        }
        return Task.CompletedTask;
    }
    private void Execute(string command)
    {
        if (command == "$$")
        {
            foreach (var entry in Settings.OrderBy(x => x.Key)) Emit($"${entry.Key}={Number(entry.Value)}");
        }
        else if (command == "$#")
        {
            Emit($"[G54:{Position(G54)}]");
            foreach (var index in new[] { 55, 56, 57, 58, 59 }) Emit($"[G{index}:0.000,0.000,0.000]");
            Emit("[G28:0.000,0.000,0.000]");
            Emit("[G30:0.000,0.000,0.000]");
            Emit($"[G92:{Position(G92)}]");
            Emit($"[TLO:{Number(Report(ToolLength))}]");
            Emit("[PRB:0.000,0.000,0.000:0]");
        }
        else if (command == "$G") Emit($"[GC:G0 {ActiveWcs} G17 {(ParserInches ? "G20" : "G21")} G90 G94 M5 M9 T0 F0 S0]");
        else if (Regex.Match(command, @"^\$(\d+)\s*=\s*([-+\d.]+)$") is { Success: true } setting)
        {
            var key = int.Parse(setting.Groups[1].Value, CultureInfo.InvariantCulture);
            if (key != IgnoreSettingWrite) Settings[key] = double.Parse(setting.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (command.Contains("G10", StringComparison.OrdinalIgnoreCase))
        {
            if (command.Contains("L20", StringComparison.OrdinalIgnoreCase))
                G54 = new(Machine.X - G92.X, Machine.Y - G92.Y, Machine.Z - G92.Z - ToolLength);
            else if (command.Contains("L2", StringComparison.OrdinalIgnoreCase) && !IgnoreClearG54)
                G54 = new(0, 0, 0);
            _pendingWcoOmissions = OmitWcoAfterDatumForQueries;
        }
        else if (command == "$H")
        {
            if (HomeDelay > TimeSpan.Zero)
            {
                _homing = true;
                State = "Home";
                _ = Task.Run(async () =>
                {
                    await Task.Delay(HomeDelay);
                    if (!IsOpen) return;
                    Machine = new(-2, -2, -2);
                    State = "Idle";
                    _homing = false;
                    Emit("ok");
                });
                return;
            }
            Machine = new(-2, -2, -2);
            State = "Idle";
        }
        else if (Regex.IsMatch(command, @"^G21 G90 G54 G1 X0 Y0 Z0 F\d+(?:\.\d+)?$"))
        {
            ParserInches = false;
            ActiveWcs = "G54";
            _pendingWcoOmissions = OmitWcoAfterWorkForQueries;
            if (!IgnoreWorkMove)
            {
                _workMoveTarget = new(G54.X + G92.X, G54.Y + G92.Y, G54.Z + G92.Z + ToolLength);
                State = "Run";
                if (!HoldWorkMove) CompleteWorkMove();
            }
        }
        else if (command.StartsWith("$J=", StringComparison.OrdinalIgnoreCase))
        {
            var words = Regex.Matches(command[3..], @"([A-Z])\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+))", RegexOptions.IgnoreCase)
                .Select(m => (Letter: char.ToUpperInvariant(m.Groups[1].Value[0]), Value: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
            bool incremental = words.Any(w => w.Letter == 'G' && w.Value == 91);
            bool machineCoordinates = words.Any(w => w.Letter == 'G' && w.Value == 53);
            double scale = words.Any(w => w.Letter == 'G' && w.Value == 20) ? 25.4 : 1;
            double Axis(char name, double start, double offset)
            {
                var axis = words.Where(w => w.Letter == name).ToArray();
                return axis.Length == 0 ? start : axis.Single().Value * scale + (incremental ? start : machineCoordinates ? 0 : offset);
            }
            JogTarget = new(Axis('X', Machine.X, G54.X + G92.X), Axis('Y', Machine.Y, G54.Y + G92.Y), Axis('Z', Machine.Z, G54.Z + G92.Z + ToolLength));
            JogFeed = words.Single(w => w.Letter == 'F').Value * scale;
            State = "Jog";
        }
        else if (command == "G54") ActiveWcs = "G54";
        else if (command == "G20") ParserInches = true;
        else if (command == "G21") ParserInches = false;
        else if (command == "G92.1") G92 = new(0, 0, 0);
        else if (command == "G49") ToolLength = 0;
        Emit("ok");
    }
    public void Emit(string line) => LineReceived?.Invoke(line);
    public void AdvanceJog(double fraction)
    {
        if (JogTarget is not { } target || !IsOpen) return;
        Machine = new(Machine.X + (target.X - Machine.X) * fraction, Machine.Y + (target.Y - Machine.Y) * fraction, Machine.Z + (target.Z - Machine.Z) * fraction);
        if (fraction >= 1) { JogTarget = null; State = "Idle"; }
    }
    public void CompleteJogCancel() { JogTarget = null; State = "Idle"; }
    public void CompleteWorkMove()
    {
        if (_workMoveTarget is not { } target || !IsOpen) return;
        Machine = target;
        _workMoveTarget = null;
        State = "Idle";
    }
    public void Fault(string message)
    {
        IsOpen = false;
        JogTarget = null;
        Faulted?.Invoke(new IOException(message));
    }
    public void SendStatus()
    {
        var activeOffset = ActiveWcs == "G54" ? G54 : new AxisPosition(0, 0, 0);
        var wco = new AxisPosition(activeOffset.X + G92.X, activeOffset.Y + G92.Y, activeOffset.Z + G92.Z + ToolLength);
        var work = new AxisPosition(Machine.X - wco.X, Machine.Y - wco.Y, Machine.Z - wco.Z);
        bool includeWco = IncludeWco && _pendingWcoOmissions <= 0;
        if (_pendingWcoOmissions > 0) _pendingWcoOmissions--;
        var position = SuppressPosition ? "" : $"|{(SendWorkPosition ? "WPos" : "MPos")}:{Position(SendWorkPosition ? work : Machine)}";
        Emit($"<{State}{position}|FS:0,0{(includeWco ? $"|WCO:{Position(wco)}" : "") }>");
    }
    private double Report(double value) => Settings[13] == 1 ? value / 25.4 : value;
    private string Position(AxisPosition position) => $"{Number(Report(position.X))},{Number(Report(position.Y))},{Number(Report(position.Z))}";
    private static string Number(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);
    private static bool Matches(string? expected, string command) => expected is not null && expected.Replace(" ", "").Equals(command.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
}
