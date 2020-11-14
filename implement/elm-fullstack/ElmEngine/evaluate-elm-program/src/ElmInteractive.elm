module ElmInteractive exposing
    ( InteractiveContext(..)
    , SubmissionResponse(..)
    , elmValueAsExpression
    , elmValueAsJson
    , evaluateExpressionText
    , parseElmModuleText
    , parseElmModuleTextToJson
    , submissionInInteractive
    )

import Dict
import Elm.Parser
import Elm.Processing
import Elm.Syntax.Declaration
import Elm.Syntax.Expression
import Elm.Syntax.File
import Elm.Syntax.Module
import Elm.Syntax.Node
import Elm.Syntax.Pattern
import Elm.Syntax.Range
import Elm.Syntax.Type
import Json.Encode
import Parser
import Pine exposing (PineExpression(..), PineValue(..))
import Result.Extra


type InteractiveSubmission
    = ExpressionSubmission Elm.Syntax.Expression.Expression
    | DeclarationSubmission Elm.Syntax.Declaration.Declaration


type InteractiveContext
    = DefaultContext
    | InitContextFromApp { modulesTexts : List String }


type SubmissionResponse
    = SubmissionResponseValue { value : ElmValue }
    | SubmissionResponseNoValue


type ElmValue
    = ElmList (List ElmValue)
    | ElmStringOrInteger String
    | ElmTag String (List ElmValue)
    | ElmRecord (List ( String, ElmValue ))


evaluateExpressionText : InteractiveContext -> String -> Result String Json.Encode.Value
evaluateExpressionText context elmExpressionText =
    submissionInInteractive context [] elmExpressionText
        |> Result.andThen
            (\submissionResponse ->
                case submissionResponse of
                    SubmissionResponseNoValue ->
                        Err "This submission does not evaluate to a value."

                    SubmissionResponseValue responseWithValue ->
                        Ok (elmValueAsJson responseWithValue.value)
            )


submissionInInteractive : InteractiveContext -> List String -> String -> Result String SubmissionResponse
submissionInInteractive context previousSubmissions submission =
    case parseInteractiveSubmissionFromString submission of
        Err error ->
            Err ("Failed to parse submission: " ++ error.asExpressionError)

        Ok (DeclarationSubmission _) ->
            Ok SubmissionResponseNoValue

        Ok (ExpressionSubmission elmExpression) ->
            case pineExpressionFromElm elmExpression of
                Err error ->
                    Err ("Failed to map from Elm to Pine expression: " ++ error)

                Ok pineExpression ->
                    case pineExpressionContextForElmInteractive context of
                        Err error ->
                            Err ("Failed to prepare the initial context: " ++ error)

                        Ok initialContext ->
                            case expandContextWithListOfInteractiveSubmissions previousSubmissions initialContext of
                                Err error ->
                                    Err ("Failed to apply previous submissions: " ++ error)

                                Ok expressionContext ->
                                    case Pine.evaluatePineExpression expressionContext pineExpression of
                                        Err error ->
                                            Err ("Failed to evaluate Pine expression: " ++ error)

                                        Ok pineValue ->
                                            case pineValueAsElmValue pineValue of
                                                Err error ->
                                                    Err ("Failed to encode as Elm value: " ++ error)

                                                Ok valueAsElmValue ->
                                                    Ok (SubmissionResponseValue { value = valueAsElmValue })


expandContextWithListOfInteractiveSubmissions : List String -> Pine.PineExpressionContext -> Result String Pine.PineExpressionContext
expandContextWithListOfInteractiveSubmissions submissions contextBefore =
    submissions
        |> List.foldl
            (\submission -> Result.andThen (expandContextWithInteractiveSubmission submission))
            (Ok contextBefore)


expandContextWithInteractiveSubmission : String -> Pine.PineExpressionContext -> Result String Pine.PineExpressionContext
expandContextWithInteractiveSubmission submission contextBefore =
    case parseInteractiveSubmissionFromString submission of
        Ok (DeclarationSubmission elmDeclaration) ->
            case elmDeclaration of
                Elm.Syntax.Declaration.FunctionDeclaration functionDeclaration ->
                    case pineExpressionFromElmFunction functionDeclaration of
                        Err error ->
                            Err ("Failed to translate Elm function declaration: " ++ error)

                        Ok ( declaredName, declaredFunctionExpression ) ->
                            contextBefore
                                |> Pine.addToContext [ Pine.pineValueFromContextExpansionWithName ( declaredName, PineExpressionValue declaredFunctionExpression ) ]
                                |> Ok

                _ ->
                    Ok contextBefore

        _ ->
            Ok contextBefore


