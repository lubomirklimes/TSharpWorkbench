# TestWorkbench sample target

A small, ordinary TSharpVision application used to prove the TestWorkbench pipeline without any other application: a menu
bar (File with a disabled Print entry, Window with a nested Arrange submenu, Help), a status line with two help contexts,
modeless note windows and a modal dialog. It performs no network, device or process I/O and never reads user state; the
only file it touches is the one its fixture scenario's setup writes into the controller-owned working directory.

It only runs as a diagnostic target (`--tsharpvision-test-target` plus an authenticated TestWorkbench session); started
directly it prints usage and exits 2. It is not packed and has no interactive driver.

| Scenario | Kind / grid | Interaction | Proves |
|---|---|---|---|
| `sample/dialog/basic` | dialog, ≥ 60x20 | F3, Tab, Tab, Shift+Tab ×2, Esc | modal dialog, focus navigation, view tree, geometry/shadow/focus assertions |
| `sample/menu/navigation` | menu | Alt+F, Down, Right, Enter, Esc, Esc | drop-down, selection, next menu, submenu, disabled entry |
| `sample/menu/selection` | menu | F10 (this app's status item), Down, Down ×3, Alt+W, Enter, Esc ×2 | bar activation, repeated identical keys, mnemonic, submenu, restore — no expectation predicates |
| `sample/status/context` | status, ≥ 64x12 | F4 opens a note window, Alt+F3 closes it | status context change (items, hint, help context) and return |
| `sample/screen/resize` | screen, ≥ 60x20 | F4, resize 100x30, resize 60x20, Alt+F3 | real logical resize, current-grid checkpoints, containment |
| `sample/fixture/window` | window, ≥ 60x20 | setup writes and shows `readme.txt`; Alt+F3 | explicit setup, fixture integrity, profile isolation check |
| `selftest/input/queued-burst` | mixed, ≥ 60x20 | action starts a background command producer; F3 while it runs; Esc | input acknowledgement with queued events ahead of the key |
| `selftest/failure/wrong-expectations` | expected failure | F3, Esc | wrong focus/text/menu-depth assertions FAIL with checkpoint, expected and actual |
| `selftest/failure/blocked-ui` | expected failure | action blocks the UI thread 3 s | bounded timeout (use `--timeout 500`) |
| `selftest/failure/target-crash` | expected failure | action posts an event whose handler throws | crash reported with the target's exception text; cleanup still verified |

Expected-failure scenarios are excluded from `--all`/`--group` unless `--include-expected-failures` is given. Run them with
`../../scripts/capture-sample-target.ps1` (see `../../src/TSharpWorkbench/README.md`).

Two framework findings are worked around here and documented in the report: menu entries do not follow
`cmCommandSetChanged` (the Print entry sets `TMenuItem.Disabled` itself) and a narrow `TFrame` throws on title truncation
(note windows keep a fixed size across resizes).
