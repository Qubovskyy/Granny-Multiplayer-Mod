namespace GrannyMP
{
    public enum Language { English = 0, Polish = 1 }

    /// <summary>Simple two-language localisation: every user-facing string is written as L.T("English", "Polski").</summary>
    internal static class L
    {
        public static Language Current => Cfg.Lang != null ? Cfg.Lang.Value : Language.English;
        public static bool PL => Current == Language.Polish;
        public static string T(string en, string pl) => PL ? pl : en;

        const char Sep = '\u001f';
        /// <summary>Both languages packed into one string (for messages sent over the network).</summary>
        public static string Pair(string en, string pl) => en + Sep + pl;
        /// <summary>Shows a packed string in the local language (plain strings are returned unchanged).</summary>
        public static string Show(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf(Sep);
            if (i < 0) return s;
            return PL ? s.Substring(i + 1) : s.Substring(0, i);
        }
    }
}
