﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pine;
using Pine.Json;
using System;
using System.Collections.Immutable;
using System.Text.Json;

namespace TestElmTime;

[TestClass]
public class TestStateShim
{
    [TestMethod]
    public void Estimate_serialized_state_length_from_calculator()
    {
        using var testSetup = WebHostAdminInterfaceTestSetup.Setup(
            deployAppAndInitElmState: TestElmWebAppHttpServer.CalculatorWebApp);

        var fileStore = new FileStoreFromSystemIOFile(testSetup.ProcessStoreDirectory);

        var processStore = new ElmTime.Platform.WebServer.ProcessStoreSupportingMigrations.ProcessStoreWriterInFileStore(
            fileStore,
            getTimeForCompositionLogBatch:
            () => DateTimeOffset.UtcNow, fileStore);

        using var calculatorProcess = testSetup.BranchProcess()!;

        var estimateStateLengthResult = calculatorProcess.EstimateSerializedStateLengthOnMainBranch();

        var firstEstimatedStateLength = estimateStateLengthResult.Extract(err => throw new Exception(err));

        Assert.IsTrue(0 < firstEstimatedStateLength);

        {
            var applyOperationResult =
                calculatorProcess.ApplyFunctionOnMainBranch(
                    processStore,
                    new ElmTime.AdminInterface.ApplyFunctionOnDatabaseRequest(
                        functionName: "Backend.ExposeFunctionsToAdmin.applyCalculatorOperation",
                        serializedArgumentsJson: ImmutableList.Create(
                            JsonSerializer.Serialize<CalculatorOperation>(new CalculatorOperation.AddOperation(12345678))),
                        commitResultingState: true));

            applyOperationResult.Extract(err => throw new Exception(err));
        }

        estimateStateLengthResult = calculatorProcess.EstimateSerializedStateLengthOnMainBranch();

        var secondEstimatedStateLength = estimateStateLengthResult.Extract(err => throw new Exception(err));

        var estimatedStateLengthGrowth = secondEstimatedStateLength - firstEstimatedStateLength;

        Assert.IsTrue(3 < estimatedStateLengthGrowth);
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(JsonConverterForChoiceType))]
    abstract record CalculatorOperation
    {
        public record AddOperation(int Operand)
            : CalculatorOperation;
    }

    record CalculatorBackendStateRecord(
        int httpRequestCount,
        int operationsViaHttpRequestCount,
        int resultingNumber);

    record CustomUsageReportStruct(
        int httpRequestCount,
        int operationsViaHttpRequestCount,
        string anotherField);
}
