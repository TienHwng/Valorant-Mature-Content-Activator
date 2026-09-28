using System.Security.Cryptography;

namespace ValorantMatureContent.Core;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    IEnumerable<string> EnumerateFiles(string path, string pattern = "*");
    IEnumerable<string> EnumerateDirectories(string path);
    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);
    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);
    Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken = default);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    long GetFileLength(string path);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IEnumerable<string> EnumerateFiles(string path, string pattern = "*") => Directory.EnumerateFiles(path, pattern);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) => File.ReadAllTextAsync(path, cancellationToken);
    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) => File.WriteAllTextAsync(path, contents, cancellationToken);

    public async Task CopyFileAsync(string source, string destination, bool overwrite, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public long GetFileLength(string path) => new FileInfo(path).Length;
}

public interface IHashService
{
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
    Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken cancellationToken = default);
}

public sealed class Sha256HashService(IFileSystem fileSystem) : IHashService
{
    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = await fileSystem.OpenReadAsync(path, cancellationToken);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public async Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken cancellationToken = default) =>
        fileSystem.FileExists(path) && string.Equals(await ComputeSha256Async(path, cancellationToken), expectedSha256, StringComparison.OrdinalIgnoreCase);
}