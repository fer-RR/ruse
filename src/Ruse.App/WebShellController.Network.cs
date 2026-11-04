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
}
