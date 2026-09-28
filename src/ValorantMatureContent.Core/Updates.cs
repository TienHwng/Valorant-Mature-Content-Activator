namespace ValorantMatureContent.Core;

public sealed record UpdateAvailability(bool IsAvailable, string CurrentVersion, string? LatestVersion, Uri? ReleasePage);
public sealed record PayloadUpdateAvailability(bool IsAvailable, string CurrentPayloadVersion, string? LatestPayloadVersion, Uri? ManifestUri, string? ManifestSha256);

public interface IApplicationUpdateService
{
    Task<OperationResult<UpdateAvailability>> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IPayloadUpdateService
{
    Task<OperationResult<PayloadUpdateAvailability>> CheckAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<LoadedPayload>> DownloadAndVerifyAsync(PayloadUpdateAvailability update, string destination, CancellationToken cancellationToken = default);
}