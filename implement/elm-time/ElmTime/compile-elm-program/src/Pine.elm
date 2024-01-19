module Pine exposing (..)

import BigInt
import Common
import Dict
import Hex
import Maybe.Extra
import Result.Extra


type Expression
    = LiteralExpression Value
    | ListExpression (List Expression)
    | DecodeAndEvaluateExpression DecodeAndEvaluateExpressionStructure
    | KernelApplicationExpression KernelApplicationExpressionStructure
    | ConditionalExpression ConditionalExpressionStructure
    | EnvironmentExpression
    | StringTagExpression String Expression


type alias DecodeAndEvaluateExpressionStructure =
    { expression : Expression
    , environment : Expression
    }


type alias KernelApplicationExpressionStructure =
    { functionName : String
    , argument : Expression
    }


type alias ConditionalExpressionStructure =
    { condition : Expression
    , ifTrue : Expression
    , ifFalse : Expression
    }


type Value
    = BlobValue (List Int)
    | ListValue (List Value)


type alias KernelFunction =
    Value -> Value


type alias EvalContext =
    { environment : Value }


type PathDescription a
    = DescribePathNode a (PathDescription a)
    | DescribePathEnd a


environmentFromDeclarations : List ( String, Value ) -> Value
environmentFromDeclarations declarations =
    declarations |> List.map valueFromContextExpansionWithName |> ListValue


addToEnvironment : List Value -> EvalContext -> EvalContext
addToEnvironment names context =
    let
        environment =
            case context.environment of
                ListValue applicationArgumentList ->
                    ListValue (names ++ applicationArgumentList)

                _ ->
                    ListValue names
    in
    { context | environment = environment }


emptyEvalContext : EvalContext
emptyEvalContext =
    { environment = ListValue [] }


evaluateExpression : EvalContext -> Expression -> Result (PathDescription String) Value
evaluateExpression context expression =
    case expression of
        LiteralExpression value ->
            Ok value

        ListExpression listElements ->
            listElements
                |> List.indexedMap
                    (\listElementIndex listElement ->
                        evaluateExpression context listElement
                            |> Result.mapError
                                (DescribePathNode
                                    ("Failed to evaluate list item " ++ String.fromInt listElementIndex ++ ": ")
                                )
                    )
                |> Result.Extra.combine
                |> Result.map ListValue

        DecodeAndEvaluateExpression decodeAndEvaluate ->
            evaluateDecodeAndEvaluate context decodeAndEvaluate
                |> Result.mapError
                    (\e ->
                        e |> DescribePathNode ("Failed decode and evaluate of '" ++ describeExpression 1 decodeAndEvaluate.expression ++ "'")
                    )

        KernelApplicationExpression application ->
            evaluateExpression context application.argument
                |> Result.mapError (DescribePathNode ("Failed to evaluate argument for kernel function " ++ application.functionName ++ ": "))
                |> Result.andThen
                    (\argument ->
                        application.functionName
                            |> decodeKernelFunctionFromName
                            |> Result.mapError DescribePathEnd
                            |> Result.map (\kernelFunction -> kernelFunction argument)
                    )

        ConditionalExpression conditional ->
            case evaluateExpression context conditional.condition of
                Err error ->
                    Err (DescribePathNode "Failed to evaluate condition" error)

                Ok conditionValue ->
                    evaluateExpression context
                        (if conditionValue == trueValue then
                            conditional.ifTrue

                         else
                            conditional.ifFalse
                        )

        EnvironmentExpression ->
            Ok context.environment

        StringTagExpression tag tagged ->
            let
                log =
                    Debug.log "eval expression with tag"
                        tag
            in
            evaluateExpression context tagged
                |> Result.mapError (DescribePathNode ("Failed to evaluate tagged expression '" ++ tag ++ "': "))


valueFromContextExpansionWithName : ( String, Value ) -> Value
valueFromContextExpansionWithName ( declName, declValue ) =
    ListValue [ valueFromString declName, declValue ]


