using System.Text.Json;

namespace AgentLink.Core;

public static class AgentToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } = CreateTools();

    private static IReadOnlyList<ToolDefinition> CreateTools()
    {
        const string session = "\"sessionId\":{\"type\":\"string\",\"maxLength\":128}";
        const string task = "\"taskId\":{\"type\":\"string\",\"maxLength\":128}";
        const string directory = "\"workingDirectory\":{\"type\":\"string\",\"maxLength\":4096}";
        static ToolDefinition Tool(string name, string description, string properties, bool mutating, params string[] required)
        {
            using var schema = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" + properties +
                "},\"required\":" + JsonSerializer.Serialize(required) + "}");
            return new(name, description, schema.RootElement.Clone(), mutating, "agent-delegation");
        }
        return Array.AsReadOnly(new[]
        {
            Tool("agent_list_sessions", "List this peer's broker-owned or explicitly attached agent sessions.", "", false),
            Tool("agent_start_session", "Create a managed coding session, or explicitly resume a stored sessionId. Use an intended project directory, " +
                "not a user-profile directory that contains broker private data. Never use agent sessions as a workaround for desktop-tool failures or denied control.",
                directory + "," + session, true),
            Tool("agent_attach_session", "Interactive attachment is currently unavailable (incompatible_runtime). " +
                "Never automate an existing terminal as a workaround; this operation never accepts endpoints or tokens.",
                "\"registrationId\":{\"type\":\"string\",\"maxLength\":128}," + session, true, "registrationId", "sessionId"),
            Tool("agent_detach_session", "Detach an idle session without stopping a user-owned CLI.", session, true, "sessionId"),
            Tool("agent_send", "Durably accept one scoped coding task while device access is enabled; returns immediately. " +
                "Explicitly set workspace read/write and command scope as needed; missing scope does not open an approval dialog. " +
                "Unattended work requires an explicit soft credit limit.",
                session + "," + directory + """
                ,"prompt":{"type":"string","maxLength":32768},"idempotencyKey":{"type":"string","maxLength":128},
                "timeoutSeconds":{"type":"integer","minimum":1,"maximum":86400},"unattended":{"type":"boolean","default":false},
                "maxAiCredits":{"type":"number","exclusiveMinimum":0},"allowWorkspaceRead":{"type":"boolean","default":false},
                "allowWorkspaceWrite":{"type":"boolean","default":false},
                "approvedCommands":{"type":"array","maxItems":16,"items":{"type":"string","maxLength":1024}}
                """, true, "sessionId", "prompt", "idempotencyKey", "timeoutSeconds"),
            Tool("agent_get_task", "Retrieve durable state, grant, result, and error of an owned task.", task, false, "taskId"),
            Tool("agent_read_events", "Read bounded final-answer/tool/status events after a cursor; never model reasoning.",
                task + ",\"afterCursor\":{\"type\":\"integer\",\"minimum\":0},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}", false, "taskId"),
            Tool("agent_cancel_task", "Request cancellation only of this broker's delegated turn; poll for confirmed outcome.", task, true, "taskId"),
            Tool("agent_answer_question", "Answer an existing normal question only; cannot approve permissions, credentials, or budgets.",
                task + ",\"questionId\":{\"type\":\"string\"},\"answer\":{\"type\":\"string\",\"maxLength\":8192}", true, "taskId", "questionId", "answer")
        });
    }
}
