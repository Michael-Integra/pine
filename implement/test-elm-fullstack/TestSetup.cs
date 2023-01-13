using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using ElmFullstack;
using ElmFullstack.ProcessStore;
using Pine;

namespace TestElmFullstack;

public class TestSetup
{
    static public string PathToExampleElmApps => "./../../../example-elm-apps";

    static public PineValue AppConfigComponentFromFiles(
        IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> appFiles) =>
        Composition.FromTreeWithStringPath(Composition.SortedTreeFromSetOfBlobsWithStringPath(appFiles))!;

    static public IEnumerable<(string serializedEvent, string expectedResponse)> CounterProcessTestEventsAndExpectedResponses(
        IEnumerable<(int addition, int expectedResponse)> additionsAndExpectedResponses) =>
        additionsAndExpectedResponses
        .Select(additionAndExpectedResponse =>
            (System.Text.Json.JsonSerializer.Serialize(new { addition = additionAndExpectedResponse.addition }),
            additionAndExpectedResponse.expectedResponse.ToString()));

    static public IEnumerable<(string serializedEvent, string expectedResponse)> CounterProcessTestEventsAndExpectedResponses(
        IEnumerable<int> additions, int previousValue = 0)
    {
        IEnumerable<(int addition, int expectedResponse)> enumerateWithExplicitExpectedResult()
        {
            var currentValue = previousValue;

            foreach (var addition in additions)
            {
                currentValue += addition;

                yield return (addition, currentValue);
            }
        }

        return CounterProcessTestEventsAndExpectedResponses(enumerateWithExplicitExpectedResult());
    }

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> CounterElmWebApp =
        GetElmAppFromExampleName("counter-webapp");

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> ReadSourceFileWebApp =
        GetElmAppFromExampleName("read-source-file-webapp");

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> StringBuilderElmWebApp =
        GetElmAppFromExampleName("string-builder-webapp");

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> CrossPropagateHttpHeadersToAndFromBodyElmWebApp =
       GetElmAppFromExampleName("cross-propagate-http-headers-to-and-from-body");

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> HttpProxyWebApp =
       GetElmAppFromExampleName("http-proxy");

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> WithElmFullstackJson(
        IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> originalWebAppConfig,
        WebAppConfigurationJsonStructure jsonStructure)
    {
        var filePath = ElmFullstack.WebHost.StartupAdminInterface.JsonFilePath;

        return
            jsonStructure == null ?
            originalWebAppConfig.Remove(filePath)
            :
            originalWebAppConfig
            .SetItem(filePath, System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(jsonStructure)));
    }

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> GetElmAppFromExampleName(
        string exampleName) => GetElmAppFromDirectoryPath(Path.Combine(PathToExampleElmApps, exampleName));

    static string FilePathStringFromPath(IImmutableList<string> path) =>
        Path.Combine(path.ToArray());

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> GetElmAppFromDirectoryPath(
        IImmutableList<string> directoryPath) =>
        GetElmAppFromDirectoryPath(FilePathStringFromPath(directoryPath));

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> GetElmAppFromDirectoryPath(
        string directoryPath) =>
            Composition.ToFlatDictionaryWithPathComparer(
                Filesystem.GetAllFilesFromDirectory(directoryPath)
                .OrderBy(file => string.Join('/', file.path)));

    static public IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> AsLoweredElmApp(
        IImmutableDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> originalAppFiles)
    {
        var compilationResult =
            ElmAppCompilation.AsCompletelyLoweredElmApp(
                sourceFiles: originalAppFiles,
                ElmAppInterfaceConfig.Default)
            .Extract(error => throw new Exception(ElmAppCompilation.CompileCompilationErrorsDisplayText(error)));

        return compilationResult.result.compiledFiles;
    }

    static public IProcessStoreReader EmptyProcessStoreReader() =>
        new ProcessStoreReaderFromDelegates
        (
            EnumerateSerializedCompositionsRecordsReverseDelegate: () => Array.Empty<byte[]>(),
            GetReductionDelegate: _ => null
        );
}

record ProcessStoreReaderFromDelegates(
    Func<IEnumerable<byte[]>> EnumerateSerializedCompositionsRecordsReverseDelegate,
    Func<byte[], ReductionRecord?> GetReductionDelegate)
    : IProcessStoreReader
{
    public IEnumerable<byte[]> EnumerateSerializedCompositionsRecordsReverse() =>
        EnumerateSerializedCompositionsRecordsReverseDelegate();

    public ReductionRecord? GetReduction(byte[] reducedCompositionHash) =>
        GetReductionDelegate(reducedCompositionHash);
}
