namespace GenmitsuControl.Core;

public interface IGrblTransport : IAsyncDisposable
{
    event Action<string>? LineReceived;
    event Action<Exception>? Faulted;
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct = default);
    Task WriteAsync(byte[] data, CancellationToken ct = default);
    Task CloseAsync();
}

/// <summary>All application coordinates and distances are millimetres.</summary>
public sealed record AxisPosition(double X, double Y, double Z)
{
    public bool IsZero(double tolerance = 0.001) => Math.Abs(X) <= tolerance && Math.Abs(Y) <= tolerance && Math.Abs(Z) <= tolerance;
}

public sealed record ControllerSnapshot(
    bool Connected,
    bool Busy,
    bool IsIdle,
    string State,
    string Mode,
    string Message,
    AxisPosition? MachinePosition,
    AxisPosition? WorkPosition,
    AxisPosition? G54Offset,
    IReadOnlyDictionary<int, double> Settings,
    string ActiveCoordinateSystem,
    bool ReportInches,
    bool CanMutate,
    DateTimeOffset? LastStatusUtc,
    bool ModeVerified)
{
    /// <summary>The app has cancelled a jog and retained its destination for explicit resume.</summary>
    public bool IsJogPaused { get; init; }
    public bool JogPausing { get; init; }
    public bool CanPauseJog { get; init; }
    public bool CanResumeJog { get; init; }
}

public sealed class GrblCommandException : InvalidOperationException
{
    public string Command { get; }
    public string Response { get; }
    public GrblCommandException(string command, string response) : base($"Controller rejected '{command}': {response}")
    {
        Command = command;
        Response = response;
    }
}
