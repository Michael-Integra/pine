using Pine.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Pine;

public class PineVM
{
    public delegate Result<string, PineValue> EvalExprDelegate(Expression expression, PineValue environment);

    public record FunctionApplicationCacheEntryKey(PineValue function, PineValue argument);

    readonly ConcurrentDictionary<FunctionApplicationCacheEntryKey, PineValue> functionApplicationCache = new();

    public IImmutableDictionary<FunctionApplicationCacheEntryKey, PineValue> CopyFunctionApplicationCache() =>
        functionApplicationCache.ToImmutableDictionary();

    public long FunctionApplicationCacheSize => functionApplicationCache.Count;

    public long FunctionApplicationCacheLookupCount { private set; get; }

    public long FunctionApplicationMaxEnvSize { private set; get; }

    readonly IReadOnlyDictionary<PineValue, Expression.DelegatingExpression>? decodeExpressionOverrides;

    readonly EvalExprDelegate evalExprDelegate;

    public PineVM(
        IReadOnlyDictionary<PineValue, Func<PineValue, Result<string, PineValue>>>? decodeExpressionOverrides = null,
        Func<EvalExprDelegate, EvalExprDelegate>? overrideEvaluateExpression = null)
    {
        evalExprDelegate =
            overrideEvaluateExpression?.Invoke(EvaluateExpressionDefault) ?? EvaluateExpressionDefault;

        this.decodeExpressionOverrides =
            (decodeExpressionOverrides ?? PineVMConfiguration.DecodeExpressionOverrides)
            ?.ToImmutableDictionary(
                keySelector: encodedExprAndDelegate => encodedExprAndDelegate.Key,
                elementSelector: encodedExprAndDelegate => new Expression.DelegatingExpression(encodedExprAndDelegate.Value));
    }

    public Result<string, PineValue> EvaluateExpression(
        Expression expression,
        PineValue environment) => evalExprDelegate(expression, environment);

    public Result<string, PineValue> EvaluateExpressionDefault(
        Expression expression,
        PineValue environment)
    {
        if (expression is Expression.LiteralExpression literalExpression)
            return Result<string, PineValue>.ok(literalExpression.Value);

        if (expression is Expression.ListExpression listExpression)
        {
            return
                ResultListMapCombine(
                    listExpression.List,
                    elem => evalExprDelegate(elem, environment))
                .MapError(err => "Failed to evaluate list element: " + err)
                .Map(PineValue.List);
        }

        if (expression is Expression.DecodeAndEvaluateExpression applicationExpression)
        {
            return
                EvaluateDecodeAndEvaluateExpression(applicationExpression, environment)
                .MapError(err => "Failed to evaluate decode and evaluate: " + err);
        }

        if (expression is Expression.KernelApplicationExpression kernelApplicationExpression)
        {
            return
                EvaluateKernelApplicationExpression(environment, kernelApplicationExpression)
                .MapError(err => "Failed to evaluate kernel function application: " + err);
        }

        if (expression is Expression.ConditionalExpression conditionalExpression)
        {
            return EvaluateConditionalExpression(environment, conditionalExpression);
        }

        if (expression is Expression.EnvironmentExpression)
        {
            return Result<string, PineValue>.ok(environment);
        }

        if (expression is Expression.StringTagExpression stringTagExpression)
        {
            return EvaluateExpression(stringTagExpression.tagged, environment);
        }

        if (expression is Expression.DelegatingExpression delegatingExpr)
        {
            return delegatingExpr.Delegate.Invoke(environment);
        }

        throw new NotImplementedException("Unexpected shape of expression: " + expression.GetType().FullName);
    }

