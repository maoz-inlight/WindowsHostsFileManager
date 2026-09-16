using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Tests;

public sealed class P1ReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HostsManager.P1." + Guid.NewGuid().ToString("N"));
    private readonly string _hosts;
    private readonly string _root;
    private const string Original = "127.0.0.1 original.test\r\n";
    private const string Managed = "127.0.0.1 user.test\r\n# Added by Docker Desktop\r\n127.0.0.2 docker.test\r\n# End of section\r\n# TailscaleHostsSectionStart\r\n127.0.0.3 tail.test\r\n# TailscaleHostsSectionEnd\r\n";

    public P1ReliabilityTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, ".hostsmanager-test-fixture"), "");
        _hosts = Path.Combine(_directory, "hosts");
        _root = Path.Combine(_directory, "backups");
        File.WriteAllText(_hosts, Original);
    }

    private HostsFileWriter Writer(string? target = null) =>
        new(target ?? _hosts, new BackupManager(_root, hostsPath: target ?? _hosts));

    private static PreparedHostsWrite Request(HostsFileWriter writer, string text) =>
        new(writer.HostsPath, writer.Backups.RootDirectory, Encoding.UTF8.GetBytes(text), writer.LoadedSha256, "P1 test");

    [Fact]
    public void DifferentTargetsHaveDifferentOriginalAndLatestHistory()
    {
        var other = Path.Combine(_directory, "other");
        File.WriteAllText(other, "127.0.0.2 other.test\r\n");
        // Even the same BackupManager instance cannot redirect a writer's scope.
        var configured = new BackupManager(_root, hostsPath: _hosts);
        var first = new HostsFileWriter(_hosts, configured);
        var second = new HostsFileWriter(other, configured);
        first.Load();
        second.Load();
        Assert.NotEqual(first.Backups.Directory, second.Backups.Directory);
        Assert.Equal(Original, File.ReadAllText(first.Backups.OriginalPath));
        Assert.Contains("other.test", File.ReadAllText(second.Backups.OriginalPath));
        first.Document!.AddEntry("127.0.0.3", new[] { "new.test" });
        first.Save();
        Assert.Single(second.Backups.List());
        var wrong = first.Backups.List().First(b => !b.IsOriginal);
        Assert.Throws<HostsWriteException>(() => second.Restore(wrong));
        Assert.Contains("other.test", File.ReadAllText(other));
    }

    [Fact]
    public void EquivalentPathsUseTheSameScope()
    {
        var first = Writer();
        var second = Writer(Path.Combine(_directory, ".", "hosts"));
        Assert.Equal(first.Backups.Directory, second.Backups.Directory);
        if (OperatingSystem.IsWindows())
            Assert.Equal(first.Backups.Directory, Writer(_hosts.ToUpperInvariant()).Backups.Directory);
    }

    [Fact]
    public void LegacyOriginalIsRetainedAndNeverAdopted()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, BackupManager.OriginalFileName);
        File.WriteAllText(legacy, "127.0.0.9 rehearsal.test\r\n");
        var writer = Writer();
        writer.Load();
        Assert.Equal(Original, File.ReadAllText(writer.Backups.OriginalPath));
        var old = Assert.Single(writer.Backups.ListLegacy());
        Assert.Null(old.TargetPath);
        Assert.False(writer.Backups.CanRestore(old));
        Assert.Throws<HostsWriteException>(() => writer.Restore(old));
        Assert.Contains("rehearsal.test", File.ReadAllText(legacy));
    }

    [Fact]
    public void CopiedBackupCannotBeRestoredByForgingTheSelectedEntry()
    {
        var first = Writer();
        first.Load();
        var source = Assert.Single(first.Backups.List());
        var other = Path.Combine(_directory, "other");
        File.WriteAllText(other, Original);
        var second = Writer(other);
        Directory.CreateDirectory(second.Backups.Directory);
        var copied = Path.Combine(second.Backups.Directory, "copied.bak");
        File.Copy(source.FilePath, copied);
        File.Copy(source.FilePath + ".json", copied + ".json");
        var forged = source with { FilePath = copied, TargetPath = second.HostsPath };
        Assert.Throws<HostsWriteException>(() => second.Restore(forged));
    }

    [Fact]
    public void ConcurrentBackupsHaveExclusiveNamesAndCompleteManifests()
    {
        var backups = new BackupManager(_root, retention: 100, hostsPath: _hosts);
        Parallel.For(0, 24, i => backups.Create(Encoding.UTF8.GetBytes($"127.0.0.1 host{i}.test\r\n"), $"Save {i}"));
        var all = backups.List();
        Assert.Equal(24, all.Count);
        Assert.Equal(24, all.Select(b => b.FileName).Distinct().Count());
        Assert.All(all, b => { Assert.True(backups.Verify(b)); Assert.True(backups.CanRestore(b)); });
    }

    [Fact]
    public void ConcurrentOriginalCaptureNeverOverwritesTheWinner()
    {
        var backups = new BackupManager(_root, hostsPath: _hosts);
        Parallel.For(0, 12, i => backups.EnsureOriginal(Encoding.UTF8.GetBytes($"127.0.0.1 host{i}.test\r\n"), out _));
        var original = Assert.Single(backups.List());
        Assert.True(original.IsOriginal);
        Assert.True(backups.Verify(original));
        var before = File.ReadAllBytes(original.FilePath);
        backups.EnsureOriginal(Encoding.UTF8.GetBytes(Original), out _);
        Assert.Equal(before, File.ReadAllBytes(original.FilePath));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("change")]
    [InlineData("delimiter")]
    [InlineData("newline")]
    [InlineData("reorder")]
    [InlineData("tailscale")]
    public void PreparedSaveRejectsManagedSectionChanges(string attack)
    {
        File.WriteAllText(_hosts, Managed);
        var writer = Writer();
        writer.Load();
        var proposal = attack switch
        {
            "remove" => Original,
            "change" => Managed.Replace("docker.test", "changed.test"),
            "delimiter" => Managed.Replace("# End of section", "# Removed delimiter"),
            "newline" => Managed.Replace("docker.test\r\n", "docker.test\n"),
            "tailscale" => Managed.Replace("tail.test", "changed.test"),
            _ => "127.0.0.1 user.test\r\n# TailscaleHostsSectionStart\r\n127.0.0.3 tail.test\r\n# TailscaleHostsSectionEnd\r\n# Added by Docker Desktop\r\n127.0.0.2 docker.test\r\n# End of section\r\n",
        };
        Assert.Throws<HostsVerificationException>(() => writer.CommitPrepared(Request(writer, proposal)));
        Assert.Equal(Managed, File.ReadAllText(_hosts));
        Assert.Single(writer.Backups.List());
    }

    [Fact]
    public void OrdinarySavePreservesManagedSectionsButVerifiedRestoreMayChangeThem()
    {
        var writer = Writer();
        writer.Load();
        var old = Assert.Single(writer.Backups.List());
        File.WriteAllText(_hosts, Managed);
        var doc = writer.Load();
        doc.AddEntry("127.0.0.4", new[] { "added.test" });
        writer.Save();
        Assert.Contains("docker.test", File.ReadAllText(_hosts));
        writer.Restore(old);
        Assert.Equal(Original, File.ReadAllText(_hosts));
        Assert.Contains(writer.Backups.List(), b => b.Reason.StartsWith("Before restoring"));
    }

    [Fact]
    public void RestoreOperationCannotSupplyArbitraryContent()
    {
        var writer = Writer();
        writer.Load();
        var original = Assert.Single(writer.Backups.List());
        var request = Request(writer, "127.0.0.9 arbitrary.test\r\n") with
        {
            Operation = HostsWriteOperation.Restore, RestoreBackupFileName = original.FileName,
        };
        Assert.Throws<HostsWriteException>(() => writer.CommitPrepared(request));
        Assert.Equal(Original, File.ReadAllText(_hosts));
    }

    [Fact]
    public void UnknownOperationIsRejected()
    {
        var writer = Writer();
        writer.Load();
        Assert.Throws<HostsWriteException>(() => writer.CommitPrepared(Request(writer, Original) with { Operation = (HostsWriteOperation)99 }));
    }

    [Fact]
    public void MissingOrChangedRestoreManifestIsRejected()
    {
        var writer = Writer();
        writer.Load();
        var backup = Assert.Single(writer.Backups.List());
        File.Delete(backup.FilePath + ".json");
        Assert.Throws<HostsWriteException>(() => writer.Restore(backup));
        Assert.False(writer.Backups.CanRestore(Assert.Single(writer.Backups.List())));
    }

    [Fact]
    public void PermissionCaptureFailureDoesNotWrite()
    {
        var writer = Writer();
        writer.Load();
        writer.Files = new FaultFiles { CaptureFailure = true };
        Assert.Throws<UnauthorizedAccessException>(() => writer.CommitPrepared(Request(writer, "127.0.0.2 new.test\r\n")));
        Assert.Equal(Original, File.ReadAllText(_hosts));
    }

    [Fact]
    public void PermissionPreparationFailureDoesNotWrite()
    {
        var writer = Writer();
        writer.Load();
        writer.Files = new FaultFiles { ApplyFailure = true };
        Assert.Throws<HostsWriteException>(() => writer.CommitPrepared(Request(writer, "127.0.0.2 new.test\r\n")));
        Assert.Equal(Original, File.ReadAllText(_hosts));
    }

    [Fact]
    public void PermissionComparisonIgnoresBookkeepingButChecksAccessAndProtection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = new FileSecurity();
        original.SetSecurityDescriptorSddlForm("D:(A;;FA;;;SY)");
        var inherited = new FileSecurity();
        inherited.SetSecurityDescriptorSddlForm("D:AI(A;;FA;;;SY)");
        Assert.True(HostsFileOperations.AccessRulesMatch(original, inherited));
        inherited.SetSecurityDescriptorSddlForm("D:P(A;;FA;;;SY)");
        Assert.False(HostsFileOperations.AccessRulesMatch(original, inherited));
        inherited.SetSecurityDescriptorSddlForm("D:(A;;FR;;;SY)");
        Assert.False(HostsFileOperations.AccessRulesMatch(original, inherited));
    }

    [Fact]
    public void AppliedPermissionsRetainAllAccessRules()
    {
        if (!OperatingSystem.IsWindows()) return;
        var operations = new HostsFileOperations();
        var captured = operations.CapturePermissions(_hosts)!;
        var temporary = Path.Combine(_directory, "permissions-test");
        File.WriteAllText(temporary, "test");
        operations.ApplyPermissions(temporary, captured);
        var actual = operations.CapturePermissions(temporary)!;
        Assert.True(operations.PermissionsMatch(temporary, captured),
            $"Expected: {captured.GetSecurityDescriptorSddlForm(AccessControlSections.Access)}; " +
            $"Actual: {actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access)}");
    }

    [Fact]
    public void FallbackPreservesOriginalPermissions()
    {
        var writer = Writer();
        writer.Load();
        var operations = new FaultFiles { ReplaceFailure = true };
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(_hosts);
            var access = file.GetAccessControl();
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            file.SetAccessControl(access);
        }
        var permissions = operations.CapturePermissions(_hosts);
        writer.Files = operations;
        writer.CommitPrepared(Request(writer, "127.0.0.2 new.test\r\n"));
        Assert.True(operations.Moved);
        Assert.True(operations.PermissionsMatch(_hosts, permissions));
        Assert.Contains("new.test", File.ReadAllText(_hosts));
    }

    [Fact]
    public void FinalPermissionMismatchDoesNotReportSuccess()
    {
        var writer = Writer();
        writer.Load();
        var operations = new FaultFiles { FailFinalPermissionsOnce = _hosts };
        var permissions = operations.CapturePermissions(_hosts);
        writer.Files = operations;
        Assert.Throws<HostsWriteException>(() => writer.CommitPrepared(Request(writer, "127.0.0.2 new.test\r\n")));
        Assert.Equal(Original, File.ReadAllText(_hosts));
        Assert.True(operations.PermissionsMatch(_hosts, permissions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalChangeDuringPreparationOrFallbackIsNeverOverwritten(bool duringReplace)
    {
        var writer = Writer();
        writer.Load();
        var external = "127.0.0.7 outside.test\r\n";
        Action change = () => File.WriteAllText(_hosts, external);
        writer.Files = duringReplace
            ? new FaultFiles { BeforeFailedReplace = change }
            : new FaultFiles { AfterApply = change };
        Assert.Throws<HostsDriftException>(() => writer.CommitPrepared(Request(writer, "127.0.0.2 new.test\r\n")));
        Assert.Equal(external, File.ReadAllText(_hosts));
        var recovery = writer.Backups.List().First(b => !b.IsOriginal);
        Assert.Equal(Original, File.ReadAllText(recovery.FilePath));
    }

    [Fact]
    public void ExternalWriteAfterReplacementIsNotRolledBack()
    {
        var writer = Writer();
        writer.Load();
        const string external = "127.0.0.9 outside.test\r\n";
        writer.Files = new FaultFiles { AfterReplace = () => File.WriteAllText(_hosts, external) };
        var failure = Assert.Throws<HostsWriteException>(() => writer.CommitPrepared(Request(writer, "127.0.0.2 new.test\r\n")));
        Assert.Contains("not overwritten", failure.Message);
        Assert.Equal(external, File.ReadAllText(_hosts));
    }

    [Fact]
    public void SeparateProcessesSerializeWritesAndSecondDetectsDrift()
    {
        var hash = HostsDocument.Sha256(File.ReadAllBytes(_hosts));
        var gate = Path.Combine(_directory, "gate");
        var ready1 = Path.Combine(_directory, "ready1");
        var ready2 = Path.Combine(_directory, "ready2");
        using var first = StartHost("commit", _hosts, _root, hash, "first", ready1, gate);
        using var second = StartHost("commit", _hosts, _root, hash, "second", ready2, gate);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready1) && File.Exists(ready2), TimeSpan.FromSeconds(15)));
        File.WriteAllText(gate, "go");
        Assert.True(first.WaitForExit(15000));
        Assert.True(second.WaitForExit(15000));
        Assert.Equal(new[] { 0, 2 }, new[] { first.ExitCode, second.ExitCode }.OrderBy(c => c).ToArray());
        var backups = Writer().Backups.List();
        Assert.Single(backups.Where(b => !b.IsOriginal));
    }

    [Fact]
    public void ChildWriterWaitsForTheExistingTransactionLock()
    {
        var ready = Path.Combine(_directory, "ready");
        var gate = Path.Combine(_directory, "gate");
        Process child;
        using (TargetWriteLock.Acquire(TargetIdentity.CanonicalPath(_hosts)))
        {
            child = StartHost("commit", _hosts, _root,
                HostsDocument.Sha256(File.ReadAllBytes(_hosts)), "child", ready, gate);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(15)));
            File.WriteAllText(gate, "go");
            Assert.False(child.WaitForExit(300));
            Assert.Equal(Original, File.ReadAllText(_hosts));
        }
        using (child)
        {
            Assert.True(child.WaitForExit(15000));
            Assert.Equal(0, child.ExitCode);
        }
        Assert.Contains("child.test", File.ReadAllText(_hosts));
    }

    [Theory]
    [InlineData("wrong-client")]
    [InlineData("wrong-start")]
    [InlineData("wrong-target")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task HelperRejectsUnauthenticatedOrOutOfScopeRequests(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var endpoint = new ElevatedHostsFileCommitter.Endpoint(Guid.NewGuid().ToString("N"),
            scenario == "wrong-client" ? current.Id + 1 : current.Id,
            current.StartTime.ToUniversalTime().Ticks + (scenario == "wrong-start" ? 1 : 0),
            identity.User!.Value);
        using var helper = StartHost("pipe", endpoint.ToArgument(), _hosts);
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(timeout.Token);
        var writer = Writer();
        writer.Load();
        if (scenario != "wrong-client")
        {
            var request = Request(writer, "127.0.0.2 forbidden.test\r\n");
            if (scenario == "wrong-target") request = request with { HostsPath = Path.Combine(_directory, "different") };
            ElevatedHostsFileCommitter.WriteMessage(pipe, request, timeout.Token);
        }
        var response = ElevatedHostsFileCommitter.ReadMessage<JsonElement>(pipe, timeout.Token);
        Assert.False(response.GetProperty("Success").GetBoolean());
        Assert.True(helper.WaitForExit(10000));
        Assert.Equal(1, helper.ExitCode);
        Assert.Equal(Original, File.ReadAllText(_hosts));
        Assert.Single(writer.Backups.List());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task PrivatePipeCommitsAndRestoresWithCallerOwnedBackups()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var roundtrip = StartHost("roundtrip", _hosts, _root);
        var errors = roundtrip.StandardError.ReadToEndAsync();
        Assert.True(roundtrip.WaitForExit(20000));
        Assert.True(roundtrip.ExitCode == 0, await errors);
        Assert.Equal(Original, File.ReadAllText(_hosts));
        Assert.All(Writer().Backups.List(), b => Assert.True(Writer().Backups.Verify(b)));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void UacCancellationDoesNotWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var committer = new ElevatedHostsFileCommitter(_hosts, _ => throw new System.ComponentModel.Win32Exception(1223));
        var writer = Writer();
        writer.Load();
        var error = Assert.Throws<HostsWriteException>(() => committer.Commit(Request(writer, "127.0.0.2 new.test\r\n")));
        Assert.Contains("cancelled", error.Message);
        Assert.Equal(Original, File.ReadAllText(_hosts));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ProductionHelperRejectsCustomTargetsAndMalformedEndpoints()
    {
        if (!OperatingSystem.IsWindows()) return;
        var writer = Writer();
        writer.Load();
        Assert.Throws<HostsWriteException>(() => ElevatedHostsFileCommitter.ValidateWriteScope(
            Request(writer, Original), HostsFileWriter.DefaultHostsPath));
        Assert.Throws<HostsWriteException>(() => ElevatedHostsFileCommitter.Endpoint.Parse("../../request.json"));
        Assert.Equal(1, ElevatedHostsFileCommitter.RunHelper("invalid"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void HelperProtocolRejectsOversizedAndMalformedFrames()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var oversized = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        Assert.Throws<HostsWriteException>(() => ElevatedHostsFileCommitter.ReadMessage<PreparedHostsWrite>(oversized, default));
        using var truncated = new MemoryStream(new byte[] { 1, 2 });
        Assert.Throws<EndOfStreamException>(() => ElevatedHostsFileCommitter.ReadMessage<PreparedHostsWrite>(truncated, default));
    }

    private static Process StartHost(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "integration-host", "HostsManager.TestHost.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private sealed class HelperProcesses : IDisposable
    {
        private readonly List<Process> _processes = new();
        public StringBuilder Errors { get; } = new();
        public Process Add(Process process)
        {
            _processes.Add(process);
            process.ErrorDataReceived += (_, e) => { lock (Errors) Errors.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            return process;
        }
        public void Dispose()
        {
            // The committer disposes its process handles; helpers terminate themselves.
            foreach (var process in _processes) process.Dispose();
        }
    }

    private sealed class FaultFiles : HostsFileOperations
    {
        public bool CaptureFailure { get; init; }
        public bool ApplyFailure { get; init; }
        public bool ReplaceFailure { get; init; }
        public bool Moved { get; private set; }
        public string? FailFinalPermissionsOnce { get; set; }
        public Action? AfterApply { get; init; }
        public Action? BeforeFailedReplace { get; init; }
        public Action? AfterReplace { get; init; }
        private bool _replaced;
        public override FileSecurity? CapturePermissions(string path)
        {
            if (CaptureFailure) throw new UnauthorizedAccessException("Injected ACL read failure");
            return base.CapturePermissions(path);
        }
        public override void ApplyPermissions(string path, FileSecurity? permissions)
        {
            if (ApplyFailure) throw new UnauthorizedAccessException("Injected ACL apply failure");
            base.ApplyPermissions(path, permissions);
            AfterApply?.Invoke();
        }
        public override void Replace(string temp, string destination, string backup)
        {
            BeforeFailedReplace?.Invoke();
            if (ReplaceFailure || BeforeFailedReplace is not null) throw new IOException("Injected replace failure");
            base.Replace(temp, destination, backup);
            _replaced = true;
            AfterReplace?.Invoke();
        }
        public override void Move(string temp, string destination)
        {
            base.Move(temp, destination);
            Moved = true;
        }
        public override bool PermissionsMatch(string path, FileSecurity? permissions)
        {
            if (_replaced && path == FailFinalPermissionsOnce) { FailFinalPermissionsOnce = null; return false; }
            return base.PermissionsMatch(path, permissions);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
