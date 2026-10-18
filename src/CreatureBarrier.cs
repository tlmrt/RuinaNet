using System;

namespace RuinaNet
{
    // Pure session rules. All native scene/story/save calls sit behind these
    // transitions; a packet from a prior revision cannot release a new run.
    internal sealed class CreatureBarrier
    {
        internal enum Phase { Preparing, Story, Commit, Launching, Battle, Ending, Finished, Fault }
        public readonly string Token;
        public int Revision { get; private set; }
        public Phase State { get; private set; }
        public string LocalDigest { get; private set; }
        public string PeerDigest { get; private set; }
        public bool LocalStoryDone, PeerStoryDone;
        public int LocalResult = -1, PeerResult = -1;
        public bool CommitAck { get; private set; }
        public bool LocalReady { get { return LocalDigest != null; } }
        public bool PeerReady { get { return PeerDigest != null; } }
        public bool MatchingReady { get { return LocalReady && LocalDigest == PeerDigest; } }

        public CreatureBarrier(string token) { Token = token; State = Phase.Preparing; }
        public bool Matches(string token, int revision) { return token == Token && revision == Revision; }
        public bool Configure(int revision)
        {
            if (State != Phase.Preparing || revision <= Revision) return false;
            Revision = revision; LocalDigest = PeerDigest = null; return true;
        }
        public bool Ready(bool local, string digest)
        {
            if (State != Phase.Preparing || Revision < 1 || (digest != null && !ValidDigest(digest))) return false;
            if (local) LocalDigest = digest; else PeerDigest = digest;
            return true;
        }
        public bool StartStory(string digest)
        {
            if (State != Phase.Preparing || !MatchingReady || LocalDigest != digest) return false;
            State = Phase.Story; return true;
        }
        public bool StoryDone(bool local)
        {
            if (State != Phase.Story) return false;
            if (local) LocalStoryDone = true; else PeerStoryDone = true;
            return true;
        }
        public bool BeginCommit(string digest)
        {
            if (State != Phase.Story || !LocalStoryDone || !PeerStoryDone || LocalDigest != digest) return false;
            State = Phase.Commit; return true;
        }
        public bool Ack(string digest)
        {
            if (State != Phase.Commit || LocalDigest != digest) return false;
            CommitAck = true; return true;
        }
        public bool Launch(string digest)
        {
            if (State != Phase.Commit || !CommitAck || LocalDigest != digest) return false;
            State = Phase.Launching; return true;
        }
        public void EnterBattle() { if (State == Phase.Launching) State = Phase.Battle; }
        public bool Result(bool local, int result)
        {
            if ((State != Phase.Launching && State != Phase.Battle && State != Phase.Ending) || result < 0 || result > 1) return false;
            int previous = local ? LocalResult : PeerResult;
            if (previous >= 0 && previous != result) { State = Phase.Fault; return false; }
            if (local) LocalResult = result; else PeerResult = result;
            State = Phase.Ending;
            if (LocalResult >= 0 && PeerResult >= 0 && LocalResult != PeerResult) State = Phase.Fault;
            return State != Phase.Fault;
        }
        public bool Finish(int result)
        {
            if (State != Phase.Ending || LocalResult != result || PeerResult != result) return false;
            State = Phase.Finished; return true;
        }
        public void Fail() { State = Phase.Fault; }
        private static bool ValidDigest(string digest)
        {
            if (digest.Length != 64) return false;
            foreach (char c in digest) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
    }

    internal static class CreatureRoster
    {
        public static bool Valid(int eligible, int hosts, int patrons)
        {
            return eligible > 0 && eligible < 32 && (hosts & ~eligible) == 0 &&
                (patrons & eligible & ~hosts) == 0;
        }
        public static int DefaultHosts(int eligible, int patrons)
        {
            int count = Count(eligible), hosts = eligible & patrons;
            // Keep a host even on a floor whose patron is excluded by the game.
            int target = (count + 1) / 2;
            for (int slot = 0; slot < 5 && Count(hosts) < target; slot++)
                if ((eligible & (1 << slot)) != 0) hosts |= 1 << slot;
            return hosts;
        }
        public static int Count(int mask) { int n = 0; for (; mask != 0; mask &= mask - 1) n++; return n; }
    }
}