    public Result<string, PineValue> EvaluateDecodeAndEvaluateExpression(
        Expression.DecodeAndEvaluateExpression decodeAndEvaluate,
        PineValue environment) =>
        EvaluateExpression(decodeAndEvaluate.expression, environment)
        .MapError(error => "Failed to evaluate function: " + error)
        .AndThen(functionValue => DecodeExpressionFromValue(functionValue)
        .MapError(error => "Failed to decode expression from function value: " + error)
        .AndThen(functionExpression => EvaluateExpression(decodeAndEvaluate.environment, environment)
        .MapError(error => "Failed to evaluate argument: " + error)
        .AndThen(argumentValue =>
        {
            ++FunctionApplicationCacheLookupCount;

            var cacheKey = new FunctionApplicationCacheEntryKey(function: functionValue, argument: argumentValue);

            if (functionApplicationCache.TryGetValue(cacheKey, out var cachedResult))
                return Result<string, PineValue>.ok(cachedResult);

            if (argumentValue is PineValue.ListValue list)
            {
                FunctionApplicationMaxEnvSize =
                FunctionApplicationMaxEnvSize < list.Elements.Count ? list.Elements.Count : FunctionApplicationMaxEnvSize;
            }

            var evalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            var evalResult = EvaluateExpression(environment: argumentValue, expression: functionExpression);

            if (4 <= evalStopwatch.ElapsedMilliseconds && evalResult is Result<string, PineValue>.Ok evalOk)
            {
                functionApplicationCache[cacheKey] = evalOk.Value;
            }

            return evalResult;
        })));

    public Result<string, PineValue> EvaluateKernelApplicationExpression(
        PineValue environment,
        Expression.KernelApplicationExpression application) =>
        EvaluateExpression(application.argument, environment)
        .MapError(error => "Failed to evaluate argument: " + error)
        .Map(argument => application.function(argument).WithDefault(PineValue.EmptyList));

    public Result<string, PineValue> EvaluateConditionalExpression(
        PineValue environment,
        Expression.ConditionalExpression conditional) =>
        EvaluateExpression(conditional.condition, environment)
        .MapError(error => "Failed to evaluate condition: " + error)
        .AndThen(conditionValue =>
        EvaluateExpression(conditionValue == TrueValue ? conditional.ifTrue : conditional.ifFalse, environment));

    static readonly IReadOnlyDictionary<string, Func<PineValue, Result<string, PineValue>>> NamedKernelFunctions =
        ImmutableDictionary<string, Func<PineValue, Result<string, PineValue>>>.Empty
        .SetItem(nameof(KernelFunction.equal), KernelFunction.equal)
        .SetItem(nameof(KernelFunction.logical_not), KernelFunction.logical_not)
        .SetItem(nameof(KernelFunction.logical_and), KernelFunction.logical_and)
        .SetItem(nameof(KernelFunction.logical_or), KernelFunction.logical_or)
        .SetItem(nameof(KernelFunction.length), KernelFunction.length)
        .SetItem(nameof(KernelFunction.skip), KernelFunction.skip)
        .SetItem(nameof(KernelFunction.take), KernelFunction.take)
        .SetItem(nameof(KernelFunction.reverse), KernelFunction.reverse)
        .SetItem(nameof(KernelFunction.concat), KernelFunction.concat)
        .SetItem(nameof(KernelFunction.list_head), KernelFunction.list_head)
        .SetItem(nameof(KernelFunction.neg_int), KernelFunction.neg_int)
        .SetItem(nameof(KernelFunction.add_int), KernelFunction.add_int)
        .SetItem(nameof(KernelFunction.sub_int), KernelFunction.sub_int)
        .SetItem(nameof(KernelFunction.mul_int), KernelFunction.mul_int)
        .SetItem(nameof(KernelFunction.div_int), KernelFunction.div_int)
        .SetItem(nameof(KernelFunction.is_sorted_ascending_int), value => Result<string, PineValue>.ok(KernelFunction.is_sorted_ascending_int(value)));

    static public PineValue ValueFromBool(bool b) => b ? TrueValue : FalseValue;

    static readonly public PineValue TrueValue = PineValue.Blob(new byte[] { 4 });

    static readonly public PineValue FalseValue = PineValue.Blob(new byte[] { 2 });

    static public Result<string, bool> DecodeBoolFromValue(PineValue value) =>
        value == TrueValue
        ?
        Result<string, bool>.ok(true)
        :
        (value == FalseValue ? Result<string, bool>.ok(false)
        :
        Result<string, bool>.err("Value is neither True nor False"));

