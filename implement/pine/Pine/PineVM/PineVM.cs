using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Pine.Core;

namespace Pine.PineVM;

public interface IPineVM
{
    Result<string, PineValue> EvaluateExpression(
        Expression expression,
        PineValue environment);
}

public record struct EvalCacheEntryKey(
    PineValue ExprValue,
    PineValue EnvValue);

public class PineVM : IPineVM
{
    public long EvaluateExpressionCount { private set; get; }

    public long FunctionApplicationMaxEnvSize { private set; get; }

    private IDictionary<EvalCacheEntryKey, PineValue>? EvalCache { init; get; }

    private readonly Action<EvaluationReport>? reportFunctionApplication;

    private readonly IReadOnlyDictionary<Expression, IReadOnlyList<EnvConstraintId>>? compilationEnvClasses;

    private readonly bool disableReductionInCompilation;

    private readonly bool disablePrecompiled;

    public readonly PineVMParseCache parseCache = new();

    private readonly IReadOnlyDictionary<PineValue, Func<EvalExprDelegate, PineValue, Result<string, PineValue>>>? overrideInvocations;

    public record EvaluationReport(
        PineValue ExpressionValue,
        Expression Expression,
        PineValue Environment,
        long InstructionCount,
        long ParseAndEvalCount,
        PineValue ReturnValue);

    public PineVM(
        IDictionary<EvalCacheEntryKey, PineValue>? evalCache = null,
        Action<EvaluationReport>? reportFunctionApplication = null,
        IReadOnlyDictionary<Expression, IReadOnlyList<EnvConstraintId>>? compilationEnvClasses = null,
        bool disableReductionInCompilation = false,
        bool disablePrecompiled = false,
        IReadOnlyDictionary<PineValue, Func<EvalExprDelegate, PineValue, Result<string, PineValue>>>? overrideInvocations = null)
    {
        EvalCache = evalCache;

        this.reportFunctionApplication = reportFunctionApplication;

        this.compilationEnvClasses = compilationEnvClasses;

        this.disableReductionInCompilation = disableReductionInCompilation;
        this.disablePrecompiled = disablePrecompiled;

        this.overrideInvocations = overrideInvocations;
    }

    public Result<string, PineValue> EvaluateExpression(
        Expression expression,
        PineValue environment) =>
        EvaluateExpressionOnCustomStack(
            expression,
            environment,
            config: new EvaluationConfig(ParseAndEvalCountLimit: null))
        .Map(report => report.ReturnValue);

    public record StackFrameInstructions(
        IReadOnlyList<StackInstruction> Instructions)
    {
        public virtual bool Equals(StackFrameInstructions? other)
        {
            if (other is not { } notNull)
                return false;

            return
                ReferenceEquals(this, notNull) ||
                Instructions.Count == notNull.Instructions.Count &&
                Instructions.SequenceEqual(notNull.Instructions);
        }

        public override int GetHashCode()
        {
            var hashCode = new HashCode();

            foreach (var item in Instructions)
            {
                hashCode.Add(item.GetHashCode());
            }

            return hashCode.ToHashCode();
        }
    }

    record StackFrame(
        PineValue? ExpressionValue,
        Expression Expression,
        StackFrameInstructions Instructions,
        PineValue EnvironmentValue,
        Memory<PineValue> InstructionsResultValues,
        long BeginInstructionCount,
        long BeginParseAndEvalCount,
        long BeginStackFrameCount,
        ApplyStepwise? Specialization)
    {
        public int InstructionPointer { get; set; } = 0;

        public int LastEvalResultIndex { get; set; } = -1;

        public void ReturnFromChildFrame(PineValue frameReturnValue)
        {
            if (Specialization is not null)
            {
                Specialization.ReturningFromChildFrame(frameReturnValue);
                return;
            }

            PushInstructionResult(frameReturnValue);
        }

        public void PushInstructionResult(PineValue value)
        {
            InstructionsResultValues.Span[InstructionPointer] = value;
            LastEvalResultIndex = InstructionPointer;
            ++InstructionPointer;
        }

        public PineValue LastEvalResult()
        {
            if (LastEvalResultIndex < 0)
                throw new InvalidOperationException("Reference to last eval result before first eval");

            return InstructionsResultValues.Span[LastEvalResultIndex];
        }
    }

    /*
     * TODO: Expand the stack frame instruction format so that we can model these specializations
     * as precompiled stack frames.
     * That means the stack frame (instruction) model needs to be able to loop (mutate counter in place) and to supply inputs.
     * */
    public record ApplyStepwise
    {
        public StepResult CurrentStep { private set; get; }

        public ApplyStepwise(StepResult.Continue start)
        {
            CurrentStep = start;
        }

        public void ReturningFromChildFrame(PineValue frameReturnValue)
        {
            if (CurrentStep is StepResult.Continue cont)
            {
                CurrentStep = cont.Callback(frameReturnValue);
            }
            else
            {
                throw new Exception("Returning on frame already completed earlier.");
            }
        }

        public abstract record StepResult
        {
            public sealed record Continue(
                Expression Expression,
                PineValue EnvironmentValue,
                Func<PineValue, StepResult> Callback)
                : StepResult;

            public sealed record Complete(PineValue PineValue)
                : StepResult;
        }
    }

    public record ExpressionCompilation(
        StackFrameInstructions Generic,
        IReadOnlyList<(IReadOnlyList<EnvConstraintItem> constraint, StackFrameInstructions instructions)> Specialized)
    {
        public StackFrameInstructions SelectInstructionsForEnvironment(PineValue environment)
        {
            for (var i = 0; i < Specialized.Count; i++)
            {
                var specialization = Specialized[i];

                bool foundMismatch = false;

                for (var specializationIndex = 0; specializationIndex < specialization.constraint.Count; specializationIndex++)
                {
                    var constraintItem = specialization.constraint[specializationIndex];

                    if (CodeAnalysis.ValueFromPathInValue(environment, constraintItem.Path.Span) is not { } pathValue)
                    {
                        foundMismatch = true;
                        break;
                    }

                    if (!pathValue.Equals(constraintItem.Value))
                    {
                        foundMismatch = true;
                        break;
                    }
                }

                if (!foundMismatch)
                {
                    return specialization.instructions;
                }
            }

            return Generic;
        }
    }

    public record struct EnvConstraintItem(
        ReadOnlyMemory<int> Path,
        PineValue Value);

    readonly Dictionary<Expression, ExpressionCompilation> expressionCompilationDict = [];

    StackFrame StackFrameFromExpression(
        PineValue? expressionValue,
        Expression expression,
        PineValue environment,
        long beginInstructionCount,
        long beginParseAndEvalCount,
        long beginStackFrameCount)
    {
        var compilation = GetExpressionCompilation(expression);

        var instructions = compilation.SelectInstructionsForEnvironment(environment);

        return new StackFrame(
            expressionValue,
            expression,
            instructions,
            EnvironmentValue: environment,
            new PineValue[instructions.Instructions.Count],
            BeginInstructionCount: beginInstructionCount,
            BeginParseAndEvalCount: beginParseAndEvalCount,
            BeginStackFrameCount: beginStackFrameCount,
            Specialization: null);
    }

    public ExpressionCompilation GetExpressionCompilation(
        Expression rootExpression)
    {
        if (expressionCompilationDict.TryGetValue(rootExpression, out var cachedCompilation))
        {
            return cachedCompilation;
        }

        var compilation = ExpressionCompilationLessCache(rootExpression);

        expressionCompilationDict[rootExpression] = compilation;

        return compilation;
    }

    public ExpressionCompilation ExpressionCompilationLessCache(Expression rootExpression)
    {
        IReadOnlyList<EnvConstraintId>? specializations = null;

        compilationEnvClasses?.TryGetValue(rootExpression, out specializations);

        return
            CompileExpression(
                rootExpression,
                specializations ?? [],
                parseCache: parseCache,
                disableReduction: disableReductionInCompilation);
    }