kernelFunctions : Dict.Dict String KernelFunction
kernelFunctions =
    [ ( "equal"
      , mapFromListValueOrBlobValue { fromList = list_all_same, fromBlob = list_all_same }
            >> valueFromBool
      )
    , ( "negate"
      , kernelFunction_Negate
      )
    , ( "logical_and", kernelFunctionExpectingListOfTypeBool (List.foldl (&&) True) )
    , ( "logical_or", kernelFunctionExpectingListOfTypeBool (List.foldl (||) False) )
    , ( "length"
      , mapFromListValueOrBlobValue { fromList = List.length, fromBlob = List.length }
            >> valueFromInt
      )
    , ( "skip"
      , kernelFunctionExpectingExactlyTwoArguments
            { mapArg0 = intFromValue >> Result.mapError DescribePathEnd
            , mapArg1 = Ok
            , apply =
                \count ->
                    mapFromListValueOrBlobValue
                        { fromList = List.drop count >> ListValue
                        , fromBlob = List.drop count >> BlobValue
                        }
                        >> Ok
            }
      )
    , ( "take"
      , kernelFunctionExpectingExactlyTwoArguments
            { mapArg0 = intFromValue >> Result.mapError DescribePathEnd
            , mapArg1 = Ok
            , apply =
                \count ->
                    mapFromListValueOrBlobValue
                        { fromList = List.take count >> ListValue
                        , fromBlob = List.take count >> BlobValue
                        }
                        >> Ok
            }
      )
    , ( "reverse"
      , mapFromListValueOrBlobValue
            { fromList = List.reverse >> ListValue
            , fromBlob = List.reverse >> BlobValue
            }
      )
    , ( "concat"
      , kernel_function_concat
      )
    , ( "list_head"
      , kernelFunctionExpectingList
            (List.head >> Maybe.withDefault (ListValue []))
      )
    , ( "add_int"
      , kernelFunctionExpectingListOfBigIntAndProducingBigInt BigInt.add (BigInt.fromInt 0)
      )
    , ( "mul_int"
      , kernelFunctionExpectingListOfBigIntAndProducingBigInt BigInt.mul (BigInt.fromInt 1)
      )
    , ( "is_sorted_ascending_int"
      , is_sorted_ascending_int >> valueFromBool
      )
    ]
        |> Dict.fromList


kernelFunctionsNames : List ( String, Value )
kernelFunctionsNames =
    kernelFunctions
        |> Dict.keys
        |> List.map (\name -> ( name, valueFromString name ))


kernelFunction_Negate : KernelFunction
kernelFunction_Negate value =
    case value of
        BlobValue blob ->
            case blob of
                4 :: rest ->
                    BlobValue (2 :: rest)

                2 :: rest ->
                    BlobValue (4 :: rest)

                _ ->
                    ListValue []

        ListValue _ ->
            ListValue []


kernel_function_concat : Value -> Value
kernel_function_concat value =
    case value of
        ListValue list ->
            case list of
                [] ->
                    ListValue []

                (BlobValue _) :: _ ->
                    BlobValue
                        (List.concatMap
                            (\item ->
                                case item of
                                    BlobValue blob ->
                                        blob

                                    _ ->
                                        []
                            )
                            list
                        )

                (ListValue _) :: _ ->
                    ListValue
                        (List.concatMap
                            (\item ->
                                case item of
                                    ListValue innerList ->
                                        innerList

                                    _ ->
                                        []
                            )
                            list
                        )

        _ ->
            ListValue []


list_all_same : List a -> Bool
list_all_same list =
    case list of
        [] ->
            True

        first :: rest ->
            List.all ((==) first) rest


is_sorted_ascending_int : Value -> Bool
is_sorted_ascending_int value =
    value == sort_int value


sort_int : Value -> Value
sort_int value =
    case value of
        BlobValue _ ->
            value

        ListValue list ->
            list
                |> List.map sort_int
                |> List.sortWith sort_int_order
                |> ListValue


sort_int_order : Value -> Value -> Order
sort_int_order x y =
    case ( x, y ) of
        ( BlobValue blobX, BlobValue blobY ) ->
            case ( bigIntFromBlobValue blobX, bigIntFromBlobValue blobY ) of
                ( Ok intX, Ok intY ) ->
                    BigInt.compare intX intY

                ( Ok _, Err _ ) ->
                    LT

                ( Err _, Ok _ ) ->
                    GT

                ( Err _, Err _ ) ->
                    EQ

        ( ListValue listX, ListValue listY ) ->
            compare (List.length listX) (List.length listY)

        ( ListValue _, BlobValue _ ) ->
            LT

        ( BlobValue _, ListValue _ ) ->
            GT


mapFromListValueOrBlobValue : { fromList : List Value -> a, fromBlob : List Int -> a } -> Value -> a
mapFromListValueOrBlobValue { fromList, fromBlob } value =
    case value of
        ListValue list ->
            fromList list

        BlobValue blob ->
            fromBlob blob


