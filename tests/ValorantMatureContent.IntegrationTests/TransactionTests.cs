using System.Text.Json;
using ValorantMatureContent.Core;

namespace ValorantMatureContent.IntegrationTests;

public sealed class TransactionTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"vmce-integration-{Guid.NewGuid():N}");
    private readonly string installation;
    private readonly string paks;
    private readonly string payloadRoot;
    private readonly string backupRoot;
    private readonly PhysicalFileSystem physicalFileSystem = new();
    private readonly Sha256HashService hashService;

    public TransactionTests()
    {
        installation = Path.Combine(root, "VALORANT");
        paks = Path.Combine(installation, GameFiles.PaksRelativePath);
        payloadRoot = Path.Combine(root, "payload");
        backupRoot = Path.Combine(root, "backups");
        Directory.CreateDirectory(paks);
        Directory.CreateDirectory(payloadRoot);
        hashService = new Sha256HashService(physicalFileSystem);
    }

    [Fact]
    public async Task OriginalStateEnableCreatesBackupAndInstallsPayload()
    {
        WriteSet(paks, GameFiles.Original, "original");
        var payload = await CreatePayloadAsync();
        var services = CreateOperationServices(physicalFileSystem);
        var result = await services.Executor.ExecuteAsync(CreateEnablePlan(payload));

        Assert.True(result.IsSuccess, result.Message);
        Assert.All(GameFiles.Original, name => Assert.False(File.Exists(Path.Combine(paks, name))));
        Assert.All(payload.Manifest.Files, file => Assert.True(File.Exists(Path.Combine(paks, file.Name))));
        Assert.True((await services.Backup.GetLatestAsync()).IsSuccess);
    }

    [Fact]
    public async Task EnabledStateDisableRestoresOriginalState()
    {
        WriteSet(paks, GameFiles.Original, "original");
        var payload = await CreatePayloadAsync();
        var services = CreateOperationServices(physicalFileSystem);
        Assert.True((await services.Executor.ExecuteAsync(CreateEnablePlan(payload))).IsSuccess);

        var disable = new OperationPlan(OperationKind.Disable, installation, "test-build", true, false, "disable", [], [new(PlannedActionKind.RestoreBackup, paks)], payload);
        var result = await services.Executor.ExecuteAsync(disable);

        Assert.True(result.IsSuccess, result.Message);
        Assert.All(GameFiles.Original, name => Assert.True(File.Exists(Path.Combine(paks, name))));
        Assert.All(GameFiles.Mature, name => Assert.False(File.Exists(Path.Combine(paks, name))));
    }

    [Fact]
    public async Task CopyFailureRollsBackOriginalState()
    {
        WriteSet(paks, GameFiles.Original, "original");
        var payload = await CreatePayloadAsync();
        var failing = new FailingPayloadCopyFileSystem(physicalFileSystem, payloadRoot);
        var services = CreateOperationServices(failing);

        var result = await services.Executor.ExecuteAsync(CreateEnablePlan(payload));

        Assert.False(result.IsSuccess);
        Assert.Contains("restored", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(GameFiles.Original, name => Assert.True(File.Exists(Path.Combine(paks, name))));
        Assert.All(GameFiles.Mature, name => Assert.False(File.Exists(Path.Combine(paks, name))));
    }

    [Fact]
    public async Task DryRunChangesNothing()
    {
        WriteSet(paks, GameFiles.Original, "original");
        var payload = await CreatePayloadAsync();
        var services = CreateOperationServices(physicalFileSystem);

        var result = await services.Executor.ExecuteAsync(CreateEnablePlan(payload), true);

        Assert.True(result.IsSuccess);
        Assert.All(GameFiles.Original, name => Assert.True(File.Exists(Path.Combine(paks, name))));
        Assert.All(GameFiles.Mature, name => Assert.False(File.Exists(Path.Combine(paks, name))));
        Assert.False(File.Exists(Path.Combine(backupRoot, "latest.txt")));
    }

    [Fact]
    public async Task GameRunningAbortsBeforeBackup()
    {
        WriteSet(paks, GameFiles.Original, "original");
        var payload = await CreatePayloadAsync();
        var backup = new BackupService(physicalFileSystem, hashService, backupRoot);
        var restore = new RestoreService(physicalFileSystem, hashService, backup);
        var executor = new OperationExecutor(physicalFileSystem, hashService, backup, restore, new FixedProcessDetector(true), "test");

        var result = await executor.ExecuteAsync(CreateEnablePlan(payload));

        Assert.Equal(ErrorCode.GameRunning, result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(backupRoot, "latest.txt")));
    }

    [Fact]
    public async Task CorruptedPayloadIsRejected()
    {
        var payload = await CreatePayloadAsync();
        await File.AppendAllTextAsync(Path.Combine(payloadRoot, payload.Manifest.Files[0].Name), "corrupt");
        var service = new PayloadService(physicalFileSystem, hashService);

        var result = await service.LoadAndVerifyAsync(Path.Combine(payloadRoot, "payload.manifest.json"));

        Assert.Equal(ErrorCode.PayloadHashMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task CorruptedBackupIsRejected()
    {
        WriteSet(paks, GameFiles.Original, "original");
        var backup = new BackupService(physicalFileSystem, hashService, backupRoot);
        var created = await backup.CreateAsync(installation, "test-build", "test");
        Assert.NotNull(created.Value);
        await File.AppendAllTextAsync(Path.Combine(created.Value.DirectoryPath, "files", GameFiles.Original[0]), "corrupt");

        var result = await backup.GetLatestAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.BackupFailed, result.ErrorCode);
    }

    [Fact]
    public async Task ValidEnableCalledTwiceCanBeRepresentedAsNoOp()
    {
        var payload = await CreatePayloadAsync();
        var services = CreateOperationServices(physicalFileSystem);
        var noOp = CreateEnablePlan(payload) with { IsNoOp = true, Actions = [] };

        var result = await services.Executor.ExecuteAsync(noOp);

        Assert.True(result.IsSuccess);
        Assert.Empty(Directory.EnumerateFiles(paks));
    }

    [Fact]
    public async Task RepairConflictPreservesOriginalsAndRemovesMatureFiles()
    {
        WriteSet(paks, GameFiles.Original, "original");
        WriteSet(paks, GameFiles.Mature, "unexpected");
        var payload = await CreatePayloadAsync();
        var services = CreateOperationServices(physicalFileSystem);
        var actions = new List<PlannedAction> { new(PlannedActionKind.CreateBackup, paks) };
        actions.AddRange(GameFiles.Mature.Select(name => new PlannedAction(PlannedActionKind.Delete, Path.Combine(paks, name))));
        actions.Add(new PlannedAction(PlannedActionKind.Verify, paks));
        var plan = new OperationPlan(OperationKind.Repair, installation, "test-build", true, false, "repair conflict", [], actions, payload);

        var result = await services.Executor.ExecuteAsync(plan);

        Assert.True(result.IsSuccess, result.Message);
        Assert.All(GameFiles.Original, name => Assert.True(File.Exists(Path.Combine(paks, name))));
        Assert.All(GameFiles.Mature, name => Assert.False(File.Exists(Path.Combine(paks, name))));
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }

    private async Task<LoadedPayload> CreatePayloadAsync()
    {
        WriteSet(payloadRoot, GameFiles.Mature, "payload");
        var files = new List<PayloadFile>();
        foreach (var name in GameFiles.Mature)
        {
            var path = Path.Combine(payloadRoot, name);
            files.Add(new PayloadFile(name, await hashService.ComputeSha256Async(path), new FileInfo(path).Length));
        }

        var manifest = new PayloadManifest(1, "test-payload", ["test-build"], files);
        await File.WriteAllTextAsync(Path.Combine(payloadRoot, "payload.manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        return new LoadedPayload(payloadRoot, manifest);
    }

    private OperationPlan CreateEnablePlan(LoadedPayload payload)
    {
        var actions = new List<PlannedAction> { new(PlannedActionKind.CreateBackup, paks) };
        actions.AddRange(payload.Manifest.Files.Select(file => new PlannedAction(PlannedActionKind.Install, Path.Combine(paks, file.Name), Path.Combine(payloadRoot, file.Name))));
        actions.AddRange(GameFiles.Original.Select(name => new PlannedAction(PlannedActionKind.Delete, Path.Combine(paks, name))));
        actions.Add(new PlannedAction(PlannedActionKind.Verify, paks));
        return new OperationPlan(OperationKind.Enable, installation, "test-build", true, false, "enable", [], actions, payload);
    }

    private (OperationExecutor Executor, BackupService Backup) CreateOperationServices(IFileSystem fileSystem)
    {
        var hash = new Sha256HashService(fileSystem);
        var backup = new BackupService(fileSystem, hash, backupRoot);
        var restore = new RestoreService(fileSystem, hash, backup);
        return (new OperationExecutor(fileSystem, hash, backup, restore, new FixedProcessDetector(false), "test"), backup);
    }

    private static void WriteSet(string directory, IEnumerable<string> names, string prefix)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(directory, name), $"{prefix}-{name}");
        }
    }

    private sealed class FixedProcessDetector(bool running) : IGameProcessDetector
    {
        public bool IsRunning() => running;
    }

    private sealed class FailingPayloadCopyFileSystem(IFileSystem inner, string payloadDirectory) : IFileSystem
    {
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern = "*") => inner.EnumerateFiles(path, pattern);
        public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);
        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAllTextAsync(path, cancellationToken);
        public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) => inner.WriteAllTextAsync(path, contents, cancellationToken);
        public Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken = default) =>
            source.StartsWith(payloadDirectory, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(new IOException("Injected payload copy failure."))
                : inner.CopyFileAsync(source, destination, overwrite, cancellationToken);
        public void MoveFile(string source, string destination, bool overwrite) => inner.MoveFile(source, destination, overwrite);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public long GetFileLength(string path) => inner.GetFileLength(path);
    }
}