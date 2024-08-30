using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Pine.PineVM;

#pragma warning disable IDE1006

public static class KernelFunction
{
    public static PineValue equal(PineValue value)
    {
        if (value is PineValue.ListValue listValue)
        {
            /*
            * :: rest pattern seems not implemented yet:
            * https://github.com/dotnet/csharplang/issues/6574
            * */

            if (listValue.Elements.Count < 1)
                return PineVMValues.TrueValue;

            var firstItem = listValue.Elements[0];

            for (var i = 1; i < listValue.Elements.Count; ++i)
            {
                if (!listValue.Elements[i].Equals(firstItem))
                    return PineVMValues.FalseValue;
            }

            return PineVMValues.TrueValue;
        }

        if (value is PineValue.BlobValue blobValue)
        {
            return
                BlobAllBytesEqual(blobValue.Bytes)
                ?
                PineVMValues.TrueValue
                :
                PineVMValues.FalseValue;
        }

        throw new NotImplementedException(
            "Unexpected value type: " + value.GetType().FullName);
    }

    private static bool BlobAllBytesEqual(ReadOnlyMemory<byte> readOnlyMemory) =>
        readOnlyMemory.IsEmpty ||
        !readOnlyMemory.Span.ContainsAnyExcept(readOnlyMemory.Span[0]);

    public static PineValue equal(PineValue valueA, PineValue valueB) =>
        ValueFromBool(valueA.Equals(valueB));

    public static PineValue negate(PineValue value) =>
        value switch
        {
            PineValue.BlobValue blobValue
            when 0 < blobValue.Bytes.Length =>
            blobValue.Bytes.Span[0] switch
            {
                4 =>
                PineValue.Blob([2, .. blobValue.Bytes.Span[1..]]),

                2 =>
                PineValue.Blob([4, .. blobValue.Bytes.Span[1..]]),

                _ =>
                PineValue.EmptyList
            },

            _ =>
            PineValue.EmptyList
        };

    public static PineValue length(PineValue value) =>
        PineValueAsInteger.ValueFromSignedInteger(
            value switch
            {
                PineValue.BlobValue blobValue =>
                blobValue.Bytes.Length,

                PineValue.ListValue listValue =>
                listValue.Elements.Count,

                _ => throw new NotImplementedException()
            });

    public static PineValue skip(PineValue value) =>
        value switch
        {
            PineValue.ListValue listValue =>
            listValue.Elements.Count is 2 ?
            SignedIntegerFromValueRelaxed(listValue.Elements[0]) switch
            {
                { } count =>
                skip(count, listValue.Elements[1]),

                _ =>
                PineValue.EmptyList
            }
            :
            PineValue.EmptyList,

            PineValue.BlobValue =>
            PineValue.EmptyList,

            _ =>
            throw new NotImplementedException()
        };

    public static PineValue skip(BigInteger count, PineValue value)
    {
        if (count <= 0)
            return value;

        if (value is PineValue.BlobValue blobValue)
        {
            if (blobValue.Bytes.Length <= count)
                return PineValue.EmptyBlob;

            return PineValue.Blob(blobValue.Bytes[(int)count..]);
        }

        if (value is PineValue.ListValue listValue)
        {
            if (listValue.Elements.Count <= count)
                return PineValue.EmptyList;

            var skipped = new PineValue[listValue.Elements.Count - (int)count];

            for (var i = 0; i < skipped.Length; ++i)
                skipped[i] = listValue.Elements[i + (int)count];

            return PineValue.List(skipped);
        }

        throw new NotImplementedException(
            "Unexpected value type: " + value.GetType().FullName);
    }

    public static PineValue take(PineValue value) =>
        value switch
        {
            PineValue.ListValue listValue =>
            listValue.Elements.Count is 2 ?
            SignedIntegerFromValueRelaxed(listValue.Elements[0]) switch
            {
                { } count =>
                take(count, listValue.Elements[1]),

                _ =>
                PineValue.EmptyList
            }
            :
            PineValue.EmptyList,

            PineValue.BlobValue =>
            PineValue.EmptyList,

            _ =>
            throw new NotImplementedException()
        };

    public static PineValue take(BigInteger count, PineValue value)
    {
        if (value is PineValue.ListValue listValue)
        {
            if (listValue.Elements.Count <= count)
                return value;

            if (count <= 0)
                return PineValue.EmptyList;

            var resultingCount = count <= listValue.Elements.Count ? (int)count : listValue.Elements.Count;

            var taken = new PineValue[resultingCount];

            for (var i = 0; i < taken.Length; ++i)
            {
                taken[i] = listValue.Elements[i];
            }

            return PineValue.List(taken);
        }

        if (value is PineValue.BlobValue blobValue)
        {
            if (blobValue.Bytes.Length <= count)
                return value;

            if (count <= 0)
                return PineValue.EmptyBlob;

            return PineValue.Blob(blobValue.Bytes[..(int)count]);
        }

        throw new NotImplementedException(
            "Unexpected value type: " + value.GetType().FullName);
    }

    public static PineValue reverse(PineValue value)
    {
        if (value is PineValue.ListValue listValue)
        {
            if (listValue.Elements.Count <= 1)
                return value;

            var reversed = listValue.Elements.ToArray();

            Array.Reverse(reversed);

            return PineValue.List(reversed);
        }

        if (value is PineValue.BlobValue blobValue)
        {
            if (blobValue.Bytes.Length <= 1)
                return value;

            var reversed = blobValue.Bytes.ToArray();

            MemoryExtensions.Reverse(reversed.AsMemory().Span);

            return PineValue.Blob(reversed);
        }

        throw new NotImplementedException(
            "Unexpected value type: " + value.GetType().FullName);
    }

    public static PineValue concat(PineValue value)
    {
        if (value is not PineValue.ListValue listValue)
        {
            return PineValue.EmptyList;
        }

        if (listValue.Elements.Count is 0)
        {
            return PineValue.EmptyList;
        }

        var head = listValue.Elements[0];

        if (listValue.Elements.Count is 1)
        {
            return head;
        }

        if (head is PineValue.ListValue)
        {
            var aggregated = new List<PineValue>(capacity: 40);

            for (var i = 0; i < listValue.Elements.Count; ++i)
            {
                if (listValue.Elements[i] is PineValue.ListValue listValueElement)
                {
                    aggregated.AddRange(listValueElement.Elements);
                }
            }

            return PineValue.List(aggregated);
        }

        if (head is PineValue.BlobValue)
        {
            var blobs = new List<ReadOnlyMemory<byte>>(capacity: listValue.Elements.Count);

            for (int i = 0; i < listValue.Elements.Count; ++i)
            {
                if (listValue.Elements[i] is not PineValue.BlobValue blobValue)
                    continue;

                blobs.Add(blobValue.Bytes);
            }

            return PineValue.Blob(CommonConversion.Concat(blobs));
        }

        throw new NotImplementedException(
            "Unexpected value type: " + head.GetType().FullName);
    }

    public static PineValue head(PineValue value) =>
        value switch
        {
            PineValue.ListValue listValue =>
            listValue.Elements switch
            {
            [] => PineValue.EmptyList,
            [var head, ..] => head,
            },

            _ =>
            PineValue.EmptyList
        };

    public static PineValue add_int(PineValue value) =>
        KernelFunctionExpectingListOfBigIntAndProducingBigInt(
            integers =>
            integers.Aggregate(seed: BigInteger.Zero, func: (aggregate, next) => aggregate + next),
            value);

    public static PineValue add_int(PineValue summandA, PineValue summandB)
    {
        if (SignedIntegerFromValueRelaxed(summandA) is not { } intA)
            return PineValue.EmptyList;

        if (SignedIntegerFromValueRelaxed(summandB) is not { } intB)
            return PineValue.EmptyList;

        return add_int(intA, intB);
    }

    public static PineValue add_int(BigInteger summandA, BigInteger summandB) =>
        PineValueAsInteger.ValueFromSignedInteger(summandA + summandB);

    public static PineValue mul_int(PineValue value) =>
        KernelFunctionExpectingListOfBigIntAndProducingBigInt(
            integers =>
            integers.Aggregate(seed: BigInteger.One, func: (aggregate, next) => aggregate * next),
            value);

    public static PineValue mul_int(PineValue factorA, PineValue factorB)
    {
        if (SignedIntegerFromValueRelaxed(factorA) is not { } intA)
            return PineValue.EmptyList;

        if (SignedIntegerFromValueRelaxed(factorB) is not { } intB)
            return PineValue.EmptyList;

        return mul_int(intA, intB);
    }

    public static PineValue mul_int(BigInteger factorA, BigInteger factorB) =>
        PineValueAsInteger.ValueFromSignedInteger(factorA * factorB);

