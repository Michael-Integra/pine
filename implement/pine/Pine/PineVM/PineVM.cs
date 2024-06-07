using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Pine.PineVM;

public interface IPineVM
{
    Result<string, PineValue> EvaluateExpression(
        Expression expression,
        PineValue environment);
}

public class PineVM : IPineVM
{
    public long EvaluateExpressionCount { private set; get; }

    public long FunctionApplicationMaxEnvSize { private set; get; }

    private readonly ParseExprDelegate parseExpressionDelegate;

    private IDictionary<(Expression, PineValue), PineValue>? EvalCache { init; get; }

    private Action<Expression, PineValue, TimeSpan, PineValue>? reportFunctionApplication;

    public static PineVM Construct(
        IReadOnlyDictionary<PineValue, Func<EvalExprDelegate, PineValue, Result<string, PineValue>>>? parseExpressionOverrides = null,
        IDictionary<(Expression, PineValue), PineValue>? evalCache = null)
    {
        var parseExpressionOverridesDict =
            parseExpressionOverrides
            ?.ToFrozenDictionary(
                keySelector: encodedExprAndDelegate => encodedExprAndDelegate.Key,
                elementSelector: encodedExprAndDelegate => new Expression.DelegatingExpression(encodedExprAndDelegate.Value));

        return new PineVM(
            overrideParseExpression:
            parseExpressionOverridesDict switch
            {
                null =>
                originalHandler => originalHandler,

                not null =>
                _ => value => ExpressionEncoding.ParseExpressionFromValue(value, parseExpressionOverridesDict)
            },
            evalCache: evalCache);
    }

    public PineVM(
        OverrideParseExprDelegate? overrideParseExpression = null,
        IDictionary<(Expression, PineValue), PineValue>? evalCache = null,
        Action<Expression, PineValue, TimeSpan, PineValue>? reportFunctionApplication = null)
    {
        parseExpressionDelegate =
            overrideParseExpression
            ?.Invoke(ExpressionEncoding.ParseExpressionFromValueDefault) ??
            ExpressionEncoding.ParseExpressionFromValueDefault;

        EvalCache = evalCache;

        this.reportFunctionApplication = reportFunctionApplication;
    }

    public Result<string, PineValue> EvaluateExpression(
        Expression expression,
        PineValue environment) => EvaluateExpressionDefault(expression, environment);

    public record StackFrameInstructions(
        IReadOnlyList<StackInstruction> Expressions)
    {
        public virtual bool Equals(StackFrameInstructions? other)
        {
            if (other is not { } notNull)
                return false;

            return
                ReferenceEquals(this, notNull) ||
                Expressions.Count == notNull.Expressions.Count &&
                Expressions.SequenceEqual(notNull.Expressions);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();

            foreach (var item in Expressions)
            {
                hashCode.Add(item.GetHashCode());
            }

            return hashCode.ToHashCode();
        }
    }

    record StackFrame(
        Expression Expression,
        StackFrameInstructions Instructions,
        PineValue EnvironmentValue,
        Memory<PineValue> InstructionsResultValues,
        long BeginTimestamp)
    {
        public int InstructionPointer { get; set; } = 0;
    }

    readonly Dictionary<Expression, StackFrameInstructions> stackFrameDict = new();

