internal sealed record McpLaunchOptions(bool CurrentSession, string? BrokerPipe, string? ConnectionId)
{
    public const string Usage =
        "Usage: AgentLink.Mcp.exe [--current-session | --connection connectionId] [--broker-pipe name]";

    public static McpLaunchOptions Parse(string[] args)
    {
        var currentSession = false;
        string? brokerPipe = null;
        string? connectionId = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--current-session" when !currentSession && connectionId is null:
                    currentSession = true;
                    break;
                case "--connection" when !currentSession && connectionId is null && index + 1 < args.Length:
                    connectionId = args[++index];
                    if (!IsIdentifier(connectionId, 256, allowColon: true))
                        throw new ArgumentException("Use the exact connection ID shown by Remote Agent Link.");
                    break;
                case "--broker-pipe" when brokerPipe is null && index + 1 < args.Length:
                    brokerPipe = args[++index];
                    if (!IsIdentifier(brokerPipe, 230, allowColon: false))
                        throw new ArgumentException("The broker pipe must be a simple local pipe name, not a path or network endpoint.");
                    break;
                default:
                    throw new ArgumentException(Usage);
            }
        }
        return new(currentSession, brokerPipe, connectionId);
    }

    private static bool IsIdentifier(string value, int maximumLength, bool allowColon) =>
        value.Length > 0 && value.Length <= maximumLength &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' || (allowColon && character == ':'));
}