elmValueAsExpression : ElmValue -> String
elmValueAsExpression elmValue =
    case elmValue of
        ElmList list ->
            "[" ++ (list |> List.map elmValueAsExpression |> String.join ",") ++ "]"

        ElmStringOrInteger string ->
            string |> Json.Encode.string |> Json.Encode.encode 0

        ElmRecord fields ->
            "{ " ++ (fields |> List.map (\( fieldName, fieldValue ) -> fieldName ++ " = " ++ elmValueAsExpression fieldValue) |> String.join ", ") ++ " }"

        ElmTag tagName tagArguments ->
            tagName :: (tagArguments |> List.map elmValueAsExpression) |> String.join " "


elmValueAsJson : ElmValue -> Json.Encode.Value
elmValueAsJson elmValue =
    case elmValue of
        ElmStringOrInteger string ->
            Json.Encode.string string

        ElmList list ->
            Json.Encode.list elmValueAsJson list

        ElmRecord fields ->
            Json.Encode.list (\( fieldName, fieldValue ) -> Json.Encode.list identity [ Json.Encode.string fieldName, elmValueAsJson fieldValue ]) fields

        ElmTag tagName tagArguments ->
            Json.Encode.list identity [ Json.Encode.string tagName, Json.Encode.list elmValueAsJson tagArguments ]


pineValueAsElmValue : PineValue -> Result String ElmValue
pineValueAsElmValue pineValue =
    case pineValue of
        PineStringOrInteger string ->
            -- TODO: Use type inference to distinguish between string and integer
            Ok (ElmStringOrInteger string)

        PineList list ->
            case list |> List.map pineValueAsElmValue |> Result.Extra.combine of
                Err error ->
                    Err ("Failed to combine list: " ++ error)

                Ok listValues ->
                    let
                        resultAsList =
                            Ok (ElmList listValues)

                        tryMapToRecordField possiblyRecordField =
                            case possiblyRecordField of
                                ElmList [ ElmStringOrInteger fieldName, fieldValue ] ->
                                    if not (stringStartsWithUpper fieldName) then
                                        Just ( fieldName, fieldValue )

                                    else
                                        Nothing

                                _ ->
                                    Nothing
                    in
                    case listValues |> List.map (tryMapToRecordField >> Result.fromMaybe "") |> Result.Extra.combine of
                        Ok recordFields ->
                            let
                                recordFieldsNames =
                                    List.map Tuple.first recordFields
                            in
                            if recordFieldsNames /= [] && List.sort recordFieldsNames == recordFieldsNames then
                                Ok (ElmRecord recordFields)

                            else
                                resultAsList

                        Err _ ->
                            case listValues of
                                [ ElmStringOrInteger tagName, ElmList tagArguments ] ->
                                    if stringStartsWithUpper tagName then
                                        Ok (ElmTag tagName tagArguments)

                                    else
                                        resultAsList

                                _ ->
                                    resultAsList

        PineExpressionValue _ ->
            Err "PineExpressionValue"


pineExpressionContextForElmInteractive : InteractiveContext -> Result String Pine.PineExpressionContext
pineExpressionContextForElmInteractive context =
    case elmCoreModulesTexts |> List.map parseElmModuleTextIntoPineValue |> Result.Extra.combine of
        Err error ->
            Err ("Failed to compile elm core module: " ++ error)

        Ok elmCoreModules ->
            let
                contextModulesTexts =
                    case context of
                        DefaultContext ->
                            []

                        InitContextFromApp { modulesTexts } ->
                            modulesTexts
            in
            case contextModulesTexts |> List.map parseElmModuleTextIntoPineValue |> Result.Extra.combine of
                Err error ->
                    Err ("Failed to compile elm module from context: " ++ error)

                Ok contextModules ->
                    elmValuesToExposeToGlobal
                        |> List.foldl exposeFromElmModuleToGlobal
                            { commonModel = contextModules ++ elmCoreModules
                            , provisionalArgumentStack = []
                            }
                        |> Ok


exposeFromElmModuleToGlobal : ( List String, String ) -> Pine.PineExpressionContext -> Pine.PineExpressionContext
exposeFromElmModuleToGlobal ( moduleName, nameInModule ) context =
    case Pine.lookUpNameInContext (moduleName ++ [ nameInModule ] |> String.join ".") context of
        Err _ ->
            context

        Ok ( valueFromName, _ ) ->
            { context | commonModel = Pine.pineValueFromContextExpansionWithName ( nameInModule, valueFromName ) :: context.commonModel }


