using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pine;
using Pine.Core;
using Pine.Elm;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TestElmTime;

[TestClass]
public class ElmInteractiveTests
{
    private static string PathToCoreScenariosDirectory => @"./../../../../test-and-train/elm-interactive-scenarios-core";

    private static string PathToKernelScenariosDirectory => @"./../../../../test-and-train/elm-interactive-scenarios-kernel";

    public static TreeNodeWithStringPath CompileElmProgramCodeFiles =>
        ElmCompiler.CompilerSourceFilesDefault.Value;

    private static readonly Lazy<ElmTime.JavaScript.IJavaScriptEngine> compileElmPreparedJavaScriptEngine =
        new(() => ElmTime.ElmInteractive.ElmInteractive.PrepareJavaScriptEngineToEvaluateElm(
            compileElmProgramCodeFiles: CompileElmProgramCodeFiles,
            ElmTime.ElmInteractive.InteractiveSessionJavaScript.JavaScriptEngineFlavor.V8));

    [TestMethod]
    [Timeout(1000 * 60 * 50)]
    public void TestElmInteractiveScenarios()
    {
        var console = (IConsole)StaticConsole.Instance;

        var scenarios =
            new[]
            {
                PathToCoreScenariosDirectory,
                PathToKernelScenariosDirectory
            }
            .SelectMany(Directory.EnumerateDirectories)
            .SelectMany(scenarioDirectory =>
            {
                var scenarioName = Path.GetFileName(scenarioDirectory);

                if (!Directory.EnumerateFiles(scenarioDirectory, "*", searchOption: SearchOption.AllDirectories).Any())
                {
                    // Do not stumble over empty directory here. It could be a leftover after git checkout.
                    return [];
                }

                return ImmutableList.Create((scenarioName, scenarioDirectory));
            })
            .ToImmutableList();

        var scenariosTree =
            TreeNodeWithStringPath.SortedTree(
                scenarios
                .Select(scenario =>
                (name: scenario.scenarioName,
                component: LoadFromLocalFilesystem.LoadSortedTreeFromPath(scenario.scenarioDirectory)!))
                .ToImmutableList());

        var parsedScenarios =
            ElmTime.ElmInteractive.TestElmInteractive.ParseElmInteractiveScenarios(scenariosTree, console);

        ElmTime.ElmInteractive.IInteractiveSession newInteractiveSessionFromAppCode(TreeNodeWithStringPath? appCodeTree) =>
            new ElmTime.ElmInteractive.InteractiveSessionPine(
                compileElmProgramCodeFiles: CompileElmProgramCodeFiles,
                initialState: null,
                appCodeTree: appCodeTree,
                caching: true,
                autoPGO: null);

        {
            var warmupStopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var session = newInteractiveSessionFromAppCode(null);

            session.Submit("1 + 3");

            console.WriteLine(
                "Warmup completed in " +
                warmupStopwatch.Elapsed.TotalSeconds.ToString("0.##") + " seconds.");
        }

        var scenariosResults =
            ElmTime.ElmInteractive.TestElmInteractive.TestElmInteractiveScenarios(
                parsedScenarios.NamedDistinctScenarios,
                namedScenario => (namedScenario.Key, namedScenario.Value),
                newInteractiveSessionFromAppCode,
                asyncLogDelegate:
                logEntry =>
                console.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, logEntry })));

        var allSteps =
            scenariosResults
            .SelectMany(scenario => scenario.Value.StepsReports.Select(step => (scenario, step)))
            .ToImmutableList();

        var passedSteps =
            allSteps.Where(step => step.step.result.IsOk()).ToImmutableList();

        var failedSteps =
            allSteps.Where(step => !step.step.result.IsOk()).ToImmutableList();

        var failedScenarios =
            failedSteps
            .GroupBy(failedStep => failedStep.scenario.Key.Key)
            .ToImmutableSortedDictionary(
                keySelector: failedScenario => failedScenario.Key,
                elementSelector: failedScenario => failedScenario);

        console.WriteLine("Total scenarios: " + scenariosResults.Count);
        console.WriteLine("Total steps: " + allSteps.Count);
        console.WriteLine("Passed scenarios: " + scenariosResults.Values.Count(scenarioResult => scenarioResult.Passed));
        console.WriteLine("Passed steps: " + passedSteps.Count);

        foreach (var failedScenario in failedScenarios)
        {
            var scenarioId = failedScenario.Value.Key;

            console.WriteLine(
                "Failed " + failedScenario.Value.Count() + " step(s) in scenario " + scenarioId + ":",
                color: IConsole.TextColor.Red);

            foreach (var failedStep in failedScenario.Value)
            {
                console.WriteLine(
                    "Failed step '" + failedStep.step.name + "':\n" +
                    failedStep.step.result.Unpack(fromErr: err => err, fromOk: _ => throw new NotImplementedException()).errorAsText,
                    color: IConsole.TextColor.Red);
            }
        }

        if (!failedScenarios.IsEmpty)
        {
            throw new Exception(
                "Failed for " + failedScenarios.Count + " scenarios:\n" +
                string.Join("\n", failedScenarios.Select(scenarioNameAndResult => scenarioNameAndResult.Key)));
        }
    }

    [TestMethod]
    [Timeout(1000 * 60 * 8)]
    public void First_submission_in_interactive_benefits_from_dynamic_PGO()
    {
        using var dynamicPGOShare = new Pine.PineVM.DynamicPGOShare();

        var console = (IConsole)StaticConsole.Instance;

        ElmTime.ElmInteractive.IInteractiveSession newInteractiveSessionFromAppCode(TreeNodeWithStringPath? appCodeTree) =>
            new ElmTime.ElmInteractive.InteractiveSessionPine(
                compileElmProgramCodeFiles: CompileElmProgramCodeFiles,
                initialState: null,
                appCodeTree: appCodeTree,
                dynamicPGOShare.GetVMAutoUpdating());

        {
            var warmupStopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var session = newInteractiveSessionFromAppCode(null);

            session.Submit("1 + 3");

            console.WriteLine(
                "Warmup completed in " +
                warmupStopwatch.Elapsed.TotalSeconds.ToString("0.##") + " seconds.");
        }

        {
            /*
             * The scenario with 'String.fromInt' exhibited exponential runtime on the interpreter compared to the optimized version.
             * TODO: Replace with a more challenging scenario that is also particular enough to be covered outside of core libraries and prior training.
             * */

            using var session = newInteractiveSessionFromAppCode(null);

            var submissionStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var submissionResult = session.Submit("[String.fromInt 123, String.fromInt 34567834567]");

            submissionStopwatch.Stop();

            var responseDisplayText =
                submissionResult.Unpack(
                    fromErr: err => throw new Exception(err),
                    fromOk: ok => ok.InteractiveResponse.DisplayText);

            Assert.AreEqual(expected: """["123","34567834567"]""", responseDisplayText);
        }
    }

    [TestMethod]
    public void Parse_interactive_submission()
    {
        {
            var parseResult =
                ElmTime.ElmInteractive.ElmInteractive.ParseInteractiveSubmission(
                    evalElmPreparedJavaScriptEngine: compileElmPreparedJavaScriptEngine.Value,
                    submission: "test",
                    addInspectionLogEntry: null);

            var parseResultOk =
                parseResult
                .Extract(err => throw new Exception("Unexpected parsing error: " + err));

            var asElmValue =
                Pine.ElmInteractive.ElmValueEncoding.PineValueAsElmValue(parseResultOk)
                .Extract(err => throw new Exception("Failed parsing result as Elm value: " + err));

            Assert.AreEqual(
                "ExpressionSubmission (FunctionOrValue [] \"test\")",
                Pine.ElmInteractive.ElmValue.RenderAsElmExpression(asElmValue).expressionString);
        }

        {
            var parseResult =
                ElmTime.ElmInteractive.ElmInteractive.ParseInteractiveSubmission(
                    evalElmPreparedJavaScriptEngine: compileElmPreparedJavaScriptEngine.Value,
                    submission: "test = 123",
                    addInspectionLogEntry: null);

            var parseResultOk =
                parseResult
                .Extract(err => throw new Exception("Unexpected parsing error: " + err));

            var asElmValue =
                Pine.ElmInteractive.ElmValueEncoding.PineValueAsElmValue(parseResultOk)
                .Extract(err => throw new Exception("Failed parsing result as Elm value: " + err));

            Assert.AreEqual(
                "DeclarationSubmission (FunctionDeclaration { declaration = Node { end = { column = 11, row = 6 }, start = { column = 1, row = 6 } } { arguments = [], expression = Node { end = { column = 11, row = 6 }, start = { column = 8, row = 6 } } (Integer 123), name = Node { end = { column = 5, row = 6 }, start = { column = 1, row = 6 } } \"test\" }, documentation = Nothing, signature = Nothing })",
                Pine.ElmInteractive.ElmValue.RenderAsElmExpression(asElmValue).expressionString);
        }
    }
}
