// =============================================================================
// EmojiCatalog
//
// Static Unicode glyph lists for the sticker-box Emoji pivot. Curated, not every
// code point — enough to browse by WhatsApp-style category.
// =============================================================================
using System.Collections.Generic;
using System.Globalization;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class EmojiCatalog
    {
        public const string RecentsId = "recents";
        public const string SmileysId = "smileys";
        public const string AnimalsId = "animals";
        public const string FoodId = "food";
        public const string ActivitiesId = "activities";
        public const string TravelId = "travel";
        public const string ObjectsId = "objects";
        public const string SymbolsId = "symbols";
        public const string FlagsId = "flags";

        // Arrays MUST be declared before BuiltInCategories — C# initializes static
        // fields in textual order; referencing these while still null yielded empty Glyphs.

        private static readonly string[] SmileysAndPeople =
        {
            "😀","😃","😄","😁","😆","😅","🤣","😂","🙂","🙃","😉","😊","😇","🥰","😍","🤩",
            "😘","😗","☺️","😚","😙","🥲","😋","😛","😜","🤪","😝","🤑","🤗","🤭","🤫","🤔",
            "🤐","🤨","😐","😑","😶","😏","😒","🙄","😬","🤥","😌","😔","😪","🤤","😴","😷",
            "🤒","🤕","🤢","🤮","🤧","🥵","🥶","🥴","😵","🤯","🤠","🥳","🥸","😎","🤓","🧐",
            "😕","😟","🙁","☹️","😮","😯","😲","😳","🥺","😦","😧","😨","😰","😥","😢","😭",
            "😱","😖","😣","😞","😓","😩","😫","🥱","😤","😡","😠","🤬","😈","👿","💀","☠️",
            "💩","🤡","👹","👺","👻","👽","👾","🤖","😺","😸","😹","😻","😼","😽","🙀","😿",
            "😾","🙈","🙉","🙊","💋","💌","💘","💝","💖","💗","💓","💞","💕","💟","❣️","💔",
            "❤️","🧡","💛","💚","💙","💜","🤎","🖤","🤍","💯","💢","💥","💫","💦","💨","🕳️",
            "💣","💬","👁️‍🗨️","🗨️","🗯️","💭","💤","👋","🤚","🖐️","✋","🖖","👌","🤌","🤏","✌️",
            "🤞","🤟","🤘","🤙","👈","👉","👆","🖕","👇","☝️","👍","👎","✊","👊","🤛","🤜",
            "👏","🙌","👐","🤲","🤝","🙏","✍️","💅","🤳","💪","🦾","🦿","🦵","🦶","👂","🦻",
            "👃","🧠","🫀","🫁","🦷","🦴","👀","👁️","👅","👄","👶","🧒","👦","👧","🧑","👱",
            "👨","🧔","👩","🧓","👴","👵","🙍","🙎","🙅","🙆","💁","🙋","🧏","🙇","🤦","🤷",
            "👮","🕵️","💂","🥷","👷","🤴","👸","👳","👲","🧕","🤵","👰","🤰","🤱","👼","🎅",
            "🤶","🦸","🦹","🧙","🧚","🧛","🧜","🧝","🧞","🧟","💆","💇","🚶","🧍","🧎","🏃",
            "💃","🕺","🕴️","👯","🧖","🧗","🤺","🏇","⛷️","🏂","🏌️","🏄","🚣","🏊","⛹️","🏋️",
            "🚴","🚵","🤸","🤼","🤽","🤾","🤹","🧘","🛀","🛌","👭","👫","👬","💏","💑","👪"
        };

        private static readonly string[] AnimalsAndNature =
        {
            "🐶","🐱","🐭","🐹","🐰","🦊","🐻","🐼","🐻‍❄️","🐨","🐯","🦁","🐮","🐷","🐽","🐸",
            "🐵","🙈","🙉","🙊","🐒","🐔","🐧","🐦","🐤","🐣","🐥","🦆","🦅","🦉","🦇","🐺",
            "🐗","🐴","🦄","🐝","🪱","🐛","🦋","🐌","🐞","🐜","🪰","🪲","🪳","🦟","🦗","🕷️",
            "🕸️","🦂","🐢","🐍","🦎","🦖","🦕","🐙","🦑","🦐","🦞","🦀","🐡","🐠","🐟","🐬",
            "🐳","🐋","🦈","🐊","🐅","🐆","🦓","🦍","🦧","🦣","🐘","🦛","🦏","🐪","🐫","🦒",
            "🦘","🦬","🐃","🐂","🐄","🐎","🐖","🐏","🐑","🦙","🐐","🦌","🐕","🐩","🦮","🐕‍🦺",
            "🐈","🐈‍⬛","🪶","🐓","🦃","🦤","🦚","🦜","🦢","🦩","🕊️","🐇","🦝","🦨","🦡","🦫",
            "🦦","🦥","🐁","🐀","🐿️","🦔","🌵","🎄","🌲","🌳","🌴","🪵","🌱","🌿","☘️","🍀",
            "🎍","🪴","🎋","🍃","🍂","🍁","🍄","🐚","🪨","🌾","💐","🌷","🌹","🥀","🌺","🌸",
            "🌼","🌻","🌞","🌝","🌛","🌜","🌚","🌕","🌖","🌗","🌘","🌑","🌒","🌓","🌔","🌙",
            "🌎","🌍","🌏","🪐","💫","⭐","🌟","✨","⚡","☄️","💥","🔥","🌪️","🌈","☀️","🌤️",
            "⛅","🌥️","☁️","🌦️","🌧️","⛈️","🌩️","🌨️","❄️","☃️","⛄","🌬️","💨","💧","💦","☔"
        };

        private static readonly string[] FoodAndDrink =
        {
            "🍇","🍈","🍉","🍊","🍋","🍌","🍍","🥭","🍎","🍏","🍐","🍑","🍒","🍓","🫐","🥝",
            "🍅","🫒","🥥","🥑","🍆","🥔","🥕","🌽","🌶️","🫑","🥒","🥬","🥦","🧄","🧅","🍄",
            "🥜","🌰","🍞","🥐","🥖","🫓","🥨","🥯","🥞","🧇","🧀","🍖","🍗","🥩","🥓","🍔",
            "🍟","🍕","🌭","🥪","🌮","🌯","🫔","🥙","🧆","🥚","🍳","🥘","🍲","🫕","🥣","🥗",
            "🍿","🧈","🧂","🥫","🍱","🍘","🍙","🍚","🍛","🍜","🍝","🍠","🍢","🍣","🍤","🍥",
            "🥮","🍡","🥟","🥠","🥡","🦀","🦞","🦐","🦑","🦪","🍦","🍧","🍨","🍩","🍪","🎂",
            "🍰","🧁","🥧","🍫","🍬","🍭","🍮","🍯","🍼","🥛","☕","🫖","🍵","🍶","🍾","🍷",
            "🍸","🍹","🍺","🍻","🥂","🥃","🫗","🥤","🧋","🧃","🧉","🧊","🥢","🍽️","🍴","🥄"
        };

        private static readonly string[] Activities =
        {
            "⚽","🏀","🏈","⚾","🥎","🎾","🏐","🏉","🥏","🎱","🪀","🏓","🏸","🏒","🏑","🥍",
            "🏏","🪃","🥅","⛳","🪁","🏹","🎣","🤿","🥊","🥋","🎽","🛹","🛼","🛷","⛸️","🥌",
            "🎿","⛷️","🏂","🪂","🏋️","🤼","🤸","⛹️","🤺","🤾","🏌️","🏇","🧘","🏄","🏊","🤽",
            "🚣","🧗","🚴","🚵","🎪","🎭","🩰","🎨","🎬","🎤","🎧","🎼","🎹","🥁","🪘","🎷",
            "🎺","🎸","🪕","🎻","🎲","♟️","🎯","🎳","🎮","🎰","🧩"
        };

        private static readonly string[] TravelAndPlaces =
        {
            "🚗","🚕","🚙","🚌","🚎","🏎️","🚓","🚑","🚒","🚐","🛻","🚚","🚛","🚜","🦯","🦽",
            "🦼","🛴","🚲","🛵","🏍️","🛺","🚨","🚔","🚍","🚘","🚖","🚡","🚠","🚟","🚃","🚋",
            "🚞","🚝","🚄","🚅","🚈","🚂","🚆","🚇","🚊","🚉","✈️","🛫","🛬","🛩️","💺","🛰️",
            "🚀","🛸","🚁","🛶","⛵","🚤","🛥️","🛳️","⛴️","🚢","⚓","🪝","⛽","🚧","🚦","🚥",
            "🚏","🗺️","🗿","🗽","🗼","🏰","🏯","🏟️","🎡","🎢","🎠","⛲","⛱️","🏖️","🏝️","🏜️",
            "🌋","⛰️","🏔️","🗻","🏕️","⛺","🏠","🏡","🏢","🏣","🏤","🏥","🏦","🏨","🏩",
            "🏪","🏫","🏬","🏭","🏯","🏰","💒","🗼","🗽","⛪","🕌","🛕","🕍","⛩️","🕋",
            "🌁","🌃","🏙️","🌄","🌅","🌆","🌇","🌉","♨️","🎠","🎡","🎢","💈","🎪","⛲"
        };

        private static readonly string[] Objects =
        {
            "⌚","📱","📲","💻","⌨️","🖥️","🖨️","🖱️","🖲️","🕹️","🗜️","💽","💾","💿","📀","📼",
            "📷","📸","📹","🎥","📽️","🎞️","📞","☎️","📟","📠","📺","📻","🎙️","🎚️","🎛️","🧭",
            "⏱️","⏲️","⏰","🕰️","⌛","⏳","📡","🔋","🔌","💡","🔦","🕯️","🪔","🧯","🛢️","💸",
            "💵","💴","💶","💷","🪙","💰","💳","💎","⚖️","🪜","🧰","🪛","🔧","🔨","⚒️","🛠️",
            "⛏️","🪚","🔩","⚙️","🪤","🧱","⛓️","🧲","🔫","💣","🧨","🪓","🔪","🗡️","⚔️","🛡️",
            "🚬","⚰️","🪦","⚱️","🏺","🔮","📿","🧿","💈","⚗️","🔭","🔬","🕳️","🩹","🩺","💊",
            "💉","🩸","🧬","🦠","🧫","🧪","🌡️","🧹","🪠","🧺","🧻","🚽","🚰","🚿","🛁","🛀",
            "🧼","🪥","🪒","🧽","🪣","🧴","🛎️","🔑","🗝️","🚪","🪑","🛋️","🛏️","🛌","🧸","🖼️",
            "🪞","🪟","🛍️","🛒","🎁","🎈","🎏","🎀","🪄","🪅","🎊","🎉","🎎","🏮","🎐","🧧",
            "✉️","📩","📨","📧","💌","📥","📤","📦","🏷️","🪧","📪","📫","📬","📭","📮","📯",
            "📜","📃","📄","📑","🧾","📊","📈","📉","🗒️","🗓️","📆","📅","🗑️","📇","🗃️","🗳️",
            "🗄️","📋","📁","📂","🗂️","📰","📓","📔","📒","📕","📗","📘","📙","📚","📖","🔖",
            "🧷","🔗","📎","🖇️","📐","📏","🧮","📌","📍","✂️","🖊️","🖋️","✒️","🖌️","🖍️","📝",
            "✏️","🔍","🔎","🔏","🔐","🔒","🔓"
        };

        private static readonly string[] Symbols =
        {
            "❤️","🧡","💛","💚","💙","💜","🖤","🤍","🤎","💔","❣️","💕","💞","💓","💗","💖",
            "💘","💝","💟","☮️","✝️","☪️","🕉️","☸️","✡️","🔯","🕎","☯️","☦️","🛐","⛎","♈",
            "♉","♊","♋","♌","♍","♎","♏","♐","♑","♒","♓","🆔","⚛️","🉑","☢️","☣️",
            "📴","📳","🈶","🈚","🈸","🈺","🈷️","✴️","🆚","💮","🉐","㊙️","㊗️","🈴","🈵","🈹",
            "🈲","🅰️","🅱️","🆎","🅾️","🆑","🆘","❌","⭕","🛑","⛔","📛","🚫","💯","💢","♨️",
            "🚷","🚯","🚳","🚱","🔞","📵","🚭","❗","❕","❓","❔","‼️","⁉️","🔅","🔆","〽️",
            "⚠️","🚸","🔱","⚜️","🔰","♻️","✅","🈯","💹","❇️","✳️","❎","🌐","💠","Ⓜ️","🌀",
            "💤","🏧","🚾","♿","🅿️","🛗","🈳","🈂️","🛂","🛃","🛄","🛅","♂️","♀️","⚧️","✖️",
            "➕","➖","➗","♾️","💱","💲","⚕️","☑️","✔️","➰","➿","©️","®️","™️","#️⃣","*️⃣",
            "0️⃣","1️⃣","2️⃣","3️⃣","4️⃣","5️⃣","6️⃣","7️⃣","8️⃣","9️⃣","🔟","🔠","🔡","🔢","🔣","🔤",
            "🆒","🆓","ℹ️","🆕","🆖","🆗","🆙","🈁","🔴","🟠","🟡","🟢","🔵","🟣","🟤","⚫",
            "⚪","🟥","🟧","🟨","🟩","🟦","🟪","🟫","⬛","⬜","◼️","◻️","◾","◽","▪️","▫️",
            "🔶","🔷","🔸","🔹","🔺","🔻","🔘","🔳","🔲"
        };

        private static readonly string[] Flags =
        {
            "🏳️","🏴","🏁","🚩","🏳️‍🌈","🏳️‍⚧️","🇺🇳","🇦🇫","🇦🇽","🇦🇱","🇩🇿","🇦🇸","🇦🇩","🇦🇴","🇦🇮","🇦🇶",
            "🇦🇬","🇦🇷","🇦🇲","🇦🇼","🇦🇺","🇦🇹","🇦🇿","🇧🇸","🇧🇭","🇧🇩","🇧🇧","🇧🇾","🇧🇪","🇧🇿","🇧🇯","🇧🇲",
            "🇧🇹","🇧🇴","🇧🇦","🇧🇼","🇧🇷","🇮🇴","🇻🇬","🇧🇳","🇧🇬","🇧🇫","🇧🇮","🇰🇭","🇨🇲","🇨🇦","🇮🇨","🇨🇻",
            "🇧🇶","🇰🇾","🇨🇫","🇹🇩","🇨🇱","🇨🇳","🇨🇽","🇨🇨","🇨🇴","🇰🇲","🇨🇬","🇨🇩","🇨🇰","🇨🇷","🇨🇮","🇭🇷",
            "🇨🇺","🇨🇼","🇨🇾","🇨🇿","🇩🇰","🇩🇯","🇩🇲","🇩🇴","🇪🇨","🇪🇬","🇸🇻","🇬🇶","🇪🇷","🇪🇪","🇸🇿","🇪🇹",
            "🇪🇺","🇫🇰","🇫🇴","🇫🇯","🇫🇮","🇫🇷","🇬🇫","🇵🇫","🇹🇫","🇬🇦","🇬🇲","🇬🇪","🇩🇪","🇬🇭","🇬🇮","🇬🇷",
            "🇬🇱","🇬🇩","🇬🇵","🇬🇺","🇬🇹","🇬🇬","🇬🇳","🇬🇼","🇬🇾","🇭🇹","🇭🇳","🇭🇰","🇭🇺","🇮🇸","🇮🇳","🇮🇩",
            "🇮🇷","🇮🇶","🇮🇪","🇮🇲","🇮🇱","🇮🇹","🇯🇲","🇯🇵","🇯🇪","🇯🇴","🇰🇿","🇰🇪","🇰🇮","🇽🇰","🇰🇼","🇰🇬",
            "🇱🇦","🇱🇻","🇱🇧","🇱🇸","🇱🇷","🇱🇾","🇱🇮","🇱🇹","🇱🇺","🇲🇴","🇲🇬","🇲🇼","🇲🇾","🇲🇻","🇲🇱","🇲🇹",
            "🇲🇭","🇲🇶","🇲🇷","🇲🇺","🇾🇹","🇲🇽","🇫🇲","🇲🇩","🇲🇨","🇲🇳","🇲🇪","🇲🇸","🇲🇦","🇲🇿","🇲🇲","🇳🇦",
            "🇳🇷","🇳🇵","🇳🇱","🇳🇨","🇳🇿","🇳🇮","🇳🇪","🇳🇬","🇳🇺","🇳🇫","🇰🇵","🇲🇰","🇲🇵","🇳🇴","🇴🇲","🇵🇰",
            "🇵🇼","🇵🇸","🇵🇦","🇵🇬","🇵🇾","🇵🇪","🇵🇭","🇵🇳","🇵🇱","🇵🇹","🇵🇷","🇶🇦","🇷🇪","🇷🇴","🇷🇺","🇷🇼",
            "🇼🇸","🇸🇲","🇸🇹","🇸🇦","🇸🇳","🇷🇸","🇸🇨","🇸🇱","🇸🇬","🇸🇽","🇸🇰","🇸🇮","🇬🇸","🇸🇧","🇸🇴","🇿🇦",
            "🇰🇷","🇸🇸","🇪🇸","🇱🇰","🇸🇩","🇸🇷","🇸🇪","🇨🇭","🇸🇾","🇹🇼","🇹🇯","🇹🇿","🇹🇭","🇹🇱","🇹🇬","🇹🇰",
            "🇹🇴","🇹🇹","🇹🇳","🇹🇷","🇹🇲","🇹🇨","🇹🇻","🇺🇬","🇺🇦","🇦🇪","🇬🇧","🇺🇸","🇺🇾","🇺🇿","🇻🇺","🇻🇦",
            "🇻🇪","🇻🇳","🇼🇫","🇪🇭","🇾🇪","🇿🇲","🇿🇼"
        };

        /// <summary>
        /// Built-in sections without Recents. The ViewModel prepends Recents from the store.
        /// Declared after the glyph arrays so static init sees non-null lists.
        /// </summary>
        // Jump tiles: Segoe Fluent / MDL2 where possible; UnisonIcons EA05–08 for
        // sports / nature / beverage / symbols (Fluent has no matching category glyphs).
        public static IReadOnlyList<EmojiCategory> BuiltInCategories { get; } = new[]
        {
            new EmojiCategory(SmileysId, "Smileys & People", "\uE76E", false, SmileysAndPeople),
            new EmojiCategory(AnimalsId, "Animals & Nature", "\uEA06", true, AnimalsAndNature),
            new EmojiCategory(FoodId, "Food & Drink", "\uEA07", true, FoodAndDrink),
            new EmojiCategory(ActivitiesId, "Activities", "\uEA05", true, Activities),
            new EmojiCategory(TravelId, "Travel & Places", "\uE709", false, TravelAndPlaces),
            new EmojiCategory(ObjectsId, "Objects", "\uEA80", false, Objects),
            new EmojiCategory(SymbolsId, "Symbols", "\uEA08", true, Symbols),
            new EmojiCategory(FlagsId, "Flags", "\uE7C1", false, Flags)
        };

        private static HashSet<string> _knownGlyphs;

        /// <summary>
        /// Same built-in sections on every device. Flags stream last via the sticker-box
        /// category plan so Mobile still paints Smileys + ProgressRing before the flag grid.
        /// </summary>
        public static IReadOnlyList<EmojiCategory> BuiltInCategoriesFor(DeviceCategory category)
        {
            // category kept for call-site compatibility / future per-device cuts.
            _ = category;
            return BuiltInCategories;
        }

        /// <summary>
        /// Unique catalog glyphs found in <paramref name="text"/>, first-seen order
        /// (later bumps MRU when recorded after send).
        /// </summary>
        public static IReadOnlyList<string> CollectKnownGlyphsInText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new string[0];
            }

            HashSet<string> known = KnownGlyphs();
            var found = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (string.IsNullOrEmpty(element) || !known.Contains(element) || !seen.Add(element))
                {
                    continue;
                }

                found.Add(element);
            }

            return found;
        }

        /// <summary>True when <paramref name="glyph"/> is in the sticker-box catalog.</summary>
        public static bool IsKnownGlyph(string glyph)
        {
            return !string.IsNullOrEmpty(glyph) && KnownGlyphs().Contains(glyph);
        }

        private static HashSet<string> KnownGlyphs()
        {
            HashSet<string> cached = _knownGlyphs;
            if (cached != null)
            {
                return cached;
            }

            var set = new HashSet<string>(System.StringComparer.Ordinal);
            IReadOnlyList<EmojiCategory> builtIn = BuiltInCategories;
            for (int i = 0; i < builtIn.Count; i++)
            {
                IReadOnlyList<string> glyphs = builtIn[i]?.Glyphs;
                if (glyphs == null)
                {
                    continue;
                }

                for (int g = 0; g < glyphs.Count; g++)
                {
                    string glyph = glyphs[g];
                    if (!string.IsNullOrEmpty(glyph))
                    {
                        set.Add(glyph);
                    }
                }
            }

            _knownGlyphs = set;
            return set;
        }

        /// <summary>
        /// Title match keeps the whole section; otherwise only glyphs that contain
        /// <paramref name="query"/> (emoji paste / substring). Blank query returns
        /// <paramref name="source"/> unchanged.
        /// </summary>
        public static IReadOnlyList<EmojiCategory> Filter(
            IReadOnlyList<EmojiCategory> source,
            string query)
        {
            if (source == null || source.Count == 0)
            {
                return source ?? new EmojiCategory[0];
            }

            string q = query?.Trim() ?? string.Empty;
            if (q.Length == 0)
            {
                return source;
            }

            var result = new List<EmojiCategory>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                EmojiCategory category = source[i];
                if (category == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(category.Title) &&
                    category.Title.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(category);
                    continue;
                }

                IReadOnlyList<string> glyphs = category.Glyphs;
                if (glyphs == null || glyphs.Count == 0)
                {
                    continue;
                }

                var matched = new List<string>();
                for (int g = 0; g < glyphs.Count; g++)
                {
                    string glyph = glyphs[g];
                    if (!string.IsNullOrEmpty(glyph) &&
                        glyph.IndexOf(q, System.StringComparison.Ordinal) >= 0)
                    {
                        matched.Add(glyph);
                    }
                }

                if (matched.Count == 0)
                {
                    continue;
                }

                result.Add(new EmojiCategory(
                    category.Id,
                    category.Title,
                    category.JumpGlyph,
                    category.JumpUsesUnisonFont,
                    matched));
            }

            return result;
        }
    }
}
