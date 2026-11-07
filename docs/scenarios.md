# Scenarios

A scenario is a sequence of input steps and checkpoints. Checkpoints capture state and evaluate assertions. The [Scenario Editor](manual/Manual.md#editor) edits these documents; the runner validates them before execution.

## Ordinary applications

Use schema version 1 and `backend: "proxy-logical"` for an ordinary application with compatible Proxy support. This example checks a modal dialog after F3; use a key and title that match your target:

```json
{
  "schema": "tsharpworkbench.scenario",
  "version": 1,
  "id": "example/dialog",
  "title": "Open a dialog",
  "description": "Verify the dialog reached after F3.",
  "backend": "proxy-logical",
  "expectedOutcome": "passed",
  "steps": [
    { "kind": "key", "key": "kbF3" },
    {
      "kind": "checkpoint",
      "id": "dialog-open",
      "evidence": ["view-tree"],
      "assertions": [
        { "id": "title", "kind": "screen-contains", "text": "Ordinary modal" },
        { "id": "layout", "kind": "layout-audit", "profile": "geometry" }
      ]
    }
  ]
}
```

Attach the document through Scenario Manager. For CLI use, put its path in the `.twproj` configuration's `scenarioFiles` array; paths resolve relative to the project file. See the [project format](../src/TSharpWorkbench/PROJECT-FORMAT.md#external-json-scenarios).

## Steps and assertions

Supported step kinds include key, text, mouse, resize, checkpoint, and fixture action, with explicit key-down/up, modifier, and release steps where supported. Mouse coordinates refer to character cells, so choose a compatible grid.

Assertions cover screen text and attributes, view existence/focus, modal state, menu and status information, geometry, and composite [layout audits](layout.md). View-based checks need view-tree evidence. A checkpoint can specify `retry` with `attempts` and `intervalMilliseconds` to wait for an observable condition; elapsed pauses in a recording are not synchronization.

Use unique scenario/checkpoint/assertion IDs, validate in the editor, and inspect the measured result after replay. [Smart Recorder](recorder.md) can suggest checks from captured states, but they still need review.

## Diagnostic fixtures

Fixture-bound scenarios use the target's advertised fixture contract and may invoke named actions or checkpoints. They are distinct from proxy-logical scenarios; a project cannot mix those two execution kinds. The [sample target](../tests/TSharpWorkbench.SampleTarget/README.md) documents the repository fixture catalog.

See [CLI usage](usage.md#command-line) for execution and [capture types](capture.md) for evidence limits.