evaluateDecodeAndEvaluate : EvalContext -> DecodeAndEvaluateExpressionStructure -> Result (PathDescription String) Value
evaluateDecodeAndEvaluate context decodeAndEvaluate =
    evaluateExpression context decodeAndEvaluate.environment
        |> Result.mapError
            (\e ->
                e |> DescribePathNode ("Failed to evaluate environment '" ++ describeExpression 1 decodeAndEvaluate.environment ++ "'")
            )
        |> Result.andThen
            (\environmentValue ->
                evaluateExpression context decodeAndEvaluate.expression
                    |> Result.mapError
                        (\e ->
                            e |> DescribePathNode ("Failed to evaluate encoded expression '" ++ describeExpression 1 decodeAndEvaluate.expression ++ "'")
                        )
                    |> Result.andThen
                        (\functionValue ->
                            functionValue
                                |> decodeExpressionFromValue
                                |> Result.mapError
                                    (\e ->
                                        e
                                            |> DescribePathEnd
                                            |> DescribePathNode ("Failed to decode expression from value '" ++ describeValue 3 functionValue ++ "'")
                                    )
                                |> Result.andThen
                                    (\functionExpression ->
                                        evaluateExpression { environment = environmentValue } functionExpression
                                    )
                        )
            )


kernelFunctionExpectingListOfBigIntAndProducingBigInt :
    (BigInt.BigInt -> BigInt.BigInt -> BigInt.BigInt)
    -> BigInt.BigInt
    -> KernelFunction
kernelFunctionExpectingListOfBigIntAndProducingBigInt combine seed =
    let
        combineRecursive : List Value -> BigInt.BigInt -> Value
        combineRecursive remainingList aggregate =
            case remainingList of
                [] ->
                    valueFromBigInt aggregate

                itemValue :: following ->
                    case bigIntFromValue itemValue of
                        Err _ ->
                            ListValue []

                        Ok itemInt ->
                            combineRecursive following (combine aggregate itemInt)
    in
    kernelFunctionExpectingList
        (\list -> combineRecursive list seed)


kernelFunctionExpectingListOfTypeBool : (List Bool -> Bool) -> KernelFunction
kernelFunctionExpectingListOfTypeBool apply =
    kernelFunctionExpectingList
        (\list ->
            case list of
                [] ->
                    ListValue []

                _ ->
                    List.map boolFromValue list
                        |> Maybe.Extra.combine
                        |> Maybe.map (apply >> valueFromBool)
                        |> Maybe.withDefault (ListValue [])
        )


kernelFunctionExpectingExactlyTwoArguments :
    { mapArg0 : Value -> Result (PathDescription String) arg0
    , mapArg1 : Value -> Result (PathDescription String) arg1
    , apply : arg0 -> arg1 -> Result (PathDescription String) Value
    }
    -> KernelFunction
kernelFunctionExpectingExactlyTwoArguments configuration =
    kernelFunctionExpectingList
        (\list ->
            case list of
                [ arg0Value, arg1Value ] ->
                    case configuration.mapArg0 arg0Value of
                        Err _ ->
                            ListValue []

                        Ok arg0 ->
                            case configuration.mapArg1 arg1Value of
                                Err _ ->
                                    ListValue []

                                Ok arg1 ->
                                    case configuration.apply arg0 arg1 of
                                        Err _ ->
                                            ListValue []

                                        Ok ok ->
                                            ok

                _ ->
                    ListValue []
        )


kernelFunctionExpectingList : (List Value -> Value) -> KernelFunction
kernelFunctionExpectingList continueWithList value =
    case value of
        ListValue list ->
            continueWithList list

        _ ->
            ListValue []


boolFromValue : Value -> Maybe Bool
boolFromValue value =
    if value == trueValue then
        Just True

    else if value == falseValue then
        Just False

    else
        Nothing


valueFromBool : Bool -> Value
valueFromBool bool =
    if bool then
        trueValue

    else
        falseValue


trueValue : Value
trueValue =
    BlobValue [ 4 ]


falseValue : Value
falseValue =
    BlobValue [ 2 ]


describeExpression : Int -> Expression -> String
describeExpression depthLimit expression =
    case expression of
        ListExpression list ->
            "list["
                ++ (if depthLimit < 1 then
                        "..."

                    else
                        String.join "," (list |> List.map (describeExpression (depthLimit - 1)))
                   )
                ++ "]"

        LiteralExpression literal ->
            "literal(" ++ describeValue (depthLimit - 1) literal ++ ")"

        DecodeAndEvaluateExpression decodeAndEvaluate ->
            "decode-and-evaluate("
                ++ (if depthLimit < 1 then
                        "..."

                    else
                        describeExpression (depthLimit - 1) decodeAndEvaluate.expression
                   )
                ++ ")"

        KernelApplicationExpression application ->
            "kernel-application(" ++ application.functionName ++ ")"

        ConditionalExpression _ ->
            "conditional"

        EnvironmentExpression ->
            "environment"

        StringTagExpression tag tagged ->
            "string-tag-" ++ tag ++ "(" ++ describeExpression (depthLimit - 1) tagged ++ ")"


describeValue : Int -> Value -> String
describeValue maxDepth value =
    case value of
        BlobValue blob ->
            "BlobValue 0x" ++ hexadecimalRepresentationFromBlobValue blob

        ListValue list ->
            let
                standard =
                    "["
                        ++ (if maxDepth < 0 then
                                "..."

                            else
                                String.join ", " (List.map (describeValue (maxDepth - 1)) list)
                           )
                        ++ "]"
            in
            String.join " "
                [ "ListValue"
                , case stringFromValue value of
                    Err _ ->
                        ""

                    Ok string ->
                        "\"" ++ string ++ "\""
                , standard
                ]


displayStringFromPineError : PathDescription String -> String
displayStringFromPineError error =
    case error of
        DescribePathEnd end ->
            end

        DescribePathNode nodeDescription node ->
            nodeDescription ++ "\n" ++ prependAllLines "  " (displayStringFromPineError node)


prependAllLines : String -> String -> String
prependAllLines prefix text =
    text
        |> String.lines
        |> List.map ((++) prefix)
        |> String.join "\n"


valueFromString : String -> Value
valueFromString string =
    ListValue
        (String.foldr (\char aggregate -> valueFromChar char :: aggregate) [] string)


valueFromChar : Char -> Value
valueFromChar char =
    BlobValue (unsafeUnsignedBlobValueFromInt (Char.toCode char))


stringFromValue : Value -> Result String String
stringFromValue value =
    case value of
        ListValue charsValues ->
            stringFromListValue charsValues

        _ ->
            Err "Only a ListValue can represent a string."


stringFromListValue : List Value -> Result String String
stringFromListValue values =
    let
        continueRecursive : List Value -> List Char -> Result String (List Char)
        continueRecursive remaining processed =
            case remaining of
                [] ->
                    Ok processed

                charValue :: rest ->
                    case charValue of
                        BlobValue intValueBytes ->
                            case intValueBytes of
                                [ b1 ] ->
                                    continueRecursive rest (Char.fromCode b1 :: processed)

                                [ b1, b2 ] ->
                                    continueRecursive rest (Char.fromCode ((b1 * 256) + b2) :: processed)

                                [ b1, b2, b3 ] ->
                                    continueRecursive rest (Char.fromCode ((b1 * 65536) + (b2 * 256) + b3) :: processed)

                                _ ->
                                    Err
                                        ("Failed to map to char - unsupported number of bytes: "
                                            ++ String.fromInt (List.length intValueBytes)
                                        )

                        _ ->
                            Err "Failed to map to char - not a BlobValue"
    in
    case continueRecursive values [] of
        Err err ->
            Err ("Failed to map list items to chars: " ++ err)

        Ok chars ->
            Ok (String.fromList (List.reverse chars))


valueFromBigInt : BigInt.BigInt -> Value
valueFromBigInt bigInt =
    BlobValue (blobValueFromBigInt bigInt)


blobValueFromBigInt : BigInt.BigInt -> List Int
blobValueFromBigInt bigint =
    let
        value =
            BigInt.abs bigint

        signByte =
            if value == bigint then
                4

            else
                2

        unsignedBytesFromIntValue intValue =
            if BigInt.lt intValue (BigInt.fromInt 0x0100) then
                String.toInt (BigInt.toString intValue) |> Maybe.map List.singleton

            else
                case BigInt.divmod intValue (BigInt.fromInt 0x0100) of
                    Nothing ->
                        Nothing

                    Just ( upper, lower ) ->
                        case unsignedBytesFromIntValue upper of
                            Nothing ->
                                Nothing

                            Just upperBytes ->
                                case String.toInt (BigInt.toString lower) of
                                    Nothing ->
                                        Nothing

                                    Just lowerByte ->
                                        Just (upperBytes ++ [ lowerByte ])
    in
    signByte :: Maybe.withDefault [] (unsignedBytesFromIntValue value)


valueFromInt : Int -> Value
valueFromInt int =
    BlobValue (blobValueFromInt int)


blobValueFromInt : Int -> List Int
blobValueFromInt int =
    if int < 0 then
        2 :: unsafeUnsignedBlobValueFromInt (abs int)

    else
        4 :: unsafeUnsignedBlobValueFromInt int


unsafeUnsignedBlobValueFromInt : Int -> List Int
unsafeUnsignedBlobValueFromInt int =
    let
        recursiveFromInt : Int -> List Int -> List Int
        recursiveFromInt remaining aggregate =
            if remaining < 0x0100 then
                remaining :: aggregate

            else
                let
                    lower =
                        modBy 0x0100 remaining
                in
                recursiveFromInt (remaining // 0x0100) (lower :: aggregate)
    in
    recursiveFromInt int []


intFromValue : Value -> Result String Int
intFromValue value =
    case value of
        ListValue _ ->
            Err "Only a BlobValue can encode an integer."

        BlobValue blobBytes ->
            case blobBytes of
                [] ->
                    Err "Empty blob does not encode an integer."

                sign :: intValueBytes ->
                    case sign of
                        4 ->
                            intFromUnsignedBlobValue intValueBytes

                        2 ->
                            Result.map negate (intFromUnsignedBlobValue intValueBytes)

                        _ ->
                            Err ("Unexpected value for sign byte: " ++ String.fromInt sign)


intFromUnsignedBlobValue : List Int -> Result String Int
intFromUnsignedBlobValue intValueBytes =
    case intValueBytes of
        [] ->
            Err "Empty blob does not encode an integer."

        [ b1 ] ->
            Ok b1

        [ b1, b2 ] ->
            Ok ((b1 * 256) + b2)

        [ b1, b2, b3 ] ->
            Ok ((b1 * 65536) + (b2 * 256) + b3)

        [ b1, b2, b3, b4 ] ->
            Ok ((b1 * 16777216) + (b2 * 65536) + (b3 * 256) + b4)

        [ b1, b2, b3, b4, b5 ] ->
            Ok ((b1 * 4294967296) + (b2 * 16777216) + (b3 * 65536) + (b4 * 256) + b5)

        [ b1, b2, b3, b4, b5, b6 ] ->
            Ok ((b1 * 1099511627776) + (b2 * 4294967296) + (b3 * 16777216) + (b4 * 65536) + (b5 * 256) + b6)

        _ ->
            Err
                ("Failed to map to int - unsupported number of bytes: "
                    ++ String.fromInt (List.length intValueBytes)
                )


intFromBigInt : BigInt.BigInt -> Result String Int
intFromBigInt bigInt =
    case bigInt |> BigInt.toString |> String.toInt of
        Nothing ->
            Err "Failed to String.toInt"

        Just int ->
            if String.fromInt int /= BigInt.toString bigInt then
                Err "Integer out of supported range for String.toInt"

            else
                Ok int


bigIntFromValue : Value -> Result String BigInt.BigInt
bigIntFromValue value =
    case value of
        BlobValue blobValue ->
            bigIntFromBlobValue blobValue

        _ ->
            Err "Only a BlobValue can encode an integer."


bigIntFromBlobValue : List Int -> Result String BigInt.BigInt
bigIntFromBlobValue blobValue =
    case blobValue of
        [] ->
            Err "Empty blob does not encode an integer."

        sign :: intValueBytes ->
            case sign of
                4 ->
                    Ok (bigIntFromUnsignedBlobValue intValueBytes)

                2 ->
                    Ok (BigInt.negate (intValueBytes |> bigIntFromUnsignedBlobValue))

                _ ->
                    Err ("Unexpected value for sign byte: " ++ String.fromInt sign)


bigIntFromUnsignedBlobValue : List Int -> BigInt.BigInt
bigIntFromUnsignedBlobValue intValueBytes =
    List.foldl
        (\nextByte aggregate ->
            BigInt.add (BigInt.fromInt nextByte) (BigInt.mul (BigInt.fromInt 0x0100) aggregate)
        )
        (BigInt.fromInt 0)
        intValueBytes


hexadecimalRepresentationFromBlobValue : List Int -> String
hexadecimalRepresentationFromBlobValue =
    List.map (Hex.toString >> String.padLeft 2 '0')
        >> String.join ""


encodeExpressionAsValue : Expression -> Value
encodeExpressionAsValue expression =
    case expression of
        LiteralExpression literal ->
            encodeUnionToPineValue
                stringAsValue_Literal
                literal

        ListExpression listExpr ->
            encodeUnionToPineValue
                stringAsValue_List
                (ListValue (List.map encodeExpressionAsValue listExpr))

        DecodeAndEvaluateExpression decodeAndEvaluate ->
            encodeUnionToPineValue
                stringAsValue_DecodeAndEvaluate
                (ListValue
                    [ ListValue [ stringAsValue_environment, encodeExpressionAsValue decodeAndEvaluate.environment ]
                    , ListValue [ stringAsValue_expression, encodeExpressionAsValue decodeAndEvaluate.expression ]
                    ]
                )

        KernelApplicationExpression app ->
            encodeUnionToPineValue
                stringAsValue_KernelApplication
                (ListValue
                    [ ListValue [ stringAsValue_argument, encodeExpressionAsValue app.argument ]
                    , ListValue [ stringAsValue_functionName, valueFromString app.functionName ]
                    ]
                )

        ConditionalExpression conditional ->
            encodeUnionToPineValue
                stringAsValue_Conditional
                (ListValue
                    [ ListValue [ stringAsValue_condition, encodeExpressionAsValue conditional.condition ]
                    , ListValue [ stringAsValue_ifFalse, encodeExpressionAsValue conditional.ifFalse ]
                    , ListValue [ stringAsValue_ifTrue, encodeExpressionAsValue conditional.ifTrue ]
                    ]
                )

        EnvironmentExpression ->
            encodeUnionToPineValue
                stringAsValue_Environment
                (ListValue [])

        StringTagExpression tag tagged ->
            encodeUnionToPineValue
                stringAsValue_StringTag
                (ListValue
                    [ valueFromString tag
                    , encodeExpressionAsValue tagged
                    ]
                )


decodeExpressionFromValue : Value -> Result String Expression
decodeExpressionFromValue =
    decodeUnionFromPineValue decodeExpressionFromValueDict


decodeExpressionFromValueDict : List ( ( String, Value ), Value -> Result String Expression )
decodeExpressionFromValueDict =
    [ ( "Literal"
      , LiteralExpression >> Ok
      )
    , ( "List"
      , decodeListExpression
      )
    , ( "DecodeAndEvaluate"
      , decodeDecodeAndEvaluateExpression >> Result.map DecodeAndEvaluateExpression
      )
    , ( "KernelApplication"
      , decodeKernelApplicationExpression >> Result.map KernelApplicationExpression
      )
    , ( "Conditional"
      , decodeConditionalExpression >> Result.map ConditionalExpression
      )
    , ( "Environment"
      , always (Ok EnvironmentExpression)
      )
    , ( "StringTag"
      , decodePineListValue
            >> Result.andThen decodeListWithExactlyTwoElements
            >> Result.andThen
                (\( tagValue, taggedValue ) ->
                    tagValue
                        |> stringFromValue
                        |> Result.mapError ((++) "Failed to decode tag: ")
                        |> Result.andThen
                            (\tag ->
                                taggedValue
                                    |> decodeExpressionFromValue
                                    |> Result.mapError ((++) "Failed to decoded tagged expression: ")
                                    |> Result.map (\tagged -> StringTagExpression tag tagged)
                            )
                )
      )
    ]
        |> List.map (\( tagName, decode ) -> ( ( tagName, valueFromString tagName ), decode ))


decodeListExpression : Value -> Result String Expression
decodeListExpression value =
    let
        decodeListRecursively : List Expression -> List Value -> Result String Expression
        decodeListRecursively aggregate remaining =
            case remaining of
                [] ->
                    Ok (ListExpression (List.reverse aggregate))

                itemValue :: rest ->
                    case decodeExpressionFromValue itemValue of
                        Err itemErr ->
                            Err
                                ("Failed to decode list item at index "
                                    ++ String.fromInt (List.length aggregate)
                                    ++ ": "
                                    ++ itemErr
                                )

                        Ok item ->
                            decodeListRecursively (item :: aggregate) rest
    in
    case value of
        BlobValue _ ->
            Err "Is not list but blob"

        ListValue list ->
            decodeListRecursively [] list


decodeDecodeAndEvaluateExpression : Value -> Result String DecodeAndEvaluateExpressionStructure
decodeDecodeAndEvaluateExpression value =
    case value of
        BlobValue _ ->
            Err "Is not list but blob"

        ListValue list ->
            case decodeListWithPairs list of
                Err err ->
                    Err ("Failed to decode kernel application expression: " ++ err)

                Ok pairs ->
                    case
                        Common.listFind
                            (\( fieldNameValue, _ ) ->
                                fieldNameValue == stringAsValue_expression
                            )
                            pairs
                    of
                        Nothing ->
                            Err "Did not find field 'expression'"

                        Just ( _, expressionValue ) ->
                            case decodeExpressionFromValue expressionValue of
                                Err error ->
                                    Err ("Failed to decode field 'expression': " ++ error)

                                Ok expression ->
                                    case
                                        Common.listFind
                                            (\( fieldNameValue, _ ) ->
                                                fieldNameValue == stringAsValue_environment
                                            )
                                            pairs
                                    of
                                        Nothing ->
                                            Err "Did not find field 'environment'"

                                        Just ( _, environmentValue ) ->
                                            case decodeExpressionFromValue environmentValue of
                                                Err error ->
                                                    Err ("Failed to decode field 'environment': " ++ error)

                                                Ok environment ->
                                                    Ok
                                                        { expression = expression
                                                        , environment = environment
                                                        }


decodeKernelApplicationExpression : Value -> Result String KernelApplicationExpressionStructure
decodeKernelApplicationExpression expressionValue =
    case expressionValue of
        BlobValue _ ->
            Err "Is not list but blob"

        ListValue list ->
            case decodeListWithPairs list of
                Err err ->
                    Err ("Failed to decode kernel application expression: " ++ err)

                Ok pairs ->
                    case
                        Common.listFind
                            (\( fieldNameValue, _ ) ->
                                fieldNameValue == stringAsValue_functionName
                            )
                            pairs
                    of
                        Nothing ->
                            Err "Did not find field 'functionName'"

                        Just ( _, functionNameValue ) ->
                            case
                                Common.listFind
                                    (\( _, cvalue ) -> cvalue == functionNameValue)
                                    kernelFunctionsNames
                            of
                                Nothing ->
                                    Err
                                        ("Unexpected value for field 'functionName': "
                                            ++ Result.withDefault "not a string" (stringFromValue functionNameValue)
                                        )

                                Just ( functionName, _ ) ->
                                    case
                                        Common.listFind
                                            (\( fieldNameValue, _ ) ->
                                                fieldNameValue == stringAsValue_argument
                                            )
                                            pairs
                                    of
                                        Nothing ->
                                            Err "Did not find field 'argument'"

                                        Just ( _, argumentValue ) ->
                                            case decodeExpressionFromValue argumentValue of
                                                Err error ->
                                                    Err ("Failed to decode field 'argument': " ++ error)

                                                Ok argument ->
                                                    Ok
                                                        { functionName = functionName
                                                        , argument = argument
                                                        }


decodeKernelFunctionFromName : String -> Result String KernelFunction
decodeKernelFunctionFromName functionName =
    case Dict.get functionName kernelFunctions of
        Nothing ->
            Err
                ("Did not find kernel function '"
                    ++ functionName
                    ++ "'. There are "
                    ++ String.fromInt (Dict.size kernelFunctions)
                    ++ " kernel functions available: "
                    ++ String.join ", " (Dict.keys kernelFunctions)
                )

        Just kernelFunction ->
            Ok kernelFunction


decodeConditionalExpression : Value -> Result String ConditionalExpressionStructure
decodeConditionalExpression expressionValue =
    case expressionValue of
        BlobValue _ ->
            Err "Is not list but blob"

        ListValue list ->
            case decodeListWithPairs list of
                Err err ->
                    Err ("Failed to decode kernel application expression: " ++ err)

                Ok pairs ->
                    case
                        Common.listFind
                            (\( fieldNameValue, _ ) ->
                                fieldNameValue == stringAsValue_condition
                            )
                            pairs
                    of
                        Nothing ->
                            Err "Did not find field 'condition'"

                        Just ( _, conditionValue ) ->
                            case decodeExpressionFromValue conditionValue of
                                Err error ->
                                    Err ("Failed to decode field 'condition': " ++ error)

                                Ok condition ->
                                    case
                                        Common.listFind
                                            (\( fieldNameValue, _ ) ->
                                                fieldNameValue == stringAsValue_ifTrue
                                            )
                                            pairs
                                    of
                                        Nothing ->
                                            Err "Did not find field 'ifTrue'"

                                        Just ( _, ifTrueValue ) ->
                                            case decodeExpressionFromValue ifTrueValue of
                                                Err error ->
                                                    Err ("Failed to decode field 'ifTrue': " ++ error)

                                                Ok ifTrue ->
                                                    case
                                                        Common.listFind
                                                            (\( fieldNameValue, _ ) ->
                                                                fieldNameValue == stringAsValue_ifFalse
                                                            )
                                                            pairs
                                                    of
                                                        Nothing ->
                                                            Err "Did not find field 'ifFalse'"

                                                        Just ( _, ifFalseValue ) ->
                                                            case decodeExpressionFromValue ifFalseValue of
                                                                Err error ->
                                                                    Err ("Failed to decode field 'ifFalse': " ++ error)

                                                                Ok ifFalse ->
                                                                    Ok
                                                                        { condition = condition
                                                                        , ifTrue = ifTrue
                                                                        , ifFalse = ifFalse
                                                                        }


encodeUnionToPineValue : Value -> Value -> Value
encodeUnionToPineValue tagNameValue unionTagValue =
    ListValue [ tagNameValue, unionTagValue ]


decodeUnionFromPineValue : List ( ( String, Value ), Value -> Result String a ) -> Value -> Result String a
decodeUnionFromPineValue tags value =
    case Result.andThen decodeListWithExactlyTwoElements (decodePineListValue value) of
        Err err ->
            Err ("Failed to decode union: " ++ err)

        Ok ( tagNameValue, unionTagValue ) ->
            case
                Common.listFind
                    (\( ( _, availableTagValue ), _ ) -> availableTagValue == tagNameValue)
                    tags
            of
                Nothing ->
                    Err
                        ("Unexpected tag name: "
                            ++ Result.Extra.unpack identity identity (stringFromValue tagNameValue)
                        )

                Just ( ( tagName, _ ), tagDecode ) ->
                    case tagDecode unionTagValue of
                        Err err ->
                            Err (("Failed to decode value for tag " ++ tagName ++ ": ") ++ err)

                        Ok ok ->
                            Ok ok


decodeListWithPairs : List Value -> Result String (List ( Value, Value ))
decodeListWithPairs list =
    let
        continueRecursive : List Value -> List ( Value, Value ) -> Result String (List ( Value, Value ))
        continueRecursive remaining aggregate =
            case remaining of
                [] ->
                    Ok (List.reverse aggregate)

                itemValue :: rest ->
                    case itemValue of
                        BlobValue _ ->
                            Err "Is not list but blob"

                        ListValue [ first, second ] ->
                            continueRecursive rest (( first, second ) :: aggregate)

                        ListValue innerList ->
                            Err
                                ("Unexpected number of list items for pair: "
                                    ++ String.fromInt (List.length innerList)
                                )
    in
    continueRecursive list []


decodeListWithExactlyTwoElements : List a -> Result String ( a, a )
decodeListWithExactlyTwoElements list =
    case list of
        [ a, b ] ->
            Ok ( a, b )

        _ ->
            Err ("Unexpected number of elements in list: Not 2 but " ++ String.fromInt (List.length list))


decodePineListValue : Value -> Result String (List Value)
decodePineListValue value =
    case value of
        ListValue list ->
            Ok list

        BlobValue _ ->
            Err "Is not list but blob"


stringAsValue_Literal : Value
stringAsValue_Literal =
    valueFromString "Literal"


stringAsValue_List : Value
stringAsValue_List =
    valueFromString "List"


stringAsValue_DecodeAndEvaluate : Value
stringAsValue_DecodeAndEvaluate =
    valueFromString "DecodeAndEvaluate"


stringAsValue_KernelApplication : Value
stringAsValue_KernelApplication =
    valueFromString "KernelApplication"


stringAsValue_Conditional : Value
stringAsValue_Conditional =
    valueFromString "Conditional"


stringAsValue_Environment : Value
stringAsValue_Environment =
    valueFromString "Environment"


stringAsValue_Function : Value
stringAsValue_Function =
    valueFromString "Function"


stringAsValue_StringTag : Value
stringAsValue_StringTag =
    valueFromString "StringTag"


stringAsValue_functionName : Value
stringAsValue_functionName =
    valueFromString "functionName"


stringAsValue_argument : Value
stringAsValue_argument =
    valueFromString "argument"


stringAsValue_condition : Value
stringAsValue_condition =
    valueFromString "condition"


stringAsValue_ifTrue : Value
stringAsValue_ifTrue =
    valueFromString "ifTrue"


stringAsValue_ifFalse : Value
stringAsValue_ifFalse =
    valueFromString "ifFalse"


stringAsValue_environment : Value
stringAsValue_environment =
    valueFromString "environment"


stringAsValue_expression : Value
stringAsValue_expression =
    valueFromString "expression"
