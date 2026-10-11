using System.Text.RegularExpressions;
using GenmitsuControl.Core;

namespace GenmitsuControl.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        await Run("Connect reads actual mode, settings and both coordinate systems", async () => await WithController(async (c, t) =>
        {
            Check(c.Snapshot.Connected && c.Snapshot.IsIdle, "Expected connected Idle state.");
            Check(c.Snapshot.ModeVerified, "Initial mode must be based on settings readback.");
            Position(c.Snapshot.MachinePosition, 50, 60, 10);
            Position(c.Snapshot.WorkPosition, 38, 36, 7);
            Position(c.Snapshot.G54Offset, 12, 24, 3);
            Check(c.Snapshot.Settings[100] == 200, "Calibration readback missing.");
            await Task.CompletedTask;
        }));

        await Run("Laser writes exactly four settings and clears/verifies G54", async () => await WithController(async (c, t) =>
        {
            var unrelated = t.Settings.Where(p => !new[] { 32, 120, 121, 122 }.Contains(p.Key)).ToDictionary();
            t.Commands.Clear();
            await c.SwitchModeAsync(true);
            var commands = t.Commands.ToArray();
            Sequence(commands.Where(IsSettingWrite), "$32=1", "$120=1000", "$121=1000", "$122=300");
            Check(Array.IndexOf(commands, "M5") >= 0 && Array.IndexOf(commands, "M5") < Array.IndexOf(commands, "$32=1"), "Mode settings changed before output was stopped with M5.");
            Check(commands.Any(IsClearDatum), "Laser entry did not clear G54.");
            var clearIndex = Array.FindIndex(commands, IsClearDatum);
            Check(Array.FindLastIndex(commands, x => x == "$#") > clearIndex, "G54 was not read back after clearing.");
            Check(Array.FindLastIndex(commands, x => x == "$$") > Array.FindLastIndex(commands, IsSettingWrite), "Mode settings were not read back after writes.");
            Check(c.Snapshot.ModeVerified, "Laser mode was not verified.");
            Position(c.Snapshot.G54Offset, 0, 0, 0);
            Check(unrelated.All(p => t.Settings[p.Key] == p.Value), "Unrelated calibration, travel or limit settings changed.");
            Check(!commands.Any(x => x.StartsWith("$RST", StringComparison.OrdinalIgnoreCase)), "Factory reset is forbidden.");
        }));

        await Run("Returning to milling never restores an earlier milling datum", async () => await WithController(async (c, t) =>
        {
            await c.SwitchModeAsync(true);
            t.Commands.Clear();
            await c.SwitchModeAsync(false);
            Sequence(t.Commands.Where(IsSettingWrite), "$32=0", "$120=300", "$121=300", "$122=300");
            var commands = t.Commands.ToArray();
            Check(Array.IndexOf(commands, "M5") >= 0 && Array.IndexOf(commands, "M5") < Array.IndexOf(commands, "$32=0"), "Milling settings changed before output was stopped with M5.");
            Check(!t.Commands.Any(IsAnyDatumWrite), "Milling mode wrote or restored a datum.");
            Position(c.Snapshot.G54Offset, 0, 0, 0);
            Check(c.Snapshot.ModeVerified, "Milling was not verified.");
        }));

        await Run("A deliberately set laser datum remains until a requested clear", async () => await WithController(async (c, t) =>
        {
            await c.SwitchModeAsync(true);
            await c.SetWorkDatumAsync();
            Position(c.Snapshot.WorkPosition, 0, 0, 0);
            Position(c.Snapshot.G54Offset, 50, 60, 10);
            t.Commands.Clear();
            await c.SwitchModeAsync(false);
            Check(!t.Commands.Any(IsAnyDatumWrite), "Milling entry changed the deliberately set laser datum.");
            Position(c.Snapshot.G54Offset, 50, 60, 10);
        }));

        await Run("Set and clear datum do not issue movement", async () => await WithController(async (c, t) =>
        {
            var initial = t.Machine;
            t.Commands.Clear();
            await c.SetWorkDatumAsync();
            Position(c.Snapshot.WorkPosition, 0, 0, 0);
            await c.ClearWorkDatumAsync();
            Position(c.Snapshot.G54Offset, 0, 0, 0);
            Position(c.Snapshot.WorkPosition, 50, 60, 10);
            Check(t.Machine == initial, "Datum operation moved machine.");
            Check(!t.Commands.Any(IsMotion), "Datum operation sent a motion or home command.");
        }));

        await Run("Home runs homing without modifying work datum", async () => await WithController(async (c, t) =>
        {
            var datum = t.G54;
            t.Commands.Clear();
            await c.HomeAsync();
            Check(t.Commands.Count(x => x == "$H") == 1, "Expected one homing command.");
            Check(!t.Commands.Any(IsAnyDatumWrite), "Home silently changed a work datum.");
            Check(t.G54 == datum, "Homing changed stored G54.");
        }));

        await Run("Work returns to G54 XYZ zero in millimetres without changing offsets", async () => await WithController(async (c, t) =>
        {
            var datum = t.G54;
            var settings = t.Settings.ToDictionary();
            t.Commands.Clear();
            await c.MoveToWorkZeroAsync(600.5);
            var commands = t.Commands.ToArray();
            const string move = "G21 G90 G54 G1 X0 Y0 Z0 F600.5";
            Sequence(commands.Where(IsMotion), move);
            Check(Array.IndexOf(commands, "M5") >= 0 && Array.IndexOf(commands, "M5") < Array.IndexOf(commands, move), "Work motion started before M5 acknowledgement.");
            Check(!commands.Any(IsAnyDatumWrite) && !commands.Any(IsSettingWrite), "Work return changed offsets or settings.");
            Check(t.G54 == datum && t.G92 == new AxisPosition(2, 3, 1) && t.ToolLength == 4, "Work return altered stored or temporary offsets.");
            Check(settings.All(p => t.Settings[p.Key] == p.Value), "Work return changed controller calibration.");
            Check(c.Snapshot.ActiveCoordinateSystem == "G54" && !t.ParserInches && c.Snapshot.IsIdle, "Work return did not select G54 and explicit millimetres.");
            Position(c.Snapshot.WorkPosition, 0, 0, 0);
            Position(c.Snapshot.MachinePosition, 14, 27, 8);
        }, setup: t => { t.ActiveWcs = "G55"; t.ParserInches = true; t.Settings[13] = 1; t.G92 = new(2, 3, 1); t.ToolLength = 4; }));

        await Run("Work feed validation rejects invalid values before any command", async () => await WithController(async (c, t) =>
        {
            t.Commands.Clear();
            foreach (var feed in new[] { 0, -1, double.NaN, double.PositiveInfinity, 10000.1, 0.00001 })
                await MustFail(() => c.MoveToWorkZeroAsync(feed));
            Check(t.Commands.IsEmpty, "Invalid feed transmitted a controller command.");
        }));

        await Run("Work return requires connected fresh Idle before starting motion", async () => await WithController(async (c, t) =>
        {
            t.State = "Run";
            t.SendStatus();
            t.Commands.Clear();
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            Check(!t.Commands.Any(IsMotion) && !t.Commands.Contains("M5"), "Non-idle Work return sent a motion or tool command.");
            await c.DisconnectAsync();
            t.Commands.Clear();
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            Check(t.Commands.IsEmpty, "Disconnected Work return transmitted commands.");
        }));

        await Run("Work early acknowledgement remains busy until fresh zero and Idle", async () => await WithController(async (c, t) =>
        {
            t.HoldWorkMove = true;
            t.OmitWcoAfterWorkForQueries = 3;
            t.Commands.Clear();
            var pending = c.MoveToWorkZeroAsync(500);
            await WaitUntil(() => c.Snapshot.State == "Run", TimeSpan.FromSeconds(2));
            Check(!pending.IsCompleted && c.Snapshot.Busy && !c.Snapshot.CanMutate, "Early ok falsely completed Work return.");
            await MustFail(() => c.SwitchModeAsync(true));
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            Check(t.Commands.Count(IsMotion) == 1, "A second operation queued another move.");
            t.CompleteWorkMove();
            await pending;
            Check(c.Snapshot.IsIdle && !c.Snapshot.Busy, "Completed Work return did not release busy state.");
            Position(c.Snapshot.WorkPosition, 0, 0, 0);
        }));

        await Run("Rejected M5 or work motion is not replayed and is reported incomplete", async () => await WithController(async (c, t) =>
        {
            t.Commands.Clear();
            t.FailNextCommand = "M5";
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            Check(!t.Commands.Any(IsMotion), "Work motion continued after rejected M5.");
            t.Commands.Clear();
            t.FailNextCommand = "G21 G90 G54 G1 X0 Y0 Z0 F500";
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            Check(t.Commands.Count(IsMotion) == 1, "Rejected work motion was replayed.");
            Check(c.Snapshot.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase), "Work failure was hidden.");
            Check(t.Commands.Contains("$$") && t.Commands.Contains("$#"), "Work failure did not refresh actual state.");
            Position(c.Snapshot.MachinePosition, 50, 60, 10);
        }));

        await Run("Idle away from zero cannot report successful Work completion", async () => await WithController(async (c, t) =>
        {
            t.IgnoreWorkMove = true;
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            Check(c.Snapshot.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase), "Incorrect work coordinates were accepted as success.");
            Position(c.Snapshot.WorkPosition, 38, 36, 7);
        }));

        await Run("Software stop interrupts acknowledged Work motion without replay", async () => await WithController(async (c, t) =>
        {
            t.HoldWorkMove = true;
            t.Commands.Clear();
            var original = t.Machine;
            var pending = c.MoveToWorkZeroAsync(500);
            await WaitUntil(() => c.Snapshot.State == "Run", TimeSpan.FromSeconds(2));
            await c.StopAsync();
            await MustFail(() => pending);
            t.CompleteWorkMove();
            Check(t.Realtime.Contains((byte)0x18) && !c.Snapshot.Connected, "Software stop did not reset and disconnect.");
            Check(t.Commands.Count(IsMotion) == 1 && t.Machine == original, "Stopped Work motion was replayed or completed.");
        }));

        await Run("Disconnect interrupts acknowledged Work motion and releases transport", async () => await WithController(async (c, t) =>
        {
            t.HoldWorkMove = true;
            t.Commands.Clear();
            var pending = c.MoveToWorkZeroAsync(500);
            await WaitUntil(() => c.Snapshot.State == "Run", TimeSpan.FromSeconds(2));
            await c.DisconnectAsync();
            await MustFail(() => pending);
            Check(!t.IsOpen && !c.Snapshot.Busy && !c.Snapshot.CanMutate, "Disconnect left Work motion or transport active.");
            Check(t.Commands.Count(IsMotion) == 1, "Disconnected Work motion was replayed.");
        }));

        await Run("Missing status interrupts acknowledged Work motion without replay", async () => await WithController(async (c, t) =>
        {
            t.HoldWorkMove = true;
            t.Commands.Clear();
            var pending = c.MoveToWorkZeroAsync(500);
            await WaitUntil(() => c.Snapshot.State == "Run", TimeSpan.FromSeconds(2));
            t.SuppressStatus = true;
            await MustFail(() => pending);
            // Background polling can cancel the operation before its asynchronous
            // port closure finishes. Both must settle, without sending more motion.
            await WaitUntil(() => !t.IsOpen && !c.Snapshot.Connected, TimeSpan.FromSeconds(1));
            Check(!t.IsOpen && !c.Snapshot.Connected && !c.Snapshot.CanMutate, "Missing status left Work return connected and actionable.");
            Check(t.Commands.Count(IsMotion) == 1, "Timed-out Work motion was replayed.");
        }));

        await Run("A setting error stops the transaction and refreshes actual settings", async () => await WithController(async (c, t) =>
        {
            t.Commands.Clear();
            t.FailNextCommand = "$121=1000";
            await AllowFailure(() => c.SwitchModeAsync(true));
            var commands = t.Commands.ToArray();
            Check(!commands.Contains("$122=300"), "Transaction continued after rejected setting.");
            Check(!commands.Any(IsClearDatum), "Failed mode transaction unexpectedly cleared datum.");
            Check(commands.Contains("$$") && commands.Contains("$#"), "Actual state was not refreshed after failure.");
            Check(c.Snapshot.Settings[32] == 1 && c.Snapshot.Settings[120] == 1000 && c.Snapshot.Settings[121] == 300, "Displayed settings do not reflect the partial update.");
            Incomplete(c);
        }));

        await Run("Acknowledged but mismatched setting cannot report success", async () => await WithController(async (c, t) =>
        {
            t.IgnoreSettingWrite = 121;
            await AllowFailure(() => c.SwitchModeAsync(true));
            Check(c.Snapshot.Settings[121] == 300, "Actual readback was hidden by requested value.");
            Incomplete(c);
        }));

        await Run("Acknowledged G54 clear must also pass offset verification", async () => await WithController(async (c, t) =>
        {
            t.IgnoreClearG54 = true;
            await AllowFailure(() => c.SwitchModeAsync(true));
            Position(c.Snapshot.G54Offset, 12, 24, 3);
            Incomplete(c);
        }));

        await Run("Even a reported 0.001 mm G54 offset fails zero verification", async () => await WithController(async (c, t) =>
        {
            t.IgnoreClearG54 = true;
            await AllowFailure(() => c.SwitchModeAsync(true));
            Incomplete(c);
            Check(c.Snapshot.G54Offset?.X == 0.001, "Small nonzero readback must remain visible.");
        }, setup: t => t.G54 = new(0.001, 0, 0)));

        await Run("Datum refresh waits for fresh WCO after prewrite Idle status", async () => await WithController(async (c, t) =>
        {
            var unexpectedAfterAck = false;
            var datumApplied = false;
            var datumCommandSent = false;
            c.StateChanged += () =>
            {
                if (datumApplied && c.Snapshot.WorkPosition is { } work && !work.IsZero(1e-9)) unexpectedAfterAck = true;
            };
            t.OmitWcoAfterDatumForQueries = 10;
            t.BeforeCommand = command =>
            {
                if (command.Contains("G10 L20")) datumCommandSent = true;
                if (datumCommandSent && command == "$#") datumApplied = true;
            };
            await c.SetWorkDatumAsync();
            Position(c.Snapshot.WorkPosition, 0, 0, 0);
            Check(!unexpectedAfterAck, "Old WCO produced stale work coordinates after datum write.");
            t.BeforeCommand = null;
            datumApplied = false;
            await c.ClearWorkDatumAsync();
            Position(c.Snapshot.WorkPosition, 50, 60, 10);
        }));

        await Run("No fresh WCO leaves work position unavailable after datum clear", async () => await WithController(async (c, t) =>
        {
            t.OmitWcoAfterDatumForQueries = 100;
            await c.ClearWorkDatumAsync();
            Position(c.Snapshot.MachinePosition, 50, 60, 10);
            Check(c.Snapshot.WorkPosition is null, "Displayed work position reused stale prewrite WCO.");
        }));

        await Run("Set datum accounts for active G92 and tool length without moving", async () => await WithController(async (c, t) =>
        {
            var originalMachine = t.Machine;
            t.Commands.Clear();
            await c.SetWorkDatumAsync();
            Position(c.Snapshot.WorkPosition, 0, 0, 0);
            Position(c.Snapshot.G54Offset, 48, 57, 5);
            Check(t.G92 == new AxisPosition(2, 3, 1) && t.ToolLength == 4, "Set datum changed unrelated temporary or tool offsets.");
            Check(t.Machine == originalMachine && !t.Commands.Any(IsMotion), "Setting compensated datum commanded motion.");
        }, setup: t => { t.G92 = new(2, 3, 1); t.ToolLength = 4; }));

        await Run("Homing survives status suppression beyond ordinary command timeout", async () => await WithController(async (c, t) =>
        {
            t.State = "Alarm";
            t.SendStatus();
            t.HomeDelay = TimeSpan.FromMilliseconds(750);
            await c.HomeAsync();
            Check(c.Snapshot.Connected && c.Snapshot.IsIdle, "Homing status suppression closed the connection.");
            Check(t.HomingStatusQueries == 0, "Polling continued while GRBL homing suppressed status.");
            Position(c.Snapshot.G54Offset, 12, 24, 3);
        }, commandTimeout: TimeSpan.FromMilliseconds(120)));

        await Run("Running controller blocks mode changes and datum changes", async () => await WithController(async (c, t) =>
        {
            t.State = "Run";
            t.SendStatus();
            t.Commands.Clear();
            await AllowFailure(() => c.SwitchModeAsync(true));
            await AllowFailure(() => c.SetWorkDatumAsync());
            await AllowFailure(() => c.ClearWorkDatumAsync());
            Check(!t.Commands.Any(IsSettingWrite) && !t.Commands.Any(IsAnyDatumWrite), "Idle interlock allowed mutation while running.");
            Check(!c.Snapshot.CanMutate, "UI mutation availability is unsafe while running.");
        }));

        await Run("Jog uses GRBL jog command and realtime cancellation", async () => await WithController(async (c, t) =>
        {
            t.Commands.Clear();
            await c.JogAsync('X', -2.5, 600);
            Check(t.Commands.Any(x => x.StartsWith("$J=", StringComparison.Ordinal) && x.Contains("G21") && x.Contains("G91") && x.Contains("X-2.5") && x.Contains("F600")), "Jog command must state millimetres and incremental travel.");
            await c.CancelJogAsync();
            Check(t.Realtime.Contains((byte)0x85), "Jog cancel must use GRBL realtime byte 0x85.");
        }));

        await Run("Software stop bypasses a pending acknowledgement", async () => await WithController(async (c, t) =>
        {
            t.DropNextCommand = "$G";
            t.Commands.Clear();
            var pending = c.SendConsoleAsync("$G");
            await WaitUntil(() => t.Commands.Contains("$G"), TimeSpan.FromSeconds(1));
            var stop = c.StopAsync();
            await WaitUntil(() => t.Realtime.Contains((byte)0x18), TimeSpan.FromMilliseconds(500));
            await AllowFailure(() => pending);
            await AllowFailure(() => stop);
        }));

        await Run("Timeout prevents a delayed ok from acknowledging a new mutation", async () => await WithController(async (c, t) =>
        {
            t.DropNextCommand = "$G";
            await AllowFailure(() => c.SendConsoleAsync("$G"));
            t.Emit("ok");
            t.Commands.Clear();
            await AllowFailure(() => c.SwitchModeAsync(true));
            Check(!t.Commands.Any(IsSettingWrite), "Mutation was permitted after acknowledgement synchronization was lost.");
            Check(!c.Snapshot.CanMutate, "Timeout left mutation controls enabled.");
        }, commandTimeout: TimeSpan.FromMilliseconds(120)));

        await Run("Disconnect closes transport and permits another client to own the port", async () => await WithController(async (c, t) =>
        {
            await c.DisconnectAsync();
            Check(!t.IsOpen && t.CloseCount > 0, "Disconnect did not call transport close.");
            Check(!c.Snapshot.Connected && !c.Snapshot.CanMutate, "Disconnected state was not published.");
            await c.ConnectAsync();
            Check(c.Snapshot.Connected, "Clean reconnection failed.");
        }));

        await Run("Transport failure cancels pending work and disables controls", async () => await WithController(async (c, t) =>
        {
            t.DropNextCommand = "$G";
            var pending = c.SendConsoleAsync("$G");
            await WaitUntil(() => t.Commands.Contains("$G"), TimeSpan.FromSeconds(1));
            t.Fault("USB cable removed");
            await AllowFailure(() => pending);
            Check(!c.Snapshot.Connected && !c.Snapshot.CanMutate, "Fault left connection/mutation enabled.");
        }));

        await Run("Failed serial open clears busy state and permits a retry", async () =>
        {
            await using var transport = new FakeTransport { OpenFailuresRemaining = 1 };
            await using var controller = new GrblController(transport, TimeSpan.FromMilliseconds(500), TimeSpan.Zero);
            await AllowFailure(() => controller.ConnectAsync());
            Check(!controller.Snapshot.Connected && !controller.Snapshot.Busy, "Failed open left the application busy or connected.");
            await controller.ConnectAsync();
            Check(controller.Snapshot.Connected && controller.Snapshot.CanMutate, "Connection could not be retried after a busy-port error.");
        });

        await Run("Concurrent disconnect callers both wait for serial port closure", async () => await WithController(async (c, t) =>
        {
            t.CloseDelay = TimeSpan.FromMilliseconds(150);
            var first = c.DisconnectAsync();
            var second = c.DisconnectAsync();
            await second;
            Check(!t.IsOpen, "Second disconnect returned before serial handle was closed.");
            await first;
        }));

        await Run("Inch reporting converts all positions and G54 to millimetres", async () => await WithController(async (c, t) =>
        {
            Position(c.Snapshot.MachinePosition, 50, 60, 10);
            Position(c.Snapshot.WorkPosition, 38, 36, 7);
            Position(c.Snapshot.G54Offset, 12, 24, 3);
            Check(c.Snapshot.ReportInches, "$13 reporting mode was not captured.");
            await c.ClearWorkDatumAsync();
            Position(c.Snapshot.WorkPosition, 50, 60, 10);
        }, setup: t => { t.Settings[13] = 1; t.ParserInches = true; }));

        await Run("Work-position status reconstructs machine position from WCO", async () => await WithController(async (c, t) =>
        {
            Position(c.Snapshot.MachinePosition, 50, 60, 10);
            Position(c.Snapshot.WorkPosition, 38, 36, 7);
            t.IncludeWco = false;
            t.Machine = new(55, 63, 11);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 55, 63, 11);
            Position(c.Snapshot.WorkPosition, 43, 39, 8);
            await Task.CompletedTask;
        }, setup: t => t.SendWorkPosition = true));

        await Run("Work display includes G92 and tool-length components of WCO", async () => await WithController(async (c, t) =>
        {
            Position(c.Snapshot.MachinePosition, 50, 60, 10);
            Position(c.Snapshot.WorkPosition, 36, 33, 2);
            Position(c.Snapshot.G54Offset, 12, 24, 3);
            await Task.CompletedTask;
        }, setup: t => { t.G92 = new(2, 3, 1); t.ToolLength = 4; }));

        await Run("Controller and included simulator complete the mode/datum/home workflow", async () =>
        {
            await using var transport = new SimulatedGrblTransport { MotionDuration = TimeSpan.FromMilliseconds(40) };
            await using var controller = new GrblController(transport, TimeSpan.FromMilliseconds(500), TimeSpan.Zero);
            await controller.ConnectAsync();
            Position(controller.Snapshot.G54Offset, -210, -120, -20);
            await controller.SwitchModeAsync(true);
            Position(controller.Snapshot.G54Offset, 0, 0, 0);
            await controller.SetWorkDatumAsync();
            var newDatum = controller.Snapshot.G54Offset;
            Position(controller.Snapshot.WorkPosition, 0, 0, 0);
            await controller.SwitchModeAsync(false);
            Check(controller.Snapshot.G54Offset == newDatum, "Simulator milling mode changed the new datum.");
            await controller.HomeAsync();
            Check(controller.Snapshot.G54Offset == newDatum, "Simulator homing changed the datum.");
            await controller.MoveToWorkZeroAsync(500);
            Position(controller.Snapshot.WorkPosition, 0, 0, 0);
            Check(controller.Snapshot.G54Offset == newDatum && controller.Snapshot.IsIdle, "Simulator Work return changed datum or completed before Idle.");
            await controller.DisconnectAsync();
            Check(!transport.IsOpen, "Simulator port remained open.");
        });

        await RunJogPauseTests();
        await RunVectorJogTests();
        Console.WriteLine($"\nResults: {_passed} passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunJogPauseTests()
    {
        await Run("Pause cancels GRBL jog without reset, feed-hold, resume or further motion", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 20, 600);
            t.AdvanceJog(0.25);
            t.Commands.Clear();
            t.Realtime.Clear();
            await c.PauseJogAsync();
            Position(c.Snapshot.MachinePosition, 55, 60, 10);
            Check(c.Snapshot.IsJogPaused && c.Snapshot.CanResumeJog && !c.Snapshot.CanPauseJog && !c.Snapshot.CanMutate, "Paused state or interlocks are incorrect.");
            Check(t.Realtime.Contains((byte)0x85), "Pause did not decelerate through GRBL jog cancellation.");
            Check(!t.Realtime.Any(b => b is (byte)'!' or (byte)'~' or 0x18), "Pause used feed-hold, cycle-start or reset instead of app-managed jog cancellation.");
            Check(c.Snapshot.Connected && t.IsOpen && t.CloseCount == 0, "Pause reset or disconnected the controller.");
            Check(!t.Commands.Any(IsMotion) && t.JogTarget is null, "Pause sent a new move or left a controller-side jog target.");
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 55, 60, 10);
        }));

        await Run("Resume travels only the remaining distance at the original axis and feed", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 20, 612.5);
            t.AdvanceJog(0.25);
            await c.PauseJogAsync();
            t.Commands.Clear();
            await c.ResumeJogAsync();
            var resume = t.Commands.Single(x => x.StartsWith("$J="));
            Check(resume.Contains("G21") && resume.Contains("G90") && resume.Contains("G53") && resume.Contains("X70") && resume.Contains("F612.5"), $"Resume did not restore the original machine target/feed: {resume}");
            Position(t.JogTarget, 70, 60, 10);
            Check(t.JogFeed == 612.5 && !c.Snapshot.IsJogPaused && c.Snapshot.CanPauseJog, "Resumed jog did not remain pausable at original feed.");
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 70, 60, 10);
            Check(!c.Snapshot.CanResumeJog && !c.Snapshot.CanPauseJog, "Completed jog retained a resumable target.");
        }));

        await Run("Repeated pause and resume retain a negative Z target without accumulating travel", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('Z', -8, 250);
            for (int i = 0; i < 3; i++)
            {
                t.AdvanceJog(0.5);
                await c.PauseJogAsync();
                await c.ResumeJogAsync();
                Position(t.JogTarget, 50, 60, 2);
                Check(t.JogFeed == 250, "Repeated resume changed feed.");
            }
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 50, 60, 2);
        }));

        await Run("A jog that completes while Pause is requested leaves no remainder", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('Y', 10, 500);
            t.BeforeRealtime = value => { if (value == 0x85) t.AdvanceJog(1); };
            await c.PauseJogAsync();
            Check(!c.Snapshot.IsJogPaused && !c.Snapshot.CanResumeJog, "Pause preserved a completed target.");
            Position(c.Snapshot.MachinePosition, 50, 70, 10);
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion), "Completed jog was replayed.");
        }));

        await Run("Stop Jog discards a paused target permanently", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            await c.CancelJogAsync();
            Check(!c.Snapshot.IsJogPaused && !c.Snapshot.CanResumeJog && c.Snapshot.CanMutate, "Stop Jog did not discard pause and release interlocks.");
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion), "Stopped jog resumed.");
        }));

        await Run("Paused jog blocks new jog, mode, datum, home, Work and console movement", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            t.Commands.Clear();
            await MustFail(() => c.JogAsync('Y', 1, 500));
            await MustFail(() => c.SwitchModeAsync(true));
            await MustFail(() => c.SetWorkDatumAsync());
            await MustFail(() => c.ClearWorkDatumAsync());
            await MustFail(() => c.HomeAsync());
            await MustFail(() => c.MoveToWorkZeroAsync(500));
            await MustFail(() => c.SendConsoleAsync("G91 G0 Y1"));
            Check(!t.Commands.Any(IsMotion) && !t.Commands.Any(IsSettingWrite) && !t.Commands.Any(IsAnyDatumWrite), "Paused interlock allowed a mutation.");
            Check(c.Snapshot.IsJogPaused && c.Snapshot.CanResumeJog, "Rejected mutation lost the intended paused target.");
        }));

        await Run("Paused target is discarded by reset, software stop, disconnect, fault or alarm", async () =>
        {
            foreach (var action in new[] { "reset", "stop", "disconnect", "fault", "alarm" })
                await WithController(async (c, t) =>
                {
                    await c.JogAsync('X', 10, 500);
                    t.AdvanceJog(0.3);
                    await c.PauseJogAsync();
                    if (action == "stop") await c.StopAsync();
                    else if (action == "disconnect") await c.DisconnectAsync();
                    else if (action == "fault") t.Fault("USB removed during paused jog");
                    else if (action == "reset") t.Emit("Grbl 1.1h ['$' for help]");
                    else { t.State = "Alarm"; t.SendStatus(); }
                    Check(!c.Snapshot.IsJogPaused && !c.Snapshot.CanResumeJog, $"{action} retained a resumable target.");
                    t.Commands.Clear();
                    await MustFail(() => c.ResumeJogAsync());
                    Check(!t.Commands.Any(IsMotion), $"{action} allowed a cancelled target to move.");
                });
        });

        await Run("Pause waits for deceleration and fresh Idle before allowing resume", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            t.HoldJogCancel = true;
            var pause = c.PauseJogAsync();
            await WaitUntil(() => t.Realtime.Contains((byte)0x85), TimeSpan.FromSeconds(1));
            Check(!pause.IsCompleted && c.Snapshot.JogPausing && !c.Snapshot.CanResumeJog && !c.Snapshot.CanMutate, "Jog cancellation falsely completed before Idle.");
            await MustFail(() => c.ResumeJogAsync());
            t.CompleteJogCancel();
            await pause;
            Check(c.Snapshot.IsJogPaused && c.Snapshot.CanResumeJog, "Fresh Idle did not complete pause.");
        }));

        await Run("Stop Jog during deceleration invalidates the pending pause", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            t.HoldJogCancel = true;
            var pause = c.PauseJogAsync();
            await WaitUntil(() => t.Realtime.Contains((byte)0x85), TimeSpan.FromSeconds(1));
            await c.CancelJogAsync();
            t.CompleteJogCancel();
            await AllowFailure(() => pause);
            Check(!c.Snapshot.IsJogPaused && !c.Snapshot.CanResumeJog, "Cancelled in-progress pause created a resumable target.");
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion), "Cancelling pending pause permitted a later move.");
        }));

        await Run("Pause requires fresh reported machine position rather than cached coordinates", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            t.SuppressPosition = true;
            await MustFail(() => c.PauseJogAsync());
            Check(!c.Snapshot.IsJogPaused && !c.Snapshot.CanResumeJog, "Missing fresh coordinates produced a resumable target from stale position.");
        }, commandTimeout: TimeSpan.FromMilliseconds(120)));

        await Run("Resume rejects a changed paused machine position without motion", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            t.Machine = new(53, 61, 10);
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Resume ignored changed position or retained an invalid target.");
        }));

        await Run("Resume requires fresh coordinates even when cached paused position matches", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            t.SuppressPosition = true;
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Resume trusted cached coordinates after status omitted position.");
        }, commandTimeout: TimeSpan.FromMilliseconds(120)));

        await Run("Stop Jog arriving during resume freshness check prevents the new motion", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            Task? cancel = null;
            t.BeforeRealtime = value =>
            {
                if (value != '?') return;
                t.BeforeRealtime = null;
                cancel = c.CancelJogAsync();
            };
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(cancel is not null, "Resume did not request fresh status before moving.");
            await cancel!;
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Resume queued motion after Stop Jog invalidated its target.");
        }));

        await Run("Stop Jog arriving during initial freshness check prevents queued jogging", async () => await WithController(async (c, t) =>
        {
            Task? cancel = null;
            t.BeforeRealtime = value =>
            {
                if (value != '?') return;
                t.BeforeRealtime = null;
                cancel = c.CancelJogAsync();
            };
            t.Commands.Clear();
            await MustFail(() => c.JogAsync('X', 10, 500));
            Check(cancel is not null, "Initial jog did not check fresh status.");
            await cancel!;
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "Initial jog sent motion after Stop Jog cancelled it.");
        }));

        await Run("Position change between resume validation and wire dispatch prevents movement", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            c.Log += line =>
            {
                if (!line.StartsWith("> $J=")) return;
                t.Machine = new(54, 60, 10); // Still on the original path, but no longer at the paused position.
                t.SendStatus();
            };
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Resume dispatched after its validated position changed.");
        }));

        await Run("Position change between initial validation and wire dispatch prevents movement", async () => await WithController(async (c, t) =>
        {
            c.Log += line =>
            {
                if (!line.StartsWith("> $J=")) return;
                t.Machine = new(51, 60, 10);
                t.SendStatus();
            };
            t.Commands.Clear();
            await MustFail(() => c.JogAsync('X', 10, 500));
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "Initial jog dispatched using a stale starting position.");
        }));

        await Run("Missing pause status invalidates target and closes an unsynchronized connection", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            t.SuppressStatus = true;
            await MustFail(() => c.PauseJogAsync());
            await WaitUntil(() => !t.IsOpen, TimeSpan.FromSeconds(2));
            Check(!c.Snapshot.CanResumeJog && !c.Snapshot.IsJogPaused && !c.Snapshot.Connected, "Timed-out pause remained resumable or actionable.");
        }, commandTimeout: TimeSpan.FromMilliseconds(120)));

        await Run("Rejected resume is not replayed and discards the target", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            t.BeforeCommand = command => { if (command.StartsWith("$J=")) t.FailNextCommand = command; };
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(t.Commands.Count(IsMotion) == 1 && !c.Snapshot.CanResumeJog, "Rejected resume replayed or retained its target.");
            await MustFail(() => c.ResumeJogAsync());
            Check(t.Commands.Count(IsMotion) == 1, "Second resume replayed rejected motion.");
        }));

        await Run("Missing resume acknowledgement discards target and prevents delayed replay", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('X', 10, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            t.BeforeCommand = command => { if (command.StartsWith("$J=")) t.DropNextCommand = command; };
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            t.Emit("ok");
            await MustFail(() => c.ResumeJogAsync());
            Check(t.Commands.Count(IsMotion) == 1 && !c.Snapshot.CanResumeJog && !c.Snapshot.Connected, "Timed-out resume allowed a delayed acknowledgement to restore motion.");
        }, commandTimeout: TimeSpan.FromMilliseconds(120)));

        await Run("Pause and resume preserve target with inch reports, WPos and active offsets", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync('Y', -12.5, 432.1);
            t.AdvanceJog(0.4);
            await c.PauseJogAsync();
            Position(c.Snapshot.MachinePosition, 50, 55, 10);
            await c.ResumeJogAsync();
            Position(t.JogTarget, 50, 47.5, 10);
            Check(t.JogFeed == 432.1 && t.ParserInches && t.ActiveWcs == "G54", "Jog altered parser units, work coordinate system or feed.");
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 50, 47.5, 10);
        }, setup: t => { t.Settings[13] = 1; t.ParserInches = true; t.SendWorkPosition = true; t.G92 = new(2, 3, 1); t.ToolLength = 4; }));

        await Run("Pause cannot capture an unknown jog or a non-jog operation", async () => await WithController(async (c, t) =>
        {
            foreach (var state in new[] { "Idle", "Jog", "Run", "Home", "Alarm" })
            {
                t.State = state;
                t.SendStatus();
                t.Commands.Clear();
                await MustFail(() => c.PauseJogAsync());
                await MustFail(() => c.ResumeJogAsync());
                Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, $"Unknown {state} operation was captured or moved.");
            }
        }));

        await Run("Simulator cancels and resumes the remaining machine-coordinate jog", async () =>
        {
            await using var t = new SimulatedGrblTransport { MotionDuration = TimeSpan.FromMilliseconds(800) };
            await using var c = new GrblController(t, TimeSpan.FromMilliseconds(500), TimeSpan.Zero);
            await c.ConnectAsync();
            await c.SendConsoleAsync("G20");
            await c.JogAsync('X', 20, 500);
            await WaitUntil(() => c.Snapshot.MachinePosition is { X: > -200 and < -180 }, TimeSpan.FromSeconds(2));
            await c.PauseJogAsync();
            var paused = c.Snapshot.MachinePosition;
            Check(c.Snapshot.IsJogPaused, "Simulator did not pause during jog.");
            await Task.Delay(100);
            Check(c.Snapshot.MachinePosition == paused, "Simulator continued moving after pause.");
            await c.ResumeJogAsync();
            await WaitUntil(() => c.Snapshot.IsIdle, TimeSpan.FromSeconds(3));
            Position(c.Snapshot.MachinePosition, -180, -100, -15);
            var parserLines = new List<string>();
            c.Log += parserLines.Add;
            await c.SendConsoleAsync("$G");
            Check(parserLines.Any(line => line.Contains("G20")), "Simulator jog changed modal parser units.");
        });
    }

    private static async Task RunVectorJogTests()
    {
        foreach (var (x, y, quadrant) in new[] { (12.5, 7.25, "X+ Y+"), (-12.5, 7.25, "X- Y+"), (-12.5, -7.25, "X- Y-"), (12.5, -7.25, "X+ Y-") })
            await Run($"Diagonal {quadrant} sends one coordinated XY jog without Z travel", async () => await WithController(async (c, t) =>
            {
                var datum = t.G54;
                t.Commands.Clear();
                await c.JogAsync(x, y, 0, 612.3456);
                var motion = t.Commands.Where(IsMotion).ToArray();
                Check(motion.Length == 1, "Diagonal was split into multiple motion commands.");
                JogWire(motion[0], false, new() { ['X'] = x, ['Y'] = y }, 612.346);
                Position(t.JogTarget, 50 + x, 60 + y, 10);
                Check(t.JogFeed == 612.346 && t.G54 == datum, "Coordinated feed or work datum changed.");
                t.AdvanceJog(0.4);
                t.SendStatus();
                Position(c.Snapshot.MachinePosition, 50 + x * 0.4, 60 + y * 0.4, 10);
                Check(c.Snapshot.CanPauseJog, "Valid coordinated progress lost its tracked target.");
                t.AdvanceJog(1);
                t.SendStatus();
                Position(c.Snapshot.MachinePosition, 50 + x, 60 + y, 10);
                Check(!c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "Completed diagonal retained a saved target.");
            }));

        await Run("Vector jog validates every component and feed before transmitting commands", async () => await WithController(async (c, t) =>
        {
            t.Commands.Clear();
            await MustFail(() => c.JogAsync(0, 0, 0, 500));
            await MustFail(() => c.JogAsync(-0.0, 0, -0.0, 500));
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1000.0001, -1000.0001 })
            {
                await MustFail(() => c.JogAsync(invalid, 1, 0, 500));
                await MustFail(() => c.JogAsync(1, invalid, 0, 500));
                await MustFail(() => c.JogAsync(1, 1, invalid, 500));
            }
            foreach (var subprecision in new[] { 0.00001, -0.00001 })
            {
                await MustFail(() => c.JogAsync(subprecision, 5, 0, 500));
                await MustFail(() => c.JogAsync(5, subprecision, 0, 500));
                await MustFail(() => c.JogAsync(5, 5, subprecision, 500));
                await MustFail(() => c.JogAsync(subprecision, -subprecision, 0, 500));
            }
            foreach (var feed in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 100000.1, 0.00001 })
                await MustFail(() => c.JogAsync(2, -3, 0, feed));
            Check(t.Commands.IsEmpty && t.JogTarget is null, "An invalid vector or feed reached the command channel.");
            Check(c.Snapshot.CanMutate && !c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "Rejected vector left a tracked jog or busy state.");
        }));

        await Run("Rounded diagonal target and feed match the values actually sent on the wire", async () => await WithController(async (c, t) =>
        {
            t.Commands.Clear();
            await c.JogAsync(0.123456, -0.567856, 0, 321.9876);
            JogWire(t.Commands.Single(IsMotion), false, new() { ['X'] = 0.1235, ['Y'] = -0.5679 }, 321.988);
            t.AdvanceJog(0.4);
            await c.PauseJogAsync();
            t.Commands.Clear();
            await c.ResumeJogAsync();
            JogWire(t.Commands.Single(IsMotion), true, new() { ['X'] = 50.1235, ['Y'] = 59.4321 }, 321.988);
            Check(t.JogTarget is { } target && Math.Abs(target.X - 50.1235) < 1e-9 && Math.Abs(target.Y - 59.4321) < 1e-9,
                "Saved diagonal target was based on input precision rather than the transmitted distance.");
        }));

        await Run("Repeated negative diagonal pause and resume retain the original vector and feed", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(-20, -8, 0, 612.5);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                t.AdvanceJog(0.25);
                t.Commands.Clear();
                t.Realtime.Clear();
                await c.PauseJogAsync();
                Check(c.Snapshot.IsJogPaused && c.Snapshot.CanResumeJog && !c.Snapshot.CanMutate, "Diagonal pause interlocks are incorrect.");
                Check(t.Realtime.Contains((byte)0x85) && !t.Realtime.Any(b => b is (byte)'!' or (byte)'~' or 0x18), "Diagonal pause used the wrong realtime action.");
                Check(!t.Commands.Any(IsMotion) && t.JogTarget is null, "Pause sent additional travel or kept a controller-side target.");
                t.Commands.Clear();
                await c.ResumeJogAsync();
                JogWire(t.Commands.Single(IsMotion), true, new() { ['X'] = 30, ['Y'] = 52 }, 612.5);
                Position(t.JogTarget, 30, 52, 10);
                Check(t.JogFeed == 612.5 && c.Snapshot.CanPauseJog, "Repeated diagonal resume changed feed or lost tracking.");
            }
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 30, 52, 10);
            Check(!c.Snapshot.CanResumeJog && !c.Snapshot.CanPauseJog, "Completed repeated diagonal retained a target.");
        }));

        await Run("Mixed-sign diagonal resume remains in machine millimetres with inch WPos reports and offsets", async () => await WithController(async (c, t) =>
        {
            var datum = t.G54;
            await c.JogAsync(-12.5, 7.25, 0, 432.1);
            t.AdvanceJog(0.4);
            await c.PauseJogAsync();
            Position(c.Snapshot.MachinePosition, 45, 62.9, 10);
            t.Commands.Clear();
            await c.ResumeJogAsync();
            JogWire(t.Commands.Single(IsMotion), true, new() { ['X'] = 37.5, ['Y'] = 67.25 }, 432.1);
            Position(t.JogTarget, 37.5, 67.25, 10);
            Check(t.ParserInches && t.Settings[13] == 1 && t.G54 == datum && t.G92 == new AxisPosition(2, 3, 1) && t.ToolLength == 4,
                "Diagonal jog modified parser units, reporting units or coordinate offsets.");
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 37.5, 67.25, 10);
        }, setup: t => { t.Settings[13] = 1; t.ParserInches = true; t.SendWorkPosition = true; t.G92 = new(2, 3, 1); t.ToolLength = 4; }));

        await Run("Diagonal tracking rejects a point inside the bounding box but off the commanded line", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(20, 10, 0, 500);
            t.Machine = new(55, 65, 10); // X is 25% complete; Y is 50% complete. Both are inside the rectangle.
            t.SendStatus();
            t.Commands.Clear();
            Check(!c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "Independent axis ranges accepted an off-line diagonal position.");
            await MustFail(() => c.PauseJogAsync());
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion), "Off-line diagonal position allowed a replacement motion.");
        }));

        await Run("Diagonal tracking rejects movement of inactive Z", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(20, 10, 0, 500);
            t.Machine = new(55, 62.5, 10.01);
            t.SendStatus();
            t.Commands.Clear();
            Check(!c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "A diagonal silently accepted movement of inactive Z.");
            await MustFail(() => c.PauseJogAsync());
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion), "Inactive-axis change permitted further diagonal travel.");
        }));

        await Run("Diagonal pause rejects off-line drift during jog deceleration", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(20, 10, 0, 500);
            t.AdvanceJog(0.25);
            t.BeforeRealtime = value => { if (value == 0x85) t.Machine = new(56, 65, 10); };
            t.Commands.Clear();
            await MustFail(() => c.PauseJogAsync());
            Check(!c.Snapshot.IsJogPaused && !c.Snapshot.CanResumeJog, "Idle at an off-line point created a resumable diagonal.");
            Check(!t.Commands.Any(IsMotion), "Failed diagonal deceleration verification sent another move.");
        }));

        await Run("Diagonal tracking rejects collinear positions outside the commanded segment", async () =>
        {
            foreach (var position in new[] { new AxisPosition(45, 57.5, 10), new AxisPosition(75, 72.5, 10) })
                await WithController(async (c, t) =>
                {
                    await c.JogAsync(20, 10, 0, 500);
                    t.Machine = position;
                    t.SendStatus();
                    Check(!c.Snapshot.CanPauseJog && !c.Snapshot.CanResumeJog, "A collinear point outside the jog segment retained the target.");
                    t.Commands.Clear();
                    await MustFail(() => c.PauseJogAsync());
                    Check(!t.Commands.Any(IsMotion), "Out-of-segment diagonal produced more motion.");
                });
        });

        await Run("Reaching only one diagonal target coordinate cannot report successful completion", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(20, 10, 0, 500);
            t.Machine = new(70, 65, 10);
            t.State = "Idle";
            t.SendStatus();
            Check(c.Snapshot.Message != "Jog complete.", "Reaching X alone falsely completed the XY diagonal.");
            Check(!c.Snapshot.CanResumeJog && !c.Snapshot.CanPauseJog, "Unexpected diagonal stop retained a saved target.");
        }));

        await Run("Diagonal path verification permits independent motor-step quantization", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(10, 7, 0, 500);
            // Ideal 25% point is (52.5,61.75). Unequal motors resolve 0.0125 and 0.0025 mm.
            t.Machine = new(52.51, 61.748, 10);
            t.SendStatus();
            Check(c.Snapshot.CanPauseJog, "Legitimate active-axis motor quantization discarded a coordinated jog.");
            await c.PauseJogAsync();
            Check(c.Snapshot.IsJogPaused && c.Snapshot.CanResumeJog, "Motor-quantized position could not be paused.");
            t.Commands.Clear();
            await c.ResumeJogAsync();
            JogWire(t.Commands.Single(IsMotion), true, new() { ['X'] = 60, ['Y'] = 67 }, 500);
            Position(t.JogTarget, 60, 67, 10);
        }, setup: t => { t.Settings[100] = 80; t.Settings[101] = 400; }));

        await Run("Motor-step path tolerance does not permit the paused position to change", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(10, 7, 0, 500);
            t.AdvanceJog(0.25);
            await c.PauseJogAsync();
            t.Machine = new(52.505, 61.75, 10); // Within one X motor step, but beyond report precision.
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(!t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Path quantization tolerance hid a changed paused position.");
        }, setup: t => { t.Settings[100] = 80; t.Settings[101] = 400; }));

        await Run("Stop Jog during diagonal resume freshness check prevents both axes from moving", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(-20, 10, 0, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            Task? cancel = null;
            t.BeforeRealtime = value =>
            {
                if (value != '?') return;
                t.BeforeRealtime = null;
                cancel = c.CancelJogAsync();
            };
            var paused = t.Machine;
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(cancel is not null, "Diagonal resume did not check fresh controller state.");
            await cancel!;
            Check(t.Machine == paused && !t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Stop Jog raced with a diagonal resume and queued motion.");
        }));

        await Run("Software stop between diagonal resume validation and dispatch prevents motion", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(20, -10, 0, 500);
            t.AdvanceJog(0.3);
            await c.PauseJogAsync();
            Task? stop = null;
            c.Log += line =>
            {
                if (line.StartsWith("> $J=")) stop ??= c.StopAsync();
            };
            var paused = t.Machine;
            t.Commands.Clear();
            await MustFail(() => c.ResumeJogAsync());
            Check(stop is not null, "Resume dispatch hook was not reached.");
            await stop!;
            Check(t.Realtime.Contains((byte)0x18) && !c.Snapshot.Connected, "Software stop did not reset and release connection.");
            Check(t.Machine == paused && !t.Commands.Any(IsMotion) && !c.Snapshot.CanResumeJog, "Software stop allowed the diagonal resume to dispatch.");
        }));

        await Run("A three-axis vector resumes to all original machine targets", async () => await WithController(async (c, t) =>
        {
            await c.JogAsync(4, -6, 2, 345.6);
            t.AdvanceJog(0.5);
            await c.PauseJogAsync();
            Position(c.Snapshot.MachinePosition, 52, 57, 11);
            t.Commands.Clear();
            await c.ResumeJogAsync();
            JogWire(t.Commands.Single(IsMotion), true, new() { ['X'] = 54, ['Y'] = 54, ['Z'] = 12 }, 345.6);
            Position(t.JogTarget, 54, 54, 12);
            t.AdvanceJog(1);
            t.SendStatus();
            Position(c.Snapshot.MachinePosition, 54, 54, 12);
        }));

        await Run("Simulator progresses both diagonal axes together and resumes to the original endpoint", async () =>
        {
            await using var t = new SimulatedGrblTransport { MotionDuration = TimeSpan.FromMilliseconds(1000) };
            await using var c = new GrblController(t, TimeSpan.FromMilliseconds(500), TimeSpan.Zero);
            var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
            c.Log += line => { if (line.StartsWith("> $J=")) commands.Enqueue(line[2..]); };
            await c.ConnectAsync();
            await c.JogAsync(20, -10, 0, 512.5);
            await WaitUntil(() => c.Snapshot.MachinePosition is { X: > -200 and < -180 }, TimeSpan.FromSeconds(2));
            var progress = c.Snapshot.MachinePosition!;
            Check(progress.Y < -100 && progress.Y > -110 && Math.Abs((progress.X + 200) + 2 * (progress.Y + 100)) < 0.001,
                "Simulator diagonal advanced axes separately or at unequal fractions.");
            Check(progress.Z == -15, "Simulator moved Z during an XY jog.");
            await c.PauseJogAsync();
            var paused = c.Snapshot.MachinePosition;
            Check(c.Snapshot.IsJogPaused && c.Snapshot.CanResumeJog, "Simulator diagonal did not enter paused state.");
            await Task.Delay(350);
            Check(c.Snapshot.MachinePosition == paused, "Simulator kept moving a diagonal axis after Pause.");
            await c.ResumeJogAsync();
            await WaitUntil(() => c.Snapshot.IsIdle, TimeSpan.FromSeconds(3));
            Position(c.Snapshot.MachinePosition, -180, -110, -15);
            Check(commands.Count == 2, "Simulator diagonal was split or replayed.");
            JogWire(commands.First(), false, new() { ['X'] = 20, ['Y'] = -10 }, 512.5);
            JogWire(commands.Last(), true, new() { ['X'] = -180, ['Y'] = -110 }, 512.5);
            Check(!c.Snapshot.CanResumeJog && !c.Snapshot.CanPauseJog, "Completed simulator diagonal retained a saved destination.");
        });
    }

    private static void JogWire(string command, bool absolute, Dictionary<char, double> expectedAxes, double expectedFeed)
    {
        Check(command.StartsWith("$J="), $"Expected a GRBL jog, got '{command}'.");
        var words = Regex.Matches(command[3..], @"([A-Z])([-+]?(?:\d+(?:\.\d*)?|\.\d+))")
            .Select(m => (Letter: m.Groups[1].Value[0], Value: double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var modes = words.Where(w => w.Letter == 'G').Select(w => w.Value).Order().ToArray();
        Check(modes.SequenceEqual(absolute ? new double[] { 21, 53, 90 } : new double[] { 21, 91 }), $"Incorrect jog units or coordinate modes: {command}");
        foreach (var axis in "XYZ")
        {
            var actual = words.Where(w => w.Letter == axis).ToArray();
            if (expectedAxes.TryGetValue(axis, out var expected))
                Check(actual.Length == 1 && Math.Abs(actual[0].Value - expected) < 1e-9, $"Incorrect {axis} target: {command}");
            else Check(actual.Length == 0, $"Inactive {axis} was included in the jog: {command}");
        }
        var feed = words.Where(w => w.Letter == 'F').ToArray();
        Check(feed.Length == 1 && Math.Abs(feed[0].Value - expectedFeed) < 1e-9, $"Incorrect coordinated feed: {command}");
        Check(words.All(w => w.Letter is 'G' or 'X' or 'Y' or 'Z' or 'F'), $"Unexpected jog words: {command}");
    }

    private static async Task WithController(Func<GrblController, FakeTransport, Task> test, Action<FakeTransport>? setup = null, TimeSpan? commandTimeout = null)
    {
        var transport = new FakeTransport();
        setup?.Invoke(transport);
        var controller = new GrblController(transport, commandTimeout ?? TimeSpan.FromMilliseconds(500), TimeSpan.Zero);
        try
        {
            await controller.ConnectAsync();
            await test(controller, transport);
        }
        finally { await controller.DisconnectAsync(); }
    }
    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}\n     {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }
    private static async Task AllowFailure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or OperationCanceledException) { }
    }
    private static async Task MustFail(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or OperationCanceledException or ArgumentException) { return; }
        throw new Exception("Expected operation to fail, but it reported success.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Position(AxisPosition? p, double x, double y, double z)
    {
        Check(p is not null, "Coordinate is unavailable.");
        Check(Math.Abs(p!.X - x) < 0.001 && Math.Abs(p.Y - y) < 0.001 && Math.Abs(p.Z - z) < 0.001, $"Expected ({x}, {y}, {z}), got ({p.X}, {p.Y}, {p.Z}).");
    }
    private static void Sequence(IEnumerable<string> actual, params string[] expected) => Check(actual.SequenceEqual(expected), $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    private static bool IsSettingWrite(string command) => Regex.IsMatch(command, @"^\$\d+\s*=");
    private static bool IsClearDatum(string command) => command.Replace(" ", "").Contains("G10L2P1X0Y0Z0", StringComparison.OrdinalIgnoreCase);
    private static bool IsAnyDatumWrite(string command) => Regex.IsMatch(command, @"\bG10\b|\bG92(?:\.1)?\b", RegexOptions.IgnoreCase);
    private static bool IsMotion(string command) => command == "$H" || command.StartsWith("$J=") || Regex.IsMatch(command, @"\bG0?[0123](?![.\d])\b", RegexOptions.IgnoreCase);
    private static void Incomplete(GrblController controller)
    {
        Check(!controller.Snapshot.ModeVerified, "An incomplete switch was marked verified.");
        Check(controller.Snapshot.Message.Contains("incomplete", StringComparison.OrdinalIgnoreCase), $"Expected incomplete-switch message, got '{controller.Snapshot.Message}'.");
    }
    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Test event did not arrive in time.");
            await Task.Delay(5);
        }
    }
}