    public static ExpressionCompilation CompileExpression(
        Expression rootExpression,
        IReadOnlyList<EnvConstraintId> specializations,
        PineVMParseCache parseCache,
        bool disableReduction)
    {
        var generic =
            new StackFrameInstructions(
                InstructionsFromExpressionTransitive(
                    rootExpression,
                    envConstraintId: null,
                    parseCache: parseCache,
                    disableReduction: disableReduction));

        var specialized =
            specializations
            // Order to prefer more specific constraints when selecting at runtime.
            .OrderDescending(EnvironmentClassSpecificityComparer.Instance)
            .Select(
                specialization =>
                ((IReadOnlyList<EnvConstraintItem>)
                [..specialization.ParsedEnvItems
                .Select(envItem => new EnvConstraintItem(envItem.Key.ToArray(), envItem.Value))],
                new StackFrameInstructions(
                    InstructionsFromExpressionTransitive(
                        rootExpression,
                        envConstraintId: specialization,
                        parseCache: parseCache,
                        disableReduction: disableReduction))))
            .ToImmutableArray();

        return new ExpressionCompilation(
            Generic: generic,
            Specialized: specialized);
    }

    /*
     * 
     * In the older impl to compile to C#, we had CompilationUnitEnvExprEntry to inform which
     * declarations will be available in the environment to reference.
     * Here, we dont need that info for the first iteration:
     * Instead of a global shared representation, we can inline were referenced.
     * For recursive functions, we can stop inlining which will lead to a lookup in the dictionary for each iteration.
     * However, in some cases (like the adaptive partial application emitted by the Elm compiler),
     * inlining the same expression multiple times can be better.
     * For that specific case, expanding the environment constraint (collected by CA) to enable erasing conditionals might
     * improve overall efficiency a lot.
     * Optimizing for more runtime efficiency follows in later iterations.
     * 
     * (In addition to maybe being easier to implement and read, the inlining will also improve runtime efficiency in many cases.)
     * 
     * */

    public static IReadOnlyList<StackInstruction> InstructionsFromExpressionTransitive(
        Expression rootExpression,
        EnvConstraintId? envConstraintId,
        PineVMParseCache parseCache,
        bool disableReduction)
    {
        var expressionWithEnvConstraint =
            envConstraintId is null ?
            rootExpression
            :
            SubstituteSubexpressionsForEnvironmentConstraint(
                rootExpression,
                envConstraintId: envConstraintId);

        /*
         * Substituting subexpressions for the given environment constraint once at the root should be enough.
         * */

        var reducedExpression =
            disableReduction
            ?
            expressionWithEnvConstraint
            :
            ReduceExpressionAndInlineRecursive(
                rootExpression: rootExpression,
                currentExpression: expressionWithEnvConstraint,
                inlinedParents: [],
                maxDepth: 7,
                maxSubexpressionCount: 4_000,
                parseCache: parseCache,
                envConstraintId: envConstraintId,
                disableRecurseAfterInline: false);

        IReadOnlyList<StackInstruction> allInstructions =
            [.. InstructionsFromExpressionTransitive(reducedExpression).Append(StackInstruction.Return)];

        return allInstructions;
    }

    public static Expression ReduceExpressionAndInlineRecursive(
        Expression rootExpression,
        EnvConstraintId? envConstraintId,
        int maxDepth,
        int maxSubexpressionCount,
        PineVMParseCache parseCache,
        bool disableRecurseAfterInline) =>
        ReduceExpressionAndInlineRecursive(
            rootExpression: rootExpression,
            currentExpression: rootExpression,
            inlinedParents: [],
            envConstraintId: envConstraintId,
            maxDepth: maxDepth,
            maxSubexpressionCount: maxSubexpressionCount,
            parseCache: parseCache,
            disableRecurseAfterInline: disableRecurseAfterInline);

