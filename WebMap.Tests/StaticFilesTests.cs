using System.IO;
using System.Linq;
using Xunit;

namespace WebMap.Tests
{
    // The bundled viewer: a file under web/ the content-type table does not know answers 404.
    public class StaticFilesTests
    {
        static string Web()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "WebMap", "web", "index.html"))) d = d.Parent;
            return Path.Combine(d!.FullName, "WebMap", "web");
        }

        [Fact]
        public void EveryFileUnderWebHasAContentType()
        {
            var unknown = Directory.EnumerateFiles(Web(), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetExtension(f).TrimStart('.'))
                .Where(ext => !MapDataServer.contentTypes.ContainsKey(ext)).Distinct().ToList();
            Assert.Empty(unknown);
        }
    }
}