parseElmModuleTextIntoPineValue : String -> Result String PineValue
parseElmModuleTextIntoPineValue moduleText =
    case parseElmModuleText moduleText of
        Err _ ->
            Err ("Failed to parse module text: " ++ (moduleText |> String.left 100))

        Ok file ->
            let
                moduleName =
                    file
                        |> moduleNameFromSyntaxFile
                        |> Elm.Syntax.Node.value
                        |> String.join "."

                declarationsResults =
                    file.declarations
                        |> List.map Elm.Syntax.Node.value
                        |> List.filterMap
                            (\declaration ->
                                case declaration of
                                    Elm.Syntax.Declaration.FunctionDeclaration functionDeclaration ->
                                        Just [ pineExpressionFromElmFunction functionDeclaration ]

                                    Elm.Syntax.Declaration.CustomTypeDeclaration customTypeDeclaration ->
                                        Just (customTypeDeclaration.constructors |> List.map (Elm.Syntax.Node.value >> pineExpressionFromElmValueConstructor))

                                    _ ->
                                        Nothing
                            )
                        |> List.concat
            in
            case declarationsResults |> Result.Extra.combine of
                Err error ->
                    Err ("Failed to translate declaration: " ++ error)

                Ok declarations ->
                    let
                        declarationsValues =
                            declarations
                                |> List.map
                                    (\( declaredName, namedExpression ) ->
                                        Pine.pineValueFromContextExpansionWithName ( declaredName, PineExpressionValue namedExpression )
                                    )
                    in
                    Ok (Pine.pineValueFromContextExpansionWithName ( moduleName, PineList declarationsValues ))


elmCoreModulesTexts : List String
elmCoreModulesTexts =
    [ """
module Basics exposing (..)


identity : a -> a
identity x =
    x


always : a -> b -> a
always a _ =
    a

"""
    , -- https://github.com/elm/core/blob/84f38891468e8e153fc85a9b63bdafd81b24664e/src/List.elm
      """
module List exposing (..)


import Maybe exposing (Maybe(..))


cons : a -> List a -> List a
cons element list =
    PineKernel.listCons element list


foldl : (a -> b -> b) -> b -> List a -> b
foldl func acc list =
    case list of
        [] ->
            acc

        x :: xs ->
            foldl func (func x acc) xs


foldr : (a -> b -> b) -> b -> List a -> b
foldr func acc list =
    foldl func acc (reverse list)


filter : (a -> Bool) -> List a -> List a
filter isGood list =
    foldr (\\x xs -> if isGood x then cons x xs else xs) [] list


length : List a -> Int
length xs =
    foldl (\\_ i -> i + 1) 0 xs


reverse : List a -> List a
reverse list =
    foldl cons [] list


member : a -> List a -> Bool
member x xs =
    any (\\a -> a == x) xs


any : (a -> Bool) -> List a -> Bool
any isOkay list =
    case list of
        [] ->
            False

        next :: xs ->
            if isOkay next then
                True

            else
                any isOkay xs


isEmpty : List a -> Bool
isEmpty xs =
    case xs of
        [] ->
            True

        _ ->
            False


head : List a -> Maybe a
head list =
    case list of
        x :: xs ->
            Just x

        [] ->
            Nothing


tail : List a -> Maybe (List a)
tail list =
    case list of
        x :: xs ->
            Just xs

        [] ->
            Nothing


drop : Int -> List a -> List a
drop n list =
    if n <= 0 then
        list

    else
        case list of
        [] ->
            list

        x :: xs ->
            drop (n - 1) xs

"""
    , """
module Char exposing (..)


type alias Char = Int


toCode : Char -> Int
toCode char =
    char

"""
    , """
module Maybe exposing (..)


type Maybe a
    = Just a
    | Nothing


withDefault : a -> Maybe a -> a
withDefault default maybe =
    case maybe of
        Just value -> value
        Nothing -> default


map : (a -> b) -> Maybe a -> Maybe b
map f maybe =
    case maybe of
        Just value ->
            Just (f value)

        Nothing ->
            Nothing


andThen : (a -> Maybe b) -> Maybe a -> Maybe b
andThen callback maybeValue =
    case maybeValue of
        Just value ->
            callback value

        Nothing ->
            Nothing

"""
    ]


elmValuesToExposeToGlobal : List ( List String, String )
elmValuesToExposeToGlobal =
    [ ( [ "Basics" ], "identity" )
    , ( [ "Basics" ], "always" )
    , ( [ "Maybe" ], "Nothing" )
    , ( [ "Maybe" ], "Just" )
    ]


