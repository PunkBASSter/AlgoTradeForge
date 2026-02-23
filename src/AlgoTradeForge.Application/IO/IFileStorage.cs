using System.Text;

namespace AlgoTradeForge.Application.IO;

public interface IFileStorage
{
    IEnumerable<string> ReadLines(string path);
    void WriteAllText(string path, string content, Encoding encoding);
}
