using System.Text;
using System.Windows.Threading;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly ChatService _chat = new();
    private readonly Dictionary<string, StringBuilder> _networkIncomingText = new(StringComparer.Ordinal);
    private bool _chatInitialized;

    private void InitializeChat()
    {
        if (_chatInitialized)
        {
            return;
        }

        _chatInitialized = true;
        _chat.StateChanged += OnChatStateChanged;
        _chat.IncomingMessage += OnChatIncomingMessage;
    }

    private void OnChatStateChanged(ChatStateDto state)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher dispatcher = _window.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            PostChatState(state);
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => PostChatState(state)));
        }
        catch (InvalidOperationException)
        {
            // The window can be closing while a background socket finishes.
        }
    }

    private void OnChatIncomingMessage(string ownerNodeId, ChatMessageDto message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(ownerNodeId))
        {
            return;
        }

        Dispatcher dispatcher = _window.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            ReceiveNetworkText(ownerNodeId, message.Text);
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ReceiveNetworkText(ownerNodeId, message.Text)));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ReceiveNetworkText(string ownerNodeId, string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text) ||
            !_nodes.TryGetValue(ownerNodeId, out var source) ||
            source.Source is not { Kind: SourceKind.Network })
        {
            return;
        }

        if (!_networkIncomingText.TryGetValue(ownerNodeId, out var buffer))
        {
            buffer = new StringBuilder();
            _networkIncomingText[ownerNodeId] = buffer;
        }

        if (buffer.Length > 0)
        {
            buffer.Append('\n');
        }

        buffer.Append(text.Trim());
        if (buffer.Length > 20000)
        {
            int removed = buffer.Length - 20000;
            buffer.Remove(0, removed);
            foreach (var entry in _nodes.Values)
            {
                if (entry.Source?.Kind == SourceKind.Tts &&
                    string.Equals(entry.TtsSourceNodeId, ownerNodeId, StringComparison.Ordinal) &&
                    entry.TtsSpokenLength > 0)
                {
                    entry.TtsSpokenLength = Math.Max(0, entry.TtsSpokenLength - removed);
                }
            }
        }

        foreach (var entry in _nodes.Values)
        {
            if (string.Equals(entry.TranslatorTextSourceId, ownerNodeId, StringComparison.Ordinal))
            {
                entry.TranslatorText?.Submit(text);
            }
        }
    }

    private string NetworkIncomingText(string nodeId) =>
        _networkIncomingText.TryGetValue(nodeId, out var buffer) ? buffer.ToString() : "";

    private void PostChatState(ChatStateDto state)
    {
        if (!_disposed)
        {
            Post("chat-state", state);
        }
    }

    private void ClearNetworkChatText()
    {
        _networkIncomingText.Clear();
        foreach (var entry in _nodes.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.TranslatorTextSourceId) ||
                !_nodes.TryGetValue(entry.TranslatorTextSourceId, out var source) ||
                source.Source is not { Kind: SourceKind.Network })
            {
                continue;
            }

            RestartTranslatorText(entry);
        }
    }

    private void StartChatHost(BridgeMessage message)
    {
        try
        {
            ClearNetworkChatText();
            _chat.Host(
                String(message, "name"),
                (int)Number(message, "port", ChatService.DefaultPort),
                Bool(message, "loopbackOnly", false),
                String(message, "nodeId"));
            ChatStateDto state = _chat.Snapshot();
            SetStatus(state.Port > 0
                ? $"Hosting chat on port {state.Port}. Share the pairing code."
                : "Hosting chat.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            ShowToast(ex.Message, isError: true);
            PostChatState(_chat.Snapshot());
        }
    }

    private void StartChatJoin(BridgeMessage message)
    {
        try
        {
            ClearNetworkChatText();
            _chat.Join(
                String(message, "name"),
                String(message, "host"),
                (int)Number(message, "port", ChatService.DefaultPort),
                String(message, "code"),
                String(message, "nodeId"));
            SetStatus("Connecting to chat...");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            ShowToast(ex.Message, isError: true);
            PostChatState(_chat.Snapshot());
        }
    }

    private void SendChatMessage(BridgeMessage message)
    {
        string? error = _chat.TrySend(String(message, "text"));
        if (error != null)
        {
            SetStatus(error, isError: true);
            ShowToast(error, isError: true);
        }
    }

    private void StopChat()
    {
        _chat.Stop();
        ClearNetworkChatText();
        SetStatus("Chat closed.");
    }
}
