# MillBeam 1.5.0

A native Windows desktop controller for the Genmitsu PROVerXL 6050 Plus running GRBL 1.1. Includes serial communication and a built-in simulator. This application handles machine setup, manual commands, jogging, datum management, and homing; it does not stream job files.

MillBeam uses the approved rounded teal-and-coral logo in the header and a matching Windows icon in the executable, title bar, taskbar, and Alt+Tab. The interface keeps the two mode buttons, large machine/work coordinate readouts, arrow jog controls, Set Work and Clear Work side by side, command console, and Work return button. **Pause** sits to the left of **Stop** and changes to **Resume** while a jog is paused. The single connection badge sits at the far right; the current mode appears beside it only while connected. Hover over the mode indicator to inspect actual settings.

Maximizing the window fits the complete interface into the available desktop. The console adjusts to the remaining space, with a compact layout for smaller desktop sizes or higher Windows display scaling. Only the console history scrolls; the controls and software-stop bar stay visible. There is no notification banner between the mode buttons and machine controls. All button actions, including Home, Work, mode changes, jogging, and errors, leave the controls in place. Progress and error details appear in the console and existing status indicators.

Version 1.4.2 gives the **Position & Jog** heading enough vertical space for the full letter shapes, including the bottom of the “g.” The font size is unchanged and the status badge retains its size.

## Run

Open `MillBeam.exe` from the portable Windows x64 release. No installer, administrator privileges, or separate .NET installation is needed for the self-contained release. The logo and icon are embedded in the executable. Source and build scripts are provided separately. To pin it to the taskbar, open MillBeam, right-click its taskbar icon, and choose **Pin to taskbar**.

Version 1.4.4 fixes the error on closing the window. Closing waits for connection cleanup before exiting, including after Disconnect; repeated close requests do not start cleanup again.

1. Close the machine connection in LightBurn or any other controller application.
2. Select the machine's COM port, then **Connect**. Serial settings are 115200 baud, 8 data bits, no parity, one stop bit, and no flow control.
3. Wait for the controller's settings and status to appear. The app does not change the mode merely by connecting.
4. Select **Milling Mode** or **Laser Mode** while the controller is idle.
5. Select **Disconnect** before connecting LightBurn to the same machine. This closes and disposes the serial port.

The **Simulator** selection opens no serial port. Its badge remains clearly marked SIMULATOR. Use it to explore and check the interface without hardware. Each simulator connection starts a fresh demo session; a physical controller retains its own settings and stored offsets.

## GitHub updates

