/// <summary>
/// Chains part of the main window. The shared state lives in MainWindow.xaml.cs;
/// this file only holds the code for that area.
/// </summary>
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using Ruse.App.Ui;

namespace Ruse.App;

public partial class MainWindow
{
    // ---------- Chains (subgraphs) ----------

    private void RefreshChains()
    {
        SubgraphLibrary.Load();
        ChainsList.ItemsSource = SubgraphLibrary.Chains;
        ChainsCount.Text = SubgraphLibrary.Chains.Count.ToString();
    }

    private void OnNewChainClick(object sender, RoutedEventArgs e)
    {
        var definition = ChainBuilderDialog.Show(this, null);
        if (definition == null)
        {
            return;
        }

        SubgraphLibrary.Store(definition);
        RefreshChains();
        SetStatus($"Saved the chain '{definition.Name}'.");
    }

    private void OnChainDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChainsList.SelectedItem is SubgraphDefinition definition)
        {
            AddChainNode(definition, null);
        }
    }

    /// <summary>Places a saved chain on the canvas as a single node.</summary>
    private NodeViewModel? AddChainNode(SubgraphDefinition definition, Point? at)
    {
        var processor = definition.CreateProcessor();
        var modifier = new ModifierNode(definition.Name);
        modifier.Add(processor);

        double x = at?.X ?? (120 + (_nodes.Count * 30));
        double y = at?.Y ?? (120 + (_nodes.Count * 30));

        var vm = AddModifierNodeCore(modifier, definition.Name, x, y);
        if (vm != null)
        {
            SetStatus($"Added the chain '{definition.Name}'.");
        }

        return vm;
    }

    /// <summary>Opens the builder for a chain that is already on the canvas.</summary>
    private void EditChainNode(ModifierNode modifier)
    {
        var chain = modifier.Processors.OfType<ChainProcessor>().FirstOrDefault();
        if (chain == null)
        {
            RenameNode(_nodeBacking.FirstOrDefault(kv => ReferenceEquals(kv.Value, modifier)).Key!);
            return;
        }

        var definition = SubgraphDefinition.FromProcessor(chain);
        var edited = ChainBuilderDialog.Show(this, definition);
        if (edited == null)
        {
            return;
        }

        SubgraphLibrary.Store(edited);

        // Replace the live chain with the edited one.
        modifier.Clear();
        modifier.Add(edited.CreateProcessor());
        modifier.Title = edited.Name;
        RefreshChains();
        SetStatus($"Updated the chain '{edited.Name}'.");
    }
}
