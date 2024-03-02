using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pine.PineVM;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Pine.CompilePineToDotNet;

public record SyntaxContainerConfig(
    string containerTypeName,
    string dictionaryMemberName);

public record CompileCSharpClassResult(
    SyntaxContainerConfig SyntaxContainerConfig,
    ClassDeclarationSyntax ClassDeclarationSyntax,
    IReadOnlyList<UsingDirectiveSyntax> UsingDirectives);

public record GenerateCSharpFileResult(
    SyntaxContainerConfig SyntaxContainerConfig,
    CompilationUnitSyntax CompilationUnitSyntax,
    string FileText);

public partial class CompileToCSharp
{
    static private readonly CompilerMutableCache compilerCache = new();

    public static GenerateCSharpFileResult GenerateCSharpFile(
        CompileCSharpClassResult compileCSharpClassResult,
        IReadOnlyList<MemberDeclarationSyntax>? additionalMembers = null)
    {
        var compilationUnitSyntax =
            SyntaxFactory.CompilationUnit()
                .WithUsings(new SyntaxList<UsingDirectiveSyntax>(compileCSharpClassResult.UsingDirectives))
                .WithMembers(
                    SyntaxFactory.List(
                        [.. (additionalMembers ?? []), compileCSharpClassResult.ClassDeclarationSyntax]));

        var formattedNode =
            FormatCSharpSyntaxRewriter.FormatSyntaxTree(compilationUnitSyntax.NormalizeWhitespace(eol: "\n"));

        return
            new GenerateCSharpFileResult(
                SyntaxContainerConfig: compileCSharpClassResult.SyntaxContainerConfig,
                formattedNode,
                FileText: formattedNode.ToFullString());
    }

    record CompiledExpressionFunction(
        CompiledExpressionId Identifier,
        BlockSyntax BlockSyntax,
        CompiledExpressionDependencies Dependencies);

    public static Result<string, CompileCSharpClassResult> CompileExpressionsToCSharpClass(
        IReadOnlyCollection<ExpressionUsageAnalysis> expressions,
        SyntaxContainerConfig containerConfig)
    {
        const string argumentEnvironmentName = "pine_environment";

        const string argumentEvalGenericName = "eval_generic";

        var parametersSyntaxes = new[]
        {
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(argumentEvalGenericName))
                .WithType(EvalExprDelegateTypeSyntax),

            SyntaxFactory.Parameter(SyntaxFactory.Identifier(argumentEnvironmentName))
                .WithType(SyntaxFactory.IdentifierName("PineValue")),
        };

        var usingDirectivesTypes = new[]
        {
            typeof(PineValue),
            typeof(ImmutableArray),
            typeof(IReadOnlyDictionary<,>),
            typeof(Func<,>),
            typeof(Enumerable)
        };

