using System;
using System.Collections.Generic;
using RuinaNet;

class SyncRegression
{
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    static void Main()
    {
        RoundGate gate = new RoundGate();
        gate.BeginPlanning(1);
        Check(gate.TryArm(true, 0), "first planning phase opens");
        Check(!gate.TryArm(true, 0), "duplicate arm is ignored");
        Check(gate.TryRelease(true), "first release");
        // Replay the observed 186 ARM messages while action phases were running.
        for (int r = 1; r < 188; r++)
            Check(!gate.TryArm(false, r), "action phase cannot be restarted by ARM " + r);
        Check(gate.Round == 0, "network spam cannot increment the game round");
        Check(!gate.TryArm(true, 1), "same planning phase cannot open twice");
        gate.BeginPlanning(1);
        Check(!gate.TryArm(true, 1), "repeated phase observation cannot reset handled");
        gate.BeginPlanning(2);
        Check(!gate.TryArm(true, 0), "old round ignored");
        Check(!gate.TryArm(true, 3), "future round ignored");
        Check(gate.TryArm(true, 1), "new real planning phase can open");
        Check(!gate.TryRelease(false) && gate.Armed, "no release in wrong phase");
        Check(gate.TryRelease(true), "second release");
        gate.Reset();
        gate.BeginPlanning(1);
        Check(gate.TryArm(true, 0), "new reception resets round identity");

        // A final snapshot must be applied and acknowledged for the same round.
        RoundCommitBarrier commit = new RoundCommitBarrier();
        commit.Begin(4);
        Check(!commit.NoteAck(4), "ack before snapshot cannot release");
        Check(commit.MarkSnapshotSent(4), "one final snapshot is sent");
        Check(!commit.MarkSnapshotSent(4), "duplicate snapshot send is blocked");
        Check(!commit.NoteAck(3), "stale snapshot ack is ignored");
        Check(!commit.CanRelease(4), "host still waits after stale ack");
        Check(commit.NoteAck(4), "matching snapshot ack accepted");
        Check(commit.CanRelease(4), "matching ack releases round");
        commit.Begin(5);
        Check(!commit.CanRelease(5), "new round clears prior acknowledgement");

        // Real production channel, with deterministic loss/reorder/duplicates in both directions.
        ReliableChannel a = new ReliableChannel(), b = new ReliableChannel();
        a.Reset("host", "client"); b.Reset("client", "host");
        List<string> ab = new List<string>(), ba = new List<string>();
        List<string> gotA = new List<string>(), gotB = new List<string>();
        a.SendWire = ab.Add; b.SendWire = ba.Add;
        a.Deliver = gotA.Add; b.Deliver = gotB.Add;
        for (int i=0; i<100; i++) { a.Send("state " + i,0); b.Send("choice " + i,0); }
        Random rng = new Random(34);
        for (int t=0; t<10000 && (a.PendingCount>0 || b.PendingCount>0); t+=50)
        {
            Pump(ab, b, rng); Pump(ba, a, rng);
            a.Tick(t); b.Tick(t);
        }
        Check(gotA.Count == 100 && gotB.Count == 100, "all packets delivered under loss");
        for (int i=0;i<100;i++)
        {
            Check(gotA[i] == "choice " + i, "client commands ordered exactly once");
            Check(gotB[i] == "state " + i, "host states ordered exactly once");
        }
        Check(a.PendingCount == 0 && b.PendingCount == 0, "all acknowledgements complete");
        b.Receive("REL old-session 101 bad");
        b.Receive("REL host 99999 far-future");
        b.Receive("REL host 1 state 0");
        Check(gotB.Count == 100, "old session and duplicates ignored");

        // ---- authority rules (v3.5, per handover doc section 8) ----
        // Combat state (hp / break / speed / outcomes) is host-only.
        Check(!SyncAuthority.ReceiveCombatState(true), "host never accepts peer combat state");
        Check(SyncAuthority.ReceiveCombatState(false), "client accepts host combat state");
        // Page choices: an enemy's choices come from the host, nobody else.
        Check(!SyncAuthority.ReceiveChoices(true, true, false), "host rejects any enemy data from the client");
        Check(SyncAuthority.ReceiveChoices(false, true, false), "client accepts host enemy choices");
        Check(SyncAuthority.ReceiveChoices(false, true, true), "client accepts host enemy choices regardless of ownership");
        // A librarian's choices belong to whoever owns it.
        Check(SyncAuthority.ReceiveChoices(true, false, false), "host accepts the client's librarian choices");
        Check(!SyncAuthority.ReceiveChoices(true, false, true), "host rejects its own librarians echoed back");
        Check(!SyncAuthority.ReceiveChoices(false, false, true), "client rejects the host writing its own librarians");
        Check(SyncAuthority.ReceiveChoices(false, false, false), "client accepts host state for the peer's librarians");

        Check(SyncAuthority.ReceivePlanningRound(2, 1, true, false, 1), "host ready still receives unready peer's edits");
        Check(SyncAuthority.ReceivePlanningRound(1, 2, false, false, 2), "unready client receives ready host's targets");
        Check(!SyncAuthority.ReceivePlanningRound(1, 2, false, false, 1), "future planning needs matching peer readiness");
        Check(!SyncAuthority.ReceivePlanningRound(2, 1, true, true, 2), "old edits rejected after peer submitted");
        Check(!SyncAuthority.ReceivePlanningRound(2, 0, true, false, 2), "two-round stale edit rejected");
        Check(!SyncAuthority.ReceivePlanningRound(2, 4, false, false, 4), "two-round future edit rejected");
        Check(SyncAuthority.ReceivePlanningRound(2, 2, true, true, 2), "matching final snapshot accepted");
        Console.WriteLine("PASS: " + checks + " assertions; observed round-spam regression + 200 bidirectional messages with loss, reordering and duplicates + authority and planning rules.");
    }
    static void Pump(List<string> wires, ReliableChannel target, Random rng)
    {
        List<string> batch = new List<string>(wires); wires.Clear();
        while(batch.Count > 0)
        {
            int i=rng.Next(batch.Count); string wire=batch[i]; batch.RemoveAt(i);
            if (rng.Next(4)==0) continue; // 25% loss, including acknowledgements
            target.Receive(wire);
            if (rng.Next(4)==0) target.Receive(wire);
        }
    }
}
