using System;
using System.Collections.Generic;
using System.Globalization;

namespace RuinaNet
{
    // Ordered, acknowledged delivery for state and commands. Cursor samples use UDP.
    // All methods are called on Unity's main thread, including received packets.
    internal sealed class ReliableChannel
    {
        private sealed class Packet { public string Wire; public long LastSent; }
        private readonly SortedDictionary<int, Packet> _pending = new SortedDictionary<int, Packet>();
        private readonly SortedDictionary<int, string> _received = new SortedDictionary<int, string>();
        private int _send = 1;
        private int _receive = 1;
        public string LocalSession;
        public string PeerSession;
        public Action<string> SendWire;
        public Action<string> Deliver;
        public int PendingCount { get { return _pending.Count; } }

        public void Reset(string local, string peer)
        {
            LocalSession = local;
            PeerSession = peer;
            _pending.Clear();
            _received.Clear();
            _send = _receive = 1;
        }

        public void Send(string payload, long now)
        {
            if (PeerSession == null) return;
            if (_pending.Count >= 4096) throw new InvalidOperationException("Reliable queue is full; peer is not acknowledging.");
            int seq = _send++;
            string wire = "REL " + LocalSession + " " + seq.ToString(CultureInfo.InvariantCulture) + " " + payload;
            _pending.Add(seq, new Packet { Wire = wire, LastSent = now });
            SendWire(wire);
        }

        public void Tick(long now)
        {
            // Collect before sending: callbacks in the test loop can synchronously ACK.
            List<Packet> retry = new List<Packet>();
            foreach (Packet packet in _pending.Values)
                if (now - packet.LastSent >= 250) retry.Add(packet);
            foreach (Packet packet in retry) { packet.LastSent = now; SendWire(packet.Wire); }
        }

        public void Receive(string wire)
        {
            string[] p = wire.Split(new char[] { ' ' }, 4);
            int seq;
            if (p.Length < 3 || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out seq) || seq < 1) return;
            if (p[0] == "RACK")
            {
                if (p[1] == LocalSession) _pending.Remove(seq);
                return;
            }
            if (p[0] != "REL" || p.Length != 4 || p[1] != PeerSession) return;
            if (seq >= _receive + 4096) return; // do not acknowledge data we cannot retain
            SendWire("RACK " + PeerSession + " " + seq.ToString(CultureInfo.InvariantCulture));
            if (seq < _receive) return;
            _received[seq] = p[3];
            string payload;
            while (_received.TryGetValue(_receive, out payload))
            {
                _received.Remove(_receive++);
                Deliver(payload);
            }
        }
    }
}
