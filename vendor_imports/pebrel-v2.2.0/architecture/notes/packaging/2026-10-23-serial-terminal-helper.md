# Console entry for the integrated serial terminal

## Evidence and decision

On 2026-10-23 the release GUI executable passed redirected binary TCP transfer,
but a direct ConPTY launch produced no terminal output or input. A minimal native
probe under the same PTY observed zero console code pages, null standard handles,
and failed CONIN$/CONOUT$ opens for a Windows-subsystem child. AllocConsole created
a different console, not the intended PTY. A console-subsystem probe attached with
valid console handles. A GUI executable launched without waiting from PowerShell
also allowed the shell to compete with the client for input.

Build `pebrel-serial` as an opt-in console binary from the same `client.rs` used by
the main CLI. The native settings action launches it directly inside the existing
terminal tab. Portable installation puts it in `runtime/`, alongside the existing
hook and ConPTY runtime; Cargo development builds resolve it beside `pebrel`.
Missing helpers fail through the existing terminal spawn path, never through a
fallback to Ncat or another GUI. There is still one settings/service owner and one
GUI application; the child is only a terminal transport.

This leaves upstream ConPTY creation and rendering unchanged. The build helper
includes the new binary explicitly; ordinary upstream builds remain unchanged.
The compatibility PowerShell profile invokes this console entry synchronously.
