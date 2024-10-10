// See https://aka.ms/new-console-template for more information
System.Console.WriteLine("Entering");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();

var inst = new TestElmTime.CompileElmCompilerTests();

inst.Elm_compiler_compiles_Elm_compiler();

System.Console.WriteLine("\nCompleted run in " + stopwatch.Elapsed.TotalSeconds + " seconds");
