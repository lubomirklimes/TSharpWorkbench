// TSharpVision TestWorkbench sample target.
//
// Not an interactive sample: it runs only when a TestWorkbench controller launches it with the
// test-target activation argument and an authenticated session. See README.md.
using TSharpWorkbench.Testing;
using TSharpWorkbench.SampleTarget;

if (TestTargetHost.IsActivated(args)) return TestTargetHost.Run(SampleScenarios.Definition);
Console.Error.WriteLine("This is a TestWorkbench diagnostic target. Run it through TSharpWorkbench:");
Console.Error.WriteLine("  dotnet <TSharpWorkbench.dll> run --target <this executable> --all --size both --output <dir>");
return 2;
