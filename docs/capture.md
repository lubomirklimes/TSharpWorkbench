# Captures and evidence

Checkpoints capture logical screens and requested inspection data. Functional assertions, visual comparisons, and native capture answer different questions; inspect their outcomes separately.

| Evidence | Meaning |
| --- | --- |
| Logical cells (`TVS1`) and their PNG rendering | Characters and attributes used for logical comparison; not a desktop screenshot. |
| View tree, menu, and status inspection | Semantic state used by assertions and layout audits. |
| Native console cells | Readback of an owned Windows Console buffer; not host-window pixels. |
| Renderer pixels | Pixels returned by the selected native renderer at its documented capture boundary. |
| Workbench screenshots in this documentation | Images of the frontend UI; they do not establish target rendering correctness. |

## Proxy capture selection

For proxy-logical scenarios, `proxyBackend` selects the target driver independently of the Workbench host. `proxyCapture` defaults to `none`. Supported requests include `renderer-pixels`, `gpu-completed`, and `native-console-cells`, depending on backend capabilities. The handshake checks actual availability; unsupported requests are refused rather than silently substituted.

Logical evidence remains separate from native artifacts. GPU readback and completed-frame evidence do not automatically include OS-compositor or monitor output. A passing headless test does not establish native visual correctness.

See the [project capture fields](../src/TSharpWorkbench/PROJECT-FORMAT.md#proxy-logical-sessions), [layout audit](layout.md), and [platform status](../README.md#platform-status).
