namespace AgentLink.Broker.ViewModels;

public sealed record AgentConnectionViewModel
{
    internal AgentConnectionViewModel(AgentConnectionSnapshot agent)
    {
        Id = agent.Id;
        ComputerName = PeerViewModel.FriendlyName(agent.ComputerName);
        IsLocal = agent.IsLocal;
    }

    public string Id { get; }
    public string ComputerName { get; }
    public bool IsLocal { get; }
    public string Description => $"MCP agent · {(IsLocal ? $"This computer ({ComputerName})" : $"From {ComputerName}")}";
}