    static public Result<string, PineValue> EncodeExpressionAsValue(Expression expression) =>
        expression switch
        {
            Expression.LiteralExpression literal =>
            Result<string, PineValue>.ok(EncodeChoiceTypeVariantAsPineValue("Literal", literal.Value)),

            Expression.EnvironmentExpression =>
            Result<string, PineValue>.ok(EncodeChoiceTypeVariantAsPineValue("Environment", PineValue.EmptyList)),

            Expression.ListExpression list =>
            list.List.Select(EncodeExpressionAsValue)
            .ListCombine()
            .Map(listElements => EncodeChoiceTypeVariantAsPineValue("List", PineValue.List(listElements))),

            Expression.ConditionalExpression conditional =>
            EncodeConditionalExpressionAsValue(conditional),

            Expression.DecodeAndEvaluateExpression decodeAndEval =>
            EncodeDecodeAndEvaluateExpression(decodeAndEval),

            Expression.KernelApplicationExpression kernelAppl =>
            EncodeKernelApplicationExpression(kernelAppl),

            Expression.StringTagExpression stringTag =>
            EncodeExpressionAsValue(stringTag.tagged)
            .Map(encodedTagged => EncodeChoiceTypeVariantAsPineValue(
                "StringTag",
                PineValue.List(new[] { Composition.ComponentFromString(stringTag.tag), encodedTagged }))),

            _ =>
            Result<string, PineValue>.err("Unsupported expression type: " + expression.GetType().FullName)
        };

    public Result<string, Expression> DecodeExpressionFromValue(PineValue value)
    {
        if (decodeExpressionOverrides?.TryGetValue(value, out var delegatingExpression) ?? false)
            return Result<string, Expression>.ok(delegatingExpression);

        return
            DecodeChoiceFromPineValue(
                generalDecoder: DecodeExpressionFromValue,
                ExpressionDecoders,
                value);
    }

    static public Result<string, Expression> DecodeExpressionFromValueDefault(PineValue value) =>
        DecodeChoiceFromPineValue(
            generalDecoder: DecodeExpressionFromValueDefault,
            ExpressionDecoders,
            value);

    static readonly IImmutableDictionary<string, Func<Func<PineValue, Result<string, Expression>>, PineValue, Result<string, Expression>>> ExpressionDecoders =
        ImmutableDictionary<string, Func<Func<PineValue, Result<string, Expression>>, PineValue, Result<string, Expression>>>.Empty
        .SetItem(
            "Literal",
            (generalDecoder, literal) => Result<string, Expression>.ok(new Expression.LiteralExpression(literal)))
        .SetItem(
            "List",
            (generalDecoder, listValue) =>
            DecodePineListValue(listValue)
            .AndThen(list => ResultListMapCombine(list, generalDecoder))
            .Map(expressionList => (Expression)new Expression.ListExpression(expressionList.ToImmutableArray())))
        .SetItem(
            "DecodeAndEvaluate",
            (generalDecoder, value) => DecodeDecodeAndEvaluateExpression(generalDecoder, value)
            .Map(application => (Expression)application))
        .SetItem(
            "KernelApplication",
            (generalDecoder, value) => DecodeKernelApplicationExpression(generalDecoder, value)
            .Map(application => (Expression)application))
        .SetItem(
            "Conditional",
            (generalDecoder, value) => DecodeConditionalExpression(generalDecoder, value)
            .Map(conditional => (Expression)conditional))
        .SetItem(
            "Environment",
            (_, _) => Result<string, Expression>.ok(new Expression.EnvironmentExpression()))
        .SetItem(
            "StringTag",
            (generalDecoder, value) => DecodeStringTagExpression(generalDecoder, value)
            .Map(stringTag => (Expression)stringTag));

    static public Result<string, PineValue> EncodeDecodeAndEvaluateExpression(Expression.DecodeAndEvaluateExpression decodeAndEval) =>
        EncodeExpressionAsValue(decodeAndEval.expression)
        .AndThen(encodedExpression =>
        EncodeExpressionAsValue(decodeAndEval.environment)
        .Map(encodedEnvironment =>
        EncodeChoiceTypeVariantAsPineValue("DecodeAndEvaluate",
            EncodeRecordToPineValue(
                (nameof(Expression.DecodeAndEvaluateExpression.expression), encodedExpression),
                (nameof(Expression.DecodeAndEvaluateExpression.environment), encodedEnvironment)))));

    static public Result<string, Expression.DecodeAndEvaluateExpression> DecodeDecodeAndEvaluateExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodeRecord2FromPineValue(
            value,
            ("expression", generalDecoder),
            ("environment", generalDecoder),
            (expression, environment) => new Expression.DecodeAndEvaluateExpression(expression: expression, environment: environment));

    static public Result<string, PineValue> EncodeKernelApplicationExpression(Expression.KernelApplicationExpression kernelApplicationExpression) =>
        EncodeExpressionAsValue(kernelApplicationExpression.argument)
        .Map(encodedArgument =>
        EncodeChoiceTypeVariantAsPineValue("KernelApplication",
            EncodeRecordToPineValue(
                (nameof(Expression.KernelApplicationExpression.functionName), Composition.ComponentFromString(kernelApplicationExpression.functionName)),
                (nameof(Expression.KernelApplicationExpression.argument), encodedArgument))));

    static public Result<string, Expression.KernelApplicationExpression> DecodeKernelApplicationExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodeRecord2FromPineValue(
            value,
            (nameof(Expression.KernelApplicationExpression.functionName), Composition.StringFromComponent),
            (nameof(Expression.KernelApplicationExpression.argument), generalDecoder),
            (functionName, argument) => (functionName, argument))
        .AndThen(functionNameAndArgument =>
        DecodeKernelApplicationExpression(functionNameAndArgument.functionName, functionNameAndArgument.argument));

    static public Expression.KernelApplicationExpression DecodeKernelApplicationExpressionThrowOnUnknownName(
        string functionName,
        Expression argument) =>
        DecodeKernelApplicationExpression(functionName, argument)
        .Extract(err => throw new Exception(err));

    static public Result<string, Expression.KernelApplicationExpression> DecodeKernelApplicationExpression(
        string functionName,
        Expression argument)
    {
        if (!NamedKernelFunctions.TryGetValue(functionName, out var kernelFunction))
        {
            return Result<string, Expression.KernelApplicationExpression>.err(
                "Did not find kernel function '" + functionName + "'");
        }

        return Result<string, Expression.KernelApplicationExpression>.ok(
            new Expression.KernelApplicationExpression(
                functionName: functionName,
                function: kernelFunction,
                argument: argument));
    }

    static public Result<string, PineValue> EncodeConditionalExpressionAsValue(Expression.ConditionalExpression conditionalExpression) =>
        EncodeExpressionAsValue(conditionalExpression.condition)
        .AndThen(encodedCondition =>
        EncodeExpressionAsValue(conditionalExpression.ifTrue)
        .AndThen(encodedIfTrue =>
        EncodeExpressionAsValue(conditionalExpression.ifFalse)
        .Map(encodedIfFalse =>
        EncodeChoiceTypeVariantAsPineValue("Conditional",
            EncodeRecordToPineValue(
                (nameof(Expression.ConditionalExpression.condition), encodedCondition),
                (nameof(Expression.ConditionalExpression.ifTrue), encodedIfTrue),
                (nameof(Expression.ConditionalExpression.ifFalse), encodedIfFalse))))));

    static public Result<string, Expression.ConditionalExpression> DecodeConditionalExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodeRecord3FromPineValue(
            value,
            (nameof(Expression.ConditionalExpression.condition), generalDecoder),
            (nameof(Expression.ConditionalExpression.ifTrue), generalDecoder),
            (nameof(Expression.ConditionalExpression.ifFalse), generalDecoder),
            (condition, ifTrue, ifFalse) => new Expression.ConditionalExpression(condition: condition, ifTrue: ifTrue, ifFalse: ifFalse));

    static public Result<string, Expression.StringTagExpression> DecodeStringTagExpression(
        Func<PineValue, Result<string, Expression>> generalDecoder,
        PineValue value) =>
        DecodePineListValue(value)
        .AndThen(DecodeListWithExactlyTwoElements)
        .AndThen(tagValueAndTaggedValue =>
        Composition.StringFromComponent(tagValueAndTaggedValue.Item1).MapError(err => "Failed to decode tag: " + err)
        .AndThen(tag => generalDecoder(tagValueAndTaggedValue.Item2).MapError(err => "Failed to decoded tagged expression: " + err)
        .Map(tagged => new Expression.StringTagExpression(tag: tag, tagged: tagged))));

