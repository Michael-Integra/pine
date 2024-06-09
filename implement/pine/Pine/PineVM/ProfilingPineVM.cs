using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using ExpressionUsageRecord = System.Collections.Generic.Dictionary<Pine.PineValue, Pine.PineVM.ExpressionEnvUsageRecord>;

namespace Pine.PineVM;

public record ExpressionEnvUsageRecord(
    PineValue Environment,
    List<System.TimeSpan> OrigEvalDurations,
    System.Lazy<Result<string, IReadOnlyList<ExpressionUsageAnalysis>>> Analysis)
{
    public long ParseAndEvalCountMax { get; set; }
}

public record ExpressionUsageProfile(int UsageCount);


public record ExpressionUsageAnalysis
{
    public Expression Expression { get; init; }

    public CompilePineToDotNet.CompiledExpressionId CompiledExpressionId { init; get; }

    public EnvConstraintId? EnvId { get; init; }

    public ExpressionUsageAnalysis(Expression expression, EnvConstraintId? envId)
    {
        Expression = expression;
        EnvId = envId;

        CompiledExpressionId =
            CompilePineToDotNet.CompileToCSharp.CompiledExpressionId(expression)
            .Extract(err => throw new System.Exception(err));
    }

    public override int GetHashCode()
    {
        return Expression.GetHashCode();
    }

    public virtual bool Equals(ExpressionUsageAnalysis? other)
    {
        if (ReferenceEquals(this, other))
            return true;

        if (other is null)
            return false;

        if (CompiledExpressionId.ExpressionHashBase16 != other.CompiledExpressionId.ExpressionHashBase16)
            return false;

        return EnvConstraintId.Equal(EnvId, other.EnvId);
    }
}

public class ProfilingPineVM
{
    public readonly ConcurrentQueue<System.TimeSpan> computeExpressionUsageTimes = new();

    public IPineVM PineVM { init; get; }

    private readonly PineVMCache parseExprCache = new();

    private readonly Dictionary<Expression, ExpressionUsageRecord> expressionUsages = [];

    public IReadOnlyDictionary<(Expression, PineValue), ExpressionEnvUsageRecord> ExprEnvUsagesFlat =>
        expressionUsages
        .SelectMany(
            exprUsageRecord =>
            exprUsageRecord.Value
            .Select(
                envUsageRecord =>
                new KeyValuePair<(Expression, PineValue), ExpressionEnvUsageRecord>
                ((exprUsageRecord.Key, envUsageRecord.Key), envUsageRecord.Value)))
        .ToImmutableDictionary();

