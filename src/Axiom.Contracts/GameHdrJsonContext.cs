using System.Text.Json.Serialization;

namespace Axiom.Contracts;

[JsonSerializable(typeof(GameHdrMessage))]
internal sealed partial class GameHdrJsonContext : JsonSerializerContext;
