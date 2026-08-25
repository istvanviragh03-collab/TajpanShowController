using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Interfaces;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
