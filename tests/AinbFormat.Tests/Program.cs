using AinbFormat;
using AinbFormat.Tests;

var codec = new AinbCodec();
int passed = Regression.Run(codec), failed = 0;
foreach (string path in args.Length == 0 ? [] : Directory.EnumerateFiles(args[0], "*.ainb", SearchOption.AllDirectories))
{
    if (Path.GetFileName(path) is not ("base.ainb" or "low.ainb" or "high.ainb" or "expected.ainb"))
        continue;
    try
    {
        var expected = FixtureJson.Read(Path.ChangeExtension(path, ".json"));
        var document = codec.Read(File.ReadAllBytes(path));
        if (document != expected)
            throw new Exception("Native parse differs from independent snapshot: " + Difference(document, expected));
        byte[] written = codec.Write(document);
        if (codec.Read(written) != document)
            throw new Exception("Round trip differs.");
        File.WriteAllBytes(Path.ChangeExtension(path, ".native.ainb"), written);
        passed++;
    }
    catch (Exception error)
    {
        Console.WriteLine(path + ": " + error.Message);
        failed++;
    }
}
Console.WriteLine($"Native IO fixtures: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

static string Difference(AinbDocument a, AinbDocument b)
{
    if (a with
    {
        Nodes = new()
    } != b with
    {
        Nodes = new()
    })
        return "document header/commands/modules";
    for (int i = 0; i < a.Nodes.Count; i++)
    {
        var n = a.Nodes[i];
        var m = b.Nodes[i];
        if (n == m)
            continue;
        if (n.Inputs != m.Inputs)
            return $"node {i} inputs: " + string.Join("; ", n.Inputs.Zip(m.Inputs).Where(p => p.First != p.Second));
        if (n.Plugs != m.Plugs)
            return $"node {i} plugs";
        return $"node {i}";
    }
    return "node count";
}