pineExpressionFromElm : Elm.Syntax.Expression.Expression -> Result String PineExpression
pineExpressionFromElm elmExpression =
    case elmExpression of
        Elm.Syntax.Expression.Literal literal ->
            Ok (PineLiteral (PineStringOrInteger literal))

        Elm.Syntax.Expression.CharLiteral char ->
            Ok (PineLiteral (PineStringOrInteger (String.fromInt (Char.toCode char))))

        Elm.Syntax.Expression.Integer integer ->
            Ok (PineLiteral (PineStringOrInteger (String.fromInt integer)))

        Elm.Syntax.Expression.Negation negatedElmExpression ->
            case pineExpressionFromElm (Elm.Syntax.Node.value negatedElmExpression) of
                Err error ->
                    Err ("Failed to map negated expression: " ++ error)

                Ok negatedExpression ->
                    Ok (PineApplication { function = PineFunctionOrValue "PineKernel.negate", arguments = [ negatedExpression ] })

        Elm.Syntax.Expression.FunctionOrValue moduleName localName ->
            Ok (PineFunctionOrValue (String.join "." (moduleName ++ [ localName ])))

        Elm.Syntax.Expression.Application application ->
            case application |> List.map (Elm.Syntax.Node.value >> pineExpressionFromElm) |> Result.Extra.combine of
                Err error ->
                    Err ("Failed to map application elements: " ++ error)

                Ok applicationElements ->
                    case applicationElements of
                        appliedFunctionSyntax :: arguments ->
                            Ok (PineApplication { function = appliedFunctionSyntax, arguments = arguments })

                        [] ->
                            Err "Invalid shape of application: Zero elements in the application list"

        Elm.Syntax.Expression.OperatorApplication operator _ leftExpr rightExpr ->
            let
                orderedElmExpression =
                    mapExpressionForOperatorPrecedence elmExpression
            in
            if orderedElmExpression /= elmExpression then
                pineExpressionFromElm orderedElmExpression

            else
                case
                    ( pineExpressionFromElm (Elm.Syntax.Node.value leftExpr)
                    , pineExpressionFromElm (Elm.Syntax.Node.value rightExpr)
                    )
                of
                    ( Ok left, Ok right ) ->
                        Ok
                            (PineApplication
                                { function = PineFunctionOrValue ("(" ++ operator ++ ")")
                                , arguments = [ left, right ]
                                }
                            )

                    _ ->
                        Err "Failed to map OperatorApplication left or right expression. TODO: Expand error details."

        Elm.Syntax.Expression.IfBlock elmCondition elmExpressionIfTrue elmExpressionIfFalse ->
            case pineExpressionFromElm (Elm.Syntax.Node.value elmCondition) of
                Err error ->
                    Err ("Failed to map Elm condition: " ++ error)

                Ok condition ->
                    case pineExpressionFromElm (Elm.Syntax.Node.value elmExpressionIfTrue) of
                        Err error ->
                            Err ("Failed to map Elm expressionIfTrue: " ++ error)

                        Ok expressionIfTrue ->
                            case pineExpressionFromElm (Elm.Syntax.Node.value elmExpressionIfFalse) of
                                Err error ->
                                    Err ("Failed to map Elm expressionIfFalse: " ++ error)

                                Ok expressionIfFalse ->
                                    Ok (PineIfBlock condition expressionIfTrue expressionIfFalse)

        Elm.Syntax.Expression.LetExpression letBlock ->
            pineExpressionFromElmLetBlock letBlock

        Elm.Syntax.Expression.ParenthesizedExpression parenthesizedExpression ->
            pineExpressionFromElm (Elm.Syntax.Node.value parenthesizedExpression)

        Elm.Syntax.Expression.ListExpr listExpression ->
            listExpression
                |> List.map (Elm.Syntax.Node.value >> pineExpressionFromElm)
                |> Result.Extra.combine
                |> Result.map PineListExpr

        Elm.Syntax.Expression.CaseExpression caseBlock ->
            pineExpressionFromElmCaseBlock caseBlock

        Elm.Syntax.Expression.LambdaExpression lambdaExpression ->
            pineExpressionFromElmLambda lambdaExpression

        Elm.Syntax.Expression.RecordExpr recordExpr ->
            recordExpr |> List.map Elm.Syntax.Node.value |> pineExpressionFromElmRecord

        _ ->
            Err
                ("Unsupported type of expression: "
                    ++ (elmExpression |> Elm.Syntax.Expression.encode |> Json.Encode.encode 0)
                )