    public static Expression ReduceExpressionAndInlineRecursive(
        Expression rootExpression,
        Expression currentExpression,
        ImmutableStack<Expression> inlinedParents,
        EnvConstraintId? envConstraintId,
        int maxDepth,
        int maxSubexpressionCount,
        PineVMParseCache parseCache,
        bool disableRecurseAfterInline)
    {
        var expressionSubstituted =
            envConstraintId is null
            ?
            currentExpression
            :
            SubstituteSubexpressionsForEnvironmentConstraint(currentExpression, envConstraintId);

        var expressionReduced =
            CompilePineToDotNet.ReducePineExpression.SearchForExpressionReductionRecursive(
                maxDepth: 10,
                expressionSubstituted,
                envConstraintId: envConstraintId);

        if (maxDepth <= 0)
        {
            return expressionReduced;
        }

        if (envConstraintId is null)
        {
            /*
             * Adapt to observation 2024-07-14:
             * Stopping recursion here if envConstraintId is null resulted in significantly faster
             * completion times in a test compiling all modules of the Elm compiler.
             * */
            return expressionReduced;
        }

        var subexprCount = 0;

        foreach (var subexpr in Expression.EnumerateSelfAndDescendants(expressionReduced))
        {
            ++subexprCount;

            /*
             * Install a limit after observing cases with more than a hundred million subexpressions.
             * */

            if (maxSubexpressionCount < subexprCount)
            {
                return expressionReduced;
            }
        }

        Expression? TryInlineParseAndEval(
            Expression.ParseAndEval parseAndEvalExpr,
            bool noRecursion)
        {
            Expression? ContinueReduceForKnownExprValue(PineValue exprValue)
            {
                if (parseCache.ParseExpression(exprValue) is not Result<string, Expression>.Ok parseOk)
                {
                    return null;
                }

                if (rootExpression.Equals(parseOk.Value))
                {
                    return null;
                }

                if (noRecursion)
                {
                    if (inlinedParents.Contains(parseOk.Value))
                    {
                        return null;
                    }
                }

                /*
                 * For inlining, translate instances of EnvironmentExpression to the parent environment.
                 * */

                var inlinedExpr =
                    CompilePineToDotNet.ReducePineExpression.TransformPineExpressionWithOptionalReplacement(
                        findReplacement:
                        descendant =>
                        {
                            if (descendant is Expression.Environment)
                            {
                                return parseAndEvalExpr.environment;
                            }

                            return null;
                        },
                        parseOk.Value).expr;

                if (disableRecurseAfterInline)
                {
                    return inlinedExpr;
                }

                var inlinedExprSubstituted =
                    envConstraintId is null
                    ?
                    inlinedExpr
                    :
                    SubstituteSubexpressionsForEnvironmentConstraint(inlinedExpr, envConstraintId);

                var inlinedExprReduced =
                    CompilePineToDotNet.ReducePineExpression.SearchForExpressionReductionRecursive(
                        maxDepth: 10,
                        inlinedExprSubstituted,
                        envConstraintId: envConstraintId);

                {
                    var conditionsCount = 0;
                    var invocationsCount = 0;
                    var inlinedSubExprCount = 0;

                    foreach (var subexpr in Expression.EnumerateSelfAndDescendants(inlinedExprReduced))
                    {
                        ++inlinedSubExprCount;

                        if (300 < inlinedSubExprCount)
                        {
                            return null;
                        }

                        if (subexpr is Expression.Conditional)
                        {
                            ++conditionsCount;

                            if (3 < conditionsCount)
                            {
                                return null;
                            }

                            continue;
                        }

                        if (subexpr is Expression.ParseAndEval)
                        {
                            ++invocationsCount;

                            if (4 < invocationsCount)
                            {
                                return null;
                            }

                            continue;
                        }
                    }
                }

                var inlinedFinal =
                    ReduceExpressionAndInlineRecursive(
                        rootExpression: rootExpression,
                        // currentExpression: inlinedExpr,
                        currentExpression: inlinedExprReduced,
                        inlinedParents: inlinedParents.Push(parseOk.Value),
                        envConstraintId: envConstraintId,
                        maxDepth: maxDepth - 1,
                        maxSubexpressionCount: maxSubexpressionCount,
                        parseCache: parseCache,
                        disableRecurseAfterInline: disableRecurseAfterInline);

                {
                    var conditionsCount = 0;
                    var invocationsCount = 0;
                    var inlinedSubExprCount = 0;

                    foreach (var subexpr in Expression.EnumerateSelfAndDescendants(inlinedFinal))
                    {
                        ++inlinedSubExprCount;

                        if (300 < inlinedSubExprCount)
                        {
                            return null;
                        }

                        if (subexpr is Expression.Conditional)
                        {
                            ++conditionsCount;

                            if (3 < conditionsCount)
                            {
                                return null;
                            }

                            continue;
                        }

                        if (subexpr is Expression.ParseAndEval)
                        {
                            ++invocationsCount;

                            if (4 < invocationsCount)
                            {
                                return null;
                            }

                            continue;
                        }
                    }
                }

                return inlinedFinal;
            }

            if (!parseAndEvalExpr.encoded.ReferencesEnvironment)
            {
                if (CompilePineToDotNet.ReducePineExpression.TryEvaluateExpressionIndependent(parseAndEvalExpr.encoded) is
                    Result<string, PineValue>.Ok evalExprOk)
                {
                    return ContinueReduceForKnownExprValue(evalExprOk.Value);
                }
            }

            return null;
        }

        Expression InlineParseAndEvalRecursive(
            Expression expression,
            bool underConditional)
        {
            return
                CompilePineToDotNet.ReducePineExpression.TransformPineExpressionWithOptionalReplacement(
                findReplacement: expr =>
                {
                    /*
                     * Do not inline invocations that are still conditional after substituting for the environment constraint.
                     * Inlining these cases can lead to suboptimal overall performance for various reasons.
                     * One reason is that inlining in a generic wrapper causes us to miss an opportunity to select
                     * a more specialized implementation because this selection only happens on invocation.
                     * */

                    /*
                     * 2024-07-20 Adaptation, for cases like specializations of `List.map`:
                     * When optimizing `List.map` (or its recursive helper function) (or `List.foldx` for example),
                     * better also inline the application of the generic partial application used with the function parameter.
                     * That application is conditional (list empty?), but we want to inline that to eliminate the generic wrapper for
                     * the function application and inline the parameter function directly.
                     * Thus, the new rule also enables inlining under conditional expressions unless it is recursive.
                     * */

                    if (expr is Expression.Conditional conditional)
                    {
                        var conditionInlined =
                        InlineParseAndEvalRecursive(
                            conditional.condition,
                            underConditional: underConditional);

                        var falseBranchInlined =
                        InlineParseAndEvalRecursive(
                            conditional.falseBranch,
                            underConditional: true);

                        var trueBranchInlined =
                        InlineParseAndEvalRecursive(
                            conditional.trueBranch,
                            underConditional: true);

                        return Expression.ConditionalInstance(
                            condition: conditionInlined,
                            falseBranch: falseBranchInlined,
                            trueBranch: trueBranchInlined);
                    }

                    if (expr is Expression.ParseAndEval parseAndEval)
                    {
                        if (TryInlineParseAndEval(parseAndEval, noRecursion: underConditional) is { } inlined)
                        {
                            return inlined;
                        }
                    }

                    return null;
                },
                expression).expr;
        }

        var expressionInlined =
            InlineParseAndEvalRecursive(
                expressionReduced,
                underConditional: false);

        var expressionInlinedReduced =
            CompilePineToDotNet.ReducePineExpression.SearchForExpressionReductionRecursive(
                maxDepth: 10,
                expressionInlined);

        return expressionInlinedReduced;
    }

    public static Expression SubstituteSubexpressionsForEnvironmentConstraint(
        Expression originalExpression,
        EnvConstraintId envConstraintId)
    {
        return
            CompilePineToDotNet.ReducePineExpression.TransformPineExpressionWithOptionalReplacement(
                findReplacement:
                descendant =>
                {
                    if (CodeAnalysis.TryParseExpressionAsIndexPathFromEnv(descendant) is { } indexPath)
                    {
                        if (indexPath is ExprMappedToParentEnv.LiteralInParentEnv asLiteral)
                        {
                            return Expression.LiteralInstance(asLiteral.Value);
                        }

                        if (indexPath is ExprMappedToParentEnv.PathInParentEnv pathInParentEnv)
                        {
                            if (envConstraintId?.TryGetValue(pathInParentEnv.Path) is { } value)
                            {
                                return Expression.LiteralInstance(value);
                            }
                        }
                    }

                    return null;
                },
                originalExpression).expr;
    }

    public static IReadOnlyList<StackInstruction> InstructionsFromExpressionTransitive(
        Expression rootExpression)
    {
        var node = NodeFromExpressionTransitive(
            rootExpression,
            conditionalsToSkip: []);

        return InstructionsFromNode(
            node,
            reusableExpressionResultOffset: _ => null,
            shouldSeparate: _ => false);
    }

    public static ImperativeNode NodeFromExpressionTransitive(
        Expression rootExpression,
        ImmutableHashSet<Expression.Conditional> conditionalsToSkip)
    {
        var conditionalToSplit =
            ListComponentsOrderedForCompilation(rootExpression, skipDescendants: null)
            .OfType<Expression.Conditional>()
            .Where(c => !conditionalsToSkip.Contains(c))
            .FirstOrDefault();

        if (conditionalToSplit is not null)
        {
            var conditionNode =
                NodeFromExpressionTransitive(
                    conditionalToSplit.condition,
                    conditionalsToSkip: []);

            var falseNode =
                NodeFromExpressionTransitive(
                    conditionalToSplit.falseBranch,
                    conditionalsToSkip: []);

            var trueNode =
                NodeFromExpressionTransitive(
                    conditionalToSplit.trueBranch,
                    conditionalsToSkip: []);

            var continuationNode =
                NodeFromExpressionTransitive(
                    rootExpression,
                    conditionalsToSkip: conditionalsToSkip.Add(conditionalToSplit));

            return
                new ImperativeNode.ConditionalNode(
                    Origin: conditionalToSplit,
                    Condition: conditionNode,
                    FalseBranch: falseNode,
                    TrueBranch: trueNode,
                    Continuation: continuationNode);
        }

        return new ImperativeNode.LeafNode(rootExpression);
    }

