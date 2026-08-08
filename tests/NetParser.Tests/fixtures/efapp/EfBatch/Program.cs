using EfLib;

namespace EfBatch;

// A scheduled process: nothing in the code calls it, the scheduler does.
public class Program
{
    public static void Main(string[] args)
    {
        new ExportRunner().RunExport("nightly.csv");
    }
}
