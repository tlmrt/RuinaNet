using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaNet
{
    // Snapshot ALL ordinary page zones together. A card may occur both in Use and
    // Reserved; repeated XML IDs must still be separate runtime card instances.
    internal static class HandSync
    {
        private static readonly string[] ZoneNames = { "_cardInHand", "_cardInDeck", "_cardInDiscarded", "_cardInUse", "_cardInReserved" };
        private static readonly FieldInfo[] Zones = new FieldInfo[5];
        private static readonly FieldInfo CostAdder = typeof(BattleDiceCardModel).GetField("_costAdder", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CostZero = typeof(BattleDiceCardModel).GetField("_costZero", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Reserved = typeof(BattlePlayingCardSlotDetail).GetField("_reservedPlayPoint", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<int,string> Sent = new Dictionary<int,string>();
        private static readonly Dictionary<int,int> SentRevision = new Dictionary<int,int>();
        private static float _wait;
        static HandSync()
        {
            for (int i=0;i<Zones.Length;i++) Zones[i] = typeof(BattleAllyCardDetail).GetField(ZoneNames[i], BindingFlags.Instance | BindingFlags.NonPublic);
        }
        public static void Reset() { Sent.Clear(); SentRevision.Clear(); _wait = 1f; }
        /// <summary>The peer rejected a snapshot we believe we already sent:
        /// forget the cached body so the next Tick republishes it.</summary>
        public static void HandleNack(string[] p)
        {
            int round, wire;
            if (p == null || p.Length < 3) return;
            if (!int.TryParse(p[1], out round) || !int.TryParse(p[2], out wire)) return;
            if (Sent.Remove(wire))
            {
                SentRevision.Remove(wire);
                Debug.Log("[RuinaNet][hand] peer rejected round=" + round + " unit=" + wire +
                          " -- republishing next tick");
            }
        }
        public static void Tick(float dt)
        {
            if (!BattleSync.Active || !BattleSync.InCardPhase() || BattleSync.GateArmed || BattleSync.RoundHandled) return;
            _wait += dt;
            if (_wait < 0.5f) return;
            _wait = 0;
            foreach (BattleUnitModel u in BattleObjectManager.instance.GetAliveList(true))
            {
                if (u.faction == Faction.Enemy ? !BattleSync.IsHost : !BattleSync.IsMine(u)) continue;
                try { SendUnit(BattleSync.Round,u,false); }
                catch (Exception e) { Debug.LogWarning("[RuinaNet][hand] publish failed: " + e.Message); }
            }
        }
        private static List<BattleDiceCardModel> Zone(BattleUnitModel u, int z)
        {
            if (Zones[z] == null) throw new InvalidOperationException("Missing card zone " + ZoneNames[z]);
            List<BattleDiceCardModel> list = Zones[z].GetValue(u.allyCardDetail) as List<BattleDiceCardModel>;
            if (list == null) throw new InvalidOperationException("Uninitialized card zone " + ZoneNames[z]);
            return list;
        }
        private static int Index(List<BattleDiceCardModel> cards, BattleDiceCardModel c)
        {
            int i=cards.IndexOf(c); if(i<0) { i=cards.Count; cards.Add(c); } return i;
        }
        private static string Indices(List<BattleDiceCardModel> cards, List<BattleDiceCardModel> zone)
        {
            List<string> ids=new List<string>();
            foreach(BattleDiceCardModel c in zone) ids.Add(Index(cards,c).ToString(CultureInfo.InvariantCulture));
            return ids.Count==0 ? "-" : string.Join(",",ids.ToArray());
        }
        private static string Tag(BattleDiceCardModel card)
        {
            LorId id=card.GetID();
            return Uri.EscapeDataString(string.IsNullOrEmpty(id.packageId)?"base":id.packageId)+":"+id.id;
        }
        public static string SnapshotBody(BattleUnitModel u)
        {
            List<BattleDiceCardModel> cards=new List<BattleDiceCardModel>();
            string[] zones=new string[5];
            for(int z=0;z<5;z++) zones[z]=Indices(cards,Zone(u,z));
            List<string> slots=new List<string>();
            foreach(BattlePlayingCardDataInUnitModel slot in u.cardSlotDetail.cardAry)
                slots.Add(slot==null?"-":Index(cards,slot.card).ToString(CultureInfo.InvariantCulture));
            List<string> definitions=new List<string>();
            foreach(BattleDiceCardModel c in cards)
                definitions.Add(Tag(c)+","+c.CurCost+","+(int)CostAdder.GetValue(c)+","+((bool)CostZero.GetValue(c)?1:0)+","+(c.temporary?1:0)+","+(c.exhaust?1:0)+","+(c.costSpended?1:0));
            return (definitions.Count==0?"-":string.Join("~",definitions.ToArray()))+" "+string.Join(" ",zones)+" "+
                (slots.Count==0?"-":string.Join(",",slots.ToArray()))+" "+NetState.CardsLine(u)+" "+u.cardSlotDetail.PlayPoint+" "+u.cardSlotDetail.ReservedPlayPoint;
        }
        public static void SendUnit(int round, BattleUnitModel u, bool force)
        {
            string body=SnapshotBody(u);
            int wire=NetState.WireIndex(u);
            string previous;
            bool changed=!Sent.TryGetValue(wire,out previous)||previous!=body;
            if(!force&&!changed) return;
            int revision;
            if(!SentRevision.TryGetValue(wire,out revision)) revision=0;
            if(changed) revision++;
            if(body.Length>55000) throw new InvalidOperationException("Card snapshot exceeds UDP limit for unit "+wire);
            NetLink.Send("HAND2 "+round+" "+wire+" "+revision+" "+body);
            Sent[wire]=body;
            SentRevision[wire]=revision;
            Debug.Log("[RuinaNet][hand] published round="+round+" unit="+wire+" revision="+revision);
        }
        private static int[] ParseIndices(string csv, int count, bool emptySlots)
        {
            if(csv=="-" && !emptySlots) return new int[0];
            string[] ss=csv.Split(','); int[] ids=new int[ss.Length];
            for(int i=0;i<ss.Length;i++)
            {
                if(emptySlots && ss[i]=="-") { ids[i]=-1; continue; }
                int n; if(!int.TryParse(ss[i],out n)||n<0||n>=count) throw new FormatException("Invalid card instance index");
                ids[i]=n;
            }
            return ids;
        }
        public static bool Apply(string[] p)
        {
            if(p.Length!=14 || p[0]!="HAND2") return false;
            int wire; if(!int.TryParse(p[2],out wire)) return false;
            BattleUnitModel u=NetState.Find(wire);
            if(u==null) return false;
            if(!SyncAuthority.ReceiveChoices(BattleSync.IsHost,u.faction==Faction.Enemy,BattleSync.IsMine(u))) return true;
            string[] defs=p[4]=="-"?new string[0]:p[4].Split('~');
            if(defs.Length>512) return false;
            int[][] ids=new int[5][];
            for(int z=0;z<5;z++) ids[z]=ParseIndices(p[5+z],defs.Length,false);
            int[] slotIds=u.cardSlotDetail.cardAry.Count==0 && p[10]=="-" ? new int[0] : ParseIndices(p[10],defs.Length,true);
            if(slotIds.Length!=u.cardSlotDetail.cardAry.Count) return false;
            int light,reserved;
            if(!int.TryParse(p[12],out light)||!int.TryParse(p[13],out reserved)) return false;
            List<BattleDiceCardModel> pool=new List<BattleDiceCardModel>();
            for(int z=0;z<5;z++) foreach(BattleDiceCardModel c in Zone(u,z)) if(!pool.Contains(c)) pool.Add(c);
            foreach(BattlePlayingCardDataInUnitModel slot in u.cardSlotDetail.cardAry)
                if(slot!=null&&!pool.Contains(slot.card)) pool.Add(slot.card);
            BattleDiceCardModel[] cards=new BattleDiceCardModel[defs.Length];
            int[][] values=new int[defs.Length][];
            HashSet<BattleDiceCardModel> taken=new HashSet<BattleDiceCardModel>();
            for(int i=0;i<defs.Length;i++)
            {
                string[] f=defs[i].Split(','); if(f.Length!=7) return false;
                values[i]=new int[6];
                for(int j=0;j<6;j++) if(!int.TryParse(f[j+1],out values[i][j])) return false;
                BattleDiceCardModel selected=null;
                for(int slot=0;slot<slotIds.Length;slot++)
                {
                    BattlePlayingCardDataInUnitModel existing=u.cardSlotDetail.cardAry[slot];
                    if(slotIds[slot]==i&&existing!=null&&Tag(existing.card)==f[0]&&!taken.Contains(existing.card)) { selected=existing.card; break; }
                }
                if(selected==null) selected=pool.Find(delegate(BattleDiceCardModel c){return !taken.Contains(c)&&Tag(c)==f[0];});
                if(selected==null)
                {
                    int split=f[0].LastIndexOf(':'); int id;
                    if(split<0||!int.TryParse(f[0].Substring(split+1),out id)) return false;
                    string package=Uri.UnescapeDataString(f[0].Substring(0,split));
                    DiceCardXmlInfo info=ItemXmlDataList.instance.GetCardItem(package=="base"?new LorId(id):new LorId(package,id),true);
                    if(info==null) return false;
                    selected=BattleDiceCardModel.CreatePlayingCard(info);
                }
                cards[i]=selected; taken.Add(selected);
            }
            // All references and XML identities validated before changing any live zone.
            // Slot wiring parses the peer's placement line up front. The old path
            // round-tripped through NetState.ApplyCardsLine -> PlaceCard, which
            // re-ran the tag-based lifecycle (ReturnCardToHand / UseCard /
            // OnApplyCard) and then had the instance swapped underneath it, so the
            // bound cardAbility belonged to a card that was no longer in the slot;
            // a PlaceCard throw half-way also left the slots partially mutated.
            string[] place = (p[11]=="-" || string.IsNullOrEmpty(p[11])) ? new string[0] : p[11].Split('~');
            int[] tgtIdx = new int[slotIds.Length];
            int[] tgtSlot = new int[slotIds.Length];
            string[] subCsv = new string[slotIds.Length];
            for(int slot=0;slot<slotIds.Length;slot++)
            {
                tgtIdx[slot] = -1; tgtSlot[slot] = 0; subCsv[slot] = "-";
                if(slot>=place.Length) continue;
                string[] f = place[slot].Split(',');
                if(f.Length>1) int.TryParse(f[1],out tgtIdx[slot]);
                if(f.Length>2) int.TryParse(f[2],out tgtSlot[slot]);
                if(f.Length>3) subCsv[slot]=f[3];
            }
            List<ClashSync.Choice> clashBefore=ClashSync.Capture(u);
            for(int i=0;i<cards.Length;i++)
            {
                BattleDiceCardModel c=cards[i]; int[] v=values[i];
                c.owner=u; c.SetCurrentCost(v[0]); CostAdder.SetValue(c,v[1]); CostZero.SetValue(c,v[2]!=0);
                c.temporary=v[3]!=0; c.exhaust=v[4]!=0; c.costSpended=v[5]!=0;
            }
            for(int slot=0;slot<slotIds.Length;slot++)
            {
                BattlePlayingCardDataInUnitModel m=u.cardSlotDetail.cardAry[slot];
                if(slotIds[slot]<0)
                {
                    if(m!=null)
                    {
                        if(m.cardAbility!=null){ try{m.cardAbility.OnReleaseCard();}catch(Exception){} }
                        u.cardSlotDetail.cardAry[slot]=null;
                    }
                    continue;
                }
                if(m==null){ m=new BattlePlayingCardDataInUnitModel(); u.cardSlotDetail.cardAry[slot]=m; }
                BattleDiceCardModel cm=cards[slotIds[slot]];
                bool changed=m.card!=cm;
                m.owner=u; m.card=cm; m.slotOrder=slot;
                try{ LOR_DiceSystem.SpeedDice d=u.GetSpeedDiceResult(slot); m.speedDiceResultValue=d==null?0:d.value; }
                catch(Exception){ m.speedDiceResultValue=0; }
                BattleUnitModel tgt=tgtIdx[slot]<0?null:NetState.Find(tgtIdx[slot]);
                m.target=tgt; m.targetSlotOrder=tgtSlot[slot];
                if(changed || u.faction!=Faction.Enemy){m.earlyTarget=tgt; m.earlyTargetOrder=tgtSlot[slot];}
                if(m.subTargets!=null) m.subTargets.Clear();
                if(subCsv[slot]!="-" && subCsv[slot].Length>0)
                {
                    try
                    {
                        string[] ss=subCsv[slot].Split('|');
                        if(m.subTargets==null) m.subTargets=new List<BattlePlayingCardDataInUnitModel.SubTarget>();
                        for(int i=0;i<ss.Length;i++)
                        {
                            string[] one=ss[i].Split(':');
                            if(one.Length<2) continue;
                            int si,sslot;
                            if(!int.TryParse(one[0],out si)) continue;
                            if(!int.TryParse(one[1],out sslot)) continue;
                            BattleUnitModel st=NetState.Find(si);
                            if(st==null) continue;
                            BattlePlayingCardDataInUnitModel.SubTarget sub=new BattlePlayingCardDataInUnitModel.SubTarget();
                            sub.target=st; sub.targetSlotOrder=sslot;
                            m.subTargets.Add(sub);
                        }
                    }
                    catch(Exception){}
                }
                if(changed)
                {
                    try
                    {
                        m.cardAbility=cm.CreateDiceCardSelfAbilityScript();
                        if(m.cardAbility!=null){ m.cardAbility.card=m; m.cardAbility.OnApplyCard(); }
                        m.ResetCardQueue();
                    }
                    catch(Exception e){ Debug.LogWarning("[RuinaNet][hand] ability bind failed: "+e.Message); }
                }
            }
            for(int z=0;z<5;z++)
            {
                List<BattleDiceCardModel> target=Zone(u,z); target.Clear();
                foreach(int id in ids[z]) target.Add(cards[id]);
            }
            u.cardSlotDetail.SetPlayPoint(light); Reserved.SetValue(u.cardSlotDetail,reserved);
            ClashSync.After(u,clashBefore);
            BattleSync.RefreshHandUi();
            Debug.Log("[RuinaNet][hand] adopted round="+p[1]+" unit="+wire+" revision="+p[3]+" hand="+p[5]+" slots="+p[10]);
            return true;
        }
    }
}