    public static IReadOnlyList<StackInstruction> InstructionsFromNode(
        ImperativeNode imperativeNode,
        Func<Expression, int?> reusableExpressionResultOffset,
        Func<Expression, bool> shouldSeparate)
    {
        if (imperativeNode is ImperativeNode.LeafNode leaf)
        {
            var instructionExprsFiltered =
                ExpressionsToSeparateSkippingConditional(
                    leaf.Expression,
                    expressionAlreadyCovered: expr => reusableExpressionResultOffset(expr).HasValue,
                    shouldSeparate: shouldSeparate)
                .Append(leaf.Expression)
                .Distinct()
                .ToImmutableArray();

            var localInstructionIndexFromExpr = new Dictionary<Expression, int>();

            int? reusableEvalResult(Expression expr)
            {
                {
                    if (reusableExpressionResultOffset.Invoke(expr) is { } reusableIndex)
                    {
                        return reusableIndex;
                    }
                }

                {
                    if (localInstructionIndexFromExpr.TryGetValue(expr, out var earlierIndex))
                    {
                        return earlierIndex;
                    }
                }

                return null;
            }

            var instructionsOptimized =
                instructionExprsFiltered
                .Select((expression, instructionIndex) =>
                {
                    if (reusableEvalResult(expression) is { } reusableIndex)
                    {
                        var offset = reusableIndex - instructionIndex;

                        return new Expression.StackReferenceExpression(offset);
                    }

                    localInstructionIndexFromExpr.Add(expression, instructionIndex);

                    return expression;
                })
                .Select((instruction, instructionIndex) =>
                OptimizeExpressionTransitive(instruction, instructionIndex, reusableEvalResult))
                .Select(StackInstruction.Eval)
                .ToImmutableArray();

            return instructionsOptimized;
        }

        if (imperativeNode is ImperativeNode.ConditionalNode conditional)
        {
            var unconditionalNodesUnderCondition =
                ImperativeNode.EnumerateSelfAndDescendants(
                    conditional.Condition,
                    skipBranches: true)
                .ToImmutableArray();

            var unconditionalExprUnderCondition = new HashSet<Expression>();

            foreach (var nodeUnderCondition in unconditionalNodesUnderCondition)
            {
                if (nodeUnderCondition is not ImperativeNode.LeafNode leafNode)
                    continue;

                foreach (var expression in
                    Expression.EnumerateSelfAndDescendants(
                        leafNode.Expression,
                        skipDescendants:
                        expr => reusableExpressionResultOffset(expr).HasValue))
                {
                    unconditionalExprUnderCondition.Add(expression);
                }
            }

            var otherNodes =
                new[] { conditional.FalseBranch, conditional.TrueBranch, conditional.Continuation }
                .SelectMany(otherRoot =>
                ImperativeNode.EnumerateSelfAndDescendants(
                    otherRoot,
                    skipBranches: false))
                .ToImmutableArray();

            var candidatesForCSE = new HashSet<Expression>();

            foreach (var otherNode in otherNodes)
            {
                if (otherNode is not ImperativeNode.LeafNode otherLeafNode)
                    continue;

                foreach (var otherExpr in Expression.EnumerateSelfAndDescendants(
                    otherLeafNode.Expression,
                    skipDescendants: candidatesForCSE.Contains))
                {
                    if (!ExpressionLargeEnoughForCSE(otherExpr))
                        continue;

                    if (reusableExpressionResultOffset(otherExpr).HasValue)
                        continue;

                    if (unconditionalExprUnderCondition.Contains(otherExpr))
                    {
                        candidatesForCSE.Add(otherExpr);
                    }
                }
            }

            var conditionInstructions =
                InstructionsFromNode(
                    conditional.Condition,
                    reusableExpressionResultOffset,
                    shouldSeparate: candidatesForCSE.Contains);

            var reusableFromCondition = new Dictionary<Expression, int>();

            for (int i = 0; i < conditionInstructions.Count; i++)
            {
                var instruction = conditionInstructions[i];

                if (instruction is not StackInstruction.EvalInstruction evalInstruction)
                {
                    // Only include the range of instructions that will be executed unconditionally.
                    break;
                }

                reusableFromCondition[evalInstruction.Expression] = i;
            }

            var instructionsBeforeBranchFalseCount =
                conditionInstructions.Count + 1;

            int? reusableResultOffsetForBranchFalse(Expression expression)
            {
                if (reusableExpressionResultOffset(expression) is { } earlierOffset)
                {
                    return earlierOffset - instructionsBeforeBranchFalseCount;
                }

                if (reusableFromCondition.TryGetValue(expression, out var offsetFromCondition))
                {
                    return offsetFromCondition - instructionsBeforeBranchFalseCount;
                }

                return null;
            }

            IReadOnlyList<StackInstruction> falseBranchInstructions =
                [.. InstructionsFromNode(
                    conditional.FalseBranch,
                    reusableResultOffsetForBranchFalse,
                    shouldSeparate: _ => false)];

            IReadOnlyList<StackInstruction> trueBranchInstructions =
                [.. InstructionsFromNode(
                    conditional.TrueBranch,
                    reusableExpressionResultOffset:
                    expr => reusableResultOffsetForBranchFalse(expr) - (falseBranchInstructions.Count + 1),
                    shouldSeparate: _ => false)];

            IReadOnlyList<StackInstruction> falseBranchInstructionsAndJump =
                [.. falseBranchInstructions,
                StackInstruction.Jump(trueBranchInstructions.Count + 1)
                ];

            var branchInstruction =
                new StackInstruction.ConditionalJumpInstruction(
                    InvalidBranchOffset: falseBranchInstructionsAndJump.Count + trueBranchInstructions.Count,
                    TrueBranchOffset: falseBranchInstructionsAndJump.Count);

            IReadOnlyList<StackInstruction> instructionsBeforeContinuation =
                [..conditionInstructions,
                branchInstruction,
                ..falseBranchInstructionsAndJump,
                ..trueBranchInstructions,
                new StackInstruction.CopyLastAssignedInstruction()
                ];

            int? reusableResultOffsetForContinuation(Expression expression)
            {
                if (expression == conditional.Origin)
                {
                    return -1;
                }

                return
                    reusableResultOffsetForBranchFalse(expression) -
                    (instructionsBeforeContinuation.Count - instructionsBeforeBranchFalseCount);
            }

            IReadOnlyList<StackInstruction> continuationInstructions =
                [.. InstructionsFromNode(
                    conditional.Continuation,
                    reusableResultOffsetForContinuation,
                    shouldSeparate: _ => false)
                ];

            IReadOnlyList<StackInstruction> mergedInstructions =
                [..instructionsBeforeContinuation,
                ..continuationInstructions
                ];

            return mergedInstructions;
        }

        throw new NotImplementedException(
            "Unexpected node type: " + imperativeNode.GetType().FullName);
    }

    static Expression OptimizeExpressionTransitive(
        Expression expression,
        int instructionIndex,
        Func<Expression, int?> reusableEvalResultOffset)
    {
        return
            CompilePineToDotNet.ReducePineExpression.TransformPineExpressionWithOptionalReplacement(
                findReplacement:
                expr => OptimizeExpressionStep(
                    expr,
                    instructionIndex,
                    reusableEvalResultOffset),
                expression).expr;
    }

    static Expression? OptimizeExpressionStep(
        Expression expression,
        int instructionIndex,
        Func<Expression, int?> reusableEvalResultOffset)
    {
        if (expression is Expression.Environment)
            return null;

        if (reusableEvalResultOffset(expression) is { } reusableOffset)
        {
            var offset = reusableOffset - instructionIndex;

            if (offset != 0)
            {
                if (offset >= 0)
                {
                    throw new Exception(
                        "Found non-negative offset for stack ref expr: " + offset +
                        " (selfIndex is " + instructionIndex + ")");
                }

                return new Expression.StackReferenceExpression(offset: offset);
            }
        }

        {
            /*
             * Skip over all expressions that we do not descend into when enumerating the components.
             * (EnumerateComponentsOrderedForCompilation)
             * */

            if (expression is Expression.Conditional)
            {
                // Return non-null value to stop descend.
                return expression;
            }
        }

        if (TryFuseStep(expression) is { } fused)
        {
            return
                OptimizeExpressionTransitive(
                    fused,
                    instructionIndex,
                    reusableEvalResultOffset: reusableEvalResultOffset);
        }

        return null;
    }