pineExpressionFromElmLetBlock : Elm.Syntax.Expression.LetBlock -> Result String PineExpression
pineExpressionFromElmLetBlock letBlock =
    let
        declarationsResults =
            letBlock.declarations
                |> List.map (Elm.Syntax.Node.value >> pineExpressionFromElmLetDeclaration)
    in
    case declarationsResults |> Result.Extra.combine of
        Err error ->
            Err ("Failed to map declaration in let block: " ++ error)

        Ok declarations ->
            case pineExpressionFromElm (Elm.Syntax.Node.value letBlock.expression) of
                Err error ->
                    Err ("Failed to map expression in let block: " ++ error)

                Ok expressionInExpandedContext ->
                    Ok (pineExpressionFromLetBlockDeclarationsAndExpression declarations expressionInExpandedContext)


pineExpressionFromLetBlockDeclarationsAndExpression : List ( String, PineExpression ) -> PineExpression -> PineExpression
pineExpressionFromLetBlockDeclarationsAndExpression declarations expression =
    declarations
        |> List.foldl
            (\declaration combinedExpr ->
                PineContextExpansionWithName
                    (Tuple.mapSecond PineExpressionValue declaration)
                    combinedExpr
            )
            expression


pineExpressionFromElmLetDeclaration : Elm.Syntax.Expression.LetDeclaration -> Result String ( String, PineExpression )
pineExpressionFromElmLetDeclaration declaration =
    case declaration of
        Elm.Syntax.Expression.LetFunction letFunction ->
            pineExpressionFromElmFunction letFunction

        Elm.Syntax.Expression.LetDestructuring _ _ ->
            Err "Destructuring in let block not implemented yet."


pineExpressionFromElmFunction : Elm.Syntax.Expression.Function -> Result String ( String, PineExpression )
pineExpressionFromElmFunction function =
    pineExpressionFromElmFunctionWithoutName
        { arguments = (Elm.Syntax.Node.value function.declaration).arguments |> List.map Elm.Syntax.Node.value
        , expression = Elm.Syntax.Node.value (Elm.Syntax.Node.value function.declaration).expression
        }
        |> Result.map
            (\functionWithoutName ->
                ( Elm.Syntax.Node.value (Elm.Syntax.Node.value function.declaration).name
                , functionWithoutName
                )
            )


pineExpressionFromElmFunctionWithoutName :
    { arguments : List Elm.Syntax.Pattern.Pattern, expression : Elm.Syntax.Expression.Expression }
    -> Result String PineExpression
pineExpressionFromElmFunctionWithoutName function =
    case pineExpressionFromElm function.expression of
        Err error ->
            Err ("Failed to map expression in let function: " ++ error)

        Ok letFunctionExpression ->
            let
                mapArgumentsToOnlyNameResults =
                    function.arguments
                        |> List.map
                            (\argumentPattern ->
                                case argumentPattern of
                                    Elm.Syntax.Pattern.VarPattern argumentName ->
                                        Ok argumentName

                                    Elm.Syntax.Pattern.AllPattern ->
                                        Ok "unused_from_elm_all_pattern"

                                    _ ->
                                        Err ("Unsupported type of pattern: " ++ (argumentPattern |> Elm.Syntax.Pattern.encode |> Json.Encode.encode 0))
                            )
            in
            case mapArgumentsToOnlyNameResults |> Result.Extra.combine of
                Err error ->
                    Err ("Failed to map function argument pattern: " ++ error)

                Ok argumentsNames ->
                    Ok (functionExpressionFromArgumentsNamesAndExpression argumentsNames letFunctionExpression)


functionExpressionFromArgumentsNamesAndExpression : List String -> PineExpression -> PineExpression
functionExpressionFromArgumentsNamesAndExpression argumentsNames expression =
    argumentsNames
        |> List.foldr
            (\argumentName prevExpression -> PineFunction argumentName prevExpression)
            expression


pineExpressionFromElmValueConstructor : Elm.Syntax.Type.ValueConstructor -> Result String ( String, PineExpression )
pineExpressionFromElmValueConstructor valueConstructor =
    let
        constructorName =
            Elm.Syntax.Node.value valueConstructor.name

        argumentsNames =
            valueConstructor.arguments |> List.indexedMap (\i _ -> "value_constructor_argument_" ++ String.fromInt i)
    in
    Ok
        ( constructorName
        , argumentsNames
            |> List.foldl
                (\argumentName prevExpression -> PineFunction argumentName prevExpression)
                (Pine.tagValueExpression constructorName (argumentsNames |> List.map PineFunctionOrValue))
        )


pineExpressionFromElmCaseBlock : Elm.Syntax.Expression.CaseBlock -> Result String PineExpression
pineExpressionFromElmCaseBlock caseBlock =
    case pineExpressionFromElm (Elm.Syntax.Node.value caseBlock.expression) of
        Err error ->
            Err ("Failed to map case block expression: " ++ error)

        Ok expression ->
            case caseBlock.cases |> List.map (pineExpressionFromElmCaseBlockCase expression) |> Result.Extra.combine of
                Err error ->
                    Err ("Failed to map case in case-of block: " ++ error)

                Ok cases ->
                    let
                        ifBlockFromCase deconstructedCase nextBlockExpression =
                            PineIfBlock
                                deconstructedCase.conditionExpression
                                (pineExpressionFromLetBlockDeclarationsAndExpression
                                    deconstructedCase.declarations
                                    deconstructedCase.thenExpression
                                )
                                nextBlockExpression
                    in
                    Ok
                        (List.foldr
                            ifBlockFromCase
                            (PineFunctionOrValue "Error in mapping of case-of block: No matching branch.")
                            cases
                        )


pineExpressionFromElmCaseBlockCase :
    PineExpression
    -> Elm.Syntax.Expression.Case
    -> Result String { conditionExpression : PineExpression, declarations : List ( String, PineExpression ), thenExpression : PineExpression }
pineExpressionFromElmCaseBlockCase caseBlockValueExpression ( elmPattern, elmExpression ) =
    case pineExpressionFromElm (Elm.Syntax.Node.value elmExpression) of
        Err error ->
            Err ("Failed to map case expression: " ++ error)

        Ok expressionAfterDeconstruction ->
            case Elm.Syntax.Node.value elmPattern of
                Elm.Syntax.Pattern.AllPattern ->
                    Ok
                        { conditionExpression = PineLiteral Pine.truePineValue
                        , declarations = []
                        , thenExpression = expressionAfterDeconstruction
                        }

                Elm.Syntax.Pattern.ListPattern [] ->
                    let
                        conditionExpression =
                            PineApplication
                                { function = PineFunctionOrValue "(==)"
                                , arguments =
                                    [ caseBlockValueExpression
                                    , PineListExpr []
                                    ]
                                }
                    in
                    Ok
                        { conditionExpression = conditionExpression
                        , declarations = []
                        , thenExpression = expressionAfterDeconstruction
                        }

                Elm.Syntax.Pattern.UnConsPattern unconsLeft unconsRight ->
                    case ( Elm.Syntax.Node.value unconsLeft, Elm.Syntax.Node.value unconsRight ) of
                        ( Elm.Syntax.Pattern.VarPattern unconsLeftName, Elm.Syntax.Pattern.VarPattern unconsRightName ) ->
                            let
                                declarations =
                                    [ ( unconsLeftName
                                      , PineApplication
                                            { function = PineFunctionOrValue "PineKernel.listHead"
                                            , arguments = [ caseBlockValueExpression ]
                                            }
                                      )
                                    , ( unconsRightName
                                      , PineApplication
                                            { function = PineFunctionOrValue "PineKernel.listTail"
                                            , arguments = [ caseBlockValueExpression ]
                                            }
                                      )
                                    ]

                                conditionExpression =
                                    PineApplication
                                        { function = PineFunctionOrValue "not"
                                        , arguments =
                                            [ PineApplication
                                                { function = PineFunctionOrValue "PineKernel.equals"
                                                , arguments =
                                                    [ caseBlockValueExpression
                                                    , PineApplication
                                                        { function = PineFunctionOrValue "PineKernel.listTail"
                                                        , arguments = [ caseBlockValueExpression ]
                                                        }
                                                    ]
                                                }
                                            ]
                                        }
                            in
                            Ok
                                { conditionExpression = conditionExpression
                                , declarations = declarations
                                , thenExpression = expressionAfterDeconstruction
                                }

                        _ ->
                            Err "Unsupported shape of uncons pattern."

                Elm.Syntax.Pattern.NamedPattern qualifiedName customTypeArgumentPatterns ->
                    let
                        mapArgumentsToOnlyNameResults =
                            customTypeArgumentPatterns
                                |> List.map Elm.Syntax.Node.value
                                |> List.map
                                    (\argumentPattern ->
                                        case argumentPattern of
                                            Elm.Syntax.Pattern.VarPattern argumentName ->
                                                Ok argumentName

                                            Elm.Syntax.Pattern.AllPattern ->
                                                Ok "unused_from_elm_all_pattern"

                                            _ ->
                                                Err ("Unsupported type of pattern: " ++ (argumentPattern |> Elm.Syntax.Pattern.encode |> Json.Encode.encode 0))
                                    )

                        conditionExpression =
                            PineApplication
                                { function = PineFunctionOrValue "PineKernel.equals"
                                , arguments =
                                    [ PineLiteral (PineStringOrInteger qualifiedName.name)
                                    , PineApplication
                                        { function = PineFunctionOrValue "PineKernel.listHead"
                                        , arguments = [ caseBlockValueExpression ]
                                        }
                                    ]
                                }
                    in
                    case mapArgumentsToOnlyNameResults |> Result.Extra.combine of
                        Err error ->
                            Err ("Failed to map pattern in case block: " ++ error)

                        Ok declarationsNames ->
                            let
                                declarations =
                                    declarationsNames
                                        |> List.indexedMap
                                            (\argumentIndex declarationName ->
                                                ( declarationName
                                                , PineApplication
                                                    { function = PineFunctionOrValue "PineKernel.listHead"
                                                    , arguments =
                                                        [ PineApplication
                                                            { function = PineFunctionOrValue "List.drop"
                                                            , arguments =
                                                                [ PineLiteral (PineStringOrInteger (String.fromInt argumentIndex))
                                                                , PineApplication
                                                                    { function = PineFunctionOrValue "PineKernel.listHead"
                                                                    , arguments =
                                                                        [ PineApplication
                                                                            { function = PineFunctionOrValue "PineKernel.listTail"
                                                                            , arguments = [ caseBlockValueExpression ]
                                                                            }
                                                                        ]
                                                                    }
                                                                ]
                                                            }
                                                        ]
                                                    }
                                                )
                                            )
                            in
                            Ok
                                { conditionExpression = conditionExpression
                                , declarations = declarations
                                , thenExpression = expressionAfterDeconstruction
                                }

                _ ->
                    Err
                        ("Unsupported type of pattern in case-of block case: "
                            ++ Json.Encode.encode 0 (Elm.Syntax.Pattern.encode (Elm.Syntax.Node.value elmPattern))
                        )


