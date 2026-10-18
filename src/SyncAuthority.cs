namespace RuinaNet
{
    internal static class SyncAuthority
    {
        // Unit ownership grants page choices only. Combat outcomes always belong to host.
        public static bool ReceiveCombatState(bool localHost) { return !localHost; }
        public static bool ReceiveChoices(bool localHost, bool enemy, bool locallyOwned)
        {
            if (enemy) return !localHost;
            return !locallyOwned;
        }

        public static bool ReceivePlanningRound(int localRound, int packetRound, bool armed,
                                                 bool peerSubmitted, int peerReadyRound)
        {
            if (packetRound == localRound) return true;
            if (armed && !peerSubmitted && packetRound == localRound - 1) return true;
            return !armed && packetRound == localRound + 1 && peerReadyRound == packetRound;
        }
    }
}
