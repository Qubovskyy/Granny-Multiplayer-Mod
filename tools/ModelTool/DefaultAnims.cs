using System.Reflection;

namespace GrannyModelTool
{
    /// <summary>
    /// The animations built into the tool – Mixamo "Idle", "Walking" and "Crouching Idle" made for the mod's bundled
    /// characters (Character_*.fbx, same skeleton). Every animation the user does not choose uses the built-in one.
    /// The FBX reader needs a file, so a built-in animation is written to %TEMP%\GrannyModelTool\anims when used.
    /// </summary>
    static class DefaultAnims
    {
        static readonly (string key, string title)[] All = { ("idle", "Idle"), ("walk", "Walking"), ("crouch", "Crouching Idle") };

        public static string Title(string key) => All.FirstOrDefault(a => a.key == key).title ?? key;

        /// <summary>The built-in animation for "idle" / "walk" / "crouch" as a file, or null (there is none for "crouchwalk").</summary>
        public static string PathOf(string key)
        {
            if (!All.Any(a => a.key == key)) return null;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("anim." + key + ".fbx");
            if (s == null) return null;
            var dir = Path.Combine(Path.GetTempPath(), "GrannyModelTool", "anims");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, key + ".fbx");
            if (!File.Exists(file) || new FileInfo(file).Length != s.Length)
            {
                using var f = File.Create(file);
                s.CopyTo(f);
            }
            return file;
        }
    }
}