    StackFrame StackFrameFromExpression(
        Expression expression,
        PineValue environment)
    {
        var instructions = InstructionsFromExpression(expression);

        return new StackFrame(
            expression,
            instructions,
            EnvironmentValue: environment,
            new PineValue[instructions.Expressions.Count],
            BeginTimestamp: System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public StackFrameInstructions InstructionsFromExpression(Expression rootExpression)
    {
        if (stackFrameDict.TryGetValue(rootExpression, out var cachedInstructions))
        {
            return cachedInstructions;
        }

        var instructions = InstructionsFromExpressionLessCache(rootExpression);

        stackFrameDict[rootExpression] = instructions;

        return instructions;
    }

    public static StackFrameInstructions InstructionsFromExpressionLessCache(Expression rootExpression)
    {
        var separatedExprs = InstructionsFromExpressionCore(rootExpression);

        if (separatedExprs.Count is 0)
        {
            return
                new StackFrameInstructions(
                    Expressions:
                    [StackInstruction.Eval(rootExpression), StackInstruction.Return]);
        }

        IReadOnlyList<StackInstruction> appendedForRoot =
            rootExpression switch
            {
                Expression.ConditionalExpression =>
                [],

                _ =>
                [StackInstruction.Eval(rootExpression),
                StackInstruction.Return]
            };

        var separatedExprsValuesBeforeFilter =
            separatedExprs
            .SelectMany(pair => pair.Value)
            .Concat(appendedForRoot)
            .ToImmutableArray();

        static IEnumerable<StackInstruction> InstructionsFiltered(IEnumerable<StackInstruction> instructions)
        {
            var coveredLaterExprs = new HashSet<Expression>();

            IEnumerable<StackInstruction> ExprsFilteredReverse()
            {
                foreach (var inst in instructions.Reverse())
                {
                    var instExpr =
                        inst switch
                        {
                            /*
                            StackInstruction.ReturnInstruction retInst =>
                            retInst.Returned,
                            */

                            StackInstruction.EvalInstruction evalInst =>
                            evalInst.Expression,

                            _ => null
                        };

                    if (instExpr is not null)
                    {
                        if (coveredLaterExprs.Contains(instExpr))
                            continue;

                        coveredLaterExprs.Add(instExpr);
                    }

                    yield return inst;
                }
            }

            return ExprsFilteredReverse().Reverse();
        }

        var separatedExprsValues =
            InstructionsFiltered(separatedExprsValuesBeforeFilter)
            .ToImmutableArray();

        var map = new Dictionary<StackInstruction, int>();

        for (var i = 0; i < separatedExprsValues.Length; i++)
        {
            map[separatedExprsValues[i]] = i;
        }

        StackInstruction MapInstruction(
            StackInstruction originalInst,
            int selfIndex)
        {
            if (originalInst is StackInstruction.ConditionalJumpRefInstruction conditionalJumpRef)
            {
                var ifTrueExprEntry =
                    map
                    .FirstOrDefault(c =>
                    c.Key is StackInstruction.EvalInstruction evalInst && evalInst.Expression == conditionalJumpRef.IfTrueExpr);

                if (ifTrueExprEntry.Key is null)
                {
                    throw new InvalidOperationException("Expected to find ifTrueExpr in map.");
                }

                return
                    new StackInstruction.ConditionalJumpInstruction(
                        Condition: conditionalJumpRef.Condition,
                        IfTrueOffset: ifTrueExprEntry.Value - selfIndex);
            }

            return
                StackInstruction.TransformExpressionWithOptionalReplacement(
                    findReplacement:
                    descendant =>
                    {
                        if (originalInst is StackInstruction.EvalInstruction origEval && origEval.Expression == descendant)
                        {
                            return null;
                        }

                        if (
                        map.FirstOrDefault(c => c.Key is StackInstruction.EvalInstruction evalInst && evalInst.Expression == descendant)
                        is { } pair && pair.Key is not null)
                        {
                            return new Expression.StackReferenceExpression(offset: pair.Value - selfIndex);
                        }

                        return null;
                    },
                    instruction: originalInst);
        }

        var componentExprMapped =
            separatedExprsValues
            .Select(MapInstruction)
            .ToImmutableArray();

        return
            new StackFrameInstructions(
                Expressions: [.. componentExprMapped]);
    }

    static IReadOnlyList<KeyValuePair<Expression, IReadOnlyList<StackInstruction>>>
        InstructionsFromExpressionCore(Expression rootExpression)
    {
        var allExprs =
            Expression.EnumerateSelfAndDescendants(
                rootExpression,
                /*
                 * For now, we create a new stack frame for each conditional expression.
                 * */
                skipDescendants: expr => expr is Expression.ConditionalExpression)
            .ToImmutableArray();

        var allExprsReverse =
            allExprs
            .Reverse()
            .ToImmutableArray();

        static IReadOnlyList<StackInstruction>? mappingForExpr(Expression expression)
        {
            if (expression is Expression.ParseAndEvalExpression parseAndEval)
            {
                return [StackInstruction.Eval(expression)];
            }

            /*
             * At the moment we add a new stack frame to evaluate a conditional expression.
             * 
            if (expression is Expression.ConditionalExpression conditional)
            {
                return
                [
                    new StackInstruction.ConditionalJumpRefInstruction(
                        Condition: conditional.condition,
                        IfTrueExpr:conditional.ifTrue),
                    StackInstruction.Eval(conditional.ifFalse),
                    StackInstruction.Return,
                    StackInstruction.Eval(conditional.ifTrue),
                    StackInstruction.Return
                ];
            }
            */

            return null;
        }

        var separatedExprs =
            allExprsReverse
            .Distinct()
            .SelectMany(e =>
            {
                if (mappingForExpr(e) is { } mappedExprs)
                {
                    return
                    (KeyValuePair<Expression, IReadOnlyList<StackInstruction>>[])
                    [new KeyValuePair<Expression, IReadOnlyList<StackInstruction>>(e, mappedExprs)];
                }

                return [];
            })
            .ToImmutableArray();

        return separatedExprs;
    }

    public Result<string, PineValue> EvaluateExpressionDefault(
        Expression rootExpression,
        PineValue rootEnvironment)
    {
        var stack = new Stack<StackFrame>();

        stack.Push(StackFrameFromExpression(rootExpression, rootEnvironment));

        /*
        void returnFromFrame(PineValue frameReturnValue)
        {
            stack.Pop();

            if (stack.Count is 0)
            {
                return frameReturnValue;
            }

            var previousFrame = stack.Peek();

            previousFrame.Values.Span[previousFrame.InstructionPointer] = frameReturnValue;

            previousFrame.InstructionPointer++;
        }
        */

        while (true)
        {
            var currentFrame = stack.Peek();

            if (EvalCache?.TryGetValue((currentFrame.Expression, currentFrame.EnvironmentValue), out var cachedValue) ?? false &&
                cachedValue is not null)
            {
                stack.Pop();

                if (stack.Count is 0)
                {
                    return cachedValue;
                }

                var previousFrame = stack.Peek();

                previousFrame.InstructionsResultValues.Span[previousFrame.InstructionPointer] = cachedValue;

                previousFrame.InstructionPointer++;
                continue;
            }

            if (currentFrame.Instructions.Expressions.Count <= currentFrame.InstructionPointer)
            {
                return
                    "Instruction pointer out of bounds. Missing explicit return instruction.";
            }

            ReadOnlyMemory<PineValue> stackPrevValues =
                currentFrame.InstructionsResultValues[..currentFrame.InstructionPointer];

            var currentInstruction = currentFrame.Instructions.Expressions[currentFrame.InstructionPointer];

            if (currentInstruction is StackInstruction.ReturnInstruction)
            {
                if (currentFrame.InstructionPointer is 0)
                {
                    return "Return instruction at beginning of frame";
                }

                var frameElapsedTime = System.Diagnostics.Stopwatch.GetElapsedTime(currentFrame.BeginTimestamp);

                var frameReturnValue = currentFrame.InstructionsResultValues.Span[currentFrame.InstructionPointer - 1];

                if (frameElapsedTime.TotalMilliseconds > 3 && EvalCache is not null)
                {
                    EvalCache?.TryAdd((currentFrame.Expression, currentFrame.EnvironmentValue), frameReturnValue);
                }

                reportFunctionApplication?.Invoke(
                    currentFrame.Expression,
                    currentFrame.EnvironmentValue,
                    frameElapsedTime,
                    frameReturnValue);

                stack.Pop();

                if (stack.Count is 0)
                {
                    return frameReturnValue;
                }

                var previousFrame = stack.Peek();

                previousFrame.InstructionsResultValues.Span[previousFrame.InstructionPointer] = frameReturnValue;

                previousFrame.InstructionPointer++;
                continue;
            }

            if (currentInstruction is StackInstruction.EvalInstruction evalInstr)
            {
                if (evalInstr.Expression is Expression.ParseAndEvalExpression parseAndEval)
                {
                    var evalExprValueResult =
                        EvaluateExpressionDefaultLessStack(
                            parseAndEval.expression,
                            currentFrame.EnvironmentValue,
                            stackPrevValues: stackPrevValues);

                    if (evalExprValueResult is Result<string, PineValue>.Err parseFunctionError)
                    {
                        return "Failed to evaluate expr value for parse and eval: " + parseFunctionError;
                    }

                    if (evalExprValueResult is not Result<string, PineValue>.Ok evalExprValueOk)
                    {
                        throw new NotImplementedException(
                            "Unexpected result type: " + evalExprValueResult.GetType().FullName);
                    }

                    var parseResult = parseExpressionDelegate(evalExprValueOk.Value);

                    if (parseResult is Result<string, Expression>.Err parseErr)
                    {
                        return
                            "Failed to parse expression from value: " + parseErr.Value +
                            " - expressionValue is " + DescribeValueForErrorMessage(evalExprValueOk.Value) +
                            " - environmentValue is " + DescribeValueForErrorMessage(evalExprValueOk.Value);
                    }

                    if (parseResult is not Result<string, Expression>.Ok parseOk)
                    {
                        throw new NotImplementedException("Unexpected result type: " + parseResult.GetType().FullName);
                    }

                    var evalEnvResult =
                        EvaluateExpressionDefaultLessStack(
                            parseAndEval.environment,
                            currentFrame.EnvironmentValue,
                            stackPrevValues: stackPrevValues);

                    if (evalEnvResult is Result<string, PineValue>.Err evalEnvError)
                    {
                        return "Failed to evaluate environment: " + evalEnvError;
                    }

                    if (evalEnvResult is not Result<string, PineValue>.Ok evalEnvOk)
                    {
                        throw new NotImplementedException(
                            "Unexpected result type: " + evalEnvResult.GetType().FullName);
                    }

                    stack.Push(StackFrameFromExpression(parseOk.Value, evalEnvOk.Value));

                    continue;
                }

                if (evalInstr.Expression is Expression.ConditionalExpression conditionalExpr)
                {
                    var evalConditionResult =
                        EvaluateExpressionDefaultLessStack(
                            conditionalExpr.condition,
                            currentFrame.EnvironmentValue,
                            stackPrevValues: stackPrevValues);

                    if (evalConditionResult is Result<string, PineValue>.Err evalConditionError)
                    {
                        return "Failed to evaluate condition: " + evalConditionError;
                    }

                    if (evalConditionResult is not Result<string, PineValue>.Ok evalConditionOk)
                    {
                        throw new NotImplementedException("Unexpected result type: " + evalConditionResult.GetType().FullName);
                    }

                    if (evalConditionOk.Value == PineVMValues.TrueValue)
                    {
                        stack.Push(StackFrameFromExpression(conditionalExpr.ifTrue, currentFrame.EnvironmentValue));

                        continue;
                    }

                    if (evalConditionOk.Value == PineVMValues.FalseValue)
                    {
                        stack.Push(StackFrameFromExpression(conditionalExpr.ifFalse, currentFrame.EnvironmentValue));

                        continue;
                    }

                    currentFrame.InstructionsResultValues.Span[currentFrame.InstructionPointer] = PineValue.EmptyList;
                    currentFrame.InstructionPointer++;
                    continue;
                }

                var evalResult =
                    EvaluateExpressionDefaultLessStack(
                        evalInstr.Expression,
                        currentFrame.EnvironmentValue,
                        stackPrevValues: stackPrevValues);

                if (evalResult is Result<string, PineValue>.Err evalError)
                {
                    return "Failed to evaluate expression: " + evalError;
                }

                if (evalResult is not Result<string, PineValue>.Ok evalOk)
                {
                    throw new NotImplementedException("Unexpected result type: " + evalResult.GetType().FullName);
                }

                currentFrame.InstructionsResultValues.Span[currentFrame.InstructionPointer] = evalOk.Value;
                currentFrame.InstructionPointer++;
                continue;
            }

            if (currentInstruction is StackInstruction.ConditionalJumpInstruction conditionalStatement)
            {
                var evalConditionResult =
                    EvaluateExpressionDefaultLessStack(
                        conditionalStatement.Condition,
                        currentFrame.EnvironmentValue,
                        stackPrevValues: stackPrevValues);

                if (evalConditionResult is Result<string, PineValue>.Err evalConditionError)
                {
                    return "Failed to evaluate condition: " + evalConditionError;
                }

                if (evalConditionResult is not Result<string, PineValue>.Ok evalConditionOk)
                {
                    throw new NotImplementedException("Unexpected result type: " + evalConditionResult.GetType().FullName);
                }

                if (evalConditionOk.Value == PineVMValues.TrueValue)
                {
                    currentFrame.InstructionPointer += conditionalStatement.IfTrueOffset;
                    continue;
                }

                currentFrame.InstructionPointer++;
                continue;
            }

            return "Unexpected instruction type: " + currentInstruction.GetType().FullName;
        }
    }

    private Result<string, PineValue> EvaluateExpressionDefaultLessStack(
        Expression expression,
        PineValue environment,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        if (expression is Expression.LiteralExpression literalExpression)
            return literalExpression.Value;

        if (expression is Expression.ListExpression listExpression)
        {
            return EvaluateListExpression(listExpression, environment, stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.ParseAndEvalExpression applicationExpression)
        {
            return
                EvaluateParseAndEvalExpression(
                    applicationExpression,
                    environment,
                    stackPrevValues: stackPrevValues)
                switch
                {
                    Result<string, PineValue>.Err err =>
                    "Failed to evaluate parse and evaluate: " + err,

                    var other =>
                    other
                };
        }

        if (expression is Expression.KernelApplicationExpression kernelApplicationExpression)
        {
            return
                EvaluateKernelApplicationExpression(
                    environment,
                    kernelApplicationExpression,
                    stackPrevValues: stackPrevValues) switch
                {
                    Result<string, PineValue>.Err err =>
                    "Failed to evaluate kernel function application: " + err,

                    var other =>
                    other
                };
        }

        if (expression is Expression.ConditionalExpression conditionalExpression)
        {
            return EvaluateConditionalExpression(
                environment,
                conditionalExpression,
                stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.EnvironmentExpression)
        {
            return environment;
        }

        if (expression is Expression.StringTagExpression stringTagExpression)
        {
            return EvaluateExpressionDefaultLessStack(
                stringTagExpression.tagged,
                environment,
                stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.DelegatingExpression delegatingExpr)
        {
            return delegatingExpr.Delegate.Invoke(EvaluateExpressionDefault, environment);
        }

        if (expression is Expression.StackReferenceExpression stackRef)
        {
            var index = stackPrevValues.Length + stackRef.offset;

            if (index < 0 || index >= stackPrevValues.Length)
            {
                return "Invalid stack reference: " + stackRef.offset;
            }

            return stackPrevValues.Span[index];
        }

        throw new NotImplementedException("Unexpected shape of expression: " + expression.GetType().FullName);
    }

    public Result<string, PineValue> EvaluateListExpression(
        Expression.ListExpression listExpression,
        PineValue environment,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        var listItems = new List<PineValue>(listExpression.List.Count);

        for (var i = 0; i < listExpression.List.Count; i++)
        {
            var item = listExpression.List[i];

            var itemResult = EvaluateExpressionDefaultLessStack(
                item,
                environment,
                stackPrevValues: stackPrevValues);

            if (itemResult is Result<string, PineValue>.Err itemErr)
                return "Failed to evaluate list element [" + i + "]: " + itemErr.Value;

            if (itemResult is Result<string, PineValue>.Ok itemOk)
            {
                listItems.Add(itemOk.Value);
                continue;
            }

            throw new NotImplementedException("Unexpected result type: " + itemResult.GetType().FullName);
        }

        return PineValue.List(listItems);
    }

    public Result<string, PineValue> EvaluateParseAndEvalExpression(
        Expression.ParseAndEvalExpression parseAndEval,
        PineValue environment,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        Result<string, PineValue> continueWithEnvValueAndFunction(
            PineValue environmentValue,
            Expression functionExpression)
        {
            if (environmentValue is PineValue.ListValue list)
            {
                FunctionApplicationMaxEnvSize =
                FunctionApplicationMaxEnvSize < list.Elements.Count ? list.Elements.Count : FunctionApplicationMaxEnvSize;
            }

            return EvaluateExpression(environment: environmentValue, expression: functionExpression);
        }

        return
            EvaluateExpressionDefaultLessStack(
                parseAndEval.environment,
                environment,
                stackPrevValues: stackPrevValues) switch
            {
                Result<string, PineValue>.Err envErr =>
                "Failed to evaluate argument: " + envErr.Value,

                Result<string, PineValue>.Ok environmentValue =>
                EvaluateExpressionDefaultLessStack(
                    parseAndEval.expression,
                    environment,
                    stackPrevValues: stackPrevValues) switch
                {
                    Result<string, PineValue>.Err exprErr =>
                    "Failed to evaluate expression: " + exprErr.Value,

                    Result<string, PineValue>.Ok expressionValue =>
                    parseExpressionDelegate(expressionValue.Value) switch
                    {
                        Result<string, Expression>.Err parseErr =>
                        "Failed to parse expression from value: " + parseErr.Value +
                        " - expressionValue is " + DescribeValueForErrorMessage(expressionValue.Value) +
                        " - environmentValue is " + DescribeValueForErrorMessage(expressionValue.Value),

                        Result<string, Expression>.Ok functionExpression =>
                        continueWithEnvValueAndFunction(environmentValue.Value, functionExpression.Value),

                        var otherResult =>
                        throw new NotImplementedException("Unexpected result type for parse: " + otherResult.GetType().FullName)
                    },

                    var otherResult =>
                    throw new NotImplementedException("Unexpected result type for expr: " + otherResult.GetType().FullName)
                },

                var otherResult =>
                throw new NotImplementedException("Unexpected result type for env: " + otherResult.GetType().FullName)
            };
    }

    public static string DescribeValueForErrorMessage(PineValue pineValue) =>
        PineValueAsString.StringFromValue(pineValue)
        .Unpack(fromErr: _ => "not a string", fromOk: asString => "string \'" + asString + "\'");


    public Result<string, PineValue> EvaluateKernelApplicationExpression(
        PineValue environment,
        Expression.KernelApplicationExpression application,
        ReadOnlyMemory<PineValue> stackPrevValues) =>
        EvaluateExpressionDefaultLessStack(application.argument, environment, stackPrevValues) switch
        {
            Result<string, PineValue>.Ok argument =>
            application.function(argument.Value),

            Result<string, PineValue>.Err error =>
            "Failed to evaluate argument: " + error,

            var otherResult =>
            throw new NotImplementedException("Unexpected result type: " + otherResult.GetType().FullName)
        };


    public Result<string, PineValue> EvaluateConditionalExpression(
        PineValue environment,
        Expression.ConditionalExpression conditional,
        ReadOnlyMemory<PineValue> stackPrevValues) =>
        EvaluateExpressionDefaultLessStack(
            conditional.condition,
            environment,
            stackPrevValues: stackPrevValues)
        switch
        {
            Result<string, PineValue>.Ok conditionValue =>
            conditionValue == PineVMValues.TrueValue
            ?
            EvaluateExpressionDefaultLessStack(
                conditional.ifTrue,
                environment,
                stackPrevValues: stackPrevValues)
            :
            conditionValue == PineVMValues.FalseValue
            ?
            EvaluateExpressionDefaultLessStack(
                conditional.ifFalse,
                environment,
                stackPrevValues: stackPrevValues)
            :
            PineValue.EmptyList,

            Result<string, PineValue>.Err error =>
            "Failed to evaluate condition: " + error,

            var otherResult =>
            throw new NotImplementedException("Unexpected result type: " + otherResult.GetType().FullName)
        };
}