    static IReadOnlyList<Expression> ExpressionsToSeparateSkippingConditional(
        Expression rootExpression,
        Func<Expression, bool> expressionAlreadyCovered,
        Func<Expression, bool> shouldSeparate)
    {
        var allExpressions =
            ListComponentsOrderedForCompilation(
                rootExpression,
                skipDescendants: expressionAlreadyCovered)
            .ToImmutableArray();

        var allExpressionsCount = new Dictionary<Expression, int>();

        foreach (var expression in allExpressions)
        {
            if (allExpressionsCount.TryGetValue(expression, out var count))
            {
                allExpressionsCount[expression] = count + 1;
            }
            else
            {
                allExpressionsCount[expression] = 1;
            }
        }

        var allExpressionsExceptUnderDuplicate =
            ListComponentsOrderedForCompilation(
                rootExpression,
                skipDescendants: node => 1 < allExpressionsCount[node] || expressionAlreadyCovered(node))
            .ToImmutableArray();

        var allExpressionsExceptUnderDuplicateCount = new Dictionary<Expression, int>();

        foreach (var expression in allExpressionsExceptUnderDuplicate)
        {
            if (allExpressionsExceptUnderDuplicateCount.TryGetValue(expression, out var count))
            {
                allExpressionsExceptUnderDuplicateCount[expression] = count + 1;
            }
            else
            {
                allExpressionsExceptUnderDuplicateCount[expression] = 1;
            }
        }

        var separatedInstructions =
            allExpressions
            .SelectMany(expression =>
            {
                if (expressionAlreadyCovered(expression))
                    return [];

                if (shouldSeparate(expression))
                    return [expression];

                if (expression is Expression.ParseAndEval parseAndEval)
                {
                    return (IReadOnlyList<Expression>)[expression];
                }

                if (ExpressionLargeEnoughForCSE(expression) &&
                allExpressionsExceptUnderDuplicateCount.TryGetValue(expression, out var exprInstCount) && 1 < exprInstCount)
                {
                    return [expression];
                }

                return [];
            })
            .ToImmutableArray();

        return separatedInstructions;
    }

    public static IEnumerable<Expression> ListComponentsOrderedForCompilation(
        Expression rootExpression,
        Func<Expression, bool>? skipDescendants)
    {
        var stack = new Stack<Expression>();
        var deepestDescendants = new Stack<Expression>();

        stack.Push(rootExpression);

        while (stack.TryPop(out var expression))
        {
            if (skipDescendants?.Invoke(expression) ?? false)
            {
                deepestDescendants.Push(expression);
                continue;
            }

            if (expression is Expression.List list)
            {
                for (var i = 0; i < list.items.Count; ++i)
                {
                    stack.Push(list.items[i]);
                }
            }

            if (expression is Expression.ParseAndEval parseAndEval)
            {
                stack.Push(parseAndEval.encoded);
                stack.Push(parseAndEval.environment);
            }

            if (expression is Expression.KernelApplication kernelApp)
            {
                stack.Push(kernelApp.input);
            }

            if (expression is Expression.Conditional conditional)
            {
                stack.Push(conditional.condition);

                /*
                 *
                 * For now, we create a new stack frame for each conditional expression.
                 * Therefore do not descend into the branches of the conditional expression.

                stack.Push(conditional.falseBranch);
                stack.Push(conditional.trueBranch);
                */
            }

            if (expression is Expression.StringTag stringTag)
            {
                stack.Push(stringTag.tagged);
            }

            deepestDescendants.Push(expression);
        }

        while (deepestDescendants.Count > 0)
        {
            yield return deepestDescendants.Pop();
        }
    }

    static bool ExpressionLargeEnoughForCSE(Expression expression)
    {
        if (expression is Expression.KernelApplication)
            return true;

        if (expression is Expression.List list)
        {
            for (int i = 0; i < list.items.Count; ++i)
            {
                if (ExpressionLargeEnoughForCSE(list.items[i]))
                    return true;
            }
        }

        if (expression is Expression.StringTag stringTag)
        {
            return ExpressionLargeEnoughForCSE(stringTag.tagged);
        }

        return 10 < expression.SubexpressionCount;
    }

    public static Expression? TryFuseStep(Expression expression)
    {
        if (TryMapToKernelApplications_Skip_ListHead_Path_Expression(expression) is { } fused)
        {
            return fused;
        }

        if (TryMapToKernelApplication_Equal_Two(expression) is { } fusedEqualTwo)
        {
            return fusedEqualTwo;
        }

        return null;
    }

    public static Expression.KernelApplications_Skip_Head_Path?
        TryMapToKernelApplications_Skip_ListHead_Path_Expression(Expression expression)
    {
        if (expression is not Expression.KernelApplication kernelApp)
            return null;

        if (kernelApp.function is not nameof(KernelFunction.head))
            return null;

        if (kernelApp.input is not Expression.KernelApplication innerKernelApp)
        {
            return continueWithSkipCount(skipCount: 0, kernelApp.input);
        }

        if (innerKernelApp.function is not nameof(KernelFunction.skip))
            return null;

        if (innerKernelApp.input is not Expression.List skipListExpr)
            return null;

        if (skipListExpr.items.Count is not 2)
            return null;

        var skipCountValueExpr = skipListExpr.items[0];

        /*
        if (!Expression.IsIndependent(skipCountValueExpr))
            return null;
        */

        if (skipCountValueExpr.ReferencesEnvironment)
            return null;

        if (Expression.EnumerateSelfAndDescendants(skipCountValueExpr)
            .Any(desc =>
            desc is Expression.ParseAndEval ||
            desc is Expression.StackReferenceExpression))
        {
            return null;
        }

        if (CompilePineToDotNet.ReducePineExpression.TryEvaluateExpressionIndependent(skipCountValueExpr)
            is not Result<string, PineValue>.Ok skipCountEvalOk)
            return null;

        if (KernelFunction.SignedIntegerFromValueRelaxed(skipCountEvalOk.Value) is not { } skipCount)
            return null;

        var currentArg = skipListExpr.items[1];

        return continueWithSkipCount((int)skipCount, currentArg);

        static Expression.KernelApplications_Skip_Head_Path continueWithSkipCount(
            int skipCount,
            Expression currentArg)
        {
            if (TryMapToKernelApplications_Skip_ListHead_Path_Expression(currentArg) is { } pathContinued)
            {
                return
                    pathContinued
                    with
                    {
                        SkipCounts = (int[])[.. pathContinued.SkipCounts.Span, skipCount]
                    };
            }

            return new Expression.KernelApplications_Skip_Head_Path(
                SkipCounts: (int[])[skipCount],
                Argument: currentArg);
        }
    }

    public static Expression.KernelApplication_Equal_Two? TryMapToKernelApplication_Equal_Two(Expression expression)
    {
        if (expression is not Expression.KernelApplication kernelApp)
            return null;

        if (kernelApp.function is not nameof(KernelFunction.equal))
            return null;

        if (kernelApp.input is not Expression.List listExpr)
            return null;

        if (listExpr.items.Count is not 2)
            return null;

        return new Expression.KernelApplication_Equal_Two(
            left: listExpr.items[0],
            right: listExpr.items[1]);
    }

    public record EvaluationConfig(
        int? ParseAndEvalCountLimit);

