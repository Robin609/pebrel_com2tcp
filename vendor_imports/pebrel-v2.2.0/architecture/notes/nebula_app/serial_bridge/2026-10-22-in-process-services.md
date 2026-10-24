# In-process serial services for the local Pebrel branch

## Status

Implementation on the user-authorized local `robin/serial-bridge` branch.

## Context

The user requires serial configuration, service lifecycle and terminal interaction
inside one Pebrel application, with a maintainable path for merging upstream updates.
Matching the appearance of two Windows Forms applications did not meet that need.

## Evidence

- Upstream v2.2.0 provides GPUI Settings navigation, shared controls and a
  `SettingsPaneEvent` route to the workspace's existing terminal creation path.
- The previous bridge owns each serial port and TCP listener independently and
  broadcasts serial bytes to all clients without Telnet parsing or local history.
- The Windows API already used by the application can configure exclusive serial
  handles, bounded read/write waits, parity and 1.5 stop bits without a new driver.

## Decision

An opt-in `serial-bridge` feature adds an application-owned manager and a native
settings section. The manager outlives settings views; application shutdown stops
its workers. All filesystem operations, service commands and monitoring snapshots
run outside render callbacks. One worker owns each serial handle and listener;
client writes are serialized, outgoing queues are bounded to 256 KiB per client,
and stalled clients are disconnected without stopping other channels.

The bundled console entry `runtime/pebrel-serial.exe` runs inside the existing PTY
and transfers raw TCP bytes. It creates no GUI and does not depend on Ncat, a
PowerShell script or a separately installed configuration application. The GUI's
`serial-connect` command shares the client implementation for redirected use.
The Windows subsystem distinction and observed failure are recorded in
[`serial terminal helper`](../../packaging/2026-10-23-serial-terminal-helper.md).

Versioned JSON belongs to this capability. Initial import reads the previous XML
only when the new file is absent, never writes the XML, and never automatically
starts imported services. Atomic replacement preserves the last saved JSON.
Malformed existing JSON is an error, not an invitation to overwrite it.

The branch remains based on upstream's release tag. New code stays in cohesive
feature modules; existing files contain only routing, feature and lifecycle hooks.
The source distribution owns updates, so the normal installer cannot overwrite
the compiled customization. `ROBIN-SERIAL.md` documents merging upstream tags and
rebuilding with the pinned toolchain instead.

## Rejected alternatives

- A second application with copied colors: still two windows and two lifecycles.
- Embedding the Windows Forms process/window: retains a separate service owner,
  event loop and packaging dependency rather than using Pebrel's controls.
- Replacing terminal parsing or copying the upstream theme: duplicates mature
  behavior and increases the conflict surface of every upstream update.
- Making the addon an unconditional default feature: unnecessarily changes
  upstream's ordinary product and test configurations.

## Consequences

This build adds a Windows serial adapter and a direct dependency on the already
locked XML parser for migration. Domain code does not depend on GPUI. Ordinary
upstream builds remain available without the feature; existing startup flags and
saved upstream preferences retain their meaning. The current bridge supports
Windows; the UI reports that boundary on other platforms.

## Validation

Production-module tests cover memory-serial/real-TCP transfer, reconnect, stop,
cross-channel failure and configuration migration. Native GPUI fixtures cover
controls, selection, persistence and same-window terminal requests. Five backend
tests and two GPUI interaction tests passed locally on 2026-10-23. Physical-device
I/O remains untested; an existing terminal must release the port before device acceptance.

## Supersedes

None. The standalone launcher remains a recovery entry, not the integrated product.

## Revisit when

Upstream provides a serial capability or a supported settings extension point.
Replace the local hooks with that interface while retaining transfer and saved-data
contracts; remove the local feature once upstream supplies equivalent behavior.
