namespace AgentLink.Core;

public sealed record McpPresenceRequest(string ActorId, string? ConnectionId, bool CurrentSession, bool Connected);
