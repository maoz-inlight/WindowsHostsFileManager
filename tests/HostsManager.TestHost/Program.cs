using HostsManager.Core;
using HostsManager.Services;
using System.Text;

System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
System.Diagnostics.Trace.AutoFlush = true;

// Every operation requires a target in a test-created, marked fixture directory.
static void CheckFixture(string target)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
    if (!File.Exists(Path.Combine(directory, ".hostsmanager-test-fixture"))
        || TargetIdentity.PathsEqual(TargetIdentity.CanonicalPath(target), HostsFileWriter.DefaultHostsPath))
        throw new InvalidOperationException("The integration host accepts marked test fixtures only.");
}

if (args.Length == 3 && args[0] == "roundtrip")
{
    if (!OperatingSystem.IsWindows()) return 3;
    CheckFixture(args[1]);
    var committer = new ElevatedHostsFileCommitter(args[1], request =>
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("pipe");
        start.ArgumentList.Add(request.ArgumentList[1]);
        start.ArgumentList.Add(args[1]);
        return System.Diagnostics.Process.Start(start);
    });
    var writer = new HostsFileWriter(args[1], new BackupManager(args[2], hostsPath: args[1]), committer: committer);
    var document = writer.Load();
    var original = writer.Backups.List().Single();
    document.AddEntry("127.0.0.2", new[] { "pipe.test" });
    writer.Save();
    if (!File.ReadAllText(args[1]).Contains("pipe.test")) return 6;
    writer.Restore(original);
    return writer.Backups.List().All(writer.Backups.Verify) ? 0 : 7;
}
if (args.Length == 3 && args[0] == "pipe")
{
    if (!OperatingSystem.IsWindows()) return 3;
    CheckFixture(args[2]);
    return ElevatedHostsFileCommitter.RunHelperCore(args[1], args[2], requireAdministrator: false);
}
if (args.Length == 7 && args[0] == "commit")
{
    CheckFixture(args[1]);
    File.WriteAllText(args[5], "ready");
    var wait = System.Diagnostics.Stopwatch.StartNew();
    while (!File.Exists(args[6]))
    {
        if (wait.Elapsed > TimeSpan.FromSeconds(15)) return 4;
        Thread.Sleep(10);
    }
    var writer = new HostsFileWriter(args[1], new BackupManager(args[2], hostsPath: args[1]));
    try
    {
        writer.CommitPrepared(new PreparedHostsWrite(writer.HostsPath, writer.Backups.RootDirectory,
            Encoding.UTF8.GetBytes($"127.0.0.1 {args[4]}.test\r\n"), args[3], "Concurrent process test"));
        Console.WriteLine("saved");
        return 0;
    }
    catch (HostsDriftException) { Console.WriteLine("drift"); return 2; }
}
return 5;
