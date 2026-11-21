namespace Ruse.App;

/// <summary>Loopback-only chat check for --chat-test; no network or UI needed.</summary>
internal static class ChatSelfTest
{
    public static string Run()
    {
        var checks = new List<string>();
        bool pass = true;

        void Check(string name, bool condition)
        {
            checks.Add((condition ? "PASS " : "FAIL ") + name);
            pass &= condition;
        }

        using var host = new ChatService();
        using var wrong = new ChatService();
        using var guest = new ChatService();
        try
        {
            host.Host("Host", 0, loopbackOnly: true, ownerNodeId: "host-node");
            ChatStateDto hostState = WaitFor(
                host, state => state.Hosting && state.Port > 0, "host start");
            Check("host listens on a pairing port", hostState.Port > 0);
            Check("host reports its owner node", hostState.OwnerNodeId == "host-node");
            Check("host exposes a six-digit code",
                hostState.PairCode.Length == 6 && hostState.PairCode.All(char.IsDigit));

            string wrongCode = hostState.PairCode == "000000" ? "000001" : "000000";
            wrong.Join("Wrong", "127.0.0.1", hostState.Port, wrongCode);
            ChatStateDto wrongState = WaitFor(
                wrong, state => !state.Active && state.Error != null, "wrong pairing code");
            Check("wrong pairing code is rejected",
                !wrongState.Connected &&
                wrongState.Error?.Contains("pairing code", StringComparison.OrdinalIgnoreCase) == true);

            guest.Join("Guest", "127.0.0.1", hostState.Port, hostState.PairCode, "guest-node");
            ChatStateDto connectedHost = WaitFor(
                host, state => state.Connected, "host connected");
            ChatStateDto connectedGuest = WaitFor(
                guest, state => state.Connected, "guest connected");
            Check("pairing connects both peers",
                connectedHost.PeerName == "Guest" && connectedGuest.PeerName == "Host");
            Check("guest reports its owner node", connectedGuest.OwnerNodeId == "guest-node");

            Check("host sends text", host.TrySend("hello from host") == null);
            Check("guest sends text", guest.TrySend("hello from guest") == null);
            ChatStateDto hostMessages = WaitFor(
                host, state => state.Messages.Count >= 2, "host messages");
            ChatStateDto guestMessages = WaitFor(
                guest, state => state.Messages.Count >= 2, "guest messages");
            Check("received text round-trips",
                hostMessages.Messages.Any(message => message.Text == "hello from guest" && !message.Mine) &&
                guestMessages.Messages.Any(message => message.Text == "hello from host" && !message.Mine));
            Check("sent text is marked as mine",
                hostMessages.Messages.Any(message => message.Text == "hello from host" && message.Mine) &&
                guestMessages.Messages.Any(message => message.Text == "hello from guest" && message.Mine));
            Check("message history keeps the newest entries",
                hostMessages.Messages[^1].Text == "hello from guest" &&
                guestMessages.Messages[^1].Text == "hello from host");
            Check("whitespace messages are ignored",
                host.TrySend("   ") != null);
            Check("oversized messages are rejected",
                host.TrySend(new string('x', 4001)) != null);

            string unicode = new string('界', 4000);
            Check("unicode text is accepted", host.TrySend(unicode) == null);
            ChatStateDto unicodeGuest = WaitFor(
                guest, state => state.Messages.Any(message => message.Text == unicode), "unicode message");
            Check("unicode text round-trips",
                unicodeGuest.Connected &&
                unicodeGuest.Messages.Any(message => message.Text == unicode && !message.Mine));

            host.Stop();
            ChatStateDto stoppedHost = WaitFor(
                host, state => !state.Active, "host stop");
            Check("stopping the host closes the session",
                !stoppedHost.Hosting && !stoppedHost.Connected);
            WaitFor(guest, state => !state.Active, "guest disconnect");
            Check("guest leaves when the host stops",
                !guest.Snapshot().Active && !guest.Snapshot().Connected);
        }
        catch (Exception ex)
        {
            checks.Add("FAIL chat exception: " + ex.Message);
            pass = false;
        }

        return $"chat loopback: {string.Join(", ", checks)} -> {(pass ? "PASS" : "FAIL")}";
    }

    private static ChatStateDto WaitFor(
        ChatService service, Func<ChatStateDto, bool> condition, string label)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            ChatStateDto state = service.Snapshot();
            if (condition(state))
            {
                return state;
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException("Timed out waiting for " + label + ".");
    }
}
