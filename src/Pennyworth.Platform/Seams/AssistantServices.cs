using Microsoft.Extensions.AI;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Platform;

/// <summary>Everything the Ask screen needs; tests pass fakes.</summary>
/// <param name="CreateClient">A chat client for a profile; the key is null for local profiles.</param>
/// <param name="Download">Downloads and verifies a catalogued model into a folder.</param>
/// <param name="AvailableMemory">Physical memory free right now, in bytes.</param>
/// <param name="FreeSpace">Free bytes on the drive holding a folder.</param>
/// <param name="CanRunLocally">
/// Whether this platform can run the on-PC model. When false the Ask screen does not offer it at all:
/// better than offering a download that cannot work.
/// </param>
/// <param name="PlatformName">Which knowledge base the assistant's diagnosis rules and blocked advice come from.</param>
public sealed record AssistantServices(
    ICredentialStore Keys,
    ConversationStore Conversations,
    UsageMeter Usage,
    ModelCatalog Catalog,
    LocalModelStore Models,
    Func<AssistantProfile, string?, IChatClient> CreateClient,
    Func<LocalModel, string, IProgress<DownloadProgress>, CancellationToken, Task> Download,
    Anonymizer Anonymizer,
    Func<long> AvailableMemory,
    Func<string, long> FreeSpace,
    bool CanRunLocally = true,
    string PlatformName = KnowledgeBaseLoader.Windows);
