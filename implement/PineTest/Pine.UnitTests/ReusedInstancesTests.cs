using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pine.Core;
using System.Linq;

namespace Pine.UnitTests;

[TestClass]
public class ReusedInstancesTests
{
    [TestMethod]
    public void Ensure_reference_equality_between_mappings_between_reused_instances()
    {
        ReusedInstances.Instance.AssertReferenceEquality();
    }

    [TestMethod]
    public void Embedded_precompiled_pine_value_lists()
    {
        var fromFreshBuild =
            ReusedInstances.BuildPineListValueReusedInstances(
                ReusedInstances.ExpressionsSource());

        var file = ReusedInstances.BuildPrecompiledDictFile(fromFreshBuild);

        var destFilePath =
            System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                "pine",
                "dict.json");

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destFilePath));

        System.IO.File.WriteAllBytes(
            destFilePath,
            file.ToArray());

        Assert.IsTrue(
            ReusedInstances.Instance.ListValues
            .SequenceEqual(fromFreshBuild.PineValueLists));
    }
}