Download the Windows build from [MillBeam releases](https://github.com/BigUziHert/MillBeam/releases). Extract the portable ZIP to a writable folder and run `MillBeam.exe`. Older versions without an update button need this one-time manual upgrade.

Disconnect the controller, then click **Check for updates** at the bottom right. The update window shows your installed version and checks GitHub. **Include development releases (dev)** is selected for development builds; clear it to check stable releases only. Click **Download and install** when a newer version is available. MillBeam verifies the download's size and SHA-256 against GitHub's release metadata before starting installation. Nothing downloads or installs automatically on startup or quit.

The installer waits for this MillBeam process to close, replaces the executable in the same folder, and restarts disconnected. It keeps the prior executable as `MillBeam.exe.previous` and restores it if replacement or immediate startup fails. It does not write machine settings, change the datum, reconnect, or resume movement. The update button stays disabled while a controller is connected so the update window cannot cover motion or stop controls.

Keep the executable named `MillBeam.exe` in a folder your Windows account can write to. Updates from a ZIP preview, read-only directory, symbolic link, or `dotnet MillBeam.dll` development launch are unsupported; extract or download the portable build instead. Network/checksum failures leave the installed app unchanged and appear in the update window. Helper diagnostics are saved under `%LOCALAPPDATA%\MillBeam\Updates`.

### Building and publishing updates

The [Windows desktop workflow](https://github.com/BigUziHert/MillBeam/actions/workflows/windows-desktop.yml) runs automatically on pushes to `dev`. It runs controller and updater tests, publishes a self-contained Windows x64 EXE, then tests that exact executable's simulator UI, shutdown, and update window. Only successful builds publish a development release named `v1.5.0-dev.<run>.<attempt>` with `MillBeam.exe`, `MillBeam-Windows-x64.zip`, and `SHA256SUMS.txt`. Each release is drafted until its uploads pass size and checksum verification. Superseded development commits do not publish updates.

To publish a stable version, set the desktop project's three-part `Version` and push a matching `vX.Y.Z` tag. Stable releases exclude the development suffix. Increase the base version for the next development cycle after a stable release: versions compare numerically, and a stable version is newer than development builds of the same base. Pull requests run validation without publishing. No GitHub login or token is needed inside MillBeam; it reads this public repository's releases.

## Modes and verification

| Setting | Milling | Laser |
| --- | ---: | ---: |
| `$32` laser mode | 0 | 1 |
| `$120` X acceleration | 300 | 1000 |
| `$121` Y acceleration | 300 | 1000 |
| `$122` Z acceleration | 300 | 300 |

Accelerations are in **mm/s²**. The laser X/Y value of 1000 is the user's selected trial value, not a claim of manufacturer validation.

The four settings are sent individually, waiting for each acknowledgement. The controller is read back before the app reports success. Laser Mode also sends `G10 L2 P1 X0 Y0 Z0`, reads `$#`, and verifies the stored G54 offset is zero. A rejected command stops the operation; the console shows the error and the app refreshes the actual state when communication remains synchronized. A partial switch is reported as incomplete. The displayed mode follows actual `$32` readback; hover over the mode indicator to see all four actual values. A mixed or unverified profile is identified in the existing mode indicator and console.

The app never saves an old milling datum for later restoration. Returning to Milling Mode applies its settings without restoring a datum. Mode switching does not factory-reset the controller or overwrite travel, steps/mm, homing, soft limits, or hard limits. It turns the tool output off before applying mode changes.

## Work datum and homing

These controls are shared by both modes:

- **Set Work** selects G54 and uses `G10 L20 P1 X0 Y0 Z0` to make the current position work zero. It does not move the axes.
- **Clear Work** clears the stored G54 offset with `G10 L2 P1 X0 Y0 Z0`, verifies it, and refreshes coordinates. Clearing an offset does not move the machine to zero.
- **Home** sends `$H`. It physically moves the axes through the configured homing cycle. It does not set, clear, or restore a datum afterward. Homing must already be enabled/configured in the controller.
- **Work** turns the tool output off with `M5`, then moves X, Y, and Z together to G54 work zero at the selected XY feed (converted to mm/min when necessary). Z's separate feed applies to Z jogging. Work uses `G21 G90 G54 G1 X0 Y0 Z0 F...` and waits for Idle and a zero work-coordinate readback before reporting arrival. It does not change the stored datum. This is a coordinated XYZ move, with no automatic clearance-height retract; ensure the path is clear before pressing Work. It selects G54, millimetres, absolute positioning, and linear feed mode in the controller. Stop and Disconnect can interrupt the pending software operation.

Entering Laser Mode clears G54 once as part of that operation. You may intentionally set or clear a datum afterward in either mode. The stored G54 readout is separate from the active work-coordinate readout: G92 and tool-length offsets can also affect the latter. The application uses the controller's WCO to derive work coordinates, rather than assuming G54 is the only offset. Positions use the units selected in MillBeam, independently of the controller's `$13` report units. Unknown or invalidated coordinates display a dash until fresh data arrives.

## Millimetres and inches

The **Units** button sits between the mode indicator and connection badge at the top. It switches coordinate readouts, G54 offset, XY/Z step distances, feed rates, and labels between millimetres and inches. It converts current values using exactly 25.4 mm per inch, preserving their physical distance or speed rather than reinterpreting the same number. The distance and feed dropdowns offer separate metric and inch preset lists; an existing custom value remains converted even when it is not in the new list. XY and Z keep separate values. Switching units does not move the machine or write controller settings, and a paused jog retains its original target and feed.

GRBL communication and internal coordinates remain in millimetres. Jog and Work commands explicitly use `G21`; inch entries are converted before sending. The requested acceleration settings remain in mm/s². The command console shows raw controller messages and sends raw G-code: the Units button does not rewrite console commands or change their `G20`/`G21` modal meaning.

## Jogging, stopping, and connection loss

Jogging uses GRBL 1.1 `$J=` incremental commands with explicit millimetres and feed in mm/min after converting any inch input. The XY pad has eight directions, including all four diagonals. Set the **XY Step** and **XY Feed** before selecting a direction. A diagonal sends one coordinated move: each selected axis travels the full XY step (for example, a 1 mm upper-right jog moves X+1 and Y+1, with a path length of about 1.414 mm). The feed is speed along that diagonal path. Z stays unchanged.

The separate Z arrows use **Z Step** and **Z Feed**, independently of XY. Both metric step menus offer **1, 2, 3, 6, 13, 25, and 50 mm**. Both inch step menus offer **0.03125″ (1/32″), 0.0625″ (1/16″), 0.125″ (1/8″), 0.25″ (1/4″), 0.5″ (1/2″), 1″, and 2″**. Fractional presets show their decimal and fraction in both the open menus and the selected fields when closed. Custom positive decimal entries, including 0.1 mm, remain supported. Switching units preserves the exact physical value rather than snapping to a nearby preset.

Both XY and Z feed presets are **125, 300, 500, 1000, 1500, and 2000 mm/min** in metric mode, and **5, 12, 20, 40, 60, and 78 in/min** in inch mode. Feed menus show whole numbers. Both sets initially use 1 mm and 500 mm/min; changing one set does not change the other. **Stop** sends the real-time byte `0x85`. Each arrow has an axis/direction tooltip. Diagonal pause/resume retains both original machine-axis targets and the original feed, and verifies the stopped position lies on the original path before resuming.

**Pause** stops the current arrow-button jog while retaining its original destination and feed in the app. It waits for the controller to report a stopped position before offering **Resume**. Pressing Resume explicitly moves the remaining distance to that original destination; it does not repeat the whole step. If the jog already finished, no Resume action remains. Pausing does not reset or disconnect the controller and never resumes automatically.

GRBL's jog cancel discards the controller's remaining jog movement; a normal feed-hold does not preserve it either. This app therefore implements pause by sending jog cancel, recording the confirmed stopped machine position, and issuing a new absolute machine-coordinate jog only when Resume is pressed. The saved target is for the current session only. This follows GRBL's [documented jog cancellation and machine-coordinate behavior](https://github.com/gnea/grbl/wiki/Grbl-v1.1-Jogging).

While a jog is paused, other motion, mode changes, datum changes, and mutating console commands are blocked. **Stop** discards the paused target and allows other actions again. Reset, disconnect, communication failure, or unexpected position/state changes also invalidate the saved jog. Resume checks the stopped position again before moving. Pause applies to arrow-button jogging; it does not pause Home, Work, or motion sent through the command console.

The always-visible **STOP / RESET** button sends GRBL's real-time soft reset (`0x18`), bypassing the pending-command queue. This aborts the operation and can invalidate machine position; home again when necessary. It is a software control dependent on the computer and serial link. Use the machine's physical emergency stop in an emergency. Disconnecting is a port-release action, not an emergency stop.

Mode and datum changes require a connected controller with a fresh Idle status. Homing may also be initiated from Alarm so an unhomed controller can recover. The app serializes operations and will not queue additional mode changes or jog clicks behind a pending operation. On acknowledgement timeout, it closes the connection instead of risking a late `ok` being assigned to a different command. Reconnect to read the actual controller state. Disconnection or a software stop during a multi-command switch leaves it incomplete; inspect the readback after reconnecting before applying a profile again.

## Command console

Enter one command per submission. Replies, errors, transactions, and connection events appear in the console. Use **Save log** to export the session. The **Status reports** checkbox includes periodic status lines in the visible console; the saved log includes them regardless. Manual commands can move the machine or enable a tool. Mutation commands require Idle. The console rejects multi-line/control-character input and factory-reset commands.

Version 1.3.1 fixes clipped command-entry text by removing duplicate padding from the text-box template. It also improves the usable space in console and editable drop-down fields. This is a display fix only; controller settings and command behavior are unchanged. `$$` reads GRBL settings without modifying them.

## Build and test from source

Requires a .NET 8 SDK on Windows. From this source folder:

```powershell
dotnet build GenmitsuControl.sln -c Release
dotnet run --project GenmitsuControl.Tests -c Release
./publish.ps1
```

The portable release is written to `release/win-x64`. `System.IO.Ports` is the only direct external package. The test executable uses controlled fake transports and a simulator; it never opens a physical COM port. A WPF UI smoke test is also available:

```powershell
./GenmitsuControl.Desktop/bin/Release/net8.0-windows/MillBeam.exe --smoke-test ./ui-test-results
```

The smoke test connects to the simulator, operates the actual UI buttons through mode and datum changes, errors and mismatched settings, jogging, pause/resume, cancellation, homing, Work return, stop, and disconnect. It also checks branding, icon resources, the single disconnected badge, and complete-control visibility at normal, maximized, and compact sizes, and saves preview PNGs and logs before closing. Hardware verification with the user's controller is still required; automated tests do not establish physical stopping performance, a collision-free return path, or validate the selected acceleration on the machine.

## Protocol references

- [GRBL 1.1 interface and send-response protocol](https://github.com/gnea/grbl/wiki/Grbl-v1.1-Interface)
- [GRBL 1.1 commands, coordinate parameters, and real-time controls](https://github.com/gnea/grbl/wiki/Grbl-v1.1-Commands)
- [GRBL 1.1 jogging](https://github.com/gnea/grbl/wiki/Grbl-v1.1-Jogging)
- [GRBL laser mode](https://github.com/gnea/grbl/wiki/Grbl-v1.1-Laser-Mode)

The application is an independent controller utility and is not an official Genmitsu or LightBurn product.
