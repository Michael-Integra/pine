using Pine.Core;

public class Program
{
    public static void Main()
    {
        System.Console.WriteLine("Entering");

        Thread.Sleep(TimeSpan.FromSeconds(60));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine(ReusedInstances.Instance.ListValues.Count + " list values");

        System.Console.WriteLine("\nCompleted run in " + stopwatch.Elapsed.TotalSeconds + " seconds");
    }
}
