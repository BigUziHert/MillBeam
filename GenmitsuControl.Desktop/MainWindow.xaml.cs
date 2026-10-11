using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GenmitsuControl.Core;
using Microsoft.Win32;

namespace GenmitsuControl.Desktop;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private GrblController? _controller;
    private SimulatedGrblTransport? _activeSimulator;
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _uiTimer;
    private bool _uiBusy;
    private bool _pauseResumeInProgress;
    private bool _displayInches;
    private readonly Dictionary<ComboBox, (double Millimeters, string DisplayText)> _unitValues = new();
    private readonly Dictionary<ComboBox, Dictionary<string, double>> _unitPresets = new();
    private sealed record UnitPreset(string InputText, string DisplayText, double Millimeters);
    private bool _closing;
    private bool _allowClose;
    private bool _usingSimulator;
    private string? _localError;
    private string? _lastLoggedMessage;
    private string? _lastLoggedAttention;
    private const string SimulatorPort = "Simulator — No Machine Connected";

    public MainWindow()
    {
        InitializeComponent();
        UpdateUnitInputs(new[] { 1d, 500d, 1d, 500d });
        RefreshPorts();
        AppendLog("Ready. Connect to a GRBL 1.1 controller, or select Simulator to explore.");
        RenderState();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += (_, _) => RenderState();
        _uiTimer.Start();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => FitLayout();

    private void FitLayout()
    {
        if (RootLayout is null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        double availableWidth = Viewport.ActualWidth, availableHeight = Viewport.ActualHeight;
        double scale = Math.Min(1, availableWidth / 1200);
        // The console absorbs normal resizing. On a smaller desktop or at high DPI,
        // scale only enough to keep every control visible.
        for (int pass = 0; pass < 2; pass++)
        {
            double width = availableWidth / scale;
            var fullWidth = new Size(width, double.PositiveInfinity);
            HeaderBar.Measure(fullWidth);
            FooterBar.Measure(fullWidth);
            double requiredHeight = HeaderBar.DesiredSize.Height + FooterBar.DesiredSize.Height
                + BodyGrid.Margin.Top + BodyGrid.Margin.Bottom + BodyGrid.RowDefinitions[Grid.GetRow(ConsoleCard)].MinHeight;
            var bodyWidth = new Size(width - BodyGrid.Margin.Left - BodyGrid.Margin.Right, double.PositiveInfinity);
            foreach (FrameworkElement section in BodyGrid.Children)
            {
                if (section == ConsoleCard) continue;
                section.Measure(bodyWidth);
                requiredHeight += section.DesiredSize.Height;
            }
            scale = Math.Min(scale, availableHeight / (requiredHeight + 2));
        }
        RootLayout.Width = availableWidth / scale;
        RootLayout.Height = availableHeight / scale;
    }

    private void RefreshPorts()
    {
        var selected = PortPicker.SelectedItem as string;
        var ports = SerialGrblTransport.GetPortNames().OrderBy(x => x).ToList();
        ports.Add(SimulatorPort);
        PortPicker.ItemsSource = ports;
        PortPicker.SelectedItem = ports.Contains(selected ?? "") ? selected : ports[0];
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        try { RefreshPorts(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (_controller is not null) { await _controller.DisposeAsync(); _controller = null; }
        var port = PortPicker.SelectedItem as string ?? throw new InvalidOperationException("Select a serial port.");
        _usingSimulator = port == SimulatorPort;
        // A fresh simulator is used after Dispose, just like a new connection to a controller.
        _activeSimulator = _usingSimulator ? new SimulatedGrblTransport() : null;
        IGrblTransport transport = _activeSimulator is not null ? _activeSimulator : new SerialGrblTransport(port);
        _controller = new GrblController(transport);
        var connectedController = _controller;
        _controller.Log += line => Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
        _controller.StateChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(_controller, connectedController)) RenderState();
        }));
        AppendLog(_usingSimulator ? "SIMULATOR: fresh demo session. No serial port is opened and no physical machine is controlled." : $"Opening {port} at 115200 baud, 8-N-1.");
        await _controller.ConnectAsync();
    });

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try
        {
            // Disconnection remains available while an operation is pending.
            await _controller.DisconnectAsync();
            _localError = null;
            AppendLog("Disconnected. The serial port is released; LightBurn can connect.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { RenderState(); }
    }

    private async Task RunUiAsync(Func<Task> operation)
    {
        if (_uiBusy || _closing) return;
        _uiBusy = true;
        _localError = null;
        RenderState();
        try { await operation(); }
        catch (Exception ex) { ShowError(ex); }
        finally { _uiBusy = false; RenderState(); }
    }

    private GrblController Controller => _controller ?? throw new InvalidOperationException("Connect first.");
    private async void Milling_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => Controller.SwitchModeAsync(false));
    private async void Laser_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => Controller.SwitchModeAsync(true));
    private async void SetDatum_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => Controller.SetWorkDatumAsync());
    private async void ClearDatum_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => Controller.ClearWorkDatumAsync());
    private async void Home_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => Controller.HomeAsync());
    private async void Work_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() =>
        Controller.MoveToWorkZeroAsync(ReadMillimeters(FeedPicker, "Work return XY feed", 10000)));

    private async void Jog_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var direction = (string)((Button)sender).Tag;
        bool z = direction.StartsWith('Z');
        double distance = ReadMillimeters(z ? ZDistancePicker : DistancePicker, z ? "Z jog distance" : "XY jog distance", 100);
        double feed = ReadMillimeters(z ? ZFeedPicker : FeedPicker, z ? "Z jog feed" : "XY jog feed", 10000);
        double Axis(char axis) => direction.Contains($"{axis}+") ? distance : direction.Contains($"{axis}-") ? -distance : 0;
        await Controller.JogAsync(Axis('X'), Axis('Y'), Axis('Z'), feed);
    });

    private ComboBox[] UnitInputs => new[] { DistancePicker, FeedPicker, ZDistancePicker, ZFeedPicker };
    private string UnitSymbol => _displayInches ? "in" : "mm";
    private string FormatInput(double millimeters) => (millimeters / (_displayInches ? 25.4 : 1)).ToString("0.########", CultureInfo.CurrentCulture);

    private double ReadMillimeters(ComboBox picker, string name, double maximum)
    {
        string text = picker.Text.Trim();
        double value;
        // Keep canonical physical values for displayed conversions and presets.
        // Merely switching units never rounds a distance or feed cumulatively.
        if (_unitValues.TryGetValue(picker, out var saved) && text == saved.DisplayText) value = saved.Millimeters;
        else if (_unitPresets.TryGetValue(picker, out var presets) && presets.TryGetValue(text, out var preset)) value = preset;
        else value = PositiveNumber(text, name, maximum / (_displayInches ? 25.4 : 1)) * (_displayInches ? 25.4 : 1);
        if (!double.IsFinite(value) || value <= 0 || value > maximum + 1e-9)
            throw new InvalidOperationException($"{name} must be greater than zero and no more than {FormatInput(maximum)} {UnitSymbol}{(maximum == 10000 ? "/min" : "")}.");
        _unitValues[picker] = (value, text);
        return value;
    }

    private void UpdateUnitInputs(double[] values)
    {
        var inputs = UnitInputs;
        for (int i = 0; i < inputs.Length; i++)
        {
            var picker = inputs[i];
            // Distance and feed menus have useful native increments for each display unit.
            // Keep all stored values in millimeters; changing menus does not alter
            // the physical distance already entered in the editable field.
            bool isFeed = i % 2 == 1;
            double[] presets = isFeed
                ? (_displayInches ? new[] { 5d, 12d, 20d, 40d, 60d, 78d }.Select(inches => inches * 25.4).ToArray()
                                  : new[] { 125d, 300d, 500d, 1000d, 1500d, 2000d })
                : _displayInches
                    ? new[] { .03125, .0625, .125, .25, .5, 1, 2 }.Select(inches => inches * 25.4).ToArray()
                    : new[] { 1d, 2d, 3d, 6d, 13d, 25d, 50d };
            var items = presets.Select(value =>
            {
                string input = FormatInput(value);
                string label = input;
                if (_displayInches && !isFeed)
                {
                    string? fraction = (value / 25.4) switch
                    {
                        .03125 => "1/32", .0625 => "1/16", .125 => "1/8", .25 => "1/4", .5 => "1/2", _ => null
                    };
                    label = fraction is null ? $"{input}″" : $"{input}″ ({fraction}″)";
                }
                return new UnitPreset(input, label, value);
            }).ToArray();
            _unitPresets[picker] = items.ToDictionary(item => item.DisplayText, item => item.Millimeters);
            // Preserve the annotation after selection, while custom numeric text
            // still uses the ordinary number parser. Only exact preset values
            // receive a label after conversion; near-equivalent values never snap.
            picker.DisplayMemberPath = nameof(UnitPreset.DisplayText);
            TextSearch.SetTextPath(picker, nameof(UnitPreset.DisplayText));
            picker.ItemsSource = items;
            string text = items.FirstOrDefault(item => item.Millimeters == values[i])?.DisplayText ?? FormatInput(values[i]);
            picker.Text = text;
            _unitValues[picker] = (values[i], text);
            string axis = i < 2 ? "XY" : "Z";
            System.Windows.Automation.AutomationProperties.SetName(picker, $"{axis} jog {(i % 2 == 0 ? "distance" : "feed")} in {(_displayInches ? "inches" : "millimeters")}{(i % 2 == 1 ? " per minute" : "")}");
        }
    }

    private void Units_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Validate every entry before changing units; invalid text stays in
            // place for correction rather than being replaced with a default.
            var values = UnitInputs.Select((picker, index) => ReadMillimeters(picker,
                $"{(index < 2 ? "XY" : "Z")} jog {(index % 2 == 0 ? "distance" : "feed")}", index % 2 == 0 ? 100 : 10000)).ToArray();
            _displayInches = !_displayInches;
            UpdateUnitInputs(values);
            _localError = null;
            RenderState();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static double PositiveNumber(string text, string name, double maximum)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new InvalidOperationException($"{name} must be a number.");
        if (!double.IsFinite(value) || value <= 0 || value > maximum)
            throw new InvalidOperationException($"{name} must be greater than zero and no more than {maximum}.");
        return value;
    }

    private async void CancelJog_Click(object sender, RoutedEventArgs e)
    {
        try { await Controller.CancelJogAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void PauseJog_Click(object sender, RoutedEventArgs e)
    {
        // Core owns pause/resume serialization. Do not queue this behind UI work.
        if (_pauseResumeInProgress) return;
        _pauseResumeInProgress = true;
        _localError = null;
        RenderState();
        try
        {
            if (Controller.Snapshot.IsJogPaused) await Controller.ResumeJogAsync();
            else await Controller.PauseJogAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _pauseResumeInProgress = false; RenderState(); }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        // Intentionally bypass RunUiAsync: realtime stop must not wait for an acknowledgement.
        try { await Controller.StopAsync(); }
        catch (Exception ex) { ShowError(ex); }
        finally { RenderState(); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCommandAsync();
    private async void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !SendButton.IsEnabled) return;
        e.Handled = true;
        await SendCommandAsync();
    }

    private async Task SendCommandAsync()
    {
        string command = CommandInput.Text.Trim();
        if (string.IsNullOrEmpty(command)) return;
        await RunUiAsync(async () => { await Controller.SendConsoleAsync(command); CommandInput.Clear(); });
    }

    private void ShowError(Exception ex)
    {
        string? controllerMessage = _controller?.Snapshot.Message;
        _localError = controllerMessage is not null &&
            (controllerMessage.Contains("incomplete", StringComparison.OrdinalIgnoreCase) ||
             controllerMessage.Contains("connection closed", StringComparison.OrdinalIgnoreCase) ||
             controllerMessage.Contains("connection lost", StringComparison.OrdinalIgnoreCase))
            ? controllerMessage : ex.Message;
        AppendLog("ERROR: " + ex.Message);
        RenderState();
    }

    private void AppendLog(string line)
    {
        string entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        _log.AppendLine(entry);
        if (_log.Length > 1_000_000) _log.Remove(0, _log.Length - 800_000);
        bool status = line.Contains('<') && line.Contains('|') && line.Contains('>');
        if (status && ShowStatusCheck.IsChecked != true) return;
        if (ConsoleOutput.Text.Length > 150_000) ConsoleOutput.Text = ConsoleOutput.Text[^100_000..];
        ConsoleOutput.AppendText(entry + Environment.NewLine);
        ConsoleOutput.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) { _log.Clear(); ConsoleOutput.Clear(); }
    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Save controller log", FileName = $"MillBeam-{DateTime.Now:yyyyMMdd-HHmmss}.txt", Filter = "Text log (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) == true)
        {
            try { File.WriteAllText(dialog.FileName, _log.ToString()); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void RenderState()
    {
        var s = _controller?.Snapshot;
        bool connected = s?.Connected == true;
        bool busy = _uiBusy || s?.Busy == true;
        UpdatesButton.IsEnabled = !connected && !busy && !_closing;
        bool canMutate = connected && !busy && s!.CanMutate;
        bool paused = connected && s!.IsJogPaused;
        bool pausing = connected && s!.JogPausing;
        bool stale = connected && s!.State.Contains("stale", StringComparison.OrdinalIgnoreCase);
        bool alarming = connected && (s!.State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) || s.State.StartsWith("Door", StringComparison.OrdinalIgnoreCase));
        ConnectionBadge.Text = connected ? (_usingSimulator ? "SIMULATOR" : "CONNECTED") : "DISCONNECTED";
        ConnectionBadge.Foreground = Brush(connected && _usingSimulator ? "#654513" : "#171F57");
        ConnectionBadgeBorder.Background = Brush(connected ? (_usingSimulator ? "#FFF6E2" : "#E2FAF2") : "#EEF2F8");
        ConnectionBadgeBorder.BorderBrush = Brush(connected ? (_usingSimulator ? "#FFD57E" : "#93E4CD") : "#D0D9E9");
        ConnectionDot.Fill = Brush(connected ? (_usingSimulator ? "#FFC53B" : "#00CCA7") : "#A5ADC2");
        ModePanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ModeText.Text = connected ? s!.Mode + (busy ? " · Busy" : s.ModeVerified ? " · Verified" : " · Unverified") : "";
        ModeDot.Fill = Brush(connected ? (!busy && s!.ModeVerified ? "#00CCA7" : "#F0B83F") : "#A5ADC2");
        ControllerStateText.Text = !connected ? "OFFLINE" : stale ? "STALE" : pausing ? "PAUSING" : paused ? "PAUSED" : busy && s!.IsIdle ? "BUSY" : s!.State.ToUpperInvariant();
        ControllerStateText.ToolTip = paused ? "Jog paused; Resume continues to the original target." : s?.State ?? "Disconnected";
        StateBadge.Background = Brush(alarming ? "#FFEAEA" : stale || paused || pausing ? "#FFF3D8" : connected && s!.IsIdle && !busy ? "#D5F8ED" : "#EDF2FA");
        StateBadge.BorderBrush = Brush(alarming ? "#E6A2A8" : stale || paused || pausing ? "#E8CB83" : connected && s!.IsIdle && !busy ? "#A4ECDD" : "#D4DEEE");
        StateDot.Fill = Brush(alarming ? "#D96574" : stale || paused || pausing ? "#E6B943" : connected && s!.IsIdle && !busy ? "#00CCA7" : "#A5ADC2");
        MillingButton.Background = Brush(connected && s!.Mode == "Milling" ? "#DDEFFF" : "#EBF6FF");
        MillingButton.BorderBrush = Brush(connected && s!.Mode == "Milling" ? "#6BB9EF" : "#B4DDFB");
        LaserButton.Background = Brush(connected && s!.Mode == "Laser" ? "#FFF3D5" : "#FFFAEF");
        LaserButton.BorderBrush = Brush(connected && s!.Mode == "Laser" ? "#F1BB39" : "#F5DDA1");
        ConnectButton.IsEnabled = !connected && !busy;
        PortPicker.IsEnabled = RefreshPortsButton.IsEnabled = !connected && !busy;
        DisconnectButton.IsEnabled = connected;
        MillingButton.IsEnabled = LaserButton.IsEnabled = SetDatumButton.IsEnabled = ClearDatumButton.IsEnabled = canMutate;
        JogPad.IsEnabled = ZJogPad.IsEnabled = canMutate;
        HomeButton.IsEnabled = connected && !busy && !paused && !pausing && (s!.IsIdle || s.State.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase));
        WorkButton.IsEnabled = canMutate;
        CancelJogButton.IsEnabled = StopButton.IsEnabled = connected;
        PauseJogButton.IsEnabled = connected && !_pauseResumeInProgress && (s!.CanPauseJog || s.CanResumeJog);
        PauseJogButton.Content = pausing ? "Pausing…" : paused ? "Resume" : "Pause";
        System.Windows.Automation.AutomationProperties.SetName(PauseJogButton, PauseJogButton.Content.ToString());
        PauseJogButton.Background = Brush(paused ? "#FFF3D5" : "#E3F2FE");
        PauseJogButton.BorderBrush = Brush(paused ? "#EACB7D" : "#BBDFFF");
        PauseJogButton.ToolTip = paused ? "Resume the remaining distance to the original jog target at its original feed. Stop discards it."
            : "Pause an arrow-button jog and retain its remaining distance. This does not pause Home or Work moves.";
        DistancePicker.IsEnabled = FeedPicker.IsEnabled = ZDistancePicker.IsEnabled = ZFeedPicker.IsEnabled = !paused && !pausing;
        UnitsButton.IsEnabled = !_uiBusy && !_pauseResumeInProgress;
        UnitsButton.Content = _displayInches ? "Units: inches" : "Units: mm";
        System.Windows.Automation.AutomationProperties.SetName(UnitsButton, _displayInches ? "Display units: inches. Switch to millimeters" : "Display units: millimeters. Switch to inches");
        MachineHeader.Text = $"Machine ({UnitSymbol})";
        StoredOffsetHeader.Text = $"Stored G54 Offset ({UnitSymbol})";
        DistanceLabel.Text = $"XY Step ({UnitSymbol})";
        FeedLabel.Text = $"XY Feed ({UnitSymbol}/min)";
        ZDistanceLabel.Text = $"Z Step ({UnitSymbol})";
        ZFeedLabel.Text = $"Z Feed ({UnitSymbol}/min)";
        SendButton.IsEnabled = connected && !busy;
        string message = _localError ?? s?.Message ?? "Select a serial port and connect, or explore the simulator.";
        bool error = _localError is not null || new[] { "incomplete", "failed", "lost", "timed out", "timeout", "interrupted", "unexpected", "could not", "couldn't", "software stop" }
            .Any(word => message.Contains(word, StringComparison.OrdinalIgnoreCase));
        bool mixedProfile = connected && !busy && !s!.ModeVerified;
        string attentionMessage = stale && !error ? "Controller status is stale. Waiting for fresh data before allowing changes."
            : alarming && !error ? $"Controller state: {s!.State}. Inspect the machine before continuing."
            : mixedProfile && !error ? "Controller settings are unverified or differ from the mode profile. Hover over the mode indicator for the readback."
            : error ? message : "";
        // Progress and attention stay inside existing controls. No action inserts
        // a row or overlay, so every motion control remains under the pointer.
        ControllerStateText.ToolTip = attentionMessage.Length > 0
            ? $"Controller: {s?.State ?? "Disconnected"}. {attentionMessage}"
            : paused ? "Jog paused; Resume continues to the original target." : message;
        if (error && !alarming && !stale)
        {
            ControllerStateText.Text = "ERROR";
            StateBadge.Background = Brush("#FFEAEA");
            StateBadge.BorderBrush = Brush("#E6A2A8");
            StateDot.Fill = Brush("#D96574");
        }
        if (attentionMessage.Length > 0 && attentionMessage != _lastLoggedAttention && attentionMessage != message)
            AppendLog("ATTENTION: " + attentionMessage);
        _lastLoggedAttention = attentionMessage;
        if (s?.Message is { Length: > 0 } currentMessage && currentMessage != _lastLoggedMessage)
        {
            _lastLoggedMessage = currentMessage;
            AppendLog(currentMessage);
        }
        string Setting(int key) => connected && s!.Settings.TryGetValue(key, out var n) ? n.ToString("0.###", CultureInfo.InvariantCulture) : "—";
        SettingsText.Text = connected
            ? $"Readback:  $32 = {Setting(32)}   ·   X {Setting(120)} / Y {Setting(121)} / Z {Setting(122)} mm/s²" + (s!.ReportInches ? "   ·   Controller reports inches" : "")
            : "Mode and acceleration are read from the connected controller.";
        var m = connected ? s!.MachinePosition : null;
        var w = connected ? s!.WorkPosition : null;
        MachineX.Text = Coord(m?.X); MachineY.Text = Coord(m?.Y); MachineZ.Text = Coord(m?.Z);
        WorkX.Text = Coord(w?.X); WorkY.Text = Coord(w?.Y); WorkZ.Text = Coord(w?.Z);
        WorkHeader.Text = connected ? $"Work {s!.ActiveCoordinateSystem} ({UnitSymbol})" : $"Work ({UnitSymbol})";
        var offset = connected ? s!.G54Offset : null;
        G54X.Text = Coord(offset?.X); G54Y.Text = Coord(offset?.Y); G54Z.Text = Coord(offset?.Z);
        FitLayout();
    }

    private string Coord(double? value) => value is { } millimeters
        ? (millimeters / (_displayInches ? 25.4 : 1)).ToString(_displayInches ? "0.0000" : "0.000", CultureInfo.CurrentCulture) : "—";
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        _uiTimer.Stop();
        try { if (_controller is not null) await _controller.DisposeAsync(); }
        catch (Exception ex) { AppendLog("ERROR closing connection: " + ex.Message); }
        finally
        {
            // Disposal can complete synchronously, including when no controller
            // was connected. Let the original Closing event return before Close
            // is called again; repeated close requests must still wait for cleanup.
            _ = Dispatcher.BeginInvoke(new Action(() => { _allowClose = true; Close(); }));
        }
    }

    internal async Task RunSmokeTestAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        var stationaryControls = new FrameworkElement[] { UnitsButton, ConnectionBadgeBorder, PortPicker, ConnectButton,
            MillingButton, LaserButton, JogPad, ZJogPad, DistancePicker, ZDistancePicker, FeedPicker, ZFeedPicker,
            PauseJogButton, WorkButton, HomeButton, SetDatumButton, ClearDatumButton, ConsoleCard, CommandInput, StopButton, UpdatesButton };
        Rect[]? stableBounds = null;
        bool resizingForTest = false;
        string currentAction = "initial layout";
        string? layoutFailure = null;
        int observedLayouts = 0;
        void Assert(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("UI check failed: " + description);
            checks.Add("PASS " + description);
        }
        async Task WaitReady(bool expectError = false)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (_uiBusy && DateTime.UtcNow < deadline) await Task.Delay(50);
            await Task.Delay(200);
            Assert(!_uiBusy, "UI operation completes");
            AssertStableLayout(currentAction + " completed");
            if (_localError is not null && !expectError) throw new InvalidOperationException(_localError);
        }
        async Task WaitUntil(Func<bool> condition, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
            RenderState();
            Assert(condition(), description);
            AssertStableLayout(description);
            if (_localError is not null) throw new InvalidOperationException(_localError);
        }
        void Click(Button button)
        {
            currentAction = string.IsNullOrEmpty(button.Name) ? "Jog " + button.Tag : button.Name;
            Assert(button.IsEnabled, currentAction + " enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertStableLayout(currentAction + " requested");
        }
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(Viewport).TransformBounds(new Rect(element.RenderSize));
        EventHandler watchLayout = (_, _) =>
        {
            if (resizingForTest || stableBounds is null) return;
            observedLayouts++;
            for (int i = 0; i < stationaryControls.Length; i++)
            {
                Rect before = stableBounds[i], after = Bounds(stationaryControls[i]);
                if (Math.Abs(before.X - after.X) > .5 || Math.Abs(before.Y - after.Y) > .5
                    || Math.Abs(before.Width - after.Width) > .5 || Math.Abs(before.Height - after.Height) > .5)
                    layoutFailure ??= $"{stationaryControls[i].Name} shifted during {currentAction}: {before} -> {after}.";
            }
        };
        void AssertStableLayout(string phase)
        {
            UpdateLayout();
            if (layoutFailure is not null) throw new InvalidOperationException("Action layout check failed: " + layoutFailure);
            Assert(stableBounds is not null && FindName("MessageBorder") is null && BodyGrid.RowDefinitions.Count == 4,
                $"{phase}: fixed controls stay in place with no notification row");
        }
        void AssertCommandTextFits(string state)
        {
            string previous = CommandInput.Text;
            int previousCaret = CommandInput.CaretIndex;
            try
            {
                // Never send this sample: check real glyph layout, including descenders,
                // rather than just checking whether the surrounding input box fits.
                CommandInput.Text = "$$  $G  gypq  0123456789";
                CommandInput.CaretIndex = CommandInput.Text.Length;
                CommandInput.Focus();
                UpdateLayout();
                var host = (ScrollViewer)CommandInput.Template.FindName("PART_ContentHost", CommandInput);
                var presenter = (ScrollContentPresenter)host.Template.FindName("PART_ScrollContentPresenter", host);
                Rect viewport = presenter.TransformToAncestor(CommandInput).TransformBounds(new Rect(presenter.RenderSize));
                Rect first = CommandInput.GetRectFromCharacterIndex(0);
                Rect last = CommandInput.GetRectFromCharacterIndex(CommandInput.Text.Length - 1);
                bool fits = !first.IsEmpty && !last.IsEmpty && first.Top >= viewport.Top - .75 && last.Top >= viewport.Top - .75
                    && first.Bottom <= viewport.Bottom + .75 && last.Bottom <= viewport.Bottom + .75;
                Assert(fits, $"{state}: typed command characters fit their inner text viewport (text {first}; viewport {viewport})");
                if (state.StartsWith("connected idle") && WindowState == WindowState.Maximized)
                    Capture("MillBeam-text-entry-preview.png");
            }
            finally
            {
                CommandInput.Text = previous;
                CommandInput.CaretIndex = Math.Min(previousCaret, previous.Length);
            }
        }
        void AssertFits(string state)
        {
            FitLayout();
            UpdateLayout();
            var controls = new FrameworkElement[] { BrandLogo, ConnectionBadgeBorder, PortPicker, RefreshPortsButton,
                ConnectButton, DisconnectButton, MillingButton, LaserButton, JogPad, ZJogPad, DistancePicker,
                FeedPicker, ZDistancePicker, ZFeedPicker, UnitsButton, PauseJogButton, CancelJogButton, SetDatumButton, ClearDatumButton, HomeButton,
                WorkButton, ConsoleOutput, CommandInput, SendButton, StopButton, UpdatesButton };
            foreach (var control in controls)
            {
                var bounds = Bounds(control);
                if (!control.IsVisible || bounds.Left < -.5 || bounds.Top < -.5 || bounds.Right > Viewport.ActualWidth + .5 || bounds.Bottom > Viewport.ActualHeight + .5)
                    throw new InvalidOperationException($"UI layout check failed: {control.Name} is outside {state} viewport ({bounds}; viewport {Viewport.RenderSize}).");
                for (DependencyObject? ancestor = VisualTreeHelper.GetParent(control); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (ancestor is ScrollViewer) throw new InvalidOperationException($"UI layout check failed: {control.Name} requires page scrolling.");
            }
            Assert(Bounds(SendButton).Bottom <= Bounds(FooterBar).Top + .5 && ConsoleOutput.ActualHeight >= 70,
                $"{state}: full interface fits without page scrolling, with console input and software stop visible");
            foreach (var caption in new[] { DistanceLabel, FeedLabel, ZDistanceLabel, ZFeedLabel })
                if (caption.ActualHeight > 24 || caption.ActualWidth < 110)
                    throw new InvalidOperationException($"Jog input caption is cramped in {state}: {caption.Name} {caption.RenderSize}.");
            AssertCommandTextFits(state);
            AssertStepPresetTextFits(state);
        }
        void AssertStepPresetTextFits(string state)
        {
            if (!_displayInches) return;
            foreach (var picker in new[] { DistancePicker, ZDistancePicker })
            {
                string previous = picker.Text;
                var saved = _unitValues[picker];
                try
                {
                    // Exercise the longest annotated choice through real selection.
                    picker.SelectedIndex = 0;
                    UpdateLayout();
                    var input = (TextBox)picker.Template.FindName("PART_EditableTextBox", picker);
                    input.CaretIndex = 0;
                    input.ScrollToHome();
                    UpdateLayout();
                    var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                    var presenter = (ScrollContentPresenter)host.Template.FindName("PART_ScrollContentPresenter", host);
                    Rect viewport = presenter.TransformToAncestor(input).TransformBounds(new Rect(presenter.RenderSize));
                    Rect first = input.GetRectFromCharacterIndex(0);
                    Rect last = input.GetRectFromCharacterIndex(input.Text.Length - 1, true);
                    Assert(!first.IsEmpty && !last.IsEmpty && first.Left >= viewport.Left - .75
                        && last.Right <= viewport.Right + .75 && first.Top >= viewport.Top - .75 && last.Bottom <= viewport.Bottom + .75,
                        $"{state}: {picker.Name} selected decimal and fraction fit their editable viewport (text {first} to {last}; viewport {viewport})");
                    picker.IsDropDownOpen = true;
                    var popup = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
                    var menu = (FrameworkElement)popup.Child;
                    menu.UpdateLayout();
                    var item = (ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(0);
                    var border = (Border)item.Template.FindName("ItemBorder", item);
                    var content = (ContentPresenter)border.Child;
                    string label = ((UnitPreset)picker.Items[0]).DisplayText;
                    var text = new FormattedText(label, CultureInfo.CurrentCulture, picker.FlowDirection,
                        new Typeface(picker.FontFamily, picker.FontStyle, picker.FontWeight, picker.FontStretch),
                        picker.FontSize, picker.Foreground, VisualTreeHelper.GetDpi(picker).PixelsPerDip);
                    Assert(content.ActualWidth >= text.WidthIncludingTrailingWhitespace - 1
                        && content.ActualHeight >= text.Height - 1,
                        $"{state}: {picker.Name} dropdown fits the complete decimal and fraction label");
                    if (state == "inch controls default window") Capture($"MillBeam-{picker.Name}-presets.png", menu);
                    picker.IsDropDownOpen = false;
                    if (state == "inch controls default window") Capture($"MillBeam-{picker.Name}-selected.png");
                }
                finally
                {
                    picker.IsDropDownOpen = false;
                    picker.SelectedIndex = -1;
                    picker.Text = previous;
                    _unitValues[picker] = saved;
                }
            }
        }
        void Capture(string name, FrameworkElement? element = null)
        {
            FitLayout();
            UpdateLayout();
            var visual = element ?? (FrameworkElement)Content;
            var size = visual.RenderSize;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name));
            png.Save(output);
        }
        async Task CheckDesktopSizes(string state, bool capture = false)
        {
            resizingForTest = true;
            var originalState = WindowState;
            double originalWidth = Width, originalHeight = Height;
            WindowState = WindowState.Normal;
            foreach (var size in new[] { (1366d, 728d, "1366x768 desktop"), (1093d, 582d, "1366x768 at 125% scaling"), (911d, 500d, "compact 150% scaling"), (800d, 500d, "minimum window") })
            {
                Width = size.Item1; Height = size.Item2;
                await Task.Delay(120);
                AssertFits(state + " / " + size.Item3);
                if (capture && size.Item1 == 1093) Capture("MillBeam-compact-preview.png");
            }
            WindowState = WindowState.Maximized;
            await Task.Delay(200);
            var dpi = VisualTreeHelper.GetDpi(this);
            AssertFits($"{state} / maximized desktop ({Viewport.ActualWidth:0}×{Viewport.ActualHeight:0} DIP, DPI scale {dpi.DpiScaleX:0.##}, work area {SystemParameters.WorkArea})");
            if (capture) Capture("MillBeam-maximized-preview.png");
            WindowState = WindowState.Normal;
            Width = originalWidth; Height = originalHeight;
            WindowState = originalState;
            await Task.Delay(120);
            UpdateLayout();
            stableBounds = stationaryControls.Select(Bounds).ToArray();
            resizingForTest = false;
        }
        Assert(Title.StartsWith("MillBeam") && Icon is not null && BrandLogo.Source is not null, "MillBeam window title, logo and taskbar/window icon are loaded");
        var handle = new WindowInteropHelper(this).Handle;
        Assert(SendMessage(handle, 0x007F, (IntPtr)1, IntPtr.Zero) != IntPtr.Zero && SendMessage(handle, 0x007F, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero,
            "Windows receives both large and small MillBeam window icons");
        Assert(ModePanel.Visibility == Visibility.Collapsed && ConnectionBadge.Text == "DISCONNECTED", "Disconnected header has one connection indicator");
        AssertFits("disconnected default window");
        stableBounds = stationaryControls.Select(Bounds).ToArray();
        LayoutUpdated += watchLayout;
        Assert(Math.Abs(Bounds(ConnectionBadgeBorder).Right - Bounds(HeaderBar).Right + 28 * Bounds(RootLayout).Width / RootLayout.ActualWidth) < 1,
            "Connection indicator occupies the far right of the header");
        Capture("MillBeam-disconnected-preview.png");
        Assert(!LaserButton.IsEnabled && !SetDatumButton.IsEnabled, "Disconnected mutation controls disabled");
        Assert(UpdatesButton.IsEnabled, "Update check is available while disconnected");
        PortPicker.SelectedItem = SimulatorPort;
        Click(ConnectButton); await WaitReady();
        Assert(Controller.Snapshot.Connected, "Simulator connection completes");
        Assert(!ConnectButton.IsEnabled && !PortPicker.IsEnabled, "Connected serial controls locked");
        Assert(!UpdatesButton.IsEnabled, "Update dialog cannot obstruct motion or stop controls while connected");
        Click(LaserButton); await WaitReady();
        Assert(Controller.Snapshot.Mode == "Laser" && Controller.Snapshot.ModeVerified, "Laser settings verified");
        Assert(Controller.Snapshot.G54Offset is { X: 0, Y: 0, Z: 0 }, "Laser G54 zero readback");
        Assert(ConnectionBadge.Text == "SIMULATOR" && ModeText.Text == "Laser · Verified" && ModePanel.Visibility == Visibility.Visible, "Connected mode and simulator indicators reflect actual state");
        Assert(FindName("MessageBorder") is null && FindName("OperationMessage") is null,
            "Notification banner controls are absent from the interface");
        var positionBeforeDatum = Controller.Snapshot.MachinePosition;
        Click(SetDatumButton); await WaitReady();
        Assert(Controller.Snapshot.WorkPosition is { } work && Math.Abs(work.X) < .002 && Math.Abs(work.Y) < .002 && Math.Abs(work.Z) < .002, "Set datum updates work zero");
        Click(ClearDatumButton); await WaitReady();
        Assert(Controller.Snapshot.MachinePosition == positionBeforeDatum, "Set and Clear do not move the machine");
        Click(MillingButton); await WaitReady();
        Assert(Controller.Snapshot.G54Offset is { X: 0, Y: 0, Z: 0 }, "Milling does not restore datum");
        Click(LaserButton); await WaitReady();
        CommandInput.Text = "$32=2";
        Click(SendButton); await WaitReady(expectError: true);
        Assert(_localError is not null && ControllerStateText.Text == "ERROR" && ControllerStateText.ToolTip.ToString()!.Contains(_localError),
            "Rejected command displays ERROR in the fixed state badge with its explanation in the tooltip");
        Assert(ConsoleOutput.Text.Contains("error:", StringComparison.OrdinalIgnoreCase), "Controller error remains visible in console");
        await CheckDesktopSizes("controller error");
        CommandInput.Text = "$120=999";
        Click(SendButton); await WaitReady();
        Assert(!Controller.Snapshot.ModeVerified && !ModeText.Text.EndsWith("· Verified") && SettingsText.Text.Contains("999"), "Mixed profile displays actual readback without a Verified label");
        Assert(ConsoleOutput.Text.Contains("ATTENTION: Controller settings are unverified"), "Mixed controller profile attention is recorded in the console");
        Click(LaserButton); await WaitReady();
        AssertFits("connected default window");
        Capture("MillBeam-preview.png");
        await CheckDesktopSizes("connected idle", capture: true);
        Click(SetDatumButton); await WaitReady();
        var datumBeforeHome = Controller.Snapshot.G54Offset;
        Click(HomeButton); await WaitReady();
        Assert(Controller.Snapshot.IsIdle && Controller.Snapshot.G54Offset == datumBeforeHome, "Home button finishes without changing datum");
        Click(WorkButton); await WaitReady();
        Assert(Controller.Snapshot.IsIdle && Controller.Snapshot.WorkPosition is { } returned && Math.Abs(returned.X) < .003 && Math.Abs(returned.Y) < .003 && Math.Abs(returned.Z) < .003,
            "Work button returns XYZ to G54 zero");
        Assert(Controller.Snapshot.G54Offset == datumBeforeHome, "Work return preserves the stored datum");
        _activeSimulator!.MotionDuration = TimeSpan.FromMilliseconds(350);
        DistancePicker.Text = "2"; FeedPicker.Text = "600";
        ZDistancePicker.Text = "0.5"; ZFeedPicker.Text = "150";
        foreach (var direction in new[] { "X-Y+", "X+Y+", "X-Y-", "X+Y-" })
        {
            var diagonal = JogPad.Children.OfType<Button>().Single(button => (string)button.Tag == direction);
            var start = Controller.Snapshot.MachinePosition!;
            int logStart = _log.Length;
            Click(diagonal); await WaitReady();
            await WaitUntil(() => Controller.Snapshot.CanMutate, $"{direction} coordinated jog finishes");
            double dx = direction.StartsWith("X-") ? -2 : 2, dy = direction.EndsWith("Y-") ? -2 : 2;
            string expected = FormattableString.Invariant($"$J=G91 G21 X{dx} Y{dy} F600");
            Assert(_log.ToString()[logStart..].Contains(expected), $"{direction} sends both axes in one millimeter jog at XY feed");
            var end = Controller.Snapshot.MachinePosition!;
            Assert(Math.Abs(end.X - start.X - dx) < .003 && Math.Abs(end.Y - start.Y - dy) < .003 && Math.Abs(end.Z - start.Z) < .003,
                $"{direction} moves each XY axis by the selected step and leaves Z unchanged");
        }
        var positiveZ = ZJogPad.Children.OfType<Button>().Single(button => (string)button.Tag == "Z+");
        var zStart = Controller.Snapshot.MachinePosition!;
        Click(positiveZ); await WaitReady();
        await WaitUntil(() => Controller.Snapshot.CanMutate, "Independent Z jog finishes");
        Assert(_log.ToString().Contains("$J=G91 G21 Z0.5 F150") && Math.Abs(Controller.Snapshot.MachinePosition!.Z - zStart.Z - .5) < .003,
            "Z jog uses its own distance and feed independently of XY");
        Assert(DistancePicker.Text == "2" && FeedPicker.Text == "600", "Z jog retains separate XY input values");

        DistancePicker.Text = "12.345678"; FeedPicker.Text = "789.123456";
        ZDistancePicker.Text = "3.456789"; ZFeedPicker.Text = "123.456789";
        double[] physicalInputs = { 12.345678, 789.123456, 3.456789, 123.456789 };
        Assert(new[] { DistancePicker, ZDistancePicker }.All(picker =>
            _unitPresets[picker].Values.SequenceEqual(new[] { 1d, 2d, 3d, 6d, 13d, 25d, 50d })),
            "Both metric step menus are exactly 1, 2, 3, 6, 13, 25 and 50 millimeters");
        Assert(new[] { FeedPicker, ZFeedPicker }.All(picker => _unitPresets[picker].Values.SequenceEqual(new[] { 125d, 300d, 500d, 1000d, 1500d, 2000d })),
            "Both feed lists are exactly 125, 300, 500, 1000, 1500 and 2000 millimeters per minute");
        var settingsBeforeUnits = Controller.Snapshot.Settings.ToDictionary(pair => pair.Key, pair => pair.Value);
        int unitLogStart = _log.Length;
        for (int roundtrip = 0; roundtrip < 3; roundtrip++)
        {
            Click(UnitsButton);
            string InchPreset(double value) => value.ToString("0.########", CultureInfo.CurrentCulture);
            double[] inchSteps = { .03125, .0625, .125, .25, .5, 1, 2 };
            string[] fractionLabels = { " (1/32″)", " (1/16″)", " (1/8″)", " (1/4″)", " (1/2″)", "", "" };
            foreach (var picker in new[] { DistancePicker, ZDistancePicker })
            {
                string customText = picker.Text;
                var saved = _unitValues[picker];
                Assert(picker.Items.Cast<UnitPreset>().Select(item => item.DisplayText).SequenceEqual(
                    inchSteps.Select((value, index) => InchPreset(value) + "″" + fractionLabels[index])),
                    $"{picker.Name}: inch menu shows decimal values with fractions through 2 inches");
                for (int index = 0; index < inchSteps.Length; index++)
                {
                    picker.SelectedIndex = index;
                    Assert(picker.Text == InchPreset(inchSteps[index]) + "″" + fractionLabels[index]
                        && Math.Abs(ReadMillimeters(picker, "inch preset", 100) - inchSteps[index] * 25.4) < 1e-12,
                        $"{picker.Name}: selecting inch preset {index + 1} retains its annotated label and exact millimeter conversion");
                }
                // Restore the custom value after deliberately selecting each preset.
                picker.SelectedIndex = -1;
                picker.Text = customText;
                _unitValues[picker] = saved;
            }
            Assert(_displayInches && MachineHeader.Text == "Machine (in)" && StoredOffsetHeader.Text.EndsWith("(in)") && FeedLabel.Text.Contains("in/min") && ZDistanceLabel.Text.EndsWith("(in)"),
                "Inch display updates coordinate, XY and Z labels");
            var machine = Controller.Snapshot.MachinePosition!;
            var currentWork = Controller.Snapshot.WorkPosition!;
            var currentOffset = Controller.Snapshot.G54Offset!;
            string Inches(double value) => (value / 25.4).ToString("0.0000", CultureInfo.CurrentCulture);
            Assert(MachineX.Text == Inches(machine.X) && MachineY.Text == Inches(machine.Y) && MachineZ.Text == Inches(machine.Z)
                && WorkX.Text == Inches(currentWork.X) && WorkY.Text == Inches(currentWork.Y) && WorkZ.Text == Inches(currentWork.Z)
                && G54X.Text == Inches(currentOffset.X) && G54Y.Text == Inches(currentOffset.Y) && G54Z.Text == Inches(currentOffset.Z),
                "All machine, work and stored datum coordinates convert to inches");
            Click(UnitsButton);
            Assert(!_displayInches && UnitInputs.Select((picker, index) => Math.Abs(ReadMillimeters(picker, "roundtrip", index % 2 == 0 ? 100 : 10000) - physicalInputs[index]) < 1e-10).All(match => match),
                "Millimeter/inch roundtrip preserves all four custom physical inputs without drift");
        }
        DistancePicker.Text = "25"; FeedPicker.Text = "125";
        ZDistancePicker.Text = "0.1"; ZFeedPicker.Text = "300";
        double[] unsnappedInputs = { 25d, 125d, .1, 300d };
        for (int toggle = 0; toggle < 6; toggle++)
        {
            Click(UnitsButton);
            Assert(UnitInputs.Select((picker, index) => Math.Abs(ReadMillimeters(picker, "native preset roundtrip",
                index % 2 == 0 ? 100 : 10000) - unsnappedInputs[index]) < 1e-12).All(match => match),
                "Unit changes preserve native metric presets and custom 0.1 mm without snapping to nearby inch presets");
        }
        Assert(! _log.ToString()[unitLogStart..].Contains("> ") && Controller.Snapshot.Settings.All(pair => settingsBeforeUnits[pair.Key] == pair.Value),
            "Display-unit toggles send no commands and change no controller settings");
        Click(UnitsButton);
        Assert(new[] { FeedPicker, ZFeedPicker }.All(picker =>
            _unitPresets[picker].Keys.SequenceEqual(new[] { "5", "12", "20", "40", "60", "78" })
            && picker.Items.Cast<UnitPreset>().All(item => item.DisplayText == item.InputText)
            && _unitPresets[picker].Values.SequenceEqual(new[] { 5d, 12d, 20d, 40d, 60d, 78d }.Select(value => value * 25.4))),
            "Both inch feed menus use 5, 12, 20, 40, 60 and 78 in/min with exact millimeter conversion");
        DistancePicker.SelectedIndex = 5; ZDistancePicker.SelectedIndex = 6;
        Click(UnitsButton);
        Assert(ReadMillimeters(DistancePicker, "one inch", 100) == 25.4 && ReadMillimeters(ZDistancePicker, "two inches", 100) == 50.8,
            "Selected 1 and 2 inch steps convert to 25.4 and 50.8 mm without snapping to metric presets");
        Click(UnitsButton);
        Assert(DistancePicker.Text == "1″" && ZDistancePicker.Text == "2″", "Independent whole-inch steps survive a metric roundtrip");
        DistancePicker.Text = "0.1"; FeedPicker.Text = "20";
        ZDistancePicker.Text = "0.025"; ZFeedPicker.Text = "5";
        int inchJogLogStart = _log.Length;
        var inchDiagonal = JogPad.Children.OfType<Button>().Single(button => (string)button.Tag == "X+Y+");
        Click(inchDiagonal); await WaitReady();
        await WaitUntil(() => Controller.Snapshot.CanMutate, "Inch XY jog finishes");
        Click(positiveZ); await WaitReady();
        await WaitUntil(() => Controller.Snapshot.CanMutate, "Inch Z jog finishes");
        string inchJogLog = _log.ToString()[inchJogLogStart..];
        Assert(inchJogLog.Contains("$J=G91 G21 X2.54 Y2.54 F508") && inchJogLog.Contains("$J=G91 G21 Z0.635 F127"),
            "Inch XY and Z commands convert distance and feed to explicit G21 millimeters");
        AssertFits("inch controls default window");
        Capture("MillBeam-inches-preview.png");
        await CheckDesktopSizes("inch controls");
        Click(WorkButton); await WaitReady();
        Assert(_log.ToString().Contains("G21 G90 G54 G1 X0 Y0 Z0 F508"), "Work return uses the converted XY feed while Z feed remains independent");
        var cultureBefore = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            DistancePicker.Text = "0,1";
            Assert(Math.Abs(ReadMillimeters(DistancePicker, "locale input", 100) - 2.54) < 1e-10, "Inch input accepts the current culture's decimal separator");
        }
        finally { CultureInfo.CurrentCulture = cultureBefore; }
        DistancePicker.Text = "invalid";
        int invalidUnitLogStart = _log.Length;
        Click(UnitsButton);
        Assert(_displayInches && DistancePicker.Text == "invalid" && _localError is not null && !_log.ToString()[invalidUnitLogStart..].Contains("> "),
            "Invalid input blocks unit switching, retains the entered text and sends no command");
        DistancePicker.Text = "0.1";
        Click(UnitsButton);
        Assert(!_displayInches && _localError is null, "Corrected input allows switching back to millimeters");
        _activeSimulator!.MotionDuration = TimeSpan.FromSeconds(3);
        DistancePicker.Text = "1"; FeedPicker.Text = "500";
        var positiveX = JogPad.Children.OfType<Button>().Single(button => (string)button.Tag == "X+");
        var jogStart = Controller.Snapshot.MachinePosition!;
        UpdateLayout();
        void AssertPauseLayout(string phase)
        {
            RenderState();
            AssertStableLayout(phase);
        }
        Click(positiveX);
        AssertPauseLayout("Jog requested");
        await WaitReady();
        AssertPauseLayout("Jog running");
        Assert(_log.ToString().Contains("$J=G91 G21 X1 F500"), "Jog button uses entered distance and feed");
        Assert(!LaserButton.IsEnabled && !SetDatumButton.IsEnabled && !WorkButton.IsEnabled && StopButton.IsEnabled && DisconnectButton.IsEnabled,
            "Jog disables mutation controls while Stop and Disconnect stay available");
        Click(PauseJogButton);
        AssertPauseLayout("Pause requested");
        await WaitUntil(() => Controller.Snapshot.IsJogPaused && Controller.Snapshot.CanResumeJog, "Pause waits for stopped position and enables resume");
        AssertPauseLayout("Jog paused");
        Assert(PauseJogButton.Content.ToString() == "Resume" && ControllerStateText.Text == "PAUSED", "Paused UI offers Resume and shows PAUSED");
        Assert(!HomeButton.IsEnabled && !WorkButton.IsEnabled && !SetDatumButton.IsEnabled && !LaserButton.IsEnabled && CancelJogButton.IsEnabled && StopButton.IsEnabled,
            "Paused jog blocks other mutations and retains stop controls");
        var pausedPosition = Controller.Snapshot.MachinePosition;
        await Task.Delay(500);
        Assert(Controller.Snapshot.MachinePosition == pausedPosition, "Paused jog remains stationary until Resume");
        AssertFits("paused default window");
        Capture("MillBeam-paused-preview.png");
        await CheckDesktopSizes("paused jog");
        int pausedUnitLogStart = _log.Length;
        Click(UnitsButton);
        Assert(Controller.Snapshot.IsJogPaused && Controller.Snapshot.CanResumeJog && PauseJogButton.Content.ToString() == "Resume"
            && ! _log.ToString()[pausedUnitLogStart..].Contains("> "), "Changing display units while paused preserves the saved jog and sends no command");
        AssertPauseLayout("Paused unit change");
        Click(PauseJogButton);
        AssertPauseLayout("Resume requested");
        await WaitUntil(() => !Controller.Snapshot.IsJogPaused && Controller.Snapshot.State == "Jog", "Resume restarts the jog explicitly");
        AssertPauseLayout("Jog resumed");
        await WaitUntil(() => Controller.Snapshot.IsIdle && !Controller.Snapshot.Busy, "Resumed jog finishes");
        Assert(Controller.Snapshot.MachinePosition is { } finished && Math.Abs(finished.X - jogStart.X - 1) < .003 && Math.Abs(finished.Y - jogStart.Y) < .003 && Math.Abs(finished.Z - jogStart.Z) < .003,
            "Resume reaches original target instead of repeating the full distance");
        Assert(PauseJogButton.Content.ToString() == "Pause" && !PauseJogButton.IsEnabled, "Completed jog has no remaining Resume action");
        Click(UnitsButton);
        Click(positiveX); await WaitReady();
        Click(PauseJogButton);
        await WaitUntil(() => Controller.Snapshot.CanResumeJog, "A new jog can be paused");
        Click(CancelJogButton);
        await WaitUntil(() => !Controller.Snapshot.IsJogPaused && Controller.Snapshot.IsIdle, "Stop discards the paused jog");
        Assert(!PauseJogButton.IsEnabled && PauseJogButton.Content.ToString() == "Pause", "Stopped jog cannot be resumed");
        _activeSimulator.MotionDuration = TimeSpan.FromSeconds(1);
        Click(StopButton); await Task.Delay(600);
        Assert(!Controller.Snapshot.Connected && !LaserButton.IsEnabled, "Software stop disconnects and disables mutations");
        Click(ConnectButton); await WaitReady();
        Click(DisconnectButton); await Task.Delay(300);
        Assert(!Controller.Snapshot.Connected && !LaserButton.IsEnabled, "Disconnect disables controls");
        Assert(UpdatesButton.IsEnabled, "Disconnect makes GitHub updates available again");
        Assert(ModePanel.Visibility == Visibility.Collapsed && ModeText.Text.Length == 0, "Disconnect removes redundant mode status");
        AssertStableLayout("final disconnected state");
        LayoutUpdated -= watchLayout;
        Assert(observedLayouts > 50, $"Continuous geometry monitor observed {observedLayouts} layouts across all actions without a transient shift");
        File.WriteAllLines(Path.Combine(folder, "ui-smoke-results.txt"), checks);
        File.WriteAllText(Path.Combine(folder, "ui-smoke-log.txt"), _log.ToString());
    }
}
