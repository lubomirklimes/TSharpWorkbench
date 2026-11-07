using System.Text.Json;
using System.Security.Cryptography;
using TSharpVision;

if (args.Length != 2) throw new ArgumentException("Usage: Workbench.HelpCompiler source.json output.hlp");
var topics = JsonSerializer.Deserialize<Topic[]>(File.ReadAllText(args[0]))!;
if (topics.Select(t => t.Id).Distinct().Count() != topics.Length || !topics.Any(t => t.Id == 1)) throw new InvalidDataException("Duplicate IDs or missing Index.");
foreach (var t in topics)
    if (t.Id < 1 || t.Id > ushort.MaxValue || t.Links.Any(id => !topics.Any(x => x.Id == id))) throw new InvalidDataException("Invalid context/link.");
THelpFile.RegisterStreamableTypes();
using (var fs = new FileStream(args[1], FileMode.Create, FileAccess.ReadWrite)) {
    var stream = new Fpstream(fs);
    var file = new THelpFile(stream, THelpFile.FormatV2Utf16);
    foreach (var t in topics) {
        var topic = new THelpTopic();
        int offset = 0;
        void Add(string text) { text += "\n"; topic.AddParagraph(new TParagraph { wrap = true, Text = text }); offset += text.Length; }
        Add(t.Title); Add("");
        foreach (var line in t.Paragraphs) { Add(line); Add(""); }
        foreach (int id in t.Links) {
            string label = topics.Single(x => x.Id == id).Title;
            topic.AddCrossRef(new TCrossRef { @ref = id, offset = offset + 2, length = checked((byte)label.Length) });
            Add("  " + label.Replace(' ', '\u00a0'));
        }
        file.RecordPositionInIndex(t.Id); file.PutTopic(topic);
    }
    file.Flush();
}
File.WriteAllText(args[1] + ".sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))));
Console.WriteLine($"Compiled {topics.Length} topics to {args[1]} (TSharpVision FBH2).");
record Topic(int Id, string Title, string[] Paragraphs, int[] Links);
