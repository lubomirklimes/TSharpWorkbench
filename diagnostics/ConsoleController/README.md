# Owned console controller-death probe

This diagnostic controller uses the production TargetSession launcher, connects an ordinary
framework-only target, writes authenticated native console facts to the supplied output file, then
waits. ConsoleHostTests terminates this exact controller process to verify kill-on-controller-death.
It is not a second server or scenario runner.

Build in Release with the same local framework properties as the proxy tests.
Invocation: ConsoleController.exe <absolute ProxyOrdinary.exe> <absolute evidence.json>.
Run from the focused tests, which own the controller handle and wait for target/window cleanup.
Set TSWB_PROXY_CONTROLLER to the built executable when running those tests.

The same executable supports two focused diagnostic modes through environment variables.
TSW_CONSOLE_TARGET and TSW_CONSOLE_MARKER replace the two arguments. Setting
TSW_CONSOLE_PARENT_PROBE=1 runs a nested owned Console session, resizes the child, then compares the
parent's modes, geometry, cursor, palette, title and code pages. It peeks (never consumes) the parent's
queued records and verifies their prefix is preserved. Windows may append ordinary focus records
when a new window opens. Setting TSW_CONSOLE_RENDER_PROBE=1 runs the actual Console driver through
the proxy in this disposable host, checking native cell readback after full/fragment/overlap/redraw,
cursor position/shape/hiding and restored modes. These are renderer/lifecycle probes, not alternate
scenario runners. The mandatory ordinary-target JSON workflow never invokes this diagnostic code.
