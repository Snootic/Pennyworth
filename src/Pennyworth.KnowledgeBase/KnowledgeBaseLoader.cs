using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Pennyworth.KnowledgeBase;

/// <summary>
/// Loads the embedded data sets. Each operating system has its own set of five files (locations,
/// processes, cleanup rules, diagnosis rules, blocked advice) with the same schema; the platform name
/// (<see cref="Windows"/> or <see cref="Linux"/>, the <c>IPlatform.Name</c> of the composition root) picks one.
/// Every loader defaults to <see cref="Windows"/>, which is what the data was written for and what every
/// caller that never asked the question still means.
/// </summary>
public static class KnowledgeBaseLoader
{
    public const string Windows = "windows";
    public const string Linux = "linux";

    public static KnowledgeBaseData LoadEmbedded(string platform = Windows) =>
        Load(ResourceName(platform, "locations"), KnowledgeBaseJsonContext.Default.KnowledgeBaseData);

    public static KnowledgeBaseData Parse(string json) =>
        JsonSerializer.Deserialize(json, KnowledgeBaseJsonContext.Default.KnowledgeBaseData)
        ?? throw new InvalidDataException("The knowledge base is empty.");

    /// <summary>Plain-language names for the operating system's own and well-known processes.</summary>
    public static ProcessCatalog LoadProcesses(string platform = Windows) =>
        Load(ResourceName(platform, "processes"), CatalogJsonContext.Default.ProcessCatalog);

    /// <summary>What the cleanup screen offers, with safety levels and consequences.</summary>
    public static CleanupCatalog LoadCleanupRules(string platform = Windows) =>
        Load(ResourceName(platform, "cleanup"), CatalogJsonContext.Default.CleanupCatalog);

    /// <summary>What the assistant concludes from the facts it is given.</summary>
    public static DiagnosisCatalog LoadDiagnosisRules(string platform = Windows) =>
        Load(ResourceName(platform, "diagnosis"), AssistantCatalogJsonContext.Default.DiagnosisCatalog);

    /// <summary>Advice the assistant must never give, and what it says instead.</summary>
    public static AdviceCatalog LoadBlockedAdvice(string platform = Windows) =>
        Load(ResourceName(platform, "advice"), AssistantCatalogJsonContext.Default.AdviceCatalog);

    public static DiagnosisCatalog ParseDiagnosisRules(string json) =>
        JsonSerializer.Deserialize(json, AssistantCatalogJsonContext.Default.DiagnosisCatalog)
        ?? throw new InvalidDataException("The diagnosis rules are empty.");

    /// <exception cref="ArgumentException">There is no data set for <paramref name="platform"/>.</exception>
    private static string ResourceName(string platform, string file) => platform switch
    {
        Windows => $"Pennyworth.KnowledgeBase.{file}.json",
        Linux => $"Pennyworth.KnowledgeBase.linux.{file}.json",
        _ => throw new ArgumentException($"There is no knowledge base for the platform '{platform}'. Known platforms: {Windows}, {Linux}.", nameof(platform)),
    };

    private static T Load<T>(string resourceName, JsonTypeInfo<T> typeInfo)
    {
        using Stream stream = typeof(KnowledgeBaseLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new InvalidDataException($"The knowledge base file '{resourceName}' is empty.");
    }
}
