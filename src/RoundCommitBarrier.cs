namespace RuinaNet
{
    // Unity-independent final snapshot acknowledgement state, covered by the
    // packet-order regression executable.
    internal sealed class RoundCommitBarrier
    {
        public int Round = -1;
        public bool SnapshotSent;
        public int AckRound = -1;

        public void Reset()
        {
            Round = -1;
            SnapshotSent = false;
            AckRound = -1;
        }

        public void Begin(int round)
        {
            Round = round;
            SnapshotSent = false;
            AckRound = -1;
        }

        public bool MarkSnapshotSent(int round)
        {
            if (round != Round || SnapshotSent) return false;
            SnapshotSent = true;
            return true;
        }

        public bool NoteAck(int round)
        {
            if (!SnapshotSent || round != Round) return false;
            AckRound = round;
            return true;
        }

        public bool CanRelease(int round)
        {
            return SnapshotSent && round == Round && AckRound == Round;
        }
    }
}
