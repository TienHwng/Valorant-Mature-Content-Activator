using System.Text.Json;

namespace ValorantMatureContent.Core;

public sealed record PayloadManifest(int SchemaVersion, string PayloadVersion, IReadOnlyList<string> SupportedGameBuilds, IReadOnlyList<PayloadFile> Files);
public sealed record PayloadFile(string Name, string Sha256, long Size);
public sealed record LoadedPayload(string RootPath, PayloadManifest Manifest);

public interface IPayloadService
{
    Task<OperationResult<LoadedPayload>> LoadAndVerifyAsync(string manifestPath, CancellationToken cancellationToken = default);
}

public sealed class PayloadService(IFileSystem fileSystem, IHashService hashService) : IPayloadService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<OperationResult<LoadedPayload>> LoadAndVerifyAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!fileSystem.FileExists(manifestPath))
            {
                return OperationResult.Failure<LoadedPayload>(ErrorCode.InvalidManifest, "Payload manifest was not found.", manifestPath);
            }

            var json = await fileSystem.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PayloadManifest>(json, JsonOptions);
            if (manifest is null || manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.PayloadVersion) || manifest.Files.Count == 0)
            {
                return OperationResult.Failure<LoadedPayload>(ErrorCode.InvalidManifest, "Payload manifest is invalid or uses an unsupported schema.");
            }

            var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Name) || Path.GetFileName(file.Name) != file.Name || !names.Add(file.Name) || file.Size < 0 || file.Sha256.Length != 64)
                {
                    return OperationResult.Failure<LoadedPayload>(ErrorCode.InvalidManifest, $"Invalid payload entry: {file.Name}");
                }

                var path = Path.Combine(root, file.Name);
                if (!fileSystem.FileExists(path) || fileSystem.GetFileLength(path) != file.Size || !await hashService.VerifyAsync(path, file.Sha256, cancellationToken))
                {
                    return OperationResult.Failure<LoadedPayload>(ErrorCode.PayloadHashMismatch, $"Payload integrity check failed for {file.Name}.");
                }
            }

            return OperationResult.Success(new LoadedPayload(root, manifest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult.Failure<LoadedPayload>(ErrorCode.InvalidManifest, "Payload manifest could not be read.", exception.Message);
        }
    }
}

public interface ICompatibilityService
{
    CompatibilityStatus Check(string? gameVersion, PayloadManifest manifest);
}

public sealed class CompatibilityService : ICompatibilityService
{
    public CompatibilityStatus Check(string? gameVersion, PayloadManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return CompatibilityStatus.Unknown;
        }

        return manifest.SupportedGameBuilds.Contains(gameVersion, StringComparer.OrdinalIgnoreCase)
            ? CompatibilityStatus.Supported
            : CompatibilityStatus.Unsupported;
    }
}