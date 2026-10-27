using System.Globalization;
using System.Text.Json.Serialization;
using Pennyworth.Core.Storage;

namespace Pennyworth.KnowledgeBase;

public sealed record LocalizedText(
    string En,
    [property: JsonPropertyName("pt-BR")] string PtBr)
{
    public string Get(CultureInfo culture) =>
        culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? PtBr : En;
}

/// <summary>
/// A known folder or root file. <see cref="Path"/> supports <c>%ENV%</c> variables,
/// <c>?:</c> for any drive, <c>*</c> for one folder name and a leading <c>**</c> for any prefix.
/// The category applies to the matched item and everything below it.
/// </summary>
/// <param name="Children">
/// Set for a folder that holds one place per app or tool (<c>.var/app</c>, <c>/opt</c>, <c>.cache</c>): the storage page then
/// lists each folder inside it on its own instead of one lump. Null everywhere in the Windows data.
/// </param>
public sealed record KnownLocation(
    string Id,
    string Path,
    StorageCategory Category,
    LocalizedText Title,
    LocalizedText Description,
    ChildPlaces? Children = null);

/// <summary>
/// How the folders inside a <see cref="KnownLocation"/> that holds one place per app are named when no rule of their own
/// matches them. A folder that has its own rule keeps it.
/// </summary>
/// <param name="Title">Null: the folder is named after the app that owns it, or its own name. Otherwise <c>{0}</c> is that name.</param>
/// <param name="Description">Null: no description. <c>{0}</c> is the app's name.</param>
public sealed record ChildPlaces(LocalizedText? Title = null, LocalizedText? Description = null);

public sealed record KnowledgeBaseData(int Version, IReadOnlyList<KnownLocation> Locations);

[JsonSerializable(typeof(KnowledgeBaseData))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
internal sealed partial class KnowledgeBaseJsonContext : JsonSerializerContext;
