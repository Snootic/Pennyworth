using Microsoft.Extensions.AI;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Platform;

/// <summary>
/// Builds the Ask screen's services from the parts an operating system decides: where the API keys
/// are kept, where files live, and how to measure free disk space.
/// </summary>
public static class AssistantServicesFactory
{
    public static AssistantServices Create(
        ICredentialStore keys,
        string conversationFolder,
        string usagePath,
        string modelRoot,
        Func<long> availableMemory,
        Func<string, long> freeSpace,
        bool canRunLocally = true,
        string platformName = KnowledgeBaseLoader.Windows)
    {
        var catalog = ModelCatalog.Load();
        var models = new LocalModelStore(modelRoot);
        var local = new Dictionary<string, LocalChatClient>();
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new ModelDownloader(http);

        IChatClient CreateClient(AssistantProfile profile, string? key)
        {
            if (!profile.IsLocal)
            {
                return ChatClientFactory.Create(profile, key ?? "");
            }

            // One client per model, kept so it can stay loaded between questions and unload when idle.
            if (!local.TryGetValue(profile.ModelId, out LocalChatClient? client))
            {
                LocalModel model = catalog.Get(profile.ModelId);
                client = new LocalChatClient(models.FolderOf(model), model);
                local[profile.ModelId] = client;
            }

            return new NonDisposingClient(client);
        }

        return new AssistantServices(
            keys,
            new ConversationStore(conversationFolder),
            new UsageMeter(usagePath),
            catalog,
            models,
            CreateClient,
            (model, folder, progress, ct) => downloader.DownloadAsync(model, folder, progress, ct),
            Anonymizer.ForCurrentUser(),
            availableMemory,
            freeSpace,
            canRunLocally,
            platformName);
    }

    /// <summary>Lets the screen dispose what it was given without unloading a shared local model.</summary>
    private sealed class NonDisposingClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