pineExpressionFromElmLambda : Elm.Syntax.Expression.Lambda -> Result String PineExpression
pineExpressionFromElmLambda lambda =
    pineExpressionFromElmFunctionWithoutName
        { arguments = lambda.args |> List.map Elm.Syntax.Node.value
        , expression = Elm.Syntax.Node.value lambda.expression
        }


pineExpressionFromElmRecord : List Elm.Syntax.Expression.RecordSetter -> Result String PineExpression
pineExpressionFromElmRecord recordSetters =
    recordSetters
        |> List.map (Tuple.mapFirst Elm.Syntax.Node.value)
        |> List.sortBy Tuple.first
        |> List.map
            (\( fieldName, fieldExpressionNode ) ->
                case pineExpressionFromElm (Elm.Syntax.Node.value fieldExpressionNode) of
                    Err error ->
                        Err ("Failed to map record field: " ++ error)

                    Ok fieldExpression ->
                        Ok
                            (Pine.PineListExpr
                                [ PineLiteral (PineStringOrInteger fieldName)
                                , fieldExpression
                                ]
                            )
            )
        |> Result.Extra.combine
        |> Result.map Pine.PineListExpr


moduleNameFromSyntaxFile : Elm.Syntax.File.File -> Elm.Syntax.Node.Node (List String)
moduleNameFromSyntaxFile file =
    case Elm.Syntax.Node.value file.moduleDefinition of
        Elm.Syntax.Module.NormalModule normalModule ->
            normalModule.moduleName

        Elm.Syntax.Module.PortModule portModule ->
            portModule.moduleName

        Elm.Syntax.Module.EffectModule effectModule ->
            effectModule.moduleName


mapExpressionForOperatorPrecedence : Elm.Syntax.Expression.Expression -> Elm.Syntax.Expression.Expression
mapExpressionForOperatorPrecedence originalExpression =
    case originalExpression of
        Elm.Syntax.Expression.OperatorApplication operator direction leftExpr rightExpr ->
            let
                mappedRightExpr =
                    Elm.Syntax.Node.Node (Elm.Syntax.Node.range rightExpr)
                        (mapExpressionForOperatorPrecedence (Elm.Syntax.Node.value rightExpr))
            in
            case Elm.Syntax.Node.value mappedRightExpr of
                Elm.Syntax.Expression.OperatorApplication rightOperator _ rightLeftExpr rightRightExpr ->
                    let
                        operatorPriority =
                            operatorPrecendencePriority |> Dict.get operator |> Maybe.withDefault 0

                        operatorRightPriority =
                            operatorPrecendencePriority |> Dict.get rightOperator |> Maybe.withDefault 0

                        areStillOrderedBySyntaxRange =
                            compareLocations
                                (Elm.Syntax.Node.range leftExpr).start
                                (Elm.Syntax.Node.range rightLeftExpr).start
                                == LT
                    in
                    if
                        (operatorRightPriority < operatorPriority)
                            || ((operatorRightPriority == operatorPriority) && areStillOrderedBySyntaxRange)
                    then
                        Elm.Syntax.Expression.OperatorApplication rightOperator
                            direction
                            (Elm.Syntax.Node.Node
                                (Elm.Syntax.Range.combine [ Elm.Syntax.Node.range leftExpr, Elm.Syntax.Node.range rightLeftExpr ])
                                (Elm.Syntax.Expression.OperatorApplication operator direction leftExpr rightLeftExpr)
                            )
                            rightRightExpr

                    else
                        Elm.Syntax.Expression.OperatorApplication operator direction leftExpr mappedRightExpr

                _ ->
                    Elm.Syntax.Expression.OperatorApplication operator direction leftExpr mappedRightExpr

        _ ->
            originalExpression


