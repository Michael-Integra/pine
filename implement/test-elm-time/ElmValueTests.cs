﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pine.Core;
using Pine.ElmInteractive;
using System.Collections.Generic;

namespace TestElmTime;

[TestClass]
public class ElmValueTests
{
    [TestMethod]
    public void Test_ElmValue_as_pine_value_roundtrips()
    {
        var testCases = (IReadOnlyList<ElmValue>)[
            ElmValue.CharInstance('a'),
            ElmValue.Integer(42),
            ElmValue.String("Hello, world!"),
            new ElmValue.ElmList([
                ElmValue.Integer(31),
                ElmValue.Integer(37),
                ElmValue.Integer(39)]),
            new ElmValue.ElmRecord([
                ("alfa", ElmValue.Integer(1)),
                ("beta", ElmValue.Integer(2)),
                ("gamma", ElmValue.Integer(3)),
            ]),

            new ElmValue.ElmString("Hello, world 👋"),

            ElmValue.TagInstance("True", []),
            ElmValue.TagInstance("False", []),

            new ElmValue.ElmList([
                new ElmValue.ElmList([
                    ElmValue.Integer(7),
                    ElmValue.Integer(13)]),
                new ElmValue.ElmList([
                    ElmValue.Integer(41),
                    ElmValue.Integer(43),
                    ElmValue.Integer(47)]),
            ]),
        ];

        foreach (var testCase in testCases)
        {
            var pineValue = ElmValueEncoding.ElmValueAsPineValue(testCase);

            var roundtrip =
                ElmValueEncoding.PineValueAsElmValue(pineValue)
                .Extract(err => throw new System.Exception(err));

            Assert.AreEqual(testCase, roundtrip);
        }
    }

    [TestMethod]
    public void Test_ElmValueAsExpression()
    {
        var testCases =
            (IReadOnlyList<(ElmValue, string)>)
            [
                (ElmValue.Integer(42), "42"),

                (ElmValue.CharInstance('a'), "'a'"),

                (ElmValue.String("Hello, world!"), "\"Hello, world!\""),

                (new ElmValue.ElmList([
                ElmValue.Integer(31),
                    ElmValue.Integer(37),
                    ElmValue.Integer(39)]),
                    "[31,37,39]"),

                (new ElmValue.ElmRecord([
                ("alfa", ElmValue.Integer(1)),
                    ("beta", ElmValue.Integer(2)),
                    ("gamma", ElmValue.Integer(3))]),
                "{ alfa = 1, beta = 2, gamma = 3 }"),

                (ElmValue.TagInstance("Just", [ElmValue.Integer(43)]),
                "Just 43"),

                (ElmValue.TagInstance("Nothing", []),
                "Nothing"),

                (ElmValue.TagInstance("Just", [ElmValue.TagInstance("Nothing", [])]),
                "Just Nothing"),

                (ElmValue.TagInstance("Just", [ElmValue.TagInstance("Just", [ElmValue.Integer(47)])]),
                "Just (Just 47)"),
            ];

        foreach (var (elmValue, expectedExpression) in testCases)
        {
            var (expressionString, needsParens) = ElmValue.ElmValueAsExpression(elmValue);

            Assert.AreEqual(expectedExpression, expressionString);
        }
    }

    [TestMethod]
    public void Shallow_parsing_as_record()
    {
        var elmRecord = new ElmValue.ElmRecord(
            [("alfa", ElmValue.Integer(11)),
             ("beta", ElmValue.Integer(13)),
             ("gamma", ElmValue.Integer(17))
             ]);

        var asPineValue = ElmValueEncoding.ElmValueAsPineValue(elmRecord);

        var parseResult = ElmValueEncoding.ParsePineValueAsRecordTagged(asPineValue);

        if (parseResult is Result<string, IReadOnlyList<(string fieldName, PineValue fieldValue)>>.Err parseAsRecordErr)
        {
            Assert.Fail("Failed parsing as record: " + parseAsRecordErr.Value);
        }

        if (parseResult is not Result<string, IReadOnlyList<(string fieldName, PineValue fieldValue)>>.Ok parseAsRecordOk)
        {
            throw new System.Exception(
                "Unexpected parse result type: " + parseResult.GetType().FullName);
        }

        var parsedFields = parseAsRecordOk.Value;

        Assert.AreEqual(3, parsedFields.Count);

        Assert.AreEqual("alfa", parsedFields[0].fieldName);
        Assert.AreEqual(PineValueAsInteger.ValueFromSignedInteger(11), parsedFields[0].fieldValue);

        Assert.AreEqual("beta", parsedFields[1].fieldName);
        Assert.AreEqual(PineValueAsInteger.ValueFromSignedInteger(13), parsedFields[1].fieldValue);

        Assert.AreEqual("gamma", parsedFields[2].fieldName);
        Assert.AreEqual(PineValueAsInteger.ValueFromSignedInteger(17), parsedFields[2].fieldValue);
    }
}
