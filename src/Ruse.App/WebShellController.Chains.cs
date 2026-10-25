using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void SaveChain(BridgeMessage message)
    {
        var request = Payload<ChainSaveDto>(message);
        if (request == null)
        {
            SetStatus("The chain could not be read.", isError: true);
            return;
        }

        string name = request.Name.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetStatus("Give the chain a name.", isError: true);
            return;
        }

        if (name.Length > 80) name = name[..80];
        if (request.Elements.Count > 64)
        {
            SetStatus("A chain can contain at most 64 effects.", isError: true);
            return;
        }

        var definition = new SubgraphDefinition { Name = name };
        foreach (var saved in request.Elements)
        {
            var descriptor = AudioProcessorCatalog.Find(saved.TypeId);
            if (descriptor == null)
            {
                continue;
            }

            var element = new SubgraphElement { TypeId = saved.TypeId };
            var live = descriptor.Create();
            if (live is IParameterizedProcessor parameterized)
            {
                foreach (var parameter in parameterized.Parameters)
                {
                    if (!saved.Parameters.TryGetValue(parameter.Name, out float value))
                    {
                        continue;
                    }

                    value = Math.Clamp(value, parameter.Min, parameter.Max);
                    if (parameter.IsInteger) value = MathF.Round(value);
                    parameter.Set(value);
                    element.Parameters[parameter.Name] = value;
                }
            }

            definition.Elements.Add(element);
        }

        if (!string.IsNullOrEmpty(request.PreviousName) &&
            !string.Equals(request.PreviousName, name, StringComparison.OrdinalIgnoreCase))
        {
            var previous = SubgraphLibrary.Chains.FirstOrDefault(chain =>
                string.Equals(chain.Name, request.PreviousName, StringComparison.OrdinalIgnoreCase));
            if (previous != null)
            {
                SubgraphLibrary.Remove(previous);
            }
        }

        SubgraphLibrary.Store(definition);

        if (!string.IsNullOrEmpty(request.NodeId) &&
            _nodes.TryGetValue(request.NodeId, out var entry) &&
            entry.Modifier?.Processors.OfType<ChainProcessor>().FirstOrDefault() != null)
        {
            entry.Modifier.Clear();
            entry.Modifier.Add(definition.CreateProcessor());
            entry.Modifier.Title = definition.Name;
            entry.Title = definition.Name;
            entry.Subtitle = definition.Description;
            PublishGraph();
        }

        RefreshEffectsAndChains();
        Post("catalog", _catalog);
        SetStatus($"Saved the chain '{definition.Name}'.");
    }

    private void OpenChainForNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null)
        {
            return;
        }

        var chain = entry.Modifier.Processors.OfType<ChainProcessor>().FirstOrDefault();
        if (chain == null)
        {
            return;
        }

        var definition = SubgraphDefinition.FromProcessor(chain);
        Post("chain-edit", new ChainSaveDto
        {
            Name = definition.Name,
            PreviousName = definition.Name,
            NodeId = entry.Id,
            Elements = definition.Elements.Select(element => new ChainElementDto
            {
                TypeId = element.TypeId,
                Parameters = new Dictionary<string, float>(element.Parameters),
            }).ToList(),
        });
    }

    private void DeleteChain(string name)
    {
        var definition = SubgraphLibrary.Chains.FirstOrDefault(chain =>
            string.Equals(chain.Name, name, StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            return;
        }

        SubgraphLibrary.Remove(definition);
        RefreshEffectsAndChains();
        Post("catalog", _catalog);
        SetStatus($"Removed the chain '{definition.Name}'.");
    }
}
