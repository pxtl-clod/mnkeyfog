using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json.Serialization;

namespace MnkeyFog.BskyService;

/// <summary>
/// Persistence DTO for a <see cref="GameSession"/>: game state plus chat metadata.
/// A list of entries is used instead of a dictionary so that the polymorphic
/// type names stay resolvable by <see cref="ModelTypesBinder"/>.
/// </summary>
internal sealed record PersistedSession(
    string ChatId,
    string CreatorDid,
    DateTimeOffset CreatedAt,
    List<PersistedPlayerDid> PlayerDids,
    GameState GameState
);

internal sealed record PersistedPlayerDid(string Did, int PlayerIndex);

/// <summary>
/// Serialization binder for GameState persistence, restricting polymorphic
/// deserialization to types in the MnkeyFog.Model assembly marked
/// <see cref="ModelSerializableAttribute"/>.
/// </summary>
public class ModelTypesBinder : ISerializationBinder {
    public static ModelTypesBinder Instance { get; } = new();

    private readonly Dictionary<string, Type> _knownTypes;

    private ModelTypesBinder() {
        _knownTypes = typeof(ModelSerializableAttribute).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes<ModelSerializableAttribute>().Any())
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        // Persistence DTOs from this library.
        _knownTypes[nameof(PersistedSession)] = typeof(PersistedSession);
        _knownTypes[nameof(PersistedPlayerDid)] = typeof(PersistedPlayerDid);
    }

    public Type BindToType(string? assemblyName, string? typeName) {
        ArgumentNullException.ThrowIfNull(typeName, nameof(typeName));

        if (_knownTypes.TryGetValue(typeName, out var type)
            && (type.Assembly.Equals(typeof(ModelSerializableAttribute).Assembly)
                || type.Assembly.Equals(typeof(ModelTypesBinder).Assembly))) {
            return type;
        }

        throw new SerializationException($"Type '{typeName}' is not a known serializable MnkeyFog type.");
    }

    public void BindToName(Type serializedType, out string? assemblyName, out string typeName) {
        assemblyName = null;
        typeName = serializedType.Name;
    }
}
