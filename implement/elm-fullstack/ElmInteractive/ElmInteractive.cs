using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using ElmFullstack;
using Pine;
using static Pine.Composition;

namespace elm_fullstack.ElmInteractive;

public class ElmInteractive
{
    static public readonly Lazy<string> JavascriptToEvaluateElm = new(PrepareJavascriptToEvaluateElm, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    static public Result<string, EvaluatedSctructure> EvaluateSubmissionAndGetResultingValue(
        JavaScriptEngineSwitcher.Core.IJsEngine evalElmPreparedJsEngine,
        TreeWithStringPath? appCodeTree,
        string submission,
        IReadOnlyList<string>? previousLocalSubmissions = null)
    {
        var modulesTexts = ModulesTextsFromAppCodeTree(appCodeTree);

        var argumentsJson = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                modulesTexts = modulesTexts ?? ImmutableList<string>.Empty,
                submission = submission,
                previousLocalSubmissions = previousLocalSubmissions ?? ImmutableList<string>.Empty,
            }
        );

        var responseJson =
            evalElmPreparedJsEngine.CallFunction("evaluateSubmissionInInteractive", argumentsJson).ToString()!;

        var responseStructure =
            System.Text.Json.JsonSerializer.Deserialize<EvaluateSubmissionResponseStructure>(responseJson)!;

        if (responseStructure.DecodedArguments == null)
            throw new Exception("Failed to decode arguments: " + responseStructure.FailedToDecodeArguments);

        if (responseStructure.DecodedArguments.Evaluated == null)
            return Result<string, EvaluatedSctructure>.err(responseStructure.DecodedArguments.FailedToEvaluate!);

        return Result<string, EvaluatedSctructure>.ok(
            responseStructure.DecodedArguments.Evaluated);
    }

    static public Result<string, Component> PineEvalContextForElmInteractive(
        JavaScriptEngineSwitcher.Core.IJsEngine evalElmPreparedJsEngine,
        TreeWithStringPath? appCodeTree)
    {
        var modulesTexts = ModulesTextsFromAppCodeTree(appCodeTree);

        var argumentsJson = System.Text.Json.JsonSerializer.Serialize(modulesTexts ?? ImmutableList<string>.Empty);

        var responseJson =
            evalElmPreparedJsEngine.CallFunction("pineEvalContextForElmInteractive", argumentsJson).ToString()!;

        var responseStructure =
            System.Text.Json.JsonSerializer.Deserialize<ResultFromJsonResult<string, PineValueFromJson>>(
                responseJson,
                new System.Text.Json.JsonSerializerOptions { MaxDepth = 1000 })!;

        return
            responseStructure
            .AsResult()
            .map(fromJson => ParsePineComponentFromJson(fromJson!));
    }

    static public Result<string?, Component?> CompileInteractiveSubmissionIntoPineExpression(
        JavaScriptEngineSwitcher.Core.IJsEngine evalElmPreparedJsEngine,
        Component environment,
        string submission)
    {
        var requestJson =
            System.Text.Json.JsonSerializer.Serialize(
                new CompileInteractiveSubmissionIntoPineExpressionRequest
                (
                    environment: PineValueFromJson.FromComponent(environment),
                    submission: submission
                ),
                options: new System.Text.Json.JsonSerializerOptions { MaxDepth = 1000 });

        var responseJson =
            evalElmPreparedJsEngine.CallFunction("compileInteractiveSubmissionIntoPineExpression", requestJson).ToString()!;

        var responseStructure =
            System.Text.Json.JsonSerializer.Deserialize<ResultFromJsonResult<string?, PineValueFromJson?>>(
                responseJson,
                options: new System.Text.Json.JsonSerializerOptions { MaxDepth = 1000 })!;

        return
            responseStructure
            .AsResult()
            .map(fromJson => (Component?)ParsePineComponentFromJson(fromJson!));
    }

    record CompileInteractiveSubmissionIntoPineExpressionRequest(
        PineValueFromJson environment,
        string submission);

    static public Result<string?, EvaluatedSctructure?> SubmissionResponseFromResponsePineValue(
        JavaScriptEngineSwitcher.Core.IJsEngine evalElmPreparedJsEngine,
        Component response)
    {
        var responseJson =
            evalElmPreparedJsEngine.CallFunction(
                "submissionResponseFromResponsePineValue",
                System.Text.Json.JsonSerializer.Serialize(PineValueFromJson.FromComponent(response))).ToString()!;

        var responseStructure =
            System.Text.Json.JsonSerializer.Deserialize<ResultFromJsonResult<string?, EvaluatedSctructure?>>(responseJson)!;

        return responseStructure.AsResult();
    }

    public record ResultFromJsonResult<ErrT, OkT>
    {
        public IReadOnlyList<ErrT>? Err { set; get; }

        public IReadOnlyList<OkT>? Ok { set; get; }

        public Result<ErrT, OkT> AsResult()
        {
            if (Err?.Count == 1 && (Ok?.Count ?? 0) == 0)
                return Result<ErrT, OkT>.err(Err.Single());

            if ((Err?.Count ?? 0) == 0 && Ok?.Count == 1)
                return Result<ErrT, OkT>.ok(Ok.Single());

            throw new Exception("Unexpected shape: Err: " + Err?.Count + ", OK: " + Ok?.Count);
        }
    }


    record PineValueFromJson
    {
        public IReadOnlyList<PineValueFromJson>? List { init; get; }

        public IReadOnlyList<int>? Blob { init; get; }

        public string? ListAsString { init; get; }

        static public PineValueFromJson FromComponent(Component component)
        {
            if (component.ListContent != null)
                return new PineValueFromJson { List = component.ListContent.Select(FromComponent).ToImmutableList() };

            if (component.BlobContent != null)
                return new PineValueFromJson { Blob = component.BlobContent.Value.ToArray().Select(b => (int)b).ToImmutableArray() };

            throw new NotImplementedException("Unexpected shape");
        }
    }

    static Component ParsePineComponentFromJson(PineValueFromJson fromJson)
    {
        if (fromJson.List != null)
            return Component.List(fromJson.List.Select(ParsePineComponentFromJson).ToImmutableList());

        if (fromJson.Blob != null)
            return Component.Blob(fromJson.Blob.Select(b => (byte)b).ToArray());

        if (fromJson.ListAsString != null)
            return ComponentFromString(fromJson.ListAsString);

        throw new NotImplementedException("Unexpected shape of Pine value from JSON");
    }

    static IReadOnlyCollection<string>? ModulesTextsFromAppCodeTree(TreeWithStringPath? appCodeTree) =>
        appCodeTree == null ? null
        :
        TreeToFlatDictionaryWithPathComparer(compileTree(appCodeTree)!)
        .Select(appCodeFile => appCodeFile.Key.Last().EndsWith(".elm") ? Encoding.UTF8.GetString(appCodeFile.Value.ToArray()) : null)
        .WhereNotNull()
        .ToImmutableList();

    static TreeWithStringPath? compileTree(TreeWithStringPath? sourceTree)
    {
        if (sourceTree == null)
            return null;

        var compilationResult = ElmAppCompilation.AsCompletelyLoweredElmApp(
            sourceFiles: TreeToFlatDictionaryWithPathComparer(sourceTree),
            ElmAppInterfaceConfig.Default);

        if (compilationResult.Ok == null)
        {
            var errorMessage = "\n" + ElmAppCompilation.CompileCompilationErrorsDisplayText(compilationResult.Err) + "\n";

            Console.WriteLine(errorMessage);

            throw new Exception(errorMessage);
        }

        return SortedTreeFromSetOfBlobsWithStringPath(compilationResult.Ok.compiledAppFiles);
    }

    static public JavaScriptEngineSwitcher.Core.IJsEngine PrepareJsEngineToEvaluateElm()
    {
        var javascriptEngine = ProcessHostedWithV8.ConstructJsEngine();

        javascriptEngine.Evaluate(JavascriptToEvaluateElm.Value);

        return javascriptEngine;
    }

    static public string PrepareJavascriptToEvaluateElm()
    {
        var parseElmAppCodeFiles = ParseElmSyntaxAppCodeFiles();

        var javascriptFromElmMake =
            ProcessFromElm019Code.CompileElmToJavascript(
                parseElmAppCodeFiles,
                ImmutableList.Create("src", "Main.elm"));

        var javascriptMinusCrashes = ProcessFromElm019Code.JavascriptMinusCrashes(javascriptFromElmMake);

        var listFunctionToPublish =
            new[]
            {
                (functionNameInElm: "Main.evaluateSubmissionInInteractive",
                publicName: "evaluateSubmissionInInteractive",
                arity: 1),

                (functionNameInElm: "Main.pineEvalContextForElmInteractive",
                publicName: "pineEvalContextForElmInteractive",
                arity: 1),

                (functionNameInElm: "Main.compileInteractiveSubmissionIntoPineExpression",
                publicName: "compileInteractiveSubmissionIntoPineExpression",
                arity: 1),

                (functionNameInElm: "Main.submissionResponseFromResponsePineValue",
                publicName: "submissionResponseFromResponsePineValue",
                arity: 1),
            };

        return
            ProcessFromElm019Code.PublishFunctionsFromJavascriptFromElmMake(
                javascriptMinusCrashes,
                listFunctionToPublish);
    }

    static public IImmutableDictionary<IImmutableList<string>, ReadOnlyMemory<byte>> ParseElmSyntaxAppCodeFiles() =>
        DotNetAssembly.LoadFromAssemblyManifestResourceStreamContents(
            filePaths: new[]
            {
                ImmutableList.Create("elm.json"),
                ImmutableList.Create("src", "Pine.elm"),
                ImmutableList.Create("src", "ElmInteractive.elm"),
                ImmutableList.Create("src", "Main.elm")
            },
            resourceNameCommonPrefix: "elm_fullstack.ElmInteractive.interpret_elm_program.",
            assembly: typeof(ElmInteractive).Assembly).Ok!;

    record EvaluateSubmissionResponseStructure
        (string? FailedToDecodeArguments = null,
        DecodedArgumentsSctructure? DecodedArguments = null);

    record DecodedArgumentsSctructure(
        string? FailedToEvaluate = null,
        EvaluatedSctructure? Evaluated = null);

    public record EvaluatedSctructure(
        string displayText);
}
