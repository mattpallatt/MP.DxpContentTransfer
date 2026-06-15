using DxpContentTransfer.Models;

namespace DxpContentTransfer.Services;

public interface IContentTransferService
{
    Task<PreCheckResult> PreCheckAsync(string contentId, string targetEnvironment, bool includeChildren, bool overwriteMatchingIds);
    Task<TransferResult> TransferAsync(string contentId, string targetEnvironment, bool includeChildren, string sourceEnvironmentName, string transferStatus = "Published", List<PreCheckItemResult> plan = null, Action onItemComplete = null, IReadOnlyCollection<string> selectedLanguages = null);
}