    static public Result<ErrT, IReadOnlyList<MappedOkT>> ResultListMapCombine<ErrT, OkT, MappedOkT>(
        IReadOnlyList<OkT> list,
        Func<OkT, Result<ErrT, MappedOkT>> mapElement) =>
        list.Select(mapElement).ListCombine();

    static public Result<string, Record> DecodeRecord3FromPineValue<FieldA, FieldB, FieldC, Record>(
        PineValue value,
        (string name, Func<PineValue, Result<string, FieldA>> decode) fieldA,
        (string name, Func<PineValue, Result<string, FieldB>> decode) fieldB,
        (string name, Func<PineValue, Result<string, FieldC>> decode) fieldC,
        Func<FieldA, FieldB, FieldC, Record> compose) =>
        DecodeRecordFromPineValue(value)
        .AndThen(record =>
        {
            if (!record.TryGetValue(fieldA.name, out var fieldAValue))
                return Result<string, Record>.err("Did not find field " + fieldA.name);

            if (!record.TryGetValue(fieldB.name, out var fieldBValue))
                return Result<string, Record>.err("Did not find field " + fieldB.name);

            if (!record.TryGetValue(fieldC.name, out var fieldCValue))
                return Result<string, Record>.err("Did not find field " + fieldC.name);

            return
            fieldA.decode(fieldAValue)
            .AndThen(fieldADecoded =>
            fieldB.decode(fieldBValue)
            .AndThen(fieldBDecoded =>
            fieldC.decode(fieldCValue)
            .Map(fieldCDecoded => compose(fieldADecoded, fieldBDecoded, fieldCDecoded))));
        });

    static public Result<string, Record> DecodeRecord2FromPineValue<FieldA, FieldB, Record>(
        PineValue value,
        (string name, Func<PineValue, Result<string, FieldA>> decode) fieldA,
        (string name, Func<PineValue, Result<string, FieldB>> decode) fieldB,
        Func<FieldA, FieldB, Record> compose) =>
        DecodeRecordFromPineValue(value)
        .AndThen(record =>
        {
            if (!record.TryGetValue(fieldA.name, out var fieldAValue))
                return Result<string, Record>.err("Did not find field " + fieldA.name);

            if (!record.TryGetValue(fieldB.name, out var fieldBValue))
                return Result<string, Record>.err("Did not find field " + fieldB.name);

            return
            fieldA.decode(fieldAValue)
            .AndThen(fieldADecoded =>
            fieldB.decode(fieldBValue)
            .Map(fieldBDecoded => compose(fieldADecoded, fieldBDecoded)));
        });

    static public PineValue EncodeRecordToPineValue(params (string fieldName, PineValue fieldValue)[] fields) =>
        PineValue.List(
            fields.Select(field => PineValue.List(
                new[]
                {
                    Composition.ComponentFromString(field.fieldName),
                    field.fieldValue
                })).ToArray());


    static public Result<string, ImmutableDictionary<string, PineValue>> DecodeRecordFromPineValue(PineValue value) =>
        DecodePineListValue(value)
        .AndThen(list =>
        list
        .Aggregate(
            seed: Result<string, ImmutableDictionary<string, PineValue>>.ok(ImmutableDictionary<string, PineValue>.Empty),
            func: (aggregate, listElement) => aggregate.AndThen(recordFields =>
            DecodePineListValue(listElement)
            .AndThen(DecodeListWithExactlyTwoElements)
            .AndThen(fieldNameValueAndValue =>
            Composition.StringFromComponent(fieldNameValueAndValue.Item1)
            .Map(fieldName => recordFields.SetItem(fieldName, fieldNameValueAndValue.Item2))))));

    static public PineValue EncodeChoiceTypeVariantAsPineValue(string tagName, PineValue tagArguments) =>
        PineValue.List(
            new[]
            {
                Composition.ComponentFromString(tagName),
                tagArguments,
            });

    static public Result<string, T> DecodeChoiceFromPineValue<T>(
        Func<PineValue, Result<string, T>> generalDecoder,
        IImmutableDictionary<string, Func<Func<PineValue, Result<string, T>>, PineValue, Result<string, T>>> variants,
        PineValue value) =>
        DecodePineListValue(value)
        .AndThen(DecodeListWithExactlyTwoElements)
        .AndThen(tagNameValueAndValue =>
        Composition.StringFromComponent(tagNameValueAndValue.Item1)
        .MapError(error => "Failed to decode union tag name: " + error)
        .AndThen(tagName =>
        {
            if (!variants.TryGetValue(tagName, out var variant))
                return Result<string, T>.err("Unexpected tag name: " + tagName);

            return variant(generalDecoder, tagNameValueAndValue.Item2)!;
        }));

    static public Result<string, IImmutableList<PineValue>> DecodePineListValue(PineValue value)
    {
        if (value is not PineValue.ListValue listValue)
            return Result<string, IImmutableList<PineValue>>.err("Not a list");

        return Result<string, IImmutableList<PineValue>>.ok(
            listValue.Elements as IImmutableList<PineValue> ?? listValue.Elements.ToImmutableList());
    }

    static public Result<string, (T, T)> DecodeListWithExactlyTwoElements<T>(IImmutableList<T> list)
    {
        if (list.Count != 2)
            return Result<string, (T, T)>.err("Unexpected number of elements in list: Not 2 but " + list.Count);

        return Result<string, (T, T)>.ok((list[0], list[1]));
    }


    static public class KernelFunction
    {
        static public Result<string, PineValue> equal(PineValue value) =>
            Result<string, PineValue>.ok(
                ValueFromBool(
                    value switch
                    {
                        PineValue.ListValue list =>
                        list.Elements.Count < 1 ?
                        true
                        :
                        list.Elements.All(e => e.Equals(list.Elements[0])),

                        PineValue.BlobValue blob =>
                        blob.Bytes.Length < 1 ? true :
                        blob.Bytes.ToArray().All(b => b == blob.Bytes.Span[0]),

                        _ => throw new NotImplementedException()
                    }
                ));

        static public Result<string, PineValue> logical_not(PineValue value) =>
            DecodeBoolFromValue(value)
            .Map(b => ValueFromBool(!b));

        static public Result<string, PineValue> logical_and(PineValue value) =>
            KernelFunctionExpectingListOfTypeBool(bools => bools.Aggregate(seed: true, func: (a, b) => a && b), value);

        static public Result<string, PineValue> logical_or(PineValue value) =>
            KernelFunctionExpectingListOfTypeBool(bools => bools.Aggregate(seed: false, func: (a, b) => a || b), value);

        static public Result<string, PineValue> length(PineValue value) =>
            Result<string, PineValue>.ok(
                Composition.ComponentFromSignedInteger(
                    value switch
                    {
                        PineValue.BlobValue blobComponent => blobComponent.Bytes.Length,
                        PineValue.ListValue listComponent => listComponent.Elements.Count,
                        _ => throw new NotImplementedException()
                    }));

        static public Result<string, PineValue> skip(PineValue value) =>
            KernelFunctionExpectingExactlyTwoArguments(
                Composition.SignedIntegerFromComponent,
                Result<string, PineValue>.ok,
                compose: (count, list) =>
                Result<string, PineValue>.ok(
                    list switch
                    {
                        PineValue.BlobValue blobComponent => PineValue.Blob(blobComponent.Bytes[(int)count..]),
                        PineValue.ListValue listComponent => PineValue.List(listComponent.Elements.Skip((int)count).ToImmutableList()),
                        _ => throw new NotImplementedException()
                    }))
            (value);

        static public Result<string, PineValue> take(PineValue value) =>
            KernelFunctionExpectingExactlyTwoArguments(
                Composition.SignedIntegerFromComponent,
                Result<string, PineValue>.ok,
                compose: (count, list) =>
                Result<string, PineValue>.ok(
                    list switch
                    {
                        PineValue.BlobValue blobComponent => PineValue.Blob(blobComponent.Bytes[..(int)count]),
                        PineValue.ListValue listComponent => PineValue.List(listComponent.Elements.Take((int)count).ToImmutableList()),
                        _ => throw new NotImplementedException()
                    }))
            (value);

        static public Result<string, PineValue> reverse(PineValue value) =>
            Result<string, PineValue>.ok(
                value switch
                {
                    PineValue.BlobValue blobComponent => PineValue.Blob(blobComponent.Bytes.ToArray().Reverse().ToArray()),
                    PineValue.ListValue listComponent => PineValue.List(listComponent.Elements.Reverse().ToImmutableList()),
                    _ => throw new NotImplementedException()
                });

        static public Result<string, PineValue> concat(PineValue value) =>
            DecodePineListValue(value)
            .Map(list =>
            list.Aggregate(
                seed: PineValue.EmptyList,
                func: (aggregate, elem) =>
                elem switch
                {
                    PineValue.BlobValue elemBlobValue =>
                    aggregate switch
                    {
                        PineValue.BlobValue aggregateBlobValue =>
                        PineValue.Blob(CommonConversion.Concat(
                            aggregateBlobValue.Bytes.Span, elemBlobValue.Bytes.Span)),
                        _ => elemBlobValue
                    },
                    PineValue.ListValue elemListValue =>
                    aggregate switch
                    {
                        PineValue.ListValue aggregateListValue =>
                        PineValue.List(aggregateListValue.Elements.Concat(elemListValue.Elements).ToImmutableList()),
                        _ => elemListValue
                    },
                    _ => throw new NotImplementedException()
                }));

        static public Result<string, PineValue> list_head(PineValue value) =>
            DecodePineListValue(value)
            .Map(list => list.Count < 1 ? PineValue.EmptyList : list[0]);

        static public Result<string, PineValue> neg_int(PineValue value) =>
            Composition.SignedIntegerFromComponent(value)
            .Map(i => Composition.ComponentFromSignedInteger(-i));

        static public Result<string, PineValue> add_int(PineValue value) =>
            KernelFunctionExpectingListOfBigIntWithAtLeastOneAndProducingBigInt(
                (firstInt, otherInts) => Result<string, BigInteger>.ok(
                    otherInts.Aggregate(seed: firstInt, func: (aggregate, next) => aggregate + next)),
                value);

        static public Result<string, PineValue> sub_int(PineValue value) =>
            KernelFunctionExpectingListOfBigIntWithAtLeastOneAndProducingBigInt(
                (firstInt, otherInts) => Result<string, BigInteger>.ok(
                    otherInts.Aggregate(seed: firstInt, func: (aggregate, next) => aggregate - next)),
                value);

        static public Result<string, PineValue> mul_int(PineValue value) =>
            KernelFunctionExpectingListOfBigIntWithAtLeastOneAndProducingBigInt(
                (firstInt, otherInts) => Result<string, BigInteger>.ok(
                    otherInts.Aggregate(seed: firstInt, func: (aggregate, next) => aggregate * next)),
                value);

        static public Result<string, PineValue> div_int(PineValue value) =>
            KernelFunctionExpectingListOfBigIntWithAtLeastOneAndProducingBigInt(
                (firstInt, otherInts) =>
                otherInts.Contains(0) ?
                Result<string, BigInteger>.err("Division by zero")
                :
                Result<string, BigInteger>.ok(otherInts.Aggregate(seed: firstInt, func: (aggregate, next) => aggregate / next)),
                value);

        static public PineValue is_sorted_ascending_int(PineValue value) =>
            ValueFromBool(sort_int(value) == value);

        static public PineValue sort_int(PineValue value) =>
            value switch
            {
                PineValue.ListValue list =>
                new PineValue.ListValue(
                    list.Elements
                    .Select(sort_int)
                    .Order(valueComparerInt)
                    .ToImmutableList()),

                _ => value,
            };

        static readonly BlobValueIntComparer valueComparerInt = new();

        class BlobValueIntComparer : IComparer<PineValue>
        {
            public int Compare(PineValue? x, PineValue? y) =>
                (x, y) switch
                {
                    (PineValue.BlobValue blobX, PineValue.BlobValue blobY) =>
                    (Composition.SignedIntegerFromBlobValue(blobX.Bytes.Span),
                    Composition.SignedIntegerFromBlobValue(blobY.Bytes.Span)) switch
                    {
                        (Result<string, BigInteger>.Ok intX, Result<string, BigInteger>.Ok intY) =>
                        BigInteger.Compare(intX.Value, intY.Value),

                        (Result<string, BigInteger>.Ok _, _) => -1,
                        (_, Result<string, BigInteger>.Ok _) => 1,
                        _ => 0
                    },

                    (PineValue.ListValue listX, PineValue.ListValue listY) =>
                    listX.Elements.Count - listY.Elements.Count,

                    (PineValue.ListValue _, _) => -1,

                    (_, PineValue.ListValue _) => 1,

                    _ => 0
                };
        }

        static Result<string, PineValue> KernelFunctionExpectingListOfBigIntWithAtLeastOneAndProducingBigInt(
            Func<BigInteger, IReadOnlyList<BigInteger>, Result<string, BigInteger>> aggregate,
            PineValue value) =>
            KernelFunctionExpectingListOfBigInt(
                aggregate:
                listOfIntegers =>
                (listOfIntegers.Count < 1
                ?
                Result<string, BigInteger>.err("List is empty. Expected at least one element")
                :
                aggregate(listOfIntegers[0], listOfIntegers.Skip(1).ToImmutableArray()))
                .Map(Composition.ComponentFromSignedInteger),
                value);

        static Result<string, PineValue> KernelFunctionExpectingListOfBigInt(
            Func<IReadOnlyList<BigInteger>, Result<string, PineValue>> aggregate,
            PineValue value) =>
            DecodePineListValue(value)
            .AndThen(list => ResultListMapCombine(list, Composition.SignedIntegerFromComponent))
            .AndThen(ints => aggregate(ints));

        static Func<PineValue, Result<string, PineValue>> KernelFunctionExpectingExactlyTwoArguments<ArgA, ArgB>(
            Func<PineValue, Result<string, ArgA>> decodeArgA,
            Func<PineValue, Result<string, ArgB>> decodeArgB,
            Func<ArgA, ArgB, Result<string, PineValue>> compose) =>
            value => DecodePineListValue(value)
            .AndThen(DecodeListWithExactlyTwoElements)
            .AndThen(argsValues =>
            decodeArgA(argsValues.Item1)
            .AndThen(argA =>
            decodeArgB(argsValues.Item2)
            .AndThen(argB => compose(argA, argB))));

        static Result<string, PineValue> KernelFunctionExpectingListOfTypeBool(
            Func<IReadOnlyList<bool>, bool> compose,
            PineValue value) =>
            DecodePineListValue(value)
            .AndThen(list => ResultListMapCombine(list, DecodeBoolFromValue))
            .Map(compose)
            .Map(ValueFromBool);
    }

    [JsonConverter(typeof(JsonConverterForChoiceType))]
    public abstract record Expression
    {
        public record LiteralExpression(
            PineValue Value)
            : Expression;

        public record ListExpression(
            ImmutableArray<Expression> List)
            : Expression
        {
            public virtual bool Equals(ListExpression? other)
            {
                if (other is not ListExpression notNull)
                    return false;

                return
                    ReferenceEquals(this, notNull) ||
                    (List.Length == notNull.List.Length &&
                    List.SequenceEqual(notNull.List));
            }

            public override int GetHashCode()
            {
                var hashCode = new HashCode();

                foreach (var item in List)
                {
                    hashCode.Add(item.GetHashCode());
                }

                return hashCode.ToHashCode();
            }
        }

        public record DecodeAndEvaluateExpression(
            Expression expression,
            Expression environment)
            : Expression;

        public record KernelApplicationExpression(
            string functionName,
            Expression argument,

            [property: JsonIgnore]
            Func<PineValue, Result<string, PineValue>> function)
            : Expression
        {
            public virtual bool Equals(KernelApplicationExpression? other)
            {
                if (other is not KernelApplicationExpression notNull)
                    return false;

                return
                    notNull.functionName == functionName &&
                    (ReferenceEquals(notNull.argument, argument) || notNull.argument.Equals(argument));
            }

            public override int GetHashCode()
            {
                var hash = new HashCode();

                hash.Add(functionName);
                hash.Add(argument);

                return hash.ToHashCode();
            }
        }

        public record ConditionalExpression(
            Expression condition,
            Expression ifTrue,
            Expression ifFalse)
            : Expression;

        public record EnvironmentExpression() : Expression;

        public record StringTagExpression(
            string tag,
            Expression tagged)
            : Expression;


        public record DelegatingExpression(Func<PineValue, Result<string, PineValue>> Delegate)
            : Expression;
    }
}
