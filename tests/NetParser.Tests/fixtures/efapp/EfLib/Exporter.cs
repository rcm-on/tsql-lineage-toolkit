using EfLib.Domain;

namespace EfLib;

// Two implementations, no DI container anywhere: the binding is the `new`.
public interface IExporter
{
    void Export(string path);
}

public class CsvExporter : IExporter
{
    public void Export(string path)
    {
    }
}

public class XmlExporter : IExporter
{
    public void Export(string path)
    {
    }
}

public class ExportRunner
{
    public void RunExport(string path)
    {
        IExporter exporter = new CsvExporter();
        exporter.Export(path);

        var format = new ExportFormat();
        format.Name();
    }
}
