using ElmTime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestElmTime;

[TestClass]
public class JavaScriptEngineTest
{
    [TestMethod]
    public void Evaluate_in_JavaScriptEngine()
    {
        var jsEngine = IJsEngine.BuildJsEngine();

        Assert.AreEqual(4, jsEngine.Evaluate("3 + 1"));
    }
}
