using System.IO;
using System.Windows;
using System.Windows.Threading;
using GenmitsuControl.Core;

namespace GenmitsuControl.Desktop;

public partial class MainWindow
{
    /// <summary>Exercises real WPF Closing/Closed events without opening a serial port.</summary>
    internal static async Task RunShutdownTestsAsync(string folder)
    {
        var checks = new List<string>();
        foreach (string scenario in new[] { "Never connected", "Simulator connected", "Already disconnected", "Repeated close during cleanup", "Cleanup failure after release" })
        {
            void Assert(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException($"Shutdown check failed: {scenario}: {description}");
                checks.Add($"PASS {scenario}: {description}");
                File.WriteAllLines(Path.Combine(folder, "shutdown-smoke-results.txt"), checks);
            }

            var window = new MainWindow
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            Application.Current.MainWindow = window;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            ShutdownTestTransport? transport = null;
            GrblController? controller = null;
            if (scenario != "Never connected")
            {
                transport = new ShutdownTestTransport(scenario == "Repeated close during cleanup", scenario == "Cleanup failure after release");
                window._controller = controller = new GrblController(transport);
                window._usingSimulator = true;
                await controller.ConnectAsync();
                Assert(controller.Snapshot.Connected && transport.IsOpen, "simulator is connected before the close scenario");
                if (scenario == "Already disconnected")
                {
                    await controller.DisconnectAsync();
                    Assert(!controller.Snapshot.Connected && !transport.IsOpen, "simulator is disconnected before window close");
                }
            }

            int closedCount = 0;
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool releasedBeforeClosed = false;
            window.Closed += (_, _) =>
            {
                closedCount++;
                releasedBeforeClosed = transport is null || (!transport.IsOpen && transport.DisposeCompleted);
                closed.TrySetResult(true);
            };
            int previousCloseCalls = transport?.CloseCalls ?? 0;
            window.Close();
            if (transport?.DelayClose == true)
            {
                await transport.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(closedCount == 0 && transport.IsOpen, "window waits for pending transport release");
                window.Close();
                window.Close();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Assert(closedCount == 0 && transport.CloseCalls == previousCloseCalls + 1,
                    "repeated close requests neither close early nor repeat cleanup");
                transport.AllowClose.TrySetResult(true);
            }
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(closedCount == 1, "Closed is raised exactly once");
            Assert(releasedBeforeClosed, "transport release and disposal finish before Closed");
            Assert(!window._uiTimer.IsEnabled, "UI timer is stopped");
            if (controller is not null && transport is not null)
            {
                Assert(!controller.Snapshot.Connected && !transport.IsOpen, "controller is disconnected and transport handle is released");
                Assert(transport.DisposeCalls == 1 && transport.CloseCalls == previousCloseCalls + 1,
                    "controller cleanup and transport disposal run once");
                if (scenario == "Cleanup failure after release")
                    Assert(window._log.ToString().Contains(ShutdownTestTransport.SimulatedFailure),
                        "cleanup failure is logged without preventing window closure");
            }
        }
    }

    private sealed class ShutdownTestTransport(bool delayClose, bool failAfterDispose) : IGrblTransport
    {
        public const string SimulatedFailure = "Simulated disposal failure after release.";
        private readonly SimulatedGrblTransport _simulator = new();
        public bool DelayClose { get; } = delayClose;
        public TaskCompletionSource<bool> CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool DisposeCompleted { get; private set; }
        public bool IsOpen => _simulator.IsOpen;
        public event Action<string>? LineReceived { add => _simulator.LineReceived += value; remove => _simulator.LineReceived -= value; }
        public event Action<Exception>? Faulted { add => _simulator.Faulted += value; remove => _simulator.Faulted -= value; }
        public Task OpenAsync(CancellationToken ct = default) => _simulator.OpenAsync(ct);
        public Task WriteAsync(byte[] data, CancellationToken ct = default) => _simulator.WriteAsync(data, ct);
        public async Task CloseAsync()
        {
            CloseCalls++;
            CloseStarted.TrySetResult(true);
            if (DelayClose) await AllowClose.Task.ConfigureAwait(false);
            await _simulator.CloseAsync().ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            await _simulator.DisposeAsync().ConfigureAwait(false);
            DisposeCompleted = true;
            if (failAfterDispose) throw new IOException(SimulatedFailure);
        }
    }
}