    public static PineValue is_sorted_ascending_int(PineValue value)
    {
        if (value is PineValue.BlobValue blobValue)
        {
            var isSorted = true;

            if (blobValue.Bytes.Length > 0)
            {
                var previous = blobValue.Bytes.Span[0];

                foreach (var current in blobValue.Bytes.Span[1..])
                {
                    if (current < previous)
                    {
                        isSorted = false;
                        break;
                    }

                    previous = current;
                }
            }

            return ValueFromBool(isSorted);
        }

        if (value is PineValue.ListValue listValue)
        {
            if (listValue.Elements.Count is 0)
                return ValueFromBool(true);

            if (SignedIntegerFromValueRelaxed(listValue.Elements[0]) is not { } firstInt)
                return PineValue.EmptyList;

            var previous = firstInt;

            foreach (var next in listValue.Elements)
            {
                if (SignedIntegerFromValueRelaxed(next) is not { } nextInt)
                    return PineValue.EmptyList;

                if (nextInt < previous)
                    return ValueFromBool(false);

                previous = nextInt;
            }

            return ValueFromBool(true);
        }

        throw new NotImplementedException("Unexpected value type: " + value.GetType().FullName);
    }

    private static PineValue KernelFunctionExpectingListOfBigIntAndProducingBigInt(
        Func<IReadOnlyList<BigInteger>, BigInteger> aggregate,
        PineValue value) =>
        KernelFunctionExpectingListOfBigInt(
            aggregate:
            listOfIntegers => PineValueAsInteger.ValueFromSignedInteger(aggregate(listOfIntegers)),
            value);

    private static PineValue KernelFunctionExpectingListOfBigInt(
        Func<IReadOnlyList<BigInteger>, PineValue> aggregate,
        PineValue value) =>
        KernelFunctionExpectingList(
            value,
            list =>
            {
                var asIntegers = new BigInteger[list.Count];

                for (var i = 0; i < list.Count; ++i)
                {
                    if (SignedIntegerFromValueRelaxed(list[i]) is not { } intResult)
                        return PineValue.EmptyList;

                    asIntegers[i] = intResult;
                }

                return aggregate(asIntegers);
            });

    public static BigInteger? SignedIntegerFromValueRelaxed(PineValue value)
    {
        if (value is not PineValue.BlobValue blobValue)
            return null;

        if (blobValue.Bytes.Length is < 2)
            return null;

        var abs = new BigInteger(blobValue.Bytes.Span[1..], isUnsigned: true, isBigEndian: true);

        return
            blobValue.Bytes.Span[0] switch
            {
                4 => abs,
                2 => -abs,
                _ => null
            };
    }

    private static PineValue KernelFunctionExpectingExactlyTwoArguments<ArgA, ArgB>(
        Func<PineValue, Result<string, ArgA>> decodeArgA,
        Func<PineValue, Result<string, ArgB>> decodeArgB,
        Func<ArgA, ArgB, PineValue> compose,
        PineValue value) =>
        KernelFunctionExpectingList(
            value,
            list =>
            list switch
            {
            [var first, var second] =>
                decodeArgA(first) switch
                {
                    Result<string, ArgA>.Ok argA =>
                    decodeArgB(second) switch
                    {
                        Result<string, ArgB>.Ok argB =>
                        compose(argA.Value, argB.Value),

                        _ =>
                        PineValue.EmptyList
                    },

                    _ =>
                    PineValue.EmptyList
                },

                _ =>
                PineValue.EmptyList
            });

    private static PineValue KernelFunctionExpectingListOfTypeBool(
        Func<IReadOnlyList<bool>, bool> compose,
        PineValue value) =>
        KernelFunctionExpectingList(
            value,
            list => ResultListMapCombine(list, ParseBoolFromValue)
            .Map(compose)
            .Map(ValueFromBool)
            .WithDefault(PineValue.EmptyList));


    public static PineValue KernelFunctionExpectingList(
        PineValue value,
        Func<IReadOnlyList<PineValue>, PineValue> continueWithList) =>
        value switch
        {
            PineValue.ListValue list =>
            continueWithList(list.Elements),

            _ =>
            PineValue.EmptyList
        };

    public static Result<string, bool> ParseBoolFromValue(PineValue value) =>
        value == PineVMValues.TrueValue
        ?
        true
        :
        value == PineVMValues.FalseValue
        ?
        false
        :
        "Value is neither True nor False";

    public static PineValue ValueFromBool(bool b) =>
        b ?
        PineVMValues.TrueValue :
        PineVMValues.FalseValue;

    public static Result<ErrT, IReadOnlyList<MappedOkT>> ResultListMapCombine<ErrT, OkT, MappedOkT>(
        IReadOnlyList<OkT> list,
        Func<OkT, Result<ErrT, MappedOkT>> mapElement) =>
        list.Select(mapElement).ListCombine();

}