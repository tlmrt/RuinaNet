using System;
using System.Collections.Generic;
using System.Reflection;
using LOR_DiceSystem;

namespace RuinaNet
{
    // A reception-only copy. Never takes an equipped inventory book from another
    // librarian, and never rewrites the library roster's persistent equipment.
    internal static class CorePageReplica
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Core = typeof(UnitDataModel).GetField("_bookItem", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Custom = typeof(UnitDataModel).GetField("_CustomBookItem", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Skin = typeof(BookModel).GetField("_characterSkin", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo OriginalSkin = typeof(BookModel).GetField("_selectedOriginalSkin", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static LorId Parse(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "-") return LorId.None;
            int split = tag.LastIndexOf(':');
            int id;
            if (split < 0 || !int.TryParse(tag.Substring(split + 1), out id)) throw new FormatException("Invalid page id: " + tag);
            string package = Uri.UnescapeDataString(tag.Substring(0, split));
            return package == "base" ? new LorId(id) : new LorId(package, id);
        }

        private static BookModel MakeBook(string tag)
        {
            LorId id = Parse(tag);
            BookXmlInfo xml = Singleton<BookXmlList>.Instance.GetData(id, true);
            if (xml == null) throw new InvalidOperationException("Missing core page: " + tag);
            return new BookModel(xml);
        }

        public static string ReadSkin(BookModel book, bool original)
        {
            return book == null ? "" : (string)(original ? OriginalSkin : Skin).GetValue(book);
        }

        public static List<string> ReadPassives(BookModel book)
        {
            List<string> result = new List<string>();
            // TryGainUniquePassive matches slots by originpassive. Sending only
            // the effective id makes attributed slots look foreign and removes
            // them the next time the original game creates passive scripts.
            foreach (PassiveModel model in book.GetPassiveModelList())
            {
                if (model == null) continue;
                if (model.originpassive == null || model.originData == null || model.originData.currentpassive == null)
                    throw new InvalidOperationException("Incomplete passive slot");
                result.Add(PassiveTag(model.originpassive.id) + ">" + PassiveTag(model.originData.currentpassive.id));
            }
            return result;
        }

        private static string PassiveTag(LorId id)
        {
            return Uri.EscapeDataString(string.IsNullOrEmpty(id.packageId) ? "base" : id.packageId) + ":" + id.id;
        }

        public static UnitDataModel Build(UnitDataModel original, string bookTag,
            List<string> cardTags, List<string> passiveTags, string customTag,
            string skin, string originalSkin, string workshopSkin,
            List<List<string>> extraDecks = null, int currentDeck = 0)
        {
            if (original == null || Core == null || Custom == null || Skin == null || OriginalSkin == null || Clone == null)
                throw new InvalidOperationException("Core page binding unavailable");
            // Validate every asset before exposing any replacement to the stage.
            BookModel book = MakeBook(bookTag);
            BookModel custom = customTag == "-" ? null : MakeBook(customTag);
            List<DiceCardXmlInfo> cards = new List<DiceCardXmlInfo>();
            foreach (string tag in cardTags)
            {
                DiceCardXmlInfo xml = ItemXmlDataList.instance.GetCardItem(Parse(tag), true);
                if (xml == null) throw new InvalidOperationException("Missing combat page: " + tag);
                cards.Add(xml);
            }
            List<List<DiceCardXmlInfo>> extras = new List<List<DiceCardXmlInfo>>();
            if (extraDecks != null)
                foreach (List<string> tags in extraDecks)
                {
                    List<DiceCardXmlInfo> deck = new List<DiceCardXmlInfo>();
                    foreach (string tag in tags)
                    {
                        DiceCardXmlInfo xml = ItemXmlDataList.instance.GetCardItem(Parse(tag), true);
                        if (xml == null) throw new InvalidOperationException("Missing secondary-deck page: " + tag);
                        deck.Add(xml);
                    }
                    extras.Add(deck);
                }
            List<PassiveModel> passives = new List<PassiveModel>();
            foreach (string tag in passiveTags)
            {
                string[] pair = tag.Split('>');
                if (pair.Length != 2) throw new FormatException("Passive slot must include origin and current ids");
                LorId origin = Parse(pair[0]), current = Parse(pair[1]);
                PassiveXmlInfo originInfo = Singleton<PassiveXmlList>.Instance.GetData(origin);
                PassiveXmlInfo currentInfo = Singleton<PassiveXmlList>.Instance.GetData(current);
                if (originInfo == null || currentInfo == null)
                    throw new InvalidOperationException("Missing passive: " + tag);
                // ctor(id, bookInstanceId, isEmpty): the third argument is an
                // empty-passive flag, not an instance id.
                PassiveModel model = new PassiveModel(origin, book.instanceId, 0);
                model.originData.currentpassive = currentInfo;
                passives.Add(model);
            }
            List<DeckModel> decks = book.GetDeckAll_nocopy();
            if (decks == null || decks.Count == 0) throw new InvalidOperationException("Core page has no deck storage");
            if (extras.Count >= decks.Count || currentDeck < 0 || currentDeck >= decks.Count)
                throw new InvalidOperationException("Core page deck layout mismatch");
            // GetDeckForBattle(0), used by SetUnitData, reads deck zero.
            foreach (DeckModel deck in decks) deck.GetCardList_nocopy().Clear();
            decks[0].GetCardList_nocopy().AddRange(cards);
            for (int i = 0; i < extras.Count; i++) decks[i + 1].GetCardList_nocopy().AddRange(extras[i]);
            book.ChangeDeck(currentDeck);
            List<PassiveModel> models = book.GetPassiveModelList();
            models.Clear(); models.AddRange(passives);
            Skin.SetValue(book, skin); OriginalSkin.SetValue(book, originalSkin);
            if (custom != null) { Skin.SetValue(custom, skin); OriginalSkin.SetValue(custom, originalSkin); }

            UnitDataModel replica = (UnitDataModel)Clone.Invoke(original, null);
            Core.SetValue(replica, book); Custom.SetValue(replica, custom);
            replica.workshopSkin = workshopSkin;
            book.SetOwner(replica);
            if (custom != null) custom.SetOwner(replica);
            return replica;
        }
    }
}
