using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void SetNetworkInputPort(string id, int port)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.Network } source)
        {
            return;
        }

        source.SetNetworkPort(port);
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetNetworkOutputHost(string id, string host)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsNetworkOutput: true } output)
        {
            return;
        }

        output.SetNetworkDestination(host, output.NetworkPort);
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetNetworkOutputPort(string id, int port)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsNetworkOutput: true } output)
        {
            return;
        }

        output.SetNetworkDestination(output.NetworkHost, port);
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetNetworkVoicePort(string id, int port)
    {
        if (!_nodes.TryGetValue(id, out var entry))
        {
            return;
        }

        int clamped = Math.Clamp(port, 1, 65535);
        if (entry.Source is { Kind: SourceKind.Network } source)
        {
            source.SetNetworkPort(clamped);
        }

        if (entry.Output is { IsNetworkOutput: true } output)
        {
            output.SetNetworkDestination(output.NetworkHost, clamped);
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetNetworkChatPort(string id, int port)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsNetworkOutput: true })
        {
            return;
        }

        ChatStateDto state = _chat.Snapshot();
        if (state.Active && string.Equals(state.OwnerNodeId, id, StringComparison.Ordinal))
        {
            _chat.Stop();
        }

        entry.ChatPort = Math.Clamp(port, 1, 65535);
        PublishGraph();
    }
}