        var usingDirectives =
            usingDirectivesTypes
            .Select(t => t.Namespace)
            .WhereNotNull()
            .Distinct()
            .Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName(ns)))
            .ToImmutableList();

        MethodDeclarationSyntax memberDeclarationSyntaxForExpression(
            string declarationName,
            BlockSyntax blockSyntax)
        {
            return
                SyntaxFactory.MethodDeclaration(
                        returnType:
                        SyntaxFactory.GenericName(
                                SyntaxFactory.Identifier("Result"))
                            .WithTypeArgumentList(
                                SyntaxFactory.TypeArgumentList(
                                    SyntaxFactory.SeparatedList<TypeSyntax>(
                                        new SyntaxNodeOrToken[]
                                        {
                                            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                                            SyntaxFactory.Token(SyntaxKind.CommaToken),
                                            SyntaxFactory.IdentifierName("PineValue")
                                        }))),
                        SyntaxFactory.Identifier(declarationName))
                    .WithModifiers(
                        SyntaxFactory.TokenList(
                            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                            SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithParameterList(
                        SyntaxFactory.ParameterList(
                            SyntaxFactory.SeparatedList(parametersSyntaxes)))
                    .WithBody(blockSyntax);
        }

        static Result<string, IReadOnlyDictionary<ExpressionUsageAnalysis, CompiledExpressionFunction>> CompileExpressionFunctions(
            IReadOnlyCollection<ExpressionUsageAnalysis> expressionsUsages)
        {
            var dictionary = new Dictionary<ExpressionUsageAnalysis, CompiledExpressionFunction>();

            var expressions =
                expressionsUsages
                .Select(eu => eu.Expression)
                .Distinct()
                .ToImmutableArray();

            var queue = new Queue<Expression>(expressions);

            while (queue.Any())
            {
                var expression = queue.Dequeue();

                var onlyExprDerivedId =
                    CompiledExpressionId(expression)
                    .Extract(err => throw new Exception(err));

                var expressionUsages =
                    expressionsUsages
                    .Where(eu => eu.Expression == expression)
                    // Ensure we also have an entry for the general case, not just the constrained environments.
                    .Append(new ExpressionUsageAnalysis(expression, null))
                    .Distinct()
                    .ToImmutableArray();

                var supportedConstrainedEnvironments =
                    expressionsUsages
                    .Where(eu => eu.Expression == expression)
                    .Select(eu => eu.EnvId)
                    .WhereNotNull()
                    .ToImmutableArray();

                foreach (var expressionUsage in expressionUsages)
                {
                    if (dictionary.ContainsKey(expressionUsage))
                        continue;

                    var withEnvDerivedId =
                    CompiledExpressionId(expressionUsage.Expression)
                    .Extract(err => throw new Exception(err));

                    var result =
                        compilerCache.CompileToCSharpFunctionBlockSyntax(
                            expressionUsage,
                            branchesConstrainedEnvIds: supportedConstrainedEnvironments,
                            new FunctionCompilationEnvironment(
                                ArgumentEnvironmentName: argumentEnvironmentName,
                                ArgumentEvalGenericName: argumentEvalGenericName))
                            .MapError(err => "Failed to compile expression " + withEnvDerivedId.ExpressionHashBase16[..10] + ": " + err)
                            .Map(ok =>
                                new CompiledExpressionFunction(
                                    withEnvDerivedId,
                                    ok.blockSyntax,
                                    ok.dependencies));

                    if (result is Result<string, CompiledExpressionFunction>.Ok ok)
                    {
                        dictionary.Add(expressionUsage, ok.Value);

                        foreach (var item in ok.Value.Dependencies.ExpressionFunctions)
                            queue.Enqueue(item.Key);
                    }
                    else
                    {
                        return
                            result
                            .MapError(err => "Failed to compile expression " + withEnvDerivedId + ": " + err)
                            .Map(_ => (IReadOnlyDictionary<ExpressionUsageAnalysis, CompiledExpressionFunction>)
                            ImmutableDictionary<ExpressionUsageAnalysis, CompiledExpressionFunction>.Empty);
                    }
                }
            }

            return
                Result<string, IReadOnlyDictionary<ExpressionUsageAnalysis, CompiledExpressionFunction>>.ok(dictionary);
        }

        return
            CompileExpressionFunctions(expressions)
            .AndThen(compiledExpressionsBeforeOrdering =>
            {
                var compiledExpressions =
                compiledExpressionsBeforeOrdering.OrderBy(ce => ce.Value.Identifier.ExpressionHashBase16).ToImmutableArray();

                var aggregateDependencies =
                    CompiledExpressionDependencies.Union(compiledExpressions.Select(er => er.Value.Dependencies));

                var aggregateValueDependencies =
                    aggregateDependencies.Values
                    .Union(compiledExpressions.Select(forExprFunction => forExprFunction.Value.Identifier.ExpressionValue))
                    .Union(aggregateDependencies.Expressions.SelectMany(e => EnumerateAllLiterals(e.expression)));

                var usedValues = new HashSet<PineValue>();

                void registerValueUsagesRecursive(PineValue pineValue)
                {
                    if (usedValues.Contains(pineValue))
                        return;

                    usedValues.Add(pineValue);

                    if (pineValue is not PineValue.ListValue list)
                        return;

                    foreach (var i in list.Elements)
                        registerValueUsagesRecursive(i);
                }

                foreach (var item in aggregateValueDependencies)
                {
                    registerValueUsagesRecursive(item);
                }

                var valuesToDeclare =
                CSharpDeclarationOrder.OrderValuesForDeclaration(aggregateValueDependencies.Concat(usedValues).Distinct())
                .ToImmutableList();

                ExpressionSyntax? specialSyntaxForPineValue(PineValue pineValue) =>
                    valuesToDeclare.Contains(pineValue)
                        ? SyntaxFactory.IdentifierName(DeclarationNameForValue(pineValue))
                        : null;

                ((string memberName, TypeSyntax typeSyntax, ExpressionSyntax memberDeclaration) commonProps, ValueSyntaxKind syntaxKind)
                    memberDeclarationForValue(PineValue pineValue)
                {
                    var valueExpression = CompileToCSharpLiteralExpression(pineValue, specialSyntaxForPineValue);

                    var memberName = DeclarationNameForValue(pineValue);

                    return
                        ((memberName,
                            SyntaxFactory.IdentifierName("PineValue"),
                            valueExpression.exprSyntax),
                            valueExpression.syntaxKind);
                }

                (string memberName, TypeSyntax typeSyntax, ExpressionSyntax memberDeclaration)
                    memberDeclarationForExpression((string hash, Expression expression) hashAndExpr)
                {
                    var expressionExpression =
                        EncodePineExpressionAsCSharpExpression(hashAndExpr.expression, specialSyntaxForPineValue)
                        .Extract(err => throw new Exception("Failed to encode expression: " + err));

                    var memberName = MemberNameForExpression(hashAndExpr.hash[..10]);

                    return
                        (memberName,
                            SyntaxFactory.QualifiedName(
                                SyntaxFactory.QualifiedName(
                                    SyntaxFactory.IdentifierName("Pine"),
                                    SyntaxFactory.IdentifierName("PineVM")),
                                SyntaxFactory.IdentifierName("Expression")),
                            expressionExpression);
                }

                var valuesStaticMembers =
                    valuesToDeclare
                    .Select(valueToInclude => (valueToInclude, decl: memberDeclarationForValue(valueToInclude)))
                    .OrderBy(valueAndMember => valueAndMember.decl.syntaxKind, new CSharpDeclarationOrder.ValueSyntaxKindDeclarationOrder())
                    .ThenBy(valueAndMember => valueAndMember.valueToInclude, new CSharpDeclarationOrder.ValueDeclarationOrder())
                    .Select(valueAndMember => valueAndMember.decl.commonProps)
                    .ToImmutableList();

                var expressionStaticMembers =
                    aggregateDependencies.Expressions
                    .Select(memberDeclarationForExpression)
                    .DistinctBy(member => member.memberName)
                    .OrderBy(member => member.memberName)
                    .ToImmutableList();

                var dictionaryKeyTypeSyntax =
                CompileTypeSyntax.TypeSyntaxFromType(
                    typeof(PineValue),
                    usingDirectives);

                var dictionaryValueTypeSyntax =
                CompileTypeSyntax.TypeSyntaxFromType(
                    typeof(Func<PineVM.PineVM.EvalExprDelegate, PineValue, Result<string, PineValue>>),
                    usingDirectives);

                var dictionaryMemberType =
                CompileTypeSyntax.TypeSyntaxFromType(
                    typeof(IReadOnlyDictionary<PineValue, Func<PineVM.PineVM.EvalExprDelegate, PineValue, Result<string, PineValue>>>),
                    usingDirectives);

                var dictionaryEntries =
                    compiledExpressions
                    /*
                     * Dictionary entries only for the general case, not for the constrained environments.
                     * */
                    .DistinctBy(ce => ce.Key.Expression)
                    .Select(compiledExpression =>
                    {
                        var declarationName =
                        MemberNameForCompiledExpressionFunction(
                            compiledExpression.Value.Identifier,
                            constrainedEnv: null);

                        return
                            (SyntaxFactory.IdentifierName(DeclarationNameForValue(compiledExpression.Value.Identifier.ExpressionValue)),
                            SyntaxFactory.IdentifierName(declarationName));
                    })
                    .ToImmutableList();

                var dictionaryExpression =
                CompileDictionarySyntax.ImmutableDictionaryExpressionSyntax(
                    keyTypeSyntax: dictionaryKeyTypeSyntax,
                    valueTypeSyntax: dictionaryValueTypeSyntax,
                    dictionaryEntries: dictionaryEntries);

                var dictionaryMemberDeclaration =
                    SyntaxFactory.MethodDeclaration(
                        dictionaryMemberType,
                        identifier: containerConfig.dictionaryMemberName)
                        .WithModifiers(
                            SyntaxFactory.TokenList(
                                SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                        .WithExpressionBody(
                            SyntaxFactory.ArrowExpressionClause(dictionaryExpression))
                        .WithSemicolonToken(
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                var staticReadonlyFieldMembers = new[]
                {
                    (memberName: "value_true",
                    typeSyntax:
                    (TypeSyntax)SyntaxFactory.IdentifierName("PineValue"),
                    (ExpressionSyntax)SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("Pine"),
                                SyntaxFactory.IdentifierName("PineVM")),
                            SyntaxFactory.IdentifierName("PineVM")),
                        SyntaxFactory.IdentifierName("TrueValue"))),

                    ("value_false",
                    SyntaxFactory.IdentifierName("PineValue"),
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("Pine"),
                                SyntaxFactory.IdentifierName("PineVM")),
                            SyntaxFactory.IdentifierName("PineVM")),
                        SyntaxFactory.IdentifierName("FalseValue"))),
                }
                .Concat(valuesStaticMembers)
                .Concat(expressionStaticMembers)
                .ToImmutableList();

                var staticFieldsDeclarations =
                    staticReadonlyFieldMembers
                    .Select(member =>
                    SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(member.typeSyntax)
                        .WithVariables(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(member.memberName))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(member.Item3)))))
                    .WithModifiers(
                        SyntaxFactory.TokenList(
                            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                            SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                            SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)))
                    )
                    .ToImmutableList();

                var compiledExpressionsMemberDeclarations =
                compiledExpressions
                .Select(compiledExpression =>
                memberDeclarationSyntaxForExpression(
                    declarationName: MemberNameForCompiledExpressionFunction(
                        compiledExpression.Value.Identifier,
                        compiledExpression.Key.EnvId),
                    blockSyntax: compiledExpression.Value.BlockSyntax))
                .OrderBy(member => member.Identifier.ValueText)
                .ToImmutableList();

                return Result<string, CompileCSharpClassResult>.ok(
                    new CompileCSharpClassResult(
                        SyntaxContainerConfig: containerConfig,
                        ClassDeclarationSyntax:
                        SyntaxFactory.ClassDeclaration(containerConfig.containerTypeName)
                            .WithMembers(
                                SyntaxFactory.List(
                                    [dictionaryMemberDeclaration
                                    ,
                                        .. compiledExpressionsMemberDeclarations.Cast<MemberDeclarationSyntax>()
                                        , PineCSharpSyntaxFactory.ValueMatchesPathsPatternDeclaration
                                        , PineCSharpSyntaxFactory.ValueFromPathInValueDeclaration
                                    ,
                                        .. staticFieldsDeclarations])),
                        UsingDirectives: usingDirectives));
            });
    }

    private static QualifiedNameSyntax EvalExprDelegateTypeSyntax =>
        SyntaxFactory.QualifiedName(
            PineVmClassQualifiedNameSyntax,
            SyntaxFactory.IdentifierName(nameof(PineVM.PineVM.EvalExprDelegate)));

    private static QualifiedNameSyntax PineVmClassQualifiedNameSyntax =>
        SyntaxFactory.QualifiedName(
            SyntaxFactory.QualifiedName(
                SyntaxFactory.IdentifierName("Pine"),
                SyntaxFactory.IdentifierName("PineVM")),
            SyntaxFactory.IdentifierName("PineVM"));

    public static Result<string, (BlockSyntax blockSyntax, CompiledExpressionDependencies dependencies)>
        CompileToCSharpFunctionBlockSyntax(
            Expression expression,
            EnvConstraintId? constrainedEnvId,
            IReadOnlyList<EnvConstraintId> branchesEnvIds,
            FunctionCompilationEnvironment compilationEnv)
    {
        if (constrainedEnvId is { } envId)
        {
            return
                CompileToCSharpGeneralFunctionBlockSyntax(
                    expression,
                    branchesEnvIds: [],
                    compilationEnv,
                    envConstraint: constrainedEnvId);
        }


        return
            CompileToCSharpGeneralFunctionBlockSyntax(
                expression,
                branchesEnvIds: branchesEnvIds,
                compilationEnv,
                envConstraint: constrainedEnvId);
    }


    public static Result<string, (BlockSyntax blockSyntax, CompiledExpressionDependencies dependencies)>
        CompileToCSharpGeneralFunctionBlockSyntax(
            Expression expression,
            IReadOnlyList<EnvConstraintId> branchesEnvIds,
            FunctionCompilationEnvironment compilationEnv,
            EnvConstraintId? envConstraint) =>
        CompileToCSharpExpression(
            expression,
            new ExpressionCompilationEnvironment(
                FunctionEnvironment: compilationEnv,
                LetBindings: ImmutableDictionary<Expression, LetBinding>.Empty,
                ParentEnvironment: null,
                EnvConstraint: envConstraint),
            createLetBindingsForCse: true)
            .Map(exprWithDependencies =>
            {
                var availableLetBindings =
                exprWithDependencies.EnumerateLetBindingsTransitive();

                var returnExpression = exprWithDependencies.AsCsWithTypeResult();

                var variableDeclarations =
                CompiledExpression.VariableDeclarationsForLetBindings(
                    availableLetBindings,
                    usagesSyntaxes: [returnExpression],
                    excludeBinding: null);

                var generalExprFuncName =
                CompiledExpressionId(expression)
                .Extract(err => throw new Exception(err));

                var branchesForSpecializedRepr =
                branchesEnvIds
                .Select(envId =>
                        PineCSharpSyntaxFactory.BranchForEnvId(
                        new ExpressionUsageAnalysis(expression, envId),
                        compilationEnv: compilationEnv,
                        prependStatments: []))
                    .ToImmutableList();

                var valueDepsForBranchStatements =
                CompiledExpressionDependencies.Empty
                with
                {
                    Values =
                    branchesEnvIds.SelectMany(envId => envId.ParsedEnvItems.Select(item => item.Value))
                    .ToImmutableHashSet()
                };

                var combinedDependencies =
                    CompiledExpressionDependencies.Union(
                        [..variableDeclarations
                        .Select(b => b.letBinding.Expression.DependenciesIncludingLetBindings()),
                        exprWithDependencies.DependenciesIncludingLetBindings(),
                        valueDepsForBranchStatements]);

                return
                (SyntaxFactory.Block(
                    (StatementSyntax[])
                    ([..branchesForSpecializedRepr
                    , ..variableDeclarations.Select(b => b.declarationSyntax),
                        SyntaxFactory.ReturnStatement(returnExpression)])),
                        combinedDependencies);
            });

    public static ExpressionSyntax WrapExpressionInPineValueResultOk(ExpressionSyntax expression) =>
        SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier(nameof(Result<int, int>)))
                    .WithTypeArgumentList(
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SeparatedList<TypeSyntax>(
                                (IEnumerable<SyntaxNodeOrToken>)
                                [
                                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                                    SyntaxFactory.Token(SyntaxKind.CommaToken),
                                    SyntaxFactory.IdentifierName(nameof(PineValue))
                                ]))),
                SyntaxFactory.IdentifierName(nameof(Result<int, int>.ok))))
        .WithArgumentList(
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(expression))));

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression expression,
        ExpressionCompilationEnvironment parentEnvironment,
        bool createLetBindingsForCse)
    {
        var letBindingsAvailableFromParent =
            parentEnvironment.EnumerateSelfAndAncestorsLetBindingsTransitive();

        if (letBindingsAvailableFromParent.TryGetValue(expression, out var letBinding))
        {
            return
                Result<string, CompiledExpression>.ok(
                    new CompiledExpression(
                        Syntax: SyntaxFactory.IdentifierName(letBinding.DeclarationName),
                        IsTypeResult: letBinding.Expression.IsTypeResult,
                        LetBindings: CompiledExpression.NoLetBindings,
                        Dependencies: letBinding.Expression.Dependencies));
        }

        var letBindingsAvailableFromParentKeys =
            letBindingsAvailableFromParent.Keys.ToImmutableHashSet();

        ExpressionCompilationEnvironment DescendantEnvironmentFromNewLetBindings(
            IReadOnlyDictionary<Expression, LetBinding> newLetBindings) =>
            parentEnvironment
            with
            {
                LetBindings = newLetBindings,
                ParentEnvironment = parentEnvironment
            };

        var newLetBindingsExpressionsForCse =
            createLetBindingsForCse
            ?
            CollectForCommonSubexpressionElimination(
                expression,
                skipSubexpression: letBindingsAvailableFromParentKeys.Contains)
            :
            [];

        var newLetBindingsExpressions =
            newLetBindingsExpressionsForCse
            .Where(subexpr =>
            /*
             * 2024-03-01: Disable CSE for expressions that contain further calls.
             * The observation was that CSE for these sometimes caused infinite recursion and stack overflow.
             * The reason for the infinite recursion is that with the current implementation, the bindings
             * can end up too in too outer scope, when they should be contained in a branch that is not taken
             * when the recursion reaches the base case.
             * */
            !Expression.EnumerateSelfAndDescendants(subexpr).Any(sec => sec is Expression.ParseAndEvalExpression))
            .ToImmutableArray();

        var newLetBindings =
            CSharpDeclarationOrder.OrderExpressionsByContainment(newLetBindingsExpressions)
            .Aggregate(
                seed: CompiledExpression.NoLetBindings,
                func:
                (dict, subexpression) =>
                {
                    return
                    CompileToCSharpExpression(
                        subexpression,
                        DescendantEnvironmentFromNewLetBindings(dict),
                        createLetBindingsForCse: true)
                    .Unpack(
                        fromErr: _ => dict,
                        fromOk: compileOk =>
                        {
                            var subexpressionValue =
                            PineVM.PineVM.EncodeExpressionAsValue(subexpression)
                            .Extract(err => throw new Exception(err));

                            var expressionHash = CommonConversion.StringBase16(compilerCache.ComputeHash(subexpressionValue));

                            var declarationName = "bind_" + expressionHash[..10];

                            return dict.SetItem(
                                subexpression,
                                new LetBinding(
                                    declarationName,
                                    compileOk));
                        });
                });

        var descendantEnvironment = DescendantEnvironmentFromNewLetBindings(newLetBindings);

        return
            CompileToCSharpExpressionWithoutCSE(
                expression,
                descendantEnvironment)
            .Map(beforeNewBindings => beforeNewBindings.MergeBindings(newLetBindings));
    }

    public static ImmutableHashSet<Expression> CollectForCommonSubexpressionElimination(
        Expression expression,
        Func<Expression, bool> skipSubexpression)
    {
        var subexpressionUsages =
            CountExpressionUsage(
                expression,
                skipDescending: skipSubexpression);

        var commonSubexpressionsIncludingDescendants =
            subexpressionUsages
            .Where(kvp => 0 < kvp.Value.Unconditional && (1 < kvp.Value.Unconditional + kvp.Value.Conditional))
            .Select(kvp => kvp.Key)
            .Where(IncludeForCommonSubexpressionElimination)
            .Where(c => !skipSubexpression(c))
            .ToImmutableHashSet();

        var commonSubexpressions =
            commonSubexpressionsIncludingDescendants.Intersect(
                CountExpressionUsage(
                    expression,
                    skipDescending:
                    se => skipSubexpression(se) || commonSubexpressionsIncludingDescendants.Contains(se)).Keys);

        return commonSubexpressions;
    }

    public static bool IncludeForCommonSubexpressionElimination(Expression expression) =>
        expression switch
        {
            Expression.ParseAndEvalExpression => true,
            Expression.KernelApplicationExpression => true,
            Expression.ConditionalExpression => true,
            Expression.StringTagExpression => true,
            _ => false
        };

    private static Result<string, CompiledExpression> CompileToCSharpExpressionWithoutCSE(
        Expression expression,
        ExpressionCompilationEnvironment environment)
    {
        return
            expression switch
            {
                Expression.EnvironmentExpression =>
                Result<string, CompiledExpression>.ok(
                    CompiledExpression.WithTypePlainValue(
                        SyntaxFactory.IdentifierName(environment.FunctionEnvironment.ArgumentEnvironmentName))),

                Expression.ListExpression listExpr =>
                CompileToCSharpExpression(listExpr, environment),

                Expression.LiteralExpression literalExpr =>
                CompileToCSharpExpression(literalExpr),

                Expression.ConditionalExpression conditional =>
                CompileToCSharpExpression(conditional, environment),

                Expression.KernelApplicationExpression kernelApp =>
                CompileToCSharpExpression(kernelApp, environment),

                Expression.ParseAndEvalExpression parseAndEval =>
                CompileToCSharpExpression(parseAndEval, environment),

                Expression.StringTagExpression stringTagExpr =>
                CompileToCSharpExpression(stringTagExpr, environment),

                _ =>
                Result<string, CompiledExpression>.err(
                    "Unsupported syntax kind: " + expression.GetType().FullName)
            };
    }

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression.ListExpression listExpression,
        ExpressionCompilationEnvironment environment)
    {
        if (!listExpression.List.Any())
            return Result<string, CompiledExpression>.ok(
                CompiledExpression.WithTypePlainValue(PineCSharpSyntaxFactory.PineValueEmptyListSyntax));

        return
            listExpression.List.Select((itemExpression, itemIndex) =>
            CompileToCSharpExpression(
                itemExpression,
                environment,
                createLetBindingsForCse: false)
            .MapError(err => "Failed to translate list item " + itemIndex + ": " + err))
            .ListCombine()
            .Map(compiledItems =>
            {
                var aggregateSyntax =
                CompiledExpression.ListMapOrAndThen(
                    environment,
                    combine:
                    csharpItems =>
                    CompiledExpression.WithTypePlainValue(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("PineValue"),
                                SyntaxFactory.IdentifierName("List")))
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.CollectionExpression(
                                            SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                                                csharpItems.Select(SyntaxFactory.ExpressionElement)))))))),
                    compiledItems);

                return aggregateSyntax;
            });
    }

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression.KernelApplicationExpression kernelApplicationExpression,
        ExpressionCompilationEnvironment environment)
    {
        if (!KernelFunctionsInfo.Value.TryGetValue(kernelApplicationExpression.functionName,
              out var kernelFunctionInfo))
        {
            return
                Result<string, CompiledExpression>.err(
                    "Kernel function name " + kernelApplicationExpression.functionName + " does not match any of the " +
                    KernelFunctionsInfo.Value.Count + " known names: " +
                    string.Join(", ", KernelFunctionsInfo.Value.Keys));
        }

        return
            CompileKernelFunctionApplicationToCSharpExpression(
                kernelFunctionInfo,
                kernelApplicationExpression.argument,
                environment);
    }

    private static Result<string, CompiledExpression> CompileKernelFunctionApplicationToCSharpExpression(
        KernelFunctionInfo kernelFunctionInfo,
        Expression kernelApplicationArgumentExpression,
        ExpressionCompilationEnvironment environment)
    {
        var staticallyKnownArgumentsList =
            ParseKernelApplicationArgumentAsList(kernelApplicationArgumentExpression, environment)
            ?.Unpack(fromErr: err =>
            {
                Console.WriteLine("Failed to parse argument list: " + err);
                return null;
            },
            fromOk: ok => ok);

        static InvocationExpressionSyntax wrapInvocationInWithDefault(InvocationExpressionSyntax invocationExpressionSyntax)
        {
            return
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        invocationExpressionSyntax,
                        SyntaxFactory.IdentifierName("WithDefault")))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("PineValue"),
                                    SyntaxFactory.IdentifierName("EmptyList"))))));
        }

        if (kernelFunctionInfo.TryInline?.Invoke(kernelApplicationArgumentExpression, environment) is { } inlineNotNull)
        {
            return inlineNotNull;
        }

        if (staticallyKnownArgumentsList is not null)
        {
            foreach (var specializedImpl in kernelFunctionInfo.SpecializedImplementations)
            {
                if (specializedImpl.ParameterTypes.Count == staticallyKnownArgumentsList.Count)
                {
                    var argumentsResults =
                        specializedImpl.ParameterTypes
                            .Select((parameterType, parameterIndex) =>
                            {
                                if (!staticallyKnownArgumentsList[parameterIndex].ArgumentSyntaxFromParameterType
                                        .TryGetValue(parameterType, out var param))
                                    return Result<string, CompiledExpression>.err(
                                        "No transformation found for parameter type " + parameterType);

                                return Result<string, CompiledExpression>.ok(param);
                            });

                    if (argumentsResults.ListCombine() is
                        Result<string, IReadOnlyList<CompiledExpression>>.Ok specializedOk)
                    {
                        var aggregateDependencies =
                            CompiledExpressionDependencies.Union(specializedOk.Value.Select(p => p.Dependencies));

                        var aggregateLetBindings =
                            CompiledExpression.Union(specializedOk.Value.Select(c => c.LetBindings));

                        var expressionReturningPineValue =
                            CompiledExpression.ListMapOrAndThen(
                                environment,
                                argumentsCs =>
                                {
                                    var plainInvocationSyntax = specializedImpl.CompileInvocation(argumentsCs);

                                    return
                                    CompiledExpression.WithTypePlainValue(
                                        specializedImpl.ReturnType.IsInstanceOfResult ?
                                        wrapInvocationInWithDefault(plainInvocationSyntax)
                                        :
                                        plainInvocationSyntax,
                                        aggregateLetBindings,
                                        aggregateDependencies);
                                },
                                specializedOk.Value);

                        return
                            Result<string, CompiledExpression>.ok(expressionReturningPineValue);
                    }
                }
            }
        }

        return
            CompileToCSharpExpression(
                kernelApplicationArgumentExpression,
                environment,
                createLetBindingsForCse: false)
            .Map(compiledArgument =>
            compiledArgument.Map(environment, argumentCs => kernelFunctionInfo.CompileGenericInvocation(argumentCs))
            .MergeBindings(compiledArgument.LetBindings));
    }

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression.ConditionalExpression conditionalExpression,
        ExpressionCompilationEnvironment environment)
    {
        return
            CompileToCSharpExpression(
                conditionalExpression.condition,
                environment,
                createLetBindingsForCse: false)
            .MapError(err => "Failed to compile condition: " + err)
            .AndThen(compiledCondition =>
            CompileToCSharpExpression(
                conditionalExpression.ifTrue,
                environment,
                createLetBindingsForCse: true)
            .MapError(err => "Failed to compile branch if true: " + err)
            .AndThen(compiledIfTrue =>
            CompileToCSharpExpression(
                conditionalExpression.ifFalse,
                environment,
                createLetBindingsForCse: true)
            .MapError(err => "Failed to compile branch if false: " + err)
            .Map(compiledIfFalse =>
            {
                CompiledExpression continueWithConditionCs(ExpressionSyntax conditionCs)
                {
                    if (!(compiledIfTrue.IsTypeResult || compiledIfFalse.IsTypeResult))
                    {
                        return
                        CompiledExpression.WithTypePlainValue(SyntaxFactory.ConditionalExpression(
                            conditionCs,
                            compiledIfTrue.Syntax,
                            compiledIfFalse.Syntax));
                    }

                    return
                    CompiledExpression.WithTypeResult(
                        SyntaxFactory.ConditionalExpression(
                            conditionCs,
                            compiledIfTrue.AsCsWithTypeResult(),
                            compiledIfFalse.AsCsWithTypeResult()));
                }

                var aggregateLetBindings =
                CompiledExpression.Union(
                    [
                        compiledCondition.LetBindings,
                        compiledIfTrue.LetBindings,
                        compiledIfFalse.LetBindings
                    ]);

                var
                combinedExpr =
                compiledCondition
                .MapOrAndThen(
                    environment,
                    conditionCs =>
                    continueWithConditionCs(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.EqualsExpression,
                            SyntaxFactory.IdentifierName("value_true"),
                            conditionCs))
                    .MergeBindings(aggregateLetBindings)
                    .MergeDependencies(
                        compiledCondition.Dependencies
                        .Union(compiledIfTrue.Dependencies)
                        .Union(compiledIfFalse.Dependencies)));

                return combinedExpr;
            }
            )));
    }

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression.ParseAndEvalExpression parseAndEvalExpr,
        ExpressionCompilationEnvironment environment)
    {
        var parseAndEvalExprValue =
            PineVM.PineVM.EncodeExpressionAsValue(parseAndEvalExpr)
            .Extract(err => throw new Exception(err));

        var parseAndEvalExprHash =
            CommonConversion.StringBase16(compilerCache.ComputeHash(parseAndEvalExprValue));

        /*
         * 
         * 2024-02-17: Switch to older implementation of generic case from 2023,
         * to fix bug that caused generation of invalid C# code.
         * 
        CompiledExpression continueWithGenericCase()
        {
            var parseAndEvalExprExpressionId =
                CompiledExpressionId(parseAndEvalExpr.expression)
                .Extract(err => throw new Exception(err));

            var parseAndEvalExprEnvironmentId =
                CompiledExpressionId(parseAndEvalExpr.environment)
                .Extract(err => throw new Exception(err));

            var envResultExpr =
                InvocationExpressionForCurrentEnvironment(
                    environment.FunctionEnvironment,
                    parseAndEvalExprEnvironmentId);

            var exprResultExpr =
                InvocationExpressionForCurrentEnvironment(
                    environment.FunctionEnvironment,
                    parseAndEvalExprExpressionId);

            return
                envResultExpr
                .MapOrAndThen(
                    environment,
                    envResultExprCs =>
                    exprResultExpr
                    .MapOrAndThen(
                        environment,
                        exprResultExprCs =>
                        {
                            var parseAndEvalLiteralExpr =
                            NewConstructorOfExpressionVariant(
                                nameof(Expression.ParseAndEvalExpression),
                                NewConstructorOfExpressionVariant(
                                    nameof(Expression.LiteralExpression),
                                    exprResultExprCs),
                                NewConstructorOfExpressionVariant(
                                    nameof(Expression.LiteralExpression),
                                    envResultExprCs));

                            var invocationExpression =
                            SyntaxFactory.InvocationExpression(
                                SyntaxFactory.IdentifierName(environment.FunctionEnvironment.ArgumentEvalGenericName))
                            .WithArgumentList(
                                SyntaxFactory.ArgumentList(
                                    SyntaxFactory.SeparatedList<ArgumentSyntax>(
                                        new SyntaxNodeOrToken[]
                                        {
                                            SyntaxFactory.Argument(parseAndEvalLiteralExpr),
                                            SyntaxFactory.Token(SyntaxKind.CommaToken),
                                            SyntaxFactory.Argument(PineCSharpSyntaxFactory.PineValueEmptyListSyntax)
                                        })));

                            return
                            CompiledExpression.WithTypeResult(invocationExpression)
                                .MergeDependencies(
                                envResultExpr.Dependencies.Union(exprResultExpr.Dependencies)
                                .Union(
                                    CompiledExpressionDependencies.Empty
                                    with
                                    {
                                        ExpressionFunctions =
                                            ImmutableDictionary<Expression, CompiledExpressionId>.Empty
                                            .SetItem(parseAndEvalExpr.expression, parseAndEvalExprExpressionId)
                                            .SetItem(parseAndEvalExpr.environment, parseAndEvalExprEnvironmentId)
                                    }));
                        }));
        }
        */

        CompiledExpression continueWithGenericCase()
        {
            var invocationExpression =
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.IdentifierName(environment.FunctionEnvironment.ArgumentEvalGenericName))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList<ArgumentSyntax>(
                            new SyntaxNodeOrToken[]
                            {
                                SyntaxFactory.Argument(
                                    SyntaxFactory.IdentifierName(
                                        MemberNameForExpression(parseAndEvalExprHash))),
                                SyntaxFactory.Token(SyntaxKind.CommaToken),
                                SyntaxFactory.Argument(
                                    SyntaxFactory.IdentifierName(environment.FunctionEnvironment.ArgumentEnvironmentName))
                            })));

            return
                CompiledExpression.WithTypeResult(invocationExpression)
                .MergeDependencies(
                    CompiledExpressionDependencies.Empty
                    with
                    {
                        Expressions = ImmutableHashSet.Create((parseAndEvalExprHash, (Expression)parseAndEvalExpr)),
                    });
        }

        Result<string, CompiledExpression> continueForKnownExprValue(PineValue innerExpressionValue)
        {
            return
                compilerCache.ParseExpressionFromValue(innerExpressionValue)
                .MapError(err => "Failed to parse inner expression: " + err)
                .AndThen(innerExpression =>
                {
                    var innerExpressionId = CompiledExpressionId(innerExpressionValue);

                    return
                    CompileToCSharpExpression(
                        parseAndEvalExpr.environment,
                        environment,
                        createLetBindingsForCse: false)
                    .Map(compiledArgumentExpression =>
                    {
                        return
                        compiledArgumentExpression.MapOrAndThen(
                            environment,
                            argumentExprPlainValue =>
                        {
                            return
                            InvocationExpressionForCompiledExpressionFunction(
                                environment.FunctionEnvironment,
                                innerExpressionId,
                                argumentExprPlainValue)
                            .MergeDependencies(
                                compiledArgumentExpression.Dependencies.Union(
                                    CompiledExpressionDependencies.Empty
                                    with
                                    {
                                        ExpressionFunctions =
                                        ImmutableDictionary<Expression, CompiledExpressionId>.Empty
                                        .SetItem(innerExpression, innerExpressionId),
                                    }));
                        });
                    });
                });
        }

        if (Expression.IsIndependent(parseAndEvalExpr.expression))
        {
            return
                TryEvaluateExpressionIndependent(parseAndEvalExpr.expression)
                .MapError(err => "Failed evaluate inner as independent expression: " + err)
                .AndThen(continueForKnownExprValue);
        }

        var expressionPath = CodeAnalysis.TryParseExpressionAsIndexPathFromEnv(parseAndEvalExpr.expression);

        if (expressionPath is not null)
        {
            var exprValueFromEnvConstraint =
                environment.EnvConstraint?.TryGetValue(expressionPath);

            if (exprValueFromEnvConstraint is not null)
            {
                var exprValueFromEnvConstraintId = CompiledExpressionId(exprValueFromEnvConstraint);

                return
                    continueForKnownExprValue(exprValueFromEnvConstraint);
            }
        }

        return
            Result<string, CompiledExpression>.ok(continueWithGenericCase());
    }

    public static CompiledExpression InvocationExpressionForCurrentEnvironment(
        FunctionCompilationEnvironment environment,
        CompiledExpressionId invokedFunction) =>
        InvocationExpressionForCompiledExpressionFunction(
            environment,
            invokedFunction,
            SyntaxFactory.IdentifierName(environment.ArgumentEnvironmentName));

    public static CompiledExpression InvocationExpressionForCompiledExpressionFunction(
        FunctionCompilationEnvironment environment,
        CompiledExpressionId invokedFunction,
        ExpressionSyntax environmentExpressionSyntax) =>
        CompiledExpression.WithTypeResult(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.IdentifierName(
                    MemberNameForCompiledExpressionFunction(
                        invokedFunction,
                        constrainedEnv: null)))
            .WithArgumentList(
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList<ArgumentSyntax>(
                        new SyntaxNodeOrToken[]
                        {
                            SyntaxFactory.Argument(
                                SyntaxFactory.IdentifierName(environment.ArgumentEvalGenericName)),
                            SyntaxFactory.Token(SyntaxKind.CommaToken),
                            SyntaxFactory.Argument(environmentExpressionSyntax)
                        }))));

    public static Result<string, CompiledExpressionId> CompiledExpressionId(Expression expression) =>
        PineVM.PineVM.EncodeExpressionAsValue(expression)
        .Map(CompiledExpressionId);

    public static CompiledExpressionId CompiledExpressionId(PineValue expressionValue)
    {
        var expressionHash = CommonConversion.StringBase16(compilerCache.ComputeHash(expressionValue));

        return
            new CompiledExpressionId(
                ExpressionValue: expressionValue,
                ExpressionHashBase16: expressionHash);
    }

    static string MemberNameForExpression(string expressionValueHash) =>
        "expression_" + expressionValueHash[..10];

    public static string MemberNameForCompiledExpressionFunction(
        CompiledExpressionId derivedId,
        EnvConstraintId? constrainedEnv) =>
        "expr_function_" + derivedId.ExpressionHashBase16[..10] +
        (constrainedEnv is null ? null : "_env_" + MemberNameForConstrainedEnv(constrainedEnv));

    static string MemberNameForConstrainedEnv(EnvConstraintId constrainedEnv) =>
        constrainedEnv.HashBase16[..8];

    public static Result<string, Expression> TransformPineExpressionWithOptionalReplacement(
        Func<Expression, Result<string, Maybe<Expression>>> findReplacement,
        Expression expression)
    {
        return
            findReplacement(expression)
            .MapError(err => "Failed to find replacement: " + err)
            .AndThen(maybeReplacement =>
            maybeReplacement
            .Map(r => Result<string, Expression>.ok(r))
            .WithDefaultBuilder(() =>
            {
                return expression switch
                {
                    Expression.LiteralExpression literal =>
                    Result<string, Expression>.ok(literal),

                    Expression.EnvironmentExpression =>
                    Result<string, Expression>.ok(expression),

                    Expression.ListExpression list =>
                    list.List.Select(e => TransformPineExpressionWithOptionalReplacement(findReplacement, e))
                    .ListCombine()
                    .Map(elements => (Expression)new Expression.ListExpression([.. elements])),

                    Expression.ConditionalExpression conditional =>
                    TransformPineExpressionWithOptionalReplacement(
                        findReplacement,
                        conditional.condition)
                    .AndThen(transformedCondition =>
                    TransformPineExpressionWithOptionalReplacement(
                        findReplacement,
                        conditional.ifTrue)
                    .AndThen(transformedIfTrue =>
                    TransformPineExpressionWithOptionalReplacement(
                        findReplacement,
                        conditional.ifFalse)
                    .Map(transformedIfFalse =>
                    (Expression)new Expression.ConditionalExpression(
                        transformedCondition,
                        transformedIfTrue,
                        transformedIfFalse)))),

                    Expression.KernelApplicationExpression kernelAppl =>
                    TransformPineExpressionWithOptionalReplacement(findReplacement, kernelAppl.argument)
                    .MapError(err => "Failed to transform kernel application argument: " + err)
                    .Map(transformedArgument => (Expression)new Expression.KernelApplicationExpression(
                        functionName: kernelAppl.functionName,
                        argument: transformedArgument,
                        function: null)),

                    Expression.StringTagExpression stringTag =>
                    TransformPineExpressionWithOptionalReplacement(findReplacement, stringTag.tagged)
                    .Map(transformedTagged => (Expression)new Expression.StringTagExpression(tag: stringTag.tag, tagged: transformedTagged)),

                    _ =>
                    Result<string, Expression>.err("Unsupported expression type: " + expression.GetType().FullName)
                };
            }));
    }

    public static Result<string, PineValue> TryEvaluateExpressionIndependent(Expression expression) =>
        expression switch
        {
            Expression.EnvironmentExpression =>
            Result<string, PineValue>.err("Expression depends on environment"),

            Expression.LiteralExpression literal =>
            Result<string, PineValue>.ok(literal.Value),

            Expression.ListExpression list =>
            list.List.Select(TryEvaluateExpressionIndependent)
            .ListCombine()
            .Map(PineValue.List),

            Expression.KernelApplicationExpression kernelApplication =>
            TryEvaluateExpressionIndependent(kernelApplication.argument)
            .MapError(err => "Failed to evaluate kernel application argument independent: " + err)
            .Map(kernelApplication.function),

            Expression.ParseAndEvalExpression parseAndEvalExpr =>
            TryEvaluateExpressionIndependent(parseAndEvalExpr)
            .Map(ok =>
            {
                Console.WriteLine("Successfully evaluated ParseAndEvalExpression independent 🙃");

                return ok;
            }),

            Expression.StringTagExpression stringTag =>
            TryEvaluateExpressionIndependent(stringTag.tagged),

            _ =>
            Result<string, PineValue>.err("Unsupported expression type: " + expression.GetType().FullName)
        };

    public static Result<string, PineValue> TryEvaluateExpressionIndependent(
        Expression.ParseAndEvalExpression parseAndEvalExpr)
    {
        if (TryEvaluateExpressionIndependent(parseAndEvalExpr.environment) is Result<string, PineValue>.Ok envOk)
        {
            return
                new PineVM.PineVM().EvaluateExpression(parseAndEvalExpr, PineValue.EmptyList)
                .MapError(err => "Got independent environment, but failed to evaluated: " + err);
        }

        return
            TryEvaluateExpressionIndependent(parseAndEvalExpr.expression)
            .MapError(err => "Expression is not independent: " + err)
            .AndThen(compilerCache.ParseExpressionFromValue)
            .AndThen(innerExpr => TryEvaluateExpressionIndependent(innerExpr)
            .MapError(err => "Inner expression is not independent: " + err));
    }

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression.LiteralExpression literalExpression)
    {
        return
            Result<string, CompiledExpression>.ok(
                CompiledExpression.WithTypePlainValue(SyntaxFactory.IdentifierName(DeclarationNameForValue(literalExpression.Value)))
                .MergeDependencies(
                    CompiledExpressionDependencies.Empty with { Values = [literalExpression.Value] }));
    }

    public static string DeclarationNameForValue(PineValue pineValue) =>
        "value_" + CommonConversion.StringBase16(compilerCache.ComputeHash(pineValue))[..10];

    public static Result<string, CompiledExpression> CompileToCSharpExpression(
        Expression.StringTagExpression stringTagExpression,
        ExpressionCompilationEnvironment environment)
    {
        Console.WriteLine("Compiling string tag: " + stringTagExpression.tag);

        return
            CompileToCSharpExpression(
                stringTagExpression.tagged,
                environment,
                createLetBindingsForCse: false)
            .Map(compiledExpr =>
            compiledExpr.MapSyntax(s => s.InsertTriviaBefore(
                SyntaxFactory.Comment("/*\n" + stringTagExpression.tag + "\n*/"),
                SyntaxFactory.TriviaList())));
    }

    public abstract record ValueSyntaxKind
    {
        public record AsSignedInteger(long Value)
            : ValueSyntaxKind;

        public record AsListOfSignedIntegers(IReadOnlyList<long> Values)
            : ValueSyntaxKind;

        public record AsString(string Value)
            : ValueSyntaxKind;

        public record Other
            : ValueSyntaxKind;
    }

    public static (ExpressionSyntax exprSyntax, ValueSyntaxKind syntaxKind) CompileToCSharpLiteralExpression(
        PineValue pineValue,
        Func<PineValue, ExpressionSyntax?> overrideDefaultExpression)
    {
        (ExpressionSyntax, ValueSyntaxKind) continueCompile(PineValue pineValue) =>
            overrideDefaultExpression(pineValue) is { } fromOverride ?
            (fromOverride, new ValueSyntaxKind.Other())
            :
            CompileToCSharpLiteralExpression(pineValue, overrideDefaultExpression);

        if (pineValue == PineValue.EmptyList)
            return (PineCSharpSyntaxFactory.PineValueEmptyListSyntax, new ValueSyntaxKind.Other());

        static long? attemptMapToSignedInteger(PineValue pineValue)
        {
            if (PineValueAsInteger.SignedIntegerFromValue(pineValue) is Result<string, BigInteger>.Ok okInteger &&
                PineValueAsInteger.ValueFromSignedInteger(okInteger.Value) == pineValue &&
                okInteger.Value < long.MaxValue && long.MinValue < okInteger.Value)
                return (long)okInteger.Value;

            return null;
        }

        static ExpressionSyntax ExpressionSyntaxForSignedInt(long asInt64)
        {
            return
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(nameof(PineValueAsInteger)),
                        SyntaxFactory.IdentifierName(nameof(PineValueAsInteger.ValueFromSignedInteger))))
                .WithArgumentList(
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            PineCSharpSyntaxFactory.ExpressionSyntaxForIntegerLiteral(asInt64)))));
        }

        if (attemptMapToSignedInteger(pineValue) is { } asInt64)
        {
            return (ExpressionSyntaxForSignedInt(asInt64), new ValueSyntaxKind.AsSignedInteger(asInt64));
        }

        if (pineValue is PineValue.ListValue list)
        {
            var asIntegers =
                list.Elements
                .Select(attemptMapToSignedInteger)
                .WhereHasValue()
                .ToImmutableArray();

            if (asIntegers.Length == list.Elements.Count)
            {
                return
                    (SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("PineValue"),
                            SyntaxFactory.IdentifierName("List")))
                    .WithArgumentList(
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(
                                    SyntaxFactory.CollectionExpression(
                                        SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                                            asIntegers
                                            .Select(item => SyntaxFactory.ExpressionElement(ExpressionSyntaxForSignedInt(item))))))))),
                                            new ValueSyntaxKind.AsListOfSignedIntegers(asIntegers));
            }
        }

        if (PineValueAsString.StringFromValue(pineValue) is Result<string, string>.Ok okString)
        {
            return
                (SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(nameof(PineValueAsString)),
                        SyntaxFactory.IdentifierName(nameof(PineValueAsString.ValueFromString))))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.StringLiteralExpression,
                                    SyntaxFactory.Literal(okString.Value)))))),
                                    new ValueSyntaxKind.AsString(okString.Value));

        }

        ExpressionSyntax defaultRepresentationOfBlob(ReadOnlyMemory<byte> blob)
        {
            var bytesIntegers =
                blob
                .ToArray()
                .Select(b => PineCSharpSyntaxFactory.ExpressionSyntaxForIntegerLiteral(b));

            return
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("PineValue"),
                        SyntaxFactory.IdentifierName("Blob")))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.CastExpression(
                                    SyntaxFactory.ArrayType(
                                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ByteKeyword)))
                                    .WithRankSpecifiers(
                                        SyntaxFactory.SingletonList(
                                            SyntaxFactory.ArrayRankSpecifier(
                                                SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                                                    SyntaxFactory.OmittedArraySizeExpression())))),
                                    SyntaxFactory.CollectionExpression(
                                        SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                                            bytesIntegers.Select(SyntaxFactory.ExpressionElement))))
                                ))));
        }

        ExpressionSyntax defaultRepresentationOfList(IReadOnlyList<PineValue> list)
        {
            var itemSyntaxes =
                list.Select(item => continueCompile(item).Item1).ToImmutableList();

            return
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("PineValue"),
                        SyntaxFactory.IdentifierName("List")))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.CollectionExpression(
                                    SyntaxFactory.SeparatedList<CollectionElementSyntax>(
                                        itemSyntaxes
                                        .Select(SyntaxFactory.ExpressionElement)))
                                ))));
        }

        return pineValue switch
        {
            PineValue.BlobValue blobValue =>
            (defaultRepresentationOfBlob(blobValue.Bytes),
            new ValueSyntaxKind.Other()),

            PineValue.ListValue listValue =>
            (defaultRepresentationOfList(listValue.Elements),
            new ValueSyntaxKind.Other()),

            _ =>
            throw new Exception("Unknown value type: " + pineValue.GetType().FullName)
        };
    }

    private static IEnumerable<PineValue> EnumerateAllLiterals(Expression expression) =>
        expression switch
        {
            Expression.LiteralExpression literal =>
            [literal.Value],

            Expression.EnvironmentExpression =>
            [],

            Expression.ListExpression list =>
            list.List.SelectMany(EnumerateAllLiterals),

            Expression.KernelApplicationExpression kernelApplicationExpression =>
            EnumerateAllLiterals(kernelApplicationExpression.argument),

            Expression.ConditionalExpression conditionalExpression =>
            [.. EnumerateAllLiterals(conditionalExpression.condition)
            ,
                .. EnumerateAllLiterals(conditionalExpression.ifTrue)
            ,
                .. EnumerateAllLiterals(conditionalExpression.ifFalse)],

            Expression.ParseAndEvalExpression parseAndEvalExpr =>
            [.. EnumerateAllLiterals(parseAndEvalExpr.expression)
            ,
                .. EnumerateAllLiterals(parseAndEvalExpr.environment)],

            Expression.StringTagExpression stringTagExpression =>
            EnumerateAllLiterals(stringTagExpression.tagged),

            _ => throw new NotImplementedException("Expression type not implemented: " + expression.GetType().FullName)
        };

    public static string GetNameForExpression(ExpressionSyntax syntax)
    {
        var serialized = syntax.ToString();

        var utf8 = Encoding.UTF8.GetBytes(serialized);

        var hash = SHA256.HashData(utf8);

        return CommonConversion.StringBase16(hash)[..10];
    }

    public record ExpressionUsageCount(
        int Unconditional,
        int Conditional);

    public static IReadOnlyDictionary<Expression, ExpressionUsageCount> CountExpressionUsage(
        Expression expression,
        Func<Expression, bool> skipDescending)
    {
        var dictionary = new Dictionary<Expression, ExpressionUsageCount>();

        void Traverse(Expression expr, bool isConditional)
        {
            if (dictionary.TryGetValue(expr, out ExpressionUsageCount? currentCount))
            {
                dictionary[expr] = new ExpressionUsageCount(
                    isConditional ? currentCount.Unconditional : currentCount.Unconditional + 1,
                    isConditional ? currentCount.Conditional + 1 : currentCount.Conditional
                );
            }
            else
            {
                dictionary[expr] = new ExpressionUsageCount(isConditional ? 0 : 1, isConditional ? 1 : 0);
            }

            if (skipDescending(expr))
                return;

            switch (expr)
            {
                case Expression.LiteralExpression _:
                    // Leaf node, no further traversal needed
                    break;
                case Expression.ListExpression listExpr:
                    foreach (var subExpr in listExpr.List)
                        Traverse(subExpr, isConditional);
                    break;
                case Expression.ParseAndEvalExpression parseAndEvalExpr:
                    Traverse(parseAndEvalExpr.expression, isConditional);
                    Traverse(parseAndEvalExpr.environment, isConditional);
                    break;
                case Expression.KernelApplicationExpression kernelAppExpr:
                    Traverse(kernelAppExpr.argument, isConditional);
                    break;
                case Expression.ConditionalExpression conditionalExpr:
                    // For ConditionalExpression, traverse its branches as conditional

                    Traverse(conditionalExpr.condition, isConditional);
                    Traverse(conditionalExpr.ifTrue, true);
                    Traverse(conditionalExpr.ifFalse, true);
                    break;
                case Expression.EnvironmentExpression _:
                    // Leaf node, no further traversal needed
                    break;
                case Expression.StringTagExpression stringTagExpr:
                    Traverse(stringTagExpr.tagged, isConditional);
                    break;
                case Expression.DelegatingExpression _:
                    // DelegatingExpression might not need traversal depending on its delegate's behavior
                    // Adjust this part if necessary
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        Traverse(expression, false);
        return dictionary.ToImmutableDictionary();
    }
}