    public Result<string, EvaluationReport> EvaluateExpressionOnCustomStack(
        Expression rootExpression,
        PineValue rootEnvironment,
        EvaluationConfig config)
    {
        long instructionCount = 0;
        long parseAndEvalCount = 0;
        long stackFrameCount = 0;

        var stack = new Stack<StackFrame>();

        void pushStackFrame(StackFrame newFrame)
        {
            stack.Push(newFrame);

            ++stackFrameCount;
        }

        EvaluationReport? returnFromStackFrame(PineValue frameReturnValue)
        {
            var currentFrame = stack.Peek();

            if (currentFrame.ExpressionValue is { } currentFrameExprValue)
            {
                var frameInstructionCount = instructionCount - currentFrame.BeginInstructionCount;
                var frameParseAndEvalCount = parseAndEvalCount - currentFrame.BeginParseAndEvalCount;
                var frameStackFrameCount = stackFrameCount - currentFrame.BeginStackFrameCount;

                if (frameInstructionCount + frameStackFrameCount * 100 > 700 && EvalCache is { } evalCache)
                {
                    evalCache.TryAdd(
                        new EvalCacheEntryKey(currentFrameExprValue, currentFrame.EnvironmentValue),
                        frameReturnValue);
                }

                reportFunctionApplication?.Invoke(
                    new EvaluationReport(
                        ExpressionValue: currentFrameExprValue,
                        currentFrame.Expression,
                        currentFrame.EnvironmentValue,
                        InstructionCount: frameInstructionCount,
                        ParseAndEvalCount: frameParseAndEvalCount,
                        ReturnValue: frameReturnValue));
            }

            stack.Pop();

            if (stack.Count is 0)
            {
                var rootExprValue =
                    ExpressionEncoding.EncodeExpressionAsValue(rootExpression);

                return new EvaluationReport(
                    ExpressionValue: rootExprValue,
                    Expression: rootExpression,
                    Environment: rootEnvironment,
                    InstructionCount: instructionCount,
                    ParseAndEvalCount: parseAndEvalCount,
                    ReturnValue: frameReturnValue);
            }

            var previousFrame = stack.Peek();

            previousFrame.ReturnFromChildFrame(frameReturnValue);

            return null;
        }

        pushStackFrame(
            StackFrameFromExpression(
                expressionValue: null,
                rootExpression,
                rootEnvironment,
                beginInstructionCount: instructionCount,
                beginParseAndEvalCount: parseAndEvalCount,
                beginStackFrameCount: stackFrameCount));

        while (true)
        {
            var currentFrame = stack.Peek();

            ++instructionCount;

            if (currentFrame.Specialization is { } specializedFrame)
            {
                var stepResult = specializedFrame.CurrentStep;

                if (stepResult is ApplyStepwise.StepResult.Complete complete)
                {
                    var returnOverall =
                        returnFromStackFrame(complete.PineValue);

                    if (returnOverall is not null)
                    {
                        return returnOverall;
                    }

                    continue;
                }

                if (stepResult is ApplyStepwise.StepResult.Continue cont)
                {
                    var newFrame =
                        StackFrameFromExpression(
                            expressionValue: null,
                            expression: cont.Expression,
                            environment: cont.EnvironmentValue,
                            beginInstructionCount: instructionCount,
                            beginParseAndEvalCount: parseAndEvalCount,
                            beginStackFrameCount: stackFrameCount);

                    pushStackFrame(newFrame);

                    continue;
                }

                throw new NotImplementedException(
                    "Unexpected step result type: " + stepResult.GetType().FullName);
            }


            if (currentFrame.Instructions.Instructions.Count <= currentFrame.InstructionPointer)
            {
                return
                    "Instruction pointer out of bounds. Missing explicit return instruction.";
            }

            ReadOnlyMemory<PineValue> stackPrevValues =
                currentFrame.InstructionsResultValues[..currentFrame.InstructionPointer];

            var currentInstruction = currentFrame.Instructions.Instructions[currentFrame.InstructionPointer];

            if (currentInstruction is StackInstruction.ReturnInstruction)
            {
                var lastAssignedIndex = currentFrame.LastEvalResultIndex;

                if (lastAssignedIndex < 0)
                {
                    return "Return instruction before assignment";
                }

                var frameReturnValue =
                    currentFrame.InstructionsResultValues.Span[lastAssignedIndex];

                var returnOverall =
                    returnFromStackFrame(frameReturnValue);

                if (returnOverall is not null)
                {
                    return returnOverall;
                }

                continue;
            }

            if (currentInstruction is StackInstruction.CopyLastAssignedInstruction)
            {
                var lastAssignedIndex = currentFrame.LastEvalResultIndex;

                if (lastAssignedIndex < 0)
                {
                    return "CopyLastAssignedInstruction before assignment";
                }

                var lastAssignedValue = currentFrame.InstructionsResultValues.Span[lastAssignedIndex];

                currentFrame.PushInstructionResult(lastAssignedValue);

                continue;
            }

            if (currentInstruction is StackInstruction.EvalInstruction evalInstr)
            {
                if (evalInstr.Expression is Expression.ParseAndEval parseAndEval)
                {
                    {
                        ++parseAndEvalCount;

                        if (config.ParseAndEvalCountLimit is { } limit && parseAndEvalCount > limit)
                        {
                            return "Parse and eval count limit exceeded: " + limit;
                        }
                    }

                    var expressionValue =
                        EvaluateExpressionDefaultLessStack(
                            parseAndEval.encoded,
                            currentFrame.EnvironmentValue,
                            stackPrevValues: stackPrevValues);

                    var environmentValue =
                        EvaluateExpressionDefaultLessStack(
                            parseAndEval.environment,
                            currentFrame.EnvironmentValue,
                            stackPrevValues: stackPrevValues);

                    {
                        if (InvocationCachedResultOrOverride(
                            expressionValue: expressionValue,
                            environmentValue: environmentValue) is { } fromCacheOrDelegate)
                        {
                            currentFrame.PushInstructionResult(fromCacheOrDelegate);

                            continue;
                        }
                    }

                    var parseResult = parseCache.ParseExpression(expressionValue);

                    if (parseResult is Result<string, Expression>.Err parseErr)
                    {
                        return
                            "Failed to parse expression from value: " + parseErr.Value +
                            " - expressionValue is " + DescribeValueForErrorMessage(expressionValue) +
                            " - environmentValue is " + DescribeValueForErrorMessage(expressionValue);
                    }

                    if (parseResult is not Result<string, Expression>.Ok parseOk)
                    {
                        throw new NotImplementedException("Unexpected result type: " + parseResult.GetType().FullName);
                    }

                    if (!disablePrecompiled &&
                        Precompiled.SelectPrecompiled(parseOk.Value, environmentValue, parseCache) is { } precompiledDelegate)
                    {
                        var precompiledResult = precompiledDelegate();

                        switch (precompiledResult)
                        {
                            case Precompiled.PrecompiledResult.FinalValue finalValue:

                                stackFrameCount += finalValue.StackFrameCount;

                                currentFrame.PushInstructionResult(finalValue.Value);

                                continue;

                            case Precompiled.PrecompiledResult.ContinueParseAndEval continueParseAndEval:
                                {
                                    if (InvocationCachedResultOrOverride(
                                        expressionValue: continueParseAndEval.ExpressionValue,
                                        environmentValue: continueParseAndEval.EnvironmentValue) is { } fromCacheOrDelegate)
                                    {
                                        currentFrame.PushInstructionResult(fromCacheOrDelegate);

                                        continue;
                                    }

                                    var contParseResult = parseCache.ParseExpression(continueParseAndEval.ExpressionValue);

                                    if (contParseResult is Result<string, Expression>.Err contParseErr)
                                    {
                                        return
                                            "Failed to parse expression from value: " + contParseErr.Value +
                                            " - expressionValue is " + DescribeValueForErrorMessage(expressionValue) +
                                            " - environmentValue is " + DescribeValueForErrorMessage(expressionValue);
                                    }

                                    if (contParseResult is not Result<string, Expression>.Ok contParseOk)
                                    {
                                        throw new NotImplementedException("Unexpected result type: " + parseResult.GetType().FullName);
                                    }

                                    var newFrame =
                                        StackFrameFromExpression(
                                            expressionValue: continueParseAndEval.ExpressionValue,
                                            expression: contParseOk.Value,
                                            environment: continueParseAndEval.EnvironmentValue,
                                            beginInstructionCount: instructionCount,
                                            beginParseAndEvalCount: parseAndEvalCount,
                                            beginStackFrameCount: stackFrameCount);

                                    pushStackFrame(newFrame);

                                    continue;
                                }

                            case Precompiled.PrecompiledResult.StepwiseSpecialization specialization:
                                {
                                    var newFrame =
                                        new StackFrame(
                                            ExpressionValue: expressionValue,
                                            Expression: parseOk.Value,
                                            Instructions: null,
                                            EnvironmentValue: environmentValue,
                                            InstructionsResultValues: null,
                                            BeginInstructionCount: instructionCount,
                                            BeginParseAndEvalCount: parseAndEvalCount,
                                            BeginStackFrameCount: stackFrameCount,
                                            Specialization: specialization.Stepwise);

                                    pushStackFrame(newFrame);

                                    continue;
                                }

                            default:
                                throw new Exception("Unexpected return type from precompiled: " + precompiledResult.GetType().FullName);
                        }
                    }

                    {
                        var newFrame =
                            StackFrameFromExpression(
                                expressionValue: expressionValue,
                                parseOk.Value,
                                environmentValue,
                                beginInstructionCount: instructionCount,
                                beginParseAndEvalCount: parseAndEvalCount,
                                beginStackFrameCount: stackFrameCount);

                        pushStackFrame(newFrame);

                        continue;
                    }
                }

                if (evalInstr.Expression is Expression.Conditional conditionalExpr)
                {
                    var conditionValue =
                        EvaluateExpressionDefaultLessStack(
                            conditionalExpr.condition,
                            currentFrame.EnvironmentValue,
                            stackPrevValues: stackPrevValues);

                    var expressionToContinueWith =
                        conditionValue == PineVMValues.TrueValue
                        ?
                        conditionalExpr.trueBranch
                        :
                        conditionValue == PineVMValues.FalseValue
                        ?
                        conditionalExpr.falseBranch
                        :
                        null;

                    if (expressionToContinueWith is not null)
                    {
                        if (ExpressionShouldGetNewStackFrame(expressionToContinueWith))
                        {
                            pushStackFrame(
                                StackFrameFromExpression(
                                    expressionValue: null,
                                    expressionToContinueWith,
                                    currentFrame.EnvironmentValue,
                                    beginInstructionCount: instructionCount,
                                    beginParseAndEvalCount: parseAndEvalCount,
                                    beginStackFrameCount: stackFrameCount));

                            continue;
                        }

                        var evalBranchResult =
                            EvaluateExpressionDefaultLessStack(
                                expressionToContinueWith,
                                currentFrame.EnvironmentValue,
                                stackPrevValues: stackPrevValues);

                        currentFrame.PushInstructionResult(evalBranchResult); ;

                        continue;
                    }
                    else
                    {
                        currentFrame.PushInstructionResult(PineValue.EmptyList);

                        continue;
                    }
                }

                var evalResult =
                    EvaluateExpressionDefaultLessStack(
                        evalInstr.Expression,
                        currentFrame.EnvironmentValue,
                        stackPrevValues: stackPrevValues);

                currentFrame.PushInstructionResult(evalResult);

                continue;
            }

            if (currentInstruction is StackInstruction.JumpInstruction jumpInstruction)
            {
                currentFrame.InstructionPointer += jumpInstruction.Offset;
                continue;
            }

            if (currentInstruction is StackInstruction.ConditionalJumpInstruction conditionalStatement)
            {
                var conditionValue = currentFrame.LastEvalResult();

                currentFrame.InstructionPointer++;

                if (conditionValue == PineVMValues.FalseValue)
                {
                    continue;
                }

                if (conditionValue == PineVMValues.TrueValue)
                {
                    currentFrame.InstructionPointer += conditionalStatement.TrueBranchOffset;
                    continue;
                }

                currentFrame.PushInstructionResult(PineValue.EmptyList);
                currentFrame.InstructionPointer += conditionalStatement.InvalidBranchOffset;

                continue;
            }

            return "Unexpected instruction type: " + currentInstruction.GetType().FullName;
        }
    }

