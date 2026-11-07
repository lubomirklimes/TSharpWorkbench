using System.Reflection;
using System.Text.Json.Nodes;
using TSharpVision;
using TSharpWorkbench.Testing;

namespace TSharpWorkbench;

// Developer helper: renders every step and assertion editor dialog and stores logical PNGs.
internal static class EditorDialogCapture
{
    internal static string Run(TProgram app, string output)
    {
        Directory.CreateDirectory(output);
        var desktop = app.DeskTop!;
        int count = 0;
        var errors = new List<string>();
        foreach (string kind in ScenarioSchemaV1.StepKinds)
            Shot(app, Path.Combine(output, "step-" + kind + ".png"), () =>
                (TDialog)Create(typeof(WorkbenchScenarioStepDialog), desktop, ScenarioDocumentStep.FromJson(Sample(kind)), false, null, false), ref count, errors);
        foreach (string kind in ScenarioSchemaV1.AssertionKinds)
            Shot(app, Path.Combine(output, "assertion-" + kind + ".png"), () =>
            {
                var a = new JsonObject { ["id"] = kind, ["kind"] = kind };
                return (TDialog)Create(typeof(WorkbenchAssertionDialog), desktop, a, AssertionPanel.Create(a), true);
            }, ref count, errors);
        app.Redraw();
        return $"{count} PNG -> {output}" + (errors.Count > 0 ? "; errors: " + string.Join(" | ", errors) : "");
    }

    private static void Shot(TProgram app, string file, Func<TDialog> create, ref int count, List<string> errors)
    {
        try
        {
            var desktop = app.DeskTop!;
            var dialog = create();
            dialog.MoveTo(Math.Max(0, (desktop.size.x - dialog.size.x) / 2), Math.Max(0, (desktop.size.y - dialog.size.y) / 2));
            desktop.Insert(dialog);
            app.Redraw();
            File.WriteAllBytes(file, LogicalPng.Encode(ScreenSnapshot.CopyRoot()));
            dialog.Hide();
            count++;
        }
        catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + (ex.InnerException ?? ex).Message); }
    }

    private static JsonObject Sample(string kind) => kind switch
    {
        "key" or "keyDown" or "keyUp" => new() { ["kind"] = kind, ["key"] = "kbF10" },
        "text" => new() { ["kind"] = kind, ["text"] = "Hello" },
        "mouse" => new() { ["kind"] = kind, ["input"] = "click-left@10,5" },
        "resize" => new() { ["kind"] = kind, ["width"] = 100, ["height"] = 30 },
        "checkpoint" => new()
        {
            ["kind"] = kind, ["id"] = "main-menu", ["evidence"] = new JsonArray("view-tree"),
            ["assertions"] = new JsonArray(new JsonObject { ["kind"] = "screen-contains", ["id"] = "title", ["text"] = "File" })
        },
        _ => new() { ["kind"] = kind }
    };

    private static object Create(Type type, params object?[] args) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(c => c.GetParameters().Length == args.Length && c.GetParameters().Zip(args).All(p => p.Second is null || p.First.ParameterType.IsInstanceOfType(p.Second)))
            .Invoke(args);
}