    public ProfilingPineVM(
        OverrideParseExprDelegate? overrideParseExpression = null,
        IDictionary<EvalCacheEntryKey, PineValue>? evalCache = null,
        PineVMCache? analysisEvalCache = null)
    {
        ConcurrentDictionary<Expression, CodeAnalysis.ExprAnalysis> exprAnalysisMutatedCache = new();

        ParseExprDelegate parseExpressionForAnalysis =
            parseExprCache.BuildParseExprDelegate(ExpressionEncoding.ParseExpressionFromValueDefault);

        var parseExpressionForEval =
            overrideParseExpression?.Invoke(parseExpressionForAnalysis) ??
            parseExpressionForAnalysis;

        PineVM =
            new PineVM(
                overrideParseExpression: overrideParseExpression,
                evalCache: evalCache,
                reportFunctionApplication:
                funcApplReport =>
                {
                    var originalExpression =
                    ExpressionEncoding.ParseExpressionFromValueDefault(funcApplReport.ExpressionValue)
                    .Extract(err => throw new System.Exception(err));

                    // if (DynamicPGOShare.ShouldIncludeExpressionInCompilation(expression))
                    // if (expression is Expression.ParseAndEvalExpression parseAndEval)
                    {
                        Result<string, IReadOnlyList<ExpressionUsageAnalysis>> runAnalysis()
                        {
                            var analysisOuterStartTime = System.Diagnostics.Stopwatch.GetTimestamp();

                            {
                                try
                                {
                                    return
                                        Result<string, IReadOnlyList<ExpressionUsageAnalysis>>.ok(
                                            AnalyzeExpressionUsage(
                                                originalExpression,
                                                funcApplReport.Environment,
                                                exprAnalysisMutatedCache,
                                                parseExpressionForAnalysis: parseExpressionForAnalysis,
                                                parseExpressionForEval: parseExpressionForEval,
                                                evalCache: analysisEvalCache));
                                }
                                finally
                                {
                                    computeExpressionUsageTimes.Enqueue(
                                        System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: analysisOuterStartTime));
                                }
                            }
                        }

                        var exprUsageAlreadyInDict =
                        expressionUsages.TryGetValue(
                            key: originalExpression,
                            out var exprUsageRecord);

                        exprUsageRecord ??= [];

                        var envUsageAlreadyInDict =
                            exprUsageRecord.TryGetValue(
                                key: funcApplReport.Environment,
                                out var envContainer);

                        envContainer ??= new ExpressionEnvUsageRecord(
                            Environment: funcApplReport.Environment,
                            OrigEvalDurations: [],
                            Analysis: new System.Lazy<Result<string, IReadOnlyList<ExpressionUsageAnalysis>>>(runAnalysis));

                        envContainer.OrigEvalDurations.Add(funcApplReport.ElapsedTime);

                        envContainer.ParseAndEvalCountMax = System.Math.Max(
                            envContainer.ParseAndEvalCountMax,
                            funcApplReport.ParseAndEvalCount);

                        if (!envUsageAlreadyInDict)
                        {
                            exprUsageRecord[funcApplReport.Environment] = envContainer;
                        }

                        if (!exprUsageAlreadyInDict)
                        {
                            expressionUsages[originalExpression] = exprUsageRecord;
                        }
                    }
                });
    }

    public static IReadOnlyList<ExpressionUsageAnalysis> AnalyzeExpressionUsage(
        Expression expression,
        PineValue environment,
        ConcurrentDictionary<Expression, CodeAnalysis.ExprAnalysis> exprAnalysisMutatedCache,
        ParseExprDelegate parseExpressionForAnalysis,
        ParseExprDelegate parseExpressionForEval,
        PineVMCache? evalCache)
    {
        var analysisResult =
            CodeAnalysis.AnalyzeExpressionUsageRecursive(
                [],
                expression,
                environment,
                mutatedCache: exprAnalysisMutatedCache,
                parseExpressionForAnalysis: parseExpressionForAnalysis,
                parseExpressionForEval: parseExpressionForEval,
                evalCache: evalCache);

        var rootConstraintId =
            analysisResult.RootEnvClass is not ExpressionEnvClass.ConstrainedEnv constrained
            ?
            null
            :
            EnvConstraintId.Create(
                constrained,
                environment,
                skipUnavailableItems: true);

        var otherExprAnalysis =
            analysisResult.UsagesCompleteForRecursion
            .Select(exprInRecursion => new ExpressionUsageAnalysis(exprInRecursion.expr, exprInRecursion.expandedConstraint))
            .ToImmutableList();

        var allExprReported =
            (IReadOnlyList<ExpressionUsageAnalysis>)
            [new ExpressionUsageAnalysis(expression, rootConstraintId),
            ..otherExprAnalysis];

        return allExprReported.ToImmutableArray();
    }

    public static IReadOnlyDictionary<ExpressionUsageAnalysis, ExpressionUsageProfile> UsageProfileDictionaryFromListOfUsages(
        IReadOnlyCollection<ExpressionUsageAnalysis> usages)
    {
        var counts = new Dictionary<ExpressionUsageAnalysis, int>();

        foreach (var usage in usages)
            counts[usage] = counts.GetValueOrDefault(usage, 0) + 1;

        return
            counts
            .ToDictionary(
                keySelector: p => p.Key,
                elementSelector: p => new ExpressionUsageProfile(UsageCount: p.Value));
    }

    static public IReadOnlyDictionary<T, ExpressionUsageProfile> AggregateExpressionUsageProfiles<T>(
        IReadOnlyCollection<IReadOnlyDictionary<T, ExpressionUsageProfile>> dictionaries)
        where T : notnull
        =>
        dictionaries
        .SelectMany(dict => dict.Keys)
        .Distinct()
        .ToImmutableDictionary(
            keySelector:
            expr => expr,
            elementSelector:
            expr =>
            {
                var profiles =
                    dictionaries.SelectMany(dict =>
                    {
                        if (!dict.TryGetValue(expr, out var result))
                            return [];

                        return ImmutableList.Create(result);
                    })
                    .ToImmutableArray();

                return
                AggregateExpressionUsageProfiles(profiles);
            });

    static public ExpressionUsageProfile AggregateExpressionUsageProfiles(
        IReadOnlyList<ExpressionUsageProfile> profiles)
    {
        return new ExpressionUsageProfile(
            UsageCount: profiles.Sum(p => p.UsageCount));
    }
}