    private static bool ExpressionShouldGetNewStackFrame(Expression expression)
    {
        if (expression is Expression.Literal)
            return false;

        if (expression is Expression.Environment)
            return false;

        if (expression is Expression.ParseAndEval)
            return true;

        if (expression is Expression.KernelApplication kernelApp)
        {
            return ExpressionShouldGetNewStackFrame(kernelApp.input);
        }

        if (expression is Expression.Conditional conditional)
        {
            return
                ExpressionShouldGetNewStackFrame(conditional.condition) ||
                ExpressionShouldGetNewStackFrame(conditional.trueBranch) ||
                ExpressionShouldGetNewStackFrame(conditional.falseBranch);
        }

        if (expression is Expression.List list)
        {
            for (var i = 0; i < list.items.Count; i++)
            {
                if (ExpressionShouldGetNewStackFrame(list.items[i]))
                {
                    return true;
                }
            }

            return false;
        }

        if (expression is Expression.StringTag stringTag)
            return ExpressionShouldGetNewStackFrame(stringTag.tagged);

        if (expression is Expression.StackReferenceExpression)
            return false;

        if (expression is Expression.KernelApplications_Skip_Head_Path)
            return false;

        if (expression is Expression.KernelApplication_Equal_Two)
            return false;

        throw new NotImplementedException(
            "Unexpected shape of expression: " + expression.GetType().FullName);
    }