compareLocations : Elm.Syntax.Range.Location -> Elm.Syntax.Range.Location -> Order
compareLocations left right =
    if left.row < right.row then
        LT

    else if right.row < left.row then
        GT

    else
        compare left.column right.column


operatorPrecendencePriority : Dict.Dict String Int
operatorPrecendencePriority =
    [ ( "+", 0 )
    , ( "-", 0 )
    , ( "*", 1 )
    , ( "//", 1 )
    , ( "/", 1 )
    ]
        |> Dict.fromList


parseInteractiveSubmissionFromString : String -> Result { asExpressionError : String, asDeclarationError : String } InteractiveSubmission
parseInteractiveSubmissionFromString submission =
    case parseExpressionFromString submission of
        Ok expression ->
            Ok (ExpressionSubmission expression)

        Err expressionErr ->
            case parseDeclarationFromString submission of
                Ok declaration ->
                    Ok (DeclarationSubmission declaration)

                Err declarationErr ->
                    Err { asExpressionError = expressionErr, asDeclarationError = declarationErr }


parseExpressionFromString : String -> Result String Elm.Syntax.Expression.Expression
parseExpressionFromString expressionCode =
    let
        indentedExpressionCode =
            expressionCode
                |> String.lines
                |> List.map ((++) "    ")
                |> String.join "\n"

        moduleText =
            """
module Main exposing (..)


wrapping_expression_in_function =
"""
                ++ indentedExpressionCode
                ++ """

"""
    in
    parseElmModuleText moduleText
        |> Result.mapError (always "Failed to parse module")
        |> Result.andThen
            (\file ->
                file.declarations
                    |> List.filterMap
                        (\declaration ->
                            case Elm.Syntax.Node.value declaration of
                                Elm.Syntax.Declaration.FunctionDeclaration functionDeclaration ->
                                    functionDeclaration
                                        |> .declaration
                                        |> Elm.Syntax.Node.value
                                        |> .expression
                                        |> Elm.Syntax.Node.value
                                        |> Just

                                _ ->
                                    Nothing
                        )
                    |> List.head
                    |> Result.fromMaybe "Failed to extract the wrapping function."
            )


parseDeclarationFromString : String -> Result String Elm.Syntax.Declaration.Declaration
parseDeclarationFromString declarationCode =
    let
        moduleText =
            """
module Main exposing (..)


"""
                ++ declarationCode
                ++ """

"""
    in
    parseElmModuleText moduleText
        |> Result.mapError (always "Failed to parse module")
        |> Result.andThen
            (\file ->
                file.declarations
                    |> List.map Elm.Syntax.Node.value
                    |> List.head
                    |> Result.fromMaybe "Failed to extract the wrapping function."
            )


parseElmModuleTextToJson : String -> String
parseElmModuleTextToJson elmModule =
    let
        jsonValue =
            case parseElmModuleText elmModule of
                Err _ ->
                    [ ( "Err", "Failed to parse this as module text" |> Json.Encode.string ) ] |> Json.Encode.object

                Ok file ->
                    [ ( "Ok", file |> Elm.Syntax.File.encode ) ] |> Json.Encode.object
    in
    jsonValue |> Json.Encode.encode 0


parseElmModuleText : String -> Result (List Parser.DeadEnd) Elm.Syntax.File.File
parseElmModuleText =
    Elm.Parser.parse >> Result.map (Elm.Processing.process Elm.Processing.init)


stringStartsWithUpper : String -> Bool
stringStartsWithUpper =
    String.uncons >> Maybe.map (Tuple.first >> Char.isUpper) >> Maybe.withDefault False