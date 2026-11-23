using System.Reflection;
using System.Runtime.Loader;

var directory = Environment.GetEnvironmentVariable("BENCH_ASSEMBLIES")!;
AssemblyLoadContext.Default.Resolving += (context, name) => {
    var file = Path.Combine(directory, name.Name + ".dll");
    return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
};
var assembly = Assembly.Load("DevProxy.Abstractions");
var method = assembly.GetTypes().Single(t => t.Name == "MarkdownExtensions").GetMethod("ParseMarkdown")!;
var result = ((Dictionary<string, string>, string))method.MakeGenericMethod(typeof(Dictionary<string, string>))
    .Invoke(null, new object[] { "---\ntitle: drvlang\n---\nHello world\n" })!;
if (result.Item1["title"] != "drvlang" || result.Item2.Trim() != "Hello world")
    throw new Exception("Unexpected parsed document");
Console.WriteLine("Parsed front matter and Markdown body");