    public PineValue EvaluateExpressionDefaultLessStack(
        Expression expression,
        PineValue environment,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        if (expression is Expression.Literal literalExpression)
            return literalExpression.Value;

        if (expression is Expression.List listExpression)
        {
            return EvaluateListExpression(listExpression, environment, stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.ParseAndEval applicationExpression)
        {
            return
                EvaluateParseAndEvalExpression(
                    applicationExpression,
                    environment,
                    stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.KernelApplication kernelApplicationExpression)
        {
            return
                EvaluateKernelApplicationExpression(
                    environment,
                    kernelApplicationExpression,
                    stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.Conditional conditionalExpression)
        {
            return EvaluateConditionalExpression(
                environment,
                conditionalExpression,
                stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.Environment)
        {
            return environment;
        }

        if (expression is Expression.StringTag stringTagExpression)
        {
            return EvaluateExpressionDefaultLessStack(
                stringTagExpression.tagged,
                environment,
                stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.StackReferenceExpression stackRef)
        {
            var index = stackPrevValues.Length + stackRef.offset;

            if (index < 0 || index >= stackPrevValues.Length)
            {
                throw new InvalidExpressionException(
                    "Invalid stack reference: offset " + stackRef.offset +
                    " from " + stackPrevValues.Length +
                    " results in index " + index);
            }

            var content = stackPrevValues.Span[index];

            if (content is null)
            {
                throw new InvalidExpressionException(
                    "Null value in stack reference: offset " + stackRef.offset +
                    " from " + stackPrevValues.Length +
                    " results in index " + index);
            }

            return content;
        }

        if (expression is Expression.KernelApplications_Skip_Head_Path kernelApplicationsSkipListHead)
        {
            return
                EvaluateKernelApplications_Skip_ListHead_Expression(
                    environment: environment,
                    kernelApplicationsSkipListHead,
                    stackPrevValues: stackPrevValues);
        }

        if (expression is Expression.KernelApplication_Equal_Two kernelApplicationEqualTwo)
        {
            var leftValue = EvaluateExpressionDefaultLessStack(
                kernelApplicationEqualTwo.left,
                environment,
                stackPrevValues: stackPrevValues);

            var rightValue = EvaluateExpressionDefaultLessStack(
                kernelApplicationEqualTwo.right,
                environment,
                stackPrevValues: stackPrevValues);

            return KernelFunction.equal(leftValue, rightValue);
        }

        throw new NotImplementedException(
            "Unexpected shape of expression: " + expression.GetType().FullName);
    }

    public PineValue EvaluateListExpression(
        Expression.List listExpression,
        PineValue environment,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        var listItems = new List<PineValue>(listExpression.items.Count);

        for (var i = 0; i < listExpression.items.Count; i++)
        {
            var item = listExpression.items[i];

            var itemResult = EvaluateExpressionDefaultLessStack(
                item,
                environment,
                stackPrevValues: stackPrevValues);
            listItems.Add(itemResult);
        }

        return PineValue.List(listItems);
    }

    public PineValue EvaluateParseAndEvalExpression(
        Expression.ParseAndEval parseAndEval,
        PineValue environment,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        var environmentValue =
            EvaluateExpressionDefaultLessStack(
                parseAndEval.environment,
                environment,
                stackPrevValues: stackPrevValues);

        var expressionValue =
            EvaluateExpressionDefaultLessStack(
                parseAndEval.encoded,
                environment,
                stackPrevValues: stackPrevValues);


        if (InvocationCachedResultOrOverride(
            expressionValue: expressionValue,
            environmentValue: environmentValue) is { } fromCacheOrDelegate)
        {
            return fromCacheOrDelegate;
        }

        var parseResult = parseCache.ParseExpression(expressionValue);

        if (parseResult is Result<string, Expression>.Err parseErr)
        {
            var message =
                "Failed to parse expression from value: " + parseErr.Value +
                " - expressionValue is " + DescribeValueForErrorMessage(expressionValue) +
                " - environmentValue is " + DescribeValueForErrorMessage(expressionValue);

            throw new ParseExpressionException(message);
        }

        if (parseResult is not Result<string, Expression>.Ok parseOk)
        {
            throw new NotImplementedException("Unexpected result type: " + parseResult.GetType().FullName);
        }

        if (environmentValue is PineValue.ListValue list)
        {
            FunctionApplicationMaxEnvSize =
            FunctionApplicationMaxEnvSize < list.Elements.Count ? list.Elements.Count : FunctionApplicationMaxEnvSize;
        }

        return
            EvaluateExpressionDefaultLessStack(
                environment: environmentValue,
                expression: parseOk.Value,
                stackPrevValues: ReadOnlyMemory<PineValue>.Empty);
    }

    private PineValue? InvocationCachedResultOrOverride(
        PineValue expressionValue,
        PineValue environmentValue)
    {
        if (EvalCache is { } evalCache)
        {
            var cacheKey = new EvalCacheEntryKey(ExprValue: expressionValue, EnvValue: environmentValue);

            if (evalCache.TryGetValue(cacheKey, out var fromCache))
            {
                return fromCache;
            }
        }

        if (overrideInvocations?.TryGetValue(expressionValue, out var overrideValue) ?? false)
        {
            var result =
            overrideValue(
                (expr, envVal) => EvaluateExpressionDefaultLessStack(
                    expr,
                    envVal,
                    stackPrevValues: ReadOnlyMemory<PineValue>.Empty),
                environmentValue);

            return
                result
                .Extract(err => throw new Exception(err));
        }

        return null;
    }

    public static string DescribeValueForErrorMessage(PineValue pineValue) =>
        PineValueAsString.StringFromValue(pineValue)
        .Unpack(fromErr: _ => "not a string", fromOk: asString => "string \'" + asString + "\'");

    public PineValue EvaluateKernelApplicationExpression(
        PineValue environment,
        Expression.KernelApplication application,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        if (application.function is nameof(KernelFunction.head) &&
            application.input is Expression.KernelApplication innerKernelApplication)
        {
            if (innerKernelApplication.function == nameof(KernelFunction.skip) &&
                innerKernelApplication.input is Expression.List skipListExpr &&
                skipListExpr.items.Count is 2)
            {
                var skipValue =
                    EvaluateExpressionDefaultLessStack(
                        skipListExpr.items[0],
                        environment,
                        stackPrevValues);

                if (KernelFunction.SignedIntegerFromValueRelaxed(skipValue) is { } skipCount)
                {
                    if (EvaluateExpressionDefaultLessStack(
                        skipListExpr.items[1],
                        environment,
                        stackPrevValues) is PineValue.ListValue list)
                    {
                        if (list.Elements.Count < 1 || list.Elements.Count <= skipCount)
                        {
                            return PineValue.EmptyList;
                        }

                        return list.Elements[skipCount < 0 ? 0 : (int)skipCount];
                    }
                    else
                    {
                        return PineValue.EmptyList;
                    }
                }
            }
        }

        return EvaluateKernelApplicationExpressionGeneric(environment, application, stackPrevValues);
    }

    public PineValue EvaluateKernelApplicationExpressionGeneric(
        PineValue environment,
        Expression.KernelApplication application,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        var inputValue = EvaluateExpressionDefaultLessStack(application.input, environment, stackPrevValues);

        return
            EvaluateKernelApplicationGeneric(
                function: application.function,
                inputValue: inputValue);
    }

    public static PineValue EvaluateKernelApplicationGeneric(
        string function,
        PineValue inputValue)
    {
        return function switch
        {
            nameof(KernelFunction.equal) =>
            KernelFunction.equal(inputValue),

            nameof(KernelFunction.length) =>
            KernelFunction.length(inputValue),

            nameof(KernelFunction.head) =>
            KernelFunction.head(inputValue),

            nameof(KernelFunction.skip) =>
            KernelFunction.skip(inputValue),

            nameof(KernelFunction.take) =>
            KernelFunction.take(inputValue),

            nameof(KernelFunction.concat) =>
            KernelFunction.concat(inputValue),

            nameof(KernelFunction.reverse) =>
            KernelFunction.reverse(inputValue),

            nameof(KernelFunction.negate) =>
            KernelFunction.negate(inputValue),

            nameof(KernelFunction.add_int) =>
            KernelFunction.add_int(inputValue),

            nameof(KernelFunction.mul_int) =>
            KernelFunction.mul_int(inputValue),

            nameof(KernelFunction.is_sorted_ascending_int) =>
            KernelFunction.is_sorted_ascending_int(inputValue),

            _ =>
            throw new ParseExpressionException(
                "Did not find kernel function '" + function + "'")
        };
    }

    public PineValue EvaluateKernelApplications_Skip_ListHead_Expression(
        PineValue environment,
        Expression.KernelApplications_Skip_Head_Path application,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        var argumentValue =
            EvaluateExpressionDefaultLessStack(
                application.Argument,
                environment,
                stackPrevValues: stackPrevValues);

        return ValueFromPathInValueOrEmptyList(argumentValue, application.SkipCounts.Span);
    }

    public static PineValue ValueFromPathInValueOrEmptyList(
        PineValue environment,
        ReadOnlySpan<int> path)
    {
        if (path.Length is 0)
            return environment;

        if (environment is not PineValue.ListValue listValue)
            return PineValue.EmptyList;

        var skipCount = path[0];

        if (path[0] >= listValue.Elements.Count)
            return PineValue.EmptyList;

        return
            ValueFromPathInValueOrEmptyList(
                listValue.Elements[skipCount < 0 ? 0 : skipCount],
                path[1..]);
    }

    public PineValue EvaluateConditionalExpression(
        PineValue environment,
        Expression.Conditional conditional,
        ReadOnlyMemory<PineValue> stackPrevValues)
    {
        var conditionValue =
            EvaluateExpressionDefaultLessStack(
                conditional.condition,
                environment,
                stackPrevValues: stackPrevValues);

        return
            conditionValue == PineVMValues.TrueValue
            ?
            EvaluateExpressionDefaultLessStack(
                conditional.trueBranch,
                environment,
                stackPrevValues: stackPrevValues)
            :
            conditionValue == PineVMValues.FalseValue
            ?
            EvaluateExpressionDefaultLessStack(
                conditional.falseBranch,
                environment,
                stackPrevValues: stackPrevValues)
            :
            PineValue.EmptyList;
    }
}
