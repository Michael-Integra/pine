using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pine.Elm;

namespace TestElmTime;

[TestClass]
public class AVH4ElmFormatBinariesTests
{
    [TestMethod]
    public void Format_elm_module_text()
    {
        var elmModuleTextBeforeFormatting = @"
module Common exposing (..)

a =
    let
        b = 1
        c =
            2
    in
    b   +      c
";

        var expectedElmModuleTextAfterFormatting = @"
module Common exposing (..)


a =
    let
        b =
            1

        c =
            2
    in
    b + c
";


        var formatted =
            AVH4ElmFormatBinaries.RunElmFormat(elmModuleTextBeforeFormatting);

        Assert.AreEqual(
            expectedElmModuleTextAfterFormatting.Trim(),
            formatted.Trim());
    }
}