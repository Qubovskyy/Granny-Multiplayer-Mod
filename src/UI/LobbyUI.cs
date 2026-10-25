using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>Lobby window (IMGUI, F1) + in-game HUD (name tags, player list, notifications, chat).</summary>
    internal static class LobbyUI
    {
        public static bool Open;
        public static bool ChatOpen;
        static string _joinTarget = "", _joinPass = "", _port = "", _name = "", _chat = "", _chatInGame = "";
        static bool _stylesReady;
        static GUIStyle _fieldOn, _fieldText, _win, _title, _label, _small, _btn, _btnOn, _field, _box, _center, _tag, _tagSmall, _notice;
        static Texture2D _bg, _bgDark, _accent, _white, _trapIcon;
        static GUIStyle _huge, _keyCap, _clear;
        static bool _trapIconTried;
        static bool _fpsDisabledByUs;
        static bool _focusChat;

        // ---------- own text field (GUI.TextField is stripped in this game) ----------
        static string _focusId;
        static float _caretBlink;

        /// <summary>Text field: click to type. Enter = submitted, Esc = unfocus, Ctrl+V = paste.</summary>
        static string Field(string id, Rect r, string value, int max, out bool submitted, bool password = false)
        {
            submitted = false;
            value ??= "";
            bool focused = _focusId == id;
            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown)
            {
                if (r.Contains(e.mousePosition)) { _focusId = id; focused = true; e.Use(); }
                else if (focused) { _focusId = null; focused = false; }
            }
            if (focused && e != null && e.type == EventType.KeyDown)
            {
                var k = e.keyCode;
                bool ctrl = e.control || e.command;
                if (k == KeyCode.Return || k == KeyCode.KeypadEnter) { submitted = true; e.Use(); }
                else if (k == KeyCode.Escape) { _focusId = null; e.Use(); }
                else if (k == KeyCode.Backspace) { if (value.Length > 0) value = ctrl ? "" : value.Substring(0, value.Length - 1); e.Use(); }
                else if (ctrl && k == KeyCode.V) { value = (value + U.PasteFromClipboard().Replace("\r", "").Replace("\n", "")); e.Use(); }
                else if (ctrl && k == KeyCode.C) { U.CopyToClipboard(value); e.Use(); }
                else
                {
                    char ch = e.character;
                    if (ch != '\0' && !char.IsControl(ch) && !ctrl) { value += ch; e.Use(); }
                    else if (k != KeyCode.None && !ctrl) e.Use();
                }
                if (value.Length > max) value = value.Substring(0, max);
            }
            GUI.Box(r, "", focused ? _fieldOn : _field);
            var shown = password ? new string('*', value.Length) : value;
            _caretBlink += Time.unscaledDeltaTime;
            if (focused && ((int)(Time.unscaledTime * 2) % 2 == 0)) shown += "|";
            GUI.Label(new Rect(r.x + 4, r.y, r.width - 8, r.height), shown, _fieldText);
            return value;
        }

        public static bool TypingInField => _focusId != null;

        // ---------------- language flags (drawn in code – no image files needed) ----------------
        static Texture2D _flagUK, _flagPL;

        static bool FlagButton(Rect r, Texture2D flag, bool selected)
        {
            if (selected) GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), _accent);
            bool clicked = GUI.Button(r, "", _btn);
            var inner = new Rect(r.x + 3, r.y + 3, r.width - 6, r.height - 6);
            if (flag != null) GUI.DrawTexture(inner, flag);
            if (!selected) GUI.DrawTexture(inner, _dimTex ??= Tex(new Color(0, 0, 0, 0.45f)));
            return clicked;
        }
        static Texture2D _dimTex;

        static Texture2D FlagPL()
        {
            if (_flagPL != null) return _flagPL;
            const int w = 48, h = 30;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var red = new Color(0.86f, 0.08f, 0.24f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    t.SetPixel(x, y, y >= h / 2 ? Color.white : red); // texture y = 0 is the bottom row
            t.Apply(); t.hideFlags = HideFlags.HideAndDontSave;
            return _flagPL = t;
        }

        static Texture2D FlagUK()
        {
            if (_flagUK != null) return _flagUK;
            const int w = 60, h = 30;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var blue = new Color(0.004f, 0.13f, 0.41f); var red = new Color(0.78f, 0.06f, 0.18f);
            float diag = Mathf.Sqrt(w * w + h * h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x + 0.5f, v = y + 0.5f;
                    var c = blue;
                    // St Andrew's / St Patrick's saltire
                    float d1 = Mathf.Abs(v * w - u * h) / diag;            // bottom-left → top-right
                    float d2 = Mathf.Abs((h - v) * w - u * h) / diag;      // top-left → bottom-right
                    float d = Mathf.Min(d1, d2);
                    if (d < 3f) c = Color.white;
                    if (d < 1.2f) c = red;
                    // St George's cross
                    float cx = Mathf.Abs(u - w / 2f), cy = Mathf.Abs(v - h / 2f);
                    if (cx < 5f || cy < 5f) c = Color.white;
                    if (cx < 3f || cy < 3f) c = red;
                    t.SetPixel(x, y, c);
                }
            t.Apply(); t.hideFlags = HideFlags.HideAndDontSave;
            return _flagUK = t;
        }

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static Font _fontBold, _fontLight; static bool _fontTried;
        /// <summary>
        /// The pause menu has no font – its words are pictures in stone-textured small capitals. The closest Windows font
        /// is Copperplate Gothic (installed with Office); without it the default font stays.
        /// </summary>
        static void MenuFonts()
        {
            if (_fontTried) return;
            _fontTried = true;
            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, "COPRGTB.TTF"))) _fontBold = OsFont("Copperplate Gothic Bold");
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, "COPRGTL.TTF"))) _fontLight = OsFont("Copperplate Gothic Light");
                foreach (var f in new[] { _fontBold, _fontLight }) if (f != null) { f.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(f); }
                Log.Debug($"lobby window font: {(_fontBold != null ? "Copperplate Gothic" : "the default (Copperplate Gothic is not installed)")}");
            }
            catch (Exception e) { Log.Debug("lobby font: " + e.Message); _fontBold = _fontLight = null; }
        }

        /// <summary>A dynamic font of a Windows font (Font(string) is stripped – its native part is called directly).</summary>
        static Font OsFont(string name)
        {
            var f = new Font();
            Font.Internal_CreateFont(f, name);
            return f;
        }

        static Texture2D _panelTex; static bool _panelTried;
        /// <summary>The pause menu's panel (menu-panel.png next to the mod), null without it.</summary>
        static Texture2D PanelTexture()
        {
            if (_panelTried) return _panelTex;
            _panelTried = true;
            try
            {
                _panelTex = Png.Asset("menu-panel.png");
                if (_panelTex != null) { _panelTex.hideFlags = HideFlags.HideAndDontSave; _panelTex.wrapMode = TextureWrapMode.Clamp; }
            }
            catch (Exception e) { Log.Debug("panel texture: " + e.Message); }
            return _panelTex;
        }

        static GUIStyle Style(int size, Color text, Texture2D bg = null, Texture2D hover = null, TextAnchor align = TextAnchor.MiddleLeft, bool bold = false, bool wrap = false)
        {
            var s = new GUIStyle();
            s.fontSize = size;
            s.alignment = align;
            s.wordWrap = wrap;
            if (bold) s.fontStyle = FontStyle.Bold;
            s.normal.textColor = text;
            s.hover.textColor = text; s.active.textColor = text; s.focused.textColor = text;
            if (bg != null) { s.normal.background = bg; s.focused.background = bg; }
            if (hover != null) { s.hover.background = hover; s.active.background = hover; }
            s.padding.left = 6; s.padding.right = 6; s.padding.top = 3; s.padding.bottom = 3;
            s.clipping = TextClipping.Clip;
            return s;
        }

        static void Styles()
        {
            if (_stylesReady && U.Alive(_bg)) return;
            // the look of the game's pause menu: its black panel with the torn white edge (Bakgrundoptionmenu, shipped
            // as menu-panel.png next to the mod), stone-grey texts, plain dark buttons
            _bg = Tex(new Color(0.02f, 0.02f, 0.02f, 0.97f));
            _bgDark = Tex(new Color(0f, 0f, 0f, 0.45f));
            _accent = Tex(new Color(0.45f, 0.06f, 0.06f, 0.95f));
            _white = Tex(Color.white);
            var btnBg = Tex(new Color(0.11f, 0.11f, 0.11f, 0.9f));
            var btnHover = Tex(new Color(0.24f, 0.23f, 0.22f, 0.95f));
            var accHover = Tex(new Color(0.62f, 0.1f, 0.1f, 1f));
            var fieldBg = Tex(new Color(0.06f, 0.06f, 0.06f, 1f));
            var txt = new Color(0.87f, 0.86f, 0.83f);
            _win = Style(14, txt, _bg);
            _box = Style(14, txt, _bgDark);
            _title = Style(24, new Color(0.93f, 0.91f, 0.87f), null, null, TextAnchor.MiddleLeft, true);
            _label = Style(14, txt, null, null, TextAnchor.UpperLeft, false, true);
            _small = Style(12, new Color(0.66f, 0.65f, 0.62f), null, null, TextAnchor.UpperLeft, false, true);
            _center = Style(14, txt, null, null, TextAnchor.MiddleCenter);
            _btn = Style(14, txt, btnBg, btnHover, TextAnchor.MiddleCenter);
            _btn.hover.textColor = Color.white;
            _btnOn = Style(14, Color.white, _accent, accHover, TextAnchor.MiddleCenter, true);
            // the lettering of the pause menu (small capitals): Copperplate Gothic, when Windows has it
            MenuFonts();
            if (_fontBold != null) { _title.font = _fontBold; _btn.font = _fontBold; _btnOn.font = _fontBold; }
            if (_fontLight != null) { _label.font = _fontLight; _center.font = _fontLight; _label.fontStyle = FontStyle.Bold; }
            _field = Style(14, Color.white, fieldBg, fieldBg, TextAnchor.MiddleLeft);
            _fieldOn = Style(14, Color.white, Tex(new Color(0.1f, 0.06f, 0.06f, 1f)), null, TextAnchor.MiddleLeft);
            _fieldText = Style(14, Color.white, null, null, TextAnchor.MiddleLeft);
            _tag = Style(13, Color.white, null, null, TextAnchor.MiddleCenter, true);
            _tagSmall = Style(10, Color.white, null, null, TextAnchor.MiddleCenter, true);
            _tagSmall.padding.top = 0; _tagSmall.padding.bottom = 0;
            _notice = Style(18, Color.white, null, null, TextAnchor.MiddleCenter, true, true);
            _huge = Style(96, new Color(0.85f, 0.12f, 0.12f), null, null, TextAnchor.MiddleCenter, true);
            _clear = new GUIStyle();
            _keyCap = Style(13, Color.white, Tex(new Color(0.12f, 0.12f, 0.14f, 0.9f)), null, TextAnchor.MiddleCenter, true);
            _stylesReady = true;
        }

        public static void OpenWithJoin(string target, string error)
        {
            _joinTarget = target; Session.LastError = error;
            if (!Open) Toggle();
            _offPage = 2; _pwPrompt = true; _pwTarget = target; _directOpen = false;
            _focusId = "pass";
        }

        public static void Toggle()
        {
            Open = !Open;
            if (!Open) { _focusId = null; CloseSettings(); }
            if (Open)
            {
                if (string.IsNullOrEmpty(_name)) _name = Session.LocalName;
                if (string.IsNullOrEmpty(_port)) _port = Cfg.Port.Value.ToString();
            }
            UpdateInputCapture();
        }

        public static void ToggleChat(bool open)
        {
            ChatOpen = open; _focusChat = open;
            UpdateInputCapture();
        }

        /// <summary>While the window/chat is open in game: disable player control and show the cursor.</summary>
        public static void UpdateInputCapture()
        {
            bool capture = Open || ChatOpen || DebugMenu.Open;
            try
            {
                var fps = GameRefs.Fps;
                if (capture && U.Alive(fps) && fps.enabled) { fps.enabled = false; _fpsDisabledByUs = true; }
                else if (!capture && _fpsDisabledByUs) { if (U.Alive(fps)) fps.enabled = true; _fpsDisabledByUs = false; }
            }
            catch { }
            if (!capture && GameRefs.InGameScene) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        public static void LateUpdate()
        {
            if (Open || ChatOpen || DebugMenu.Open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        // =====================================================================
        // ---------------- interface scale (4K etc.) ----------------
        static float _scale = 1f;
        /// <summary>Screen size in GUI units (pixels divided by the interface scale).</summary>
        static float SW => Screen.width / _scale;
        static float SH => Screen.height / _scale;

        static float AutoScale => Mathf.Clamp(Mathf.Min(Screen.height / 1080f, Screen.width / 1920f), 1f, 4f);
        static float WantedScale => Cfg.UiScale.Value > 0.1f ? Mathf.Clamp(Cfg.UiScale.Value, 0.5f, 4f) : AutoScale;

        public static void Draw()
        {
            _scale = WantedScale;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1f));
            Styles();
            DrawHud();
            if (Open) DrawLobby();
            DrawDiscord();
            if (ChatOpen) DrawChatInput();
            if (DebugMenu.Open && !Open) DrawDebug();
        }

        // ---------------- main menu: the Discord icon (bottom right) ----------------
        const string DiscordUrl = "https://discord.gg/MrKK6KwMVv";
        static Texture2D _discord; static bool _discordTried; static float _discordHover;

        static void DrawDiscord()
        {
            if (GameFlow.CurrentScene != Cfg.MenuScene.Value) return;
            if (!_discordTried)
            {
                _discordTried = true;
                try
                {
                    _discord = Png.Asset("discord.png");
                }
                catch (Exception e) { Log.Debug("discord icon: " + e.Message); }
            }
            if (_discord == null) return;
            const float size = 54f;
            var spot = new Rect(SW - size - 20, SH - size - 20, size, size);
            var ev = Event.current;
            bool over = ev != null && spot.Contains(ev.mousePosition);
            if (ev != null && ev.type == EventType.Repaint)
                _discordHover = Mathf.MoveTowards(_discordHover, over ? 1f : 0f, Time.unscaledDeltaTime * 7f);
            // hover: a little bigger and brighter, with its name above
            float sz = size * (1f + 0.14f * _discordHover);
            var r = new Rect(spot.center.x - sz / 2f, spot.center.y - sz / 2f, sz, sz);
            var gc = GUI.color;
            if (_discordHover > 0.01f)
            {
                GUI.color = new Color(0.35f, 0.4f, 0.95f, 0.35f * _discordHover);
                float g = sz * 1.25f; GUI.DrawTexture(new Rect(spot.center.x - g / 2f, spot.center.y - g / 2f, g, g), _discord);
            }
            float b = 0.82f + 0.18f * _discordHover;
            GUI.color = new Color(b, b, b, 1f);
            GUI.DrawTexture(r, _discord);
            GUI.color = gc;
            if (_discordHover > 0.05f)
            {
                var c = _tag.normal.textColor;
                _tag.normal.textColor = new Color(1f, 1f, 1f, _discordHover);
                GUI.Label(new Rect(spot.center.x - 60, r.y - 24, 120, 22), "Discord", _tag);
                _tag.normal.textColor = c;
            }
            if (GUI.Button(spot, "", _clear))
            {
                try { Application.OpenURL(DiscordUrl); }
                catch
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiscordUrl) { UseShellExecute = true }); }
                    catch (Exception e) { Log.Warn("Discord link: " + e.Message); }
                }
                Log.Info("Opened the Discord invite");
            }
        }

        // ---------------- HUD ----------------
        static void DrawHud()
        {
            if (Session.State == SessionState.Offline)
            {
                if (!Open && GameFlow.CurrentScene == Cfg.MenuScene.Value)
                    GUI.Label(new Rect(10, SH - 28, 700, 24), L.T($"Granny MP: press {Cfg.ToggleKey.Value} to host or join", $"Granny MP: naciśnij {Cfg.ToggleKey.Value}, aby hostować lub dołączyć"), _small);
                DrawNotices();
                return;
            }
            // the start of the game: everybody waits for everybody (GameFlow start gate)
            if (GameFlow.StartHolding && GameRefs.InGameScene)
            {
                var gc0 = GUI.color; GUI.color = Color.black; GUI.DrawTexture(new Rect(0, 0, SW, SH), _white); GUI.color = gc0;
                GUI.Label(new Rect(0, SH / 2 - 20, SW, 40), L.T("Waiting until everybody has loaded Granny's house...", "Czekamy, aż wszyscy załadują dom Granny..."), _notice);
            }
            // PvP, playing Granny: the black screen at the start of the day, the bear-trap icon
            else if (Pvp.BlackScreen) DrawRespawn();
            else if (Pvp.ShowTrapIcon && !Open) DrawTrapIcon();
            // name tags above avatars
            var cam = GameRefs.InGameScene ? ViewCamera() : null;
            if (Cfg.ShowNametags.Value && cam != null)
            {
                foreach (var a in PlayerSync.Avatars)
                {
                    if (!U.Alive(a.Root) || !a.HasData) continue;
                    // a dead player (spectating) has no body and no name tag
                    if ((a.Flags & PlayerSync.F_Ghost) != 0 || a.Id == Spectator.Watching) continue;
                    // PvP: no name over Granny, and Granny sees no names (they would show the survivors through walls)
                    if (Pvp.IsGranny(a.Id) || Pvp.LocalIsGranny) continue;
                    var wp = U.Alive(a.Head) ? a.Head.position + Vector3.up * 0.45f : a.Root.transform.position + Vector3.up * (a.CamY + 0.45f);
                    var sp = cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0 || sp.z > 40f) continue;
                    if (TagHidden(a, cam.transform.position, wp)) continue;
                    var name = Session.NameOf(a.Id);
                    var r = new Rect(sp.x / _scale - 90, SH - sp.y / _scale - 12, 180, 24);
                    _tag.normal.textColor = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), name, _tag);
                    _tag.normal.textColor = Session.ColorOf(a.Id); GUI.Label(r, name, _tag);
                    // team badge (DEV / Helper) in small letters above the name
                    if (Badges.Of(a.Id, out var badge, out var badgeColor))
                    {
                        var br = new Rect(r.x, r.y - 11, r.width, 14);
                        _tagSmall.normal.textColor = Color.black; GUI.Label(new Rect(br.x + 1, br.y + 1, br.width, br.height), badge, _tagSmall);
                        _tagSmall.normal.textColor = badgeColor; GUI.Label(br, badge, _tagSmall);
                    }
                }
            }
            if (cam != null && Pvp.LocalIsGranny && Pvp.RevealUntil.Count > 0) DrawReveals(cam);
            if (!Open)
            {
                if (GameFlow.WaitingForWorld) GUI.Label(new Rect(SW / 2 - 250, 60, 500, 30), L.T("Synchronising with the host's world...", "Synchronizacja ze światem hosta..."), _notice);
                else if (GameFlow.WaitingForPlayers) GUI.Label(new Rect(SW / 2 - 300, 60, 600, 30), L.T("Waiting for all players to load Granny's house...", "Czekamy, aż wszyscy gracze załadują dom Granny..."), _notice);
            }
            if (VoiceChat.Talking || VoiceChat.ToggledOn) DrawMicIcon(new Rect(SW - 66, SH - 74, 56, 64), VoiceChat.Talking, VoiceChat.Level);
            if (VoiceChat.Error != null && Cfg.VoiceEnabled.Value && (Open || VoiceChat.WantsToTalk)) GUI.Label(new Rect(SW - 520, SH - 58, 510, 22), VoiceChat.Error, _small);
            // the game's crosshair dot belongs to the dead player's screen – a spectator gets its own
            if (Spectator.Active && !Open && !Spectator.MenuOpen) DrawDot(SW / 2f, SH / 2f);
            var spec = Spectator.HudText;
            if (spec != null) GUI.Label(new Rect(SW / 2 - 360, SH - 90, 720, 30), spec, _notice);
            var pvp = Open ? null : Pvp.HudText;
            if (pvp != null)
            {
                var pr = new Rect(SW / 2 - 450, SH - 90, 900, 30);
                _notice.normal.textColor = Color.black; GUI.Label(new Rect(pr.x + 2, pr.y + 2, pr.width, pr.height), pvp, _notice);
                _notice.normal.textColor = new Color(1f, 0.55f, 0.5f); GUI.Label(pr, pvp, _notice);
                _notice.normal.textColor = Color.white;
            }
            DrawNotices();
            // recent chat
            if (!Open)
            {
                float now = Time.realtimeSinceStartup;
                var lines = Session.ChatLog.Where(l => ChatOpen || now - l.t < 12f).Reverse().Take(6).Reverse().ToList();
                float y = SH - 70 - lines.Count * 20;
                foreach (var l in lines)
                {
                    var c = _label.normal.textColor; _label.normal.textColor = l.c;
                    GUI.Label(new Rect(12, y, 600, 22), l.text, _label);
                    _label.normal.textColor = c; y += 20;
                }
            }
        }

        // name tags are not seen through walls: a line from the camera to the head, re-checked every 0.15 s per avatar
        static readonly Dictionary<byte, (float at, bool hidden)> _tagOcc = new();
        static bool TagHidden(Avatar a, Vector3 from, Vector3 to)
        {
            float now = Time.unscaledTime;
            if (_tagOcc.TryGetValue(a.Id, out var c) && now < c.at) return c.hidden;
            bool hidden = false;
            try
            {
                var d = to - from; float len = d.magnitude;
                if (len > 0.01f)
                {
                    var spec = Spectator.ViewTransform;
                    foreach (var h in Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Ignore))
                    {
                        var t = h.collider != null ? h.collider.transform : null;
                        if (t == null || h.distance >= len - 0.05f) continue;
                        if (U.IsUnder(t, a.Root.transform)) continue;
                        if (U.Alive(GameRefs.PlayerRoot) && U.IsUnder(t, GameRefs.PlayerRoot)) continue;
                        if (U.Alive(spec) && U.IsUnder(t, spec)) continue;
                        hidden = true; break;
                    }
                }
            }
            catch { }
            _tagOcc[a.Id] = (now + 0.15f, hidden);
            return hidden;
        }

        /// <summary>
        /// PvP, playing Granny: a survivor who made a noise – a red ring where they are (through the walls; at the edge of
        /// the screen when they are behind), with how far away they are inside.
        /// </summary>
        static void DrawReveals(Camera cam)
        {
            float now = Time.unscaledTime;
            var ring = Ring();
            var gc = GUI.color;
            foreach (var kv in Pvp.RevealUntil)
            {
                if (now > kv.Value || !PlayerSync.TryGetAvatar(kv.Key, out var a) || !a.HasData || (a.Flags & PlayerSync.F_Ghost) != 0) continue;
                var feet = a.Root.transform.position;
                var mid = feet + Vector3.up * Mathf.Max(0.6f, a.CamY * 0.6f);
                float dist = Vector3.Distance(cam.transform.position, mid);
                var sp = cam.WorldToScreenPoint(mid);
                float sx = sp.x / _scale, sy = SH - sp.y / _scale;
                if (sp.z < 0.1f) { sx = SW - sx; sy = SH - 40f; } // behind: on the bottom edge, on the side it is
                float r = Mathf.Clamp(520f / Mathf.Max(1f, dist), 26f, 70f);
                sx = Mathf.Clamp(sx, r + 4, SW - r - 4); sy = Mathf.Clamp(sy, r + 4, SH - r - 4);
                float left = kv.Value - now, alpha = Mathf.Clamp01(left / 0.6f) * (0.8f + 0.2f * Mathf.Sin(now * 9f));
                GUI.color = new Color(1f, 0.1f, 0.06f, alpha);
                GUI.DrawTexture(new Rect(sx - r, sy - r, 2 * r, 2 * r), ring);
                GUI.color = gc;
                var txt = Mathf.RoundToInt(dist) + " m";
                var c = _tag.normal.textColor;
                _tag.normal.textColor = new Color(0f, 0f, 0f, alpha); GUI.Label(new Rect(sx - r + 1, sy - 11, 2 * r, 24), txt, _tag);
                _tag.normal.textColor = new Color(1f, 0.25f, 0.2f, alpha); GUI.Label(new Rect(sx - r, sy - 12, 2 * r, 24), txt, _tag);
                _tag.normal.textColor = c;
            }
            GUI.color = gc;
        }

        static Texture2D _ring;
        static Texture2D Ring()
        {
            if (_ring != null) return _ring;
            const int n = 128; const float outer = 62f, width = 7f;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = x + 0.5f - n / 2f, v = y + 0.5f - n / 2f, d = Mathf.Sqrt(u * u + v * v);
                    float edge = Mathf.Min(outer - d, d - (outer - width));
                    float fill = d < outer - width ? 0.12f : 0f; // a faint inside
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Max(fill, Mathf.Clamp01(edge + 0.5f))));
                }
            t.Apply();
            return _ring = t;
        }

        /// <summary>The camera that is drawing the picture now (Camera.main is a disabled camera in this game).</summary>
        static Camera ViewCamera()
        {
            var sc = Spectator.ViewTransform;
            if (U.Alive(sc)) { var c = sc.GetComponent<Camera>(); if (c != null && c.enabled) return c; }
            return GameRefs.RenderCamera() ?? Camera.main;
        }

        /// <summary>A label in <paramref name="color"/>; returns how far the next text starts (the width of this text).</summary>
        static float ColoredLabel(Rect r, string text, Color color, GUIStyle style)
        {
            var c = style.normal.textColor; style.normal.textColor = color;
            GUI.Label(new Rect(r.x, r.y, Mathf.Max(r.width, TextWidth(text, style) + (style.padding.left + style.padding.right) + 2), r.height), text, style);
            style.normal.textColor = c;
            return TextWidth(text, style);
        }

        static readonly Dictionary<(string, int, bool), float> _widths = new();
        static bool _calcSizeBroken;
        /// <summary>Width of a text in a style (without the style's padding).</summary>
        static float TextWidth(string text, GUIStyle style)
        {
            var key = (text, style.fontSize, style.fontStyle == FontStyle.Bold);
            if (_widths.TryGetValue(key, out var w)) return w;
            w = text.Length * style.fontSize * 0.56f; // estimate when the engine cannot measure
            if (!_calcSizeBroken)
            {
                // (CalcSize itself is stripped from this game – the variant with constraints is not)
                try { w = style.CalcSizeWithConstraints(new GUIContent(text), Vector2.zero).x - (style.padding.left + style.padding.right); }
                catch { _calcSizeBroken = true; w = text.Length * style.fontSize * 0.56f; }
            }
            if (w <= 0f || w > text.Length * style.fontSize * 1.2f) w = text.Length * style.fontSize * 0.56f; // a measurement that makes no sense
            return _widths[key] = w;
        }

        static Texture2D _dot;
        static void DrawDot(float x, float y)
        {
            if (_dot == null)
            {
                const int n = 16;
                _dot = new Texture2D(n, n, TextureFormat.RGBA32, false);
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        float d = Mathf.Sqrt((i + 0.5f - n / 2f) * (i + 0.5f - n / 2f) + (j + 0.5f - n / 2f) * (j + 0.5f - n / 2f));
                        _dot.SetPixel(i, j, new Color(1f, 1f, 1f, Mathf.Clamp01(n / 2f - 1f - d)));
                    }
                _dot.Apply(); _dot.hideFlags = HideFlags.HideAndDontSave;
            }
            float sz = 6f;
            GUI.DrawTexture(new Rect(x - sz / 2f, y - sz / 2f, sz, sz), _dot);
        }

        static string PlaceOf(NetPlayer p)
        {
            if (p.Ghost) return L.T("spectator", "obserwator");
            if (p.Scene == Cfg.GameScene.Value) return p.IsHost || p.SceneReady ? L.T("in game", "w grze") : L.T("loading", "ładuje");
            if (p.Scene == Cfg.IntroScene.Value) return L.T("intro", "intro");
            if (p.Scene == Cfg.MenuScene.Value || string.IsNullOrEmpty(p.Scene)) return L.T("menu", "menu");
            return p.Scene;
        }

        // ---------------- PvP: the Granny player's screen ----------------
        /// <summary>The start of the day: black screen with the countdown until Granny comes back.</summary>
        static void DrawRespawn()
        {
            var gc = GUI.color; GUI.color = Color.black; GUI.DrawTexture(new Rect(0, 0, SW, SH), _white); GUI.color = gc;
            int s = Pvp.RespawnSeconds;
            if (_tag == null) return;
            if (Pvp.KnockedOut)
            {
                GUI.Label(new Rect(0, SH / 2 - 40, SW, 36), L.T("Granny is knocked out", "Granny jest ogłuszona"), _notice);
                GUI.Label(new Rect(0, SH / 2 + 4, SW, 30), L.T("You come back when she gets up...", "Wrócisz, gdy wstanie..."), _notice);
                return;
            }
            GUI.Label(new Rect(0, SH / 2 - 120, SW, 36), L.T("You are GRANNY", "Grasz jako GRANNY"), _notice);
            GUI.Label(new Rect(0, SH / 2 - 70, SW, 120), s > 0 ? s.ToString() : "...", _huge);
            if (Pvp.WaitingDayTitle) { GUI.Label(new Rect(0, SH / 2 + 60, SW, 30), L.T("The day is starting...", "Zaczyna się dzień..."), _notice); return; }
            GUI.Label(new Rect(0, SH / 2 + 60, SW, 30), s > 0
                ? L.T("The survivors are waking up – you come back where Granny starts the day.", "Ocaleni się budzą – wrócisz tam, gdzie Granny zaczyna dzień.")
                : L.T("Getting up...", "Wstajesz..."), _notice);
        }

        /// <summary>Top right: the bear trap, its key and the time until the next one.</summary>
        static void DrawTrapIcon()
        {
            if (!U.Alive(_trapIcon) && !_trapIconTried)
            {
                // the picture next to the mod (beartrap-icon.png), otherwise a drawn one
                _trapIconTried = true;
                _trapIcon = Png.Asset("beartrap-icon.png");
                if (_trapIcon == null) _trapIcon = MakeTrapIcon();
            }
            if (!U.Alive(_trapIcon)) return;
            const float size = 64f;
            float iw = Mathf.Min(120f, size * _trapIcon.width / (float)Mathf.Max(1, _trapIcon.height)), ih = size;
            if (iw >= 120f) ih = iw * _trapIcon.height / (float)_trapIcon.width;
            var r = new Rect(SW - iw - 22, 18 + (size - ih) / 2f, iw, ih);
            var gc = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(new Rect(r.x - 4, r.y - 4, r.width + 8, r.height + 8), _white);
            float left = Pvp.TrapCooldownLeft;
            GUI.color = left > 0 ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
            GUI.DrawTexture(r, _trapIcon);
            if (left > 0)
            {
                // the dark part shrinks while the trap reloads
                float frac = Mathf.Clamp01(left / Pvp.TrapCooldownTotal);
                GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(r.x, r.y, r.width, r.height * frac), _white);
                GUI.color = gc;
                GUI.Label(r, Mathf.CeilToInt(left).ToString(), _notice);
            }
            GUI.color = gc;
            GUI.Label(new Rect(r.center.x - 18, 18 + size + 8, 36, 22), Cfg.PvpTrapKey.Value.ToString(), _keyCap);
        }

        /// <summary>A small bear-trap picture seen from the side: the two open jaws with teeth on the base plate and the chain.</summary>
        static Texture2D MakeTrapIcon()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            var steel = new Color(0.8f, 0.8f, 0.82f, 1f); var dark = new Color(0.42f, 0.42f, 0.45f, 1f); var none = new Color(0, 0, 0, 0);
            var px = new Color[n * n];
            for (int i = 0; i < px.Length; i++) px[i] = none;
            void Set(int x, int y, Color c) { if (x >= 0 && x < n && y >= 0 && y < n) px[y * n + x] = c; }
            // (the texture's y goes up – y = 0 is the bottom of the icon)
            // base plate with the spring underneath
            for (int x = 10; x <= 53; x++) for (int y = 12; y <= 16; y++) Set(x, y, dark);
            for (int x = 14; x <= 49; x++) for (int y = 8; y <= 11; y++) if (((x / 3) & 1) == 0) Set(x, y, dark);
            // two jaws: half rings opening upwards from the hinges at the ends of the plate
            foreach (int side in new[] { -1, 1 })
            {
                float hx = 31.5f + side * 13f, hy = 16f;              // the hinge
                for (int y = 16; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x - hx, dy = y - hy, d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dy < 0 || side * dx < -1f) continue;           // only the outer upper quarter
                        if (d >= 17f && d <= 21f) Set(x, y, steel);         // the jaw
                        else if (d > 12f && d < 17f && dy > 8f)
                        {
                            // teeth along the inside of the jaw, pointing at the middle
                            float a = Mathf.Atan2(dy, side * dx) * Mathf.Rad2Deg;
                            float saw = Mathf.Abs(Mathf.Repeat(a, 18f) - 9f) / 9f;   // 0 at a tooth tip
                            if ((17f - d) / 5f < 1f - saw) Set(x, y, steel);
                        }
                    }
            }
            // the chain hanging to the right
            for (int k = 0; k < 4; k++)
            {
                int cx = 54 + k * 2, cy = 12 - k * 3;
                for (int x = cx - 1; x <= cx + 1; x++) for (int y = cy - 1; y <= cy + 1; y++) Set(x, y, dark);
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        static void DrawNotices()
        {
            float y = SH * 0.18f;
            foreach (var n in Session.Notices)
            {
                var r = new Rect(SW / 2 - 350, y, 700, 30);
                _notice.normal.textColor = Color.black; GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), n.text, _notice);
                _notice.normal.textColor = new Color(1f, 0.85f, 0.85f); GUI.Label(r, n.text, _notice);
                y += 32;
            }
        }

        static void DrawChatInput()
        {
            var r = new Rect(12, SH - 46, 520, 28);
            if (_focusChat) { _focusId = "mpchat"; _focusChat = false; }
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { _chatInGame = ""; _focusId = null; ToggleChat(false); e.Use(); return; }
            _chatInGame = Field("mpchat", r, _chatInGame, 200, out var send);
            if (send) { Session.SendChat(_chatInGame); _chatInGame = ""; _focusId = null; ToggleChat(false); }
            else if (_focusId != "mpchat") { ToggleChat(false); }
        }

        // ---------------- lobby window ----------------
        static void DrawLobby()
        {
            // the character choice gets a bigger window – the previews are large there
            bool big = _settings && _characters;
            float w = Mathf.Min(big ? 1100 : 900, SW - 40), h = Mathf.Min(big ? 820 : _settings ? 700 : 600, SH - 40);
            var win = new Rect((SW - w) / 2, (SH - h) / 2, w, h);
            var panel = PanelTexture();
            if (panel != null) GUI.DrawTexture(win, panel, ScaleMode.StretchToFill);
            else GUI.Box(win, "", _win);
            float x = win.x + 30, y = win.y + 22, cw = w - 60;
            GUI.Label(new Rect(x, y, cw, 30), "GRANNY MP", _title);
            GUI.Label(new Rect(x + 200, y + 8, cw - 520, 22), $"v{Plugin.ModVersion} (beta)", _small);
            if (GearButton(new Rect(win.xMax - 98, y - 1, 32, 28), _settings)) { if (_settings) CloseSettings(); else OpenSettings(); }
            if (GUI.Button(new Rect(win.xMax - 58, y, 30, 26), "X", _btn)) Toggle();
            y += 40;

            if (_settings) DrawSettings(x, y, cw, win.yMax - y - 24);
            else if (_serverCfg && Session.State == SessionState.Connected) DrawServerConfig(x, y, cw, win.yMax - y - 24);
            else if (Session.State == SessionState.Offline)
            {
                DrawOffline(x, y, cw, win.yMax - y - 24);
                if (GameFlow.CurrentScene == Cfg.GameScene.Value && GUI.Button(new Rect(win.xMax - 302, win.y + 22, 190, 26), L.T("Back to menu", "Wyjdź do menu"), _btn)) { Toggle(); GameFlow.GoToMenu(); }
            }
            else if (Session.State == SessionState.Connecting)
            {
                GUI.Label(new Rect(x, y, cw, 30), Session.Status, _label);
                if (GUI.Button(new Rect(x, y + 40, 160, 32), L.T("Cancel", "Anuluj"), _btn)) Session.Shutdown(L.T("Cancelled", "Anulowano"));
            }
            else DrawConnected(x, y, cw, win.yMax - y - 24);

            if (!string.IsNullOrEmpty(Session.LastError))
            {
                var c = _label.normal.textColor; _label.normal.textColor = new Color(1f, 0.45f, 0.45f);
                GUI.Label(new Rect(x, win.yMax - 48, cw, 24), Session.LastError, _label);
                _label.normal.textColor = c;
            }
        }

        static int _listPage;
        static bool _serverCfg;

        static string GrannyChoiceName() => Session.GrannyChoice != 255 && Session.Players.ContainsKey(Session.GrannyChoice)
            ? Session.NameOf(Session.GrannyChoice) : L.T("a random player", "losowy gracz");

        static void CheckRow(Rect r, bool on, string label, bool enabled, Action<bool> set)
        {
            GUI.enabled = enabled;
            if (GUI.Button(r, (on ? "[x] " : "[  ] ") + label, on ? _btnOn : _btn)) set(!on);
            GUI.enabled = true;
        }

        /// <summary>The host's server options (the clients see them, greyed out) – one panel.</summary>
        static void DrawServerConfig(float x, float y, float w, float h)
        {
            bool host = Session.IsHost && !Session.HostInGame; // not while a game is running
            GUI.Box(new Rect(x, y, w, h - 44), "", _box);
            float lx = x + 16, ly = y + 10, lw = Mathf.Min(w - 32, 640);
            GUI.Label(new Rect(lx, ly, lw, 24), L.T("SERVER CONFIGURATION", "KONFIGURACJA SERWERA") + (Session.HostInGame ? L.T(" (the game is running – change it in the lobby)", " (gra trwa – zmienisz w lobby)") : Session.IsHost ? "" : L.T(" (set by the host)", " (ustawia host)")), _label); ly += 32;
            CheckRow(new Rect(lx, ly, lw, 28), Session.JoinInProgress, L.T("Allow joining during the game", "Dołączanie w trakcie gry"), host,
                v => { Session.JoinInProgress = v; Cfg.AllowJoinInProgress.Value = v; Session.PushSettings(); });
            ly += 34;
            // hiding spots (bed, coffin, car) shared by several players
            CheckRow(new Rect(lx, ly, lw, 28), Session.SpotSingle, L.T("One player per hiding spot", "Kryjówka tylko dla jednej osoby"), host,
                v => { Session.SpotSingle = v; Cfg.SpotSingle.Value = v; Session.PushSettings(); });
            ly += 34;
            CheckRow(new Rect(lx, ly, lw, 28), Session.SpotCatchAll, L.T("Granny catches everybody in the hiding spot", "Granny łapie wszystkich w kryjówce"), host && !Session.SpotSingle,
                v => { Session.SpotCatchAll = v; Cfg.SpotCatchAll.Value = v; Session.PushSettings(); });
            ly += 34;
            CheckRow(new Rect(lx, ly, lw, 28), Session.LockFov, L.T("Lock the field of view (no custom FOV)", "Blokada własnego FOV"), host,
                v => { Session.LockFov = v; Cfg.LockFov.Value = v; Session.PushSettings(); });
            ly += 30;
            GUI.Label(new Rect(lx, ly, lw, 22), L.T("With the lock on everybody plays with the game's own field of view.",
                "Z blokadą wszyscy grają z polem widzenia gry (FOV z ustawień jest pomijany)."), _small);
            ly += 30;

            // voice chat range, PvP Granny speed (sent to everybody; a slider is sent once it stops moving)
            GUI.enabled = host;
            GUI.Label(new Rect(lx, ly + 3, 220, 22), L.T("Voice chat range:", "Zasięg czatu głosowego:"), _small);
            float vr = Slider("cfgvoice", new Rect(lx + 220, ly, lw - 290, 24), Session.VoiceRange, 3f, 100f, 1f);
            GUI.Label(new Rect(lx + lw - 62, ly + 3, 62, 22), Mathf.RoundToInt(Session.VoiceRange) + " m", _small);
            if (host && Mathf.Abs(vr - Session.VoiceRange) > 0.001f) { Session.VoiceRange = vr; Cfg.VoiceRange.Value = vr; _cfgPushAt = Time.unscaledTime + 0.3f; }
            ly += 32;
            // PvP options only with PvP as the game mode
            bool pvp = Session.PvpOn;
            if (pvp)
            {
                GUI.Label(new Rect(lx, ly + 3, 220, 22), L.T("[PvP] Granny's speed:", "[PvP] Prędkość Granny:"), _small);
                float gs = Slider("cfgspeed", new Rect(lx + 220, ly, lw - 290, 24), Session.GrannySpeed, 0.5f, 2f, 0.05f);
                GUI.Label(new Rect(lx + lw - 62, ly + 3, 62, 22), "x" + Session.GrannySpeed.ToString("0.00"), _small);
                if (host && Mathf.Abs(gs - Session.GrannySpeed) > 0.0001f) { Session.GrannySpeed = gs; Cfg.PvpGrannySpeed.Value = gs; _cfgPushAt = Time.unscaledTime + 0.3f; }
                ly += 34;
            }
            GUI.enabled = true;
            if (_cfgPushAt > 0 && Time.unscaledTime >= _cfgPushAt) { _cfgPushAt = -1; Session.PushSettings(); }
            if (!pvp) _grannyDrop = false;

            // ---- PvP: who plays Granny (a drop-down; the last row, its list opens over the empty space below) ----
            if (pvp) {
            GUI.Label(new Rect(lx, ly + 4, 220, 22), L.T("[PvP] Who plays Granny:", "[PvP] Kto gra jako Granny:"), _small);
            bool canPick = host && !Session.HostInGame;
            GUI.enabled = canPick;
            var dd = new Rect(lx + 220, ly, lw - 220, 28);
            if (GUI.Button(dd, GrannyChoiceName() + (_grannyDrop ? "   ^" : "   v"), _btn)) _grannyDrop = !_grannyDrop;
            GUI.enabled = true;
            if (!canPick) _grannyDrop = false;
            ly += 32;
            if (_grannyDrop)
            {
                var opts = new List<(byte id, string name)> { (255, L.T("A random player", "Losowy gracz")) };
                foreach (var p in Session.Players.Values.OrderBy(p => p.Id)) opts.Add((p.Id, p.Name + (p.IsHost ? "  [HOST]" : "")));
                float oy = ly;
                GUI.Box(new Rect(dd.x, oy - 2, dd.width, opts.Count * 28 + 4), "", _win);
                foreach (var (id, name) in opts)
                {
                    if (oy > y + h - 80) break;
                    if (GUI.Button(new Rect(dd.x + 2, oy, dd.width - 4, 26), name, Session.GrannyChoice == id ? _btnOn : _btn))
                    { Session.GrannyChoice = id; Session.PushSettings(); _grannyDrop = false; }
                    oy += 28;
                }
            }
            else
            {
                string note = Session.HostInGame ? L.T("Chosen when the game starts – it is running now.", "Wybierane przy starcie gry – gra już trwa.") : "";
                if (note != "") GUI.Label(new Rect(lx + 220, ly, lw - 220, 22), note, _small);
            }
            }
            if (GUI.Button(new Rect(x + w - 160, y + h - 36, 160, 32), L.T("Back", "Wróć"), _btn)) { _serverCfg = false; _grannyDrop = false; if (_cfgPushAt > 0) { _cfgPushAt = -1; Session.PushSettings(); } }
        }
        static bool _grannyDrop; static float _cfgPushAt = -1;
        static bool _hostDirect;

        static int _offPage; // 0 = Host Game / Join Game, 1 = hosting, 2 = joining

        static void DrawOffline(float x, float y, float w, float h)
        {
            if (_offPage == 0) { DrawStartPage(x, y, w, h); return; }
            if (GUI.Button(new Rect(x, y + h - 36, 160, 32), L.T("Back", "Wróć"), _btn))
            {
                if (_pwPrompt) _pwPrompt = false; else if (_directOpen) _directOpen = false; else _offPage = 0;
                return;
            }
            float col = Mathf.Min(w, 560), bx = x + (w - col) / 2;
            if (_offPage == 1) DrawHostPage(bx, y, col, h - 44); else DrawJoinPage(x, y, w, h - 44);
        }

        static void DrawStartPage(float x, float y, float w, float h)
        {
            float bw = Mathf.Min(420, w - 40), bh = 70, cx = x + (w - bw) / 2, cy = y + (h - 44) / 2 - bh - 10;
            GUI.Label(new Rect(x, cy - 46, w, 24), L.T($"Playing as {Session.LocalName} – the name can be changed in the settings (gear).",
                $"Grasz jako {Session.LocalName} – nick zmienisz w ustawieniach (koło zębate)."), _center);
            if (GUI.Button(new Rect(cx, cy, bw, bh), L.T("Host Game", "Hostuj grę"), _btnOn)) _offPage = 1;
            if (GUI.Button(new Rect(cx, cy + bh + 20, bw, bh), L.T("Join Game", "Dołącz do gry"), _btn)) _offPage = 2;
        }

        static void DrawHostPage(float x, float y, float col, float h)
        {
            GUI.Box(new Rect(x, y, col, h), "", _box);
            float ly = y + 10, lx = x + 12, lw = col - 24;
            GUI.Label(new Rect(lx, ly, lw, 24), L.T("HOST A GAME", "HOSTUJ GRĘ"), _label); ly += 30;
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Password (optional):", "Hasło (opcjonalne):"), _small);
            var hp = Field("hostpass", new Rect(lx + 140, ly, lw - 140, 26), Cfg.Password.Value, 32, out _, true);
            if (hp != Cfg.Password.Value) Cfg.Password.Value = hp;
            ly += 34;
            bool steam = SteamNative.TryInit();
            if (Cfg.DisableSteam.Value) _hostDirect = true; // Steam is switched off in the config
            // hosting method first – only the options of the chosen method are shown
            GUI.Label(new Rect(lx, ly + 4, 120, 22), L.T("Method:", "Metoda:"), _small);
            float mw = (lw - 110) / 2 - 3;
            GUI.enabled = !Cfg.DisableSteam.Value;
            if (GUI.Button(new Rect(lx + 110, ly, mw, 26), "Steam", _hostDirect ? _btn : _btnOn)) _hostDirect = false;
            GUI.enabled = true;
            if (GUI.Button(new Rect(lx + 110 + mw + 6, ly, mw, 26), "Direct IP", _hostDirect ? _btnOn : _btn)) _hostDirect = true;
            ly += 34;
            if (!_hostDirect)
            {
                // who can see the Steam lobby
                bool pub = Cfg.SteamLobbyType.Value == 2;
                GUI.Label(new Rect(lx, ly + 4, 120, 22), L.T("Steam lobby:", "Lobby Steam:"), _small);
                if (GUI.Button(new Rect(lx + 110, ly, mw, 26), L.T("Friends only", "Tylko znajomi"), pub ? _btn : _btnOn)) Cfg.SteamLobbyType.Value = 1;
                if (GUI.Button(new Rect(lx + 110 + mw + 6, ly, mw, 26), L.T("Public", "Publiczne"), pub ? _btnOn : _btn)) Cfg.SteamLobbyType.Value = 2;
                ly += 32;
                GUI.enabled = steam;
                if (GUI.Button(new Rect(lx, ly, lw, 36), steam ? L.T("Host via Steam", "Hostuj przez Steam") : L.T("Steam unavailable", "Steam niedostępny"), _btnOn)) Session.HostSteam();
                GUI.enabled = true;
                ly += 42;
                if (!steam) GUI.Label(new Rect(lx, ly, lw, 36), SteamNative.LastError, _small);
            }
            else
            {
                GUI.Label(new Rect(lx, ly + 4, 120, 26), L.T("UDP port:", "Port UDP:"), _small);
                _port = Field("port", new Rect(lx + 110, ly, 90, 26), _port, 5, out _);
                ly += 32;
                if (GUI.Button(new Rect(lx, ly, lw, 36), L.T("Host Direct IP", "Hostuj Direct IP"), _btnOn))
                {
                    if (int.TryParse(_port, out var port) && port > 0 && port < 65536) { Cfg.Port.Value = port; Session.HostDirect(port); }
                    else Session.LastError = L.T("Invalid port.", "Nieprawidłowy port.");
                }
                ly += 44;
                GUI.Label(new Rect(lx, ly, lw, 80), Cfg.DisableSteam.Value
                    ? L.T($"Steam is off (config). A second copy of the game on this PC joins 127.0.0.1:{_port}.", $"Steam wyłączony (konfig). Druga kopia gry na tym PC dołącza na 127.0.0.1:{_port}.")
                    : L.T("Direct IP: friends connect to your IP address and port. Over the internet you need UDP port forwarding or a VPN like Radmin/ZeroTier/Hamachi.",
                    "Direct IP: znajomi łączą się na Twój adres IP i port. Przez internet potrzebne jest przekierowanie portu UDP albo VPN typu Radmin/ZeroTier/Hamachi."), _small);
            }
        }

        static bool _directOpen, _pwPrompt; static string _pwTarget = "";

        /// <summary>Join: the whole panel is the list of Steam lobbies; "Direct..." opens the SteamID / IP field.</summary>
        static void DrawJoinPage(float x, float y, float w, float h)
        {
            if (_pwPrompt) { DrawPasswordPrompt(x, y, w, h); return; }
            if (_directOpen) { DrawDirectPanel(x, y, w, h); return; }
            bool steam = SteamNative.TryInit();
            GUI.Box(new Rect(x, y, w, h), "", _box);
            float ry = y + 10, rx = x + 14, rw = w - 28;
            GUI.Label(new Rect(rx, ry, rw - 360, 24), L.T("JOIN A GAME", "DOŁĄCZ DO GRY"), _label);
            if (GUI.Button(new Rect(rx + rw - 230, ry - 2, 120, 28), L.T("Direct...", "Direct..."), _btn)) { _directOpen = true; _focusId = "join"; }
            GUI.enabled = steam && !LobbyBrowser.Busy;
            if (GUI.Button(new Rect(rx + rw - 104, ry - 2, 104, 28), L.T("Refresh", "Odśwież"), _btn)) { LobbyBrowser.Refresh(); _listPage = 0; }
            GUI.enabled = true;
            ry += 30;
            GUI.Label(new Rect(rx, ry, rw, 22), L.T("Steam games (public + friends)", "Gry Steam (publiczne + znajomi)") + (LobbyBrowser.Status != "" ? " · " + LobbyBrowser.Status : ""), _small);
            ry += 24;
            const float rowH = 34;
            float listH = h - (ry - y) - 66;
            int rows = Mathf.Max(3, Mathf.FloorToInt(listH / rowH));
            GUI.Box(new Rect(rx, ry, rw, rows * rowH + 4), "", _box);
            var list = LobbyBrowser.Entries;
            int pages = Mathf.Max(1, (list.Count + rows - 1) / rows);
            _listPage = Mathf.Clamp(_listPage, 0, pages - 1);
            if (list.Count == 0)
                GUI.Label(new Rect(rx + 10, ry + 10, rw - 20, 24), steam ? L.T("No games found – press Refresh.", "Nie znaleziono gier – kliknij Odśwież.") : SteamNative.LastError, _label);
            for (int i = 0; i < rows; i++)
            {
                int idx = _listPage * rows + i;
                if (idx >= list.Count) break;
                var e = list[idx];
                float yy = ry + 4 + i * rowH;
                var name = (e.Friend ? "★ " : "") + (string.IsNullOrEmpty(e.Name) ? e.Lobby.ToString() : e.Name);
                GUI.Label(new Rect(rx + 10, yy + 5, rw - 330, 24), name, _label);
                GUI.Label(new Rect(rx + rw - 310, yy + 7, 90, 22), $"{e.Players}/{(e.Max > 0 ? e.Max : Cfg.MaxPlayers.Value)}", _small);
                if (e.Password) GUI.Label(new Rect(rx + rw - 220, yy + 7, 110, 22), L.T("password", "hasło"), _small);
                if (GUI.Button(new Rect(rx + rw - 104, yy, 98, rowH - 4), L.T("Join", "Dołącz"), _btnOn))
                {
                    _joinTarget = e.Lobby.ToString();
                    if (e.Password) { _pwPrompt = true; _pwTarget = _joinTarget; _joinPass = ""; _focusId = "pass"; Session.LastError = ""; }
                    else Session.Join(_joinTarget, "");
                }
            }
            ry += rows * rowH + 10;
            if (pages > 1)
            {
                if (GUI.Button(new Rect(rx, ry, 40, 26), "<", _btn)) _listPage--;
                GUI.Label(new Rect(rx + 46, ry + 3, 80, 22), $"{_listPage + 1}/{pages}", _small);
                if (GUI.Button(new Rect(rx + 110, ry, 40, 26), ">", _btn)) _listPage++;
            }
            GUI.Label(new Rect(rx + 170, ry + 3, rw - 170, 22), L.T("Or accept a Steam invite (Shift+Tab → friends → Join game).", "Albo przyjmij zaproszenie ze Steam (Shift+Tab → znajomi → Dołącz do gry)."), _small);
        }

        /// <summary>Join by a Steam Lobby ID, the host's SteamID or IP:port.</summary>
        static void DrawDirectPanel(float x, float y, float w, float h)
        {
            float pw = Mathf.Min(w, 560), px = x + (w - pw) / 2;
            GUI.Box(new Rect(px, y, pw, 210), "", _box);
            float ry = y + 12, rx = px + 14, rw = pw - 28;
            GUI.Label(new Rect(rx, ry, rw, 24), L.T("DIRECT JOIN", "DOŁĄCZ BEZPOŚREDNIO"), _label); ry += 32;
            GUI.Label(new Rect(rx, ry, rw, 20), L.T("Steam Lobby ID, host SteamID or IP:port", "Lobby ID Steam, SteamID hosta albo IP:port"), _small); ry += 22;
            _joinTarget = Field("join", new Rect(rx, ry, rw - 84, 30), _joinTarget, 64, out var enter);
            if (GUI.Button(new Rect(rx + rw - 78, ry, 78, 30), L.T("Paste", "Wklej"), _btn)) _joinTarget = U.PasteFromClipboard().Trim();
            ry += 44;
            if (GUI.Button(new Rect(rx, ry, rw, 40), L.T("Join", "Dołącz"), _btnOn) || enter) Session.Join(_joinTarget, "");
            ry += 46;
            GUI.Label(new Rect(rx, ry, rw, 40), L.T("If the game has a password, you will be asked for it.", "Jeśli gra ma hasło, zostaniesz o nie poproszony."), _small);
        }

        /// <summary>The lobby has a password: typed before joining.</summary>
        static void DrawPasswordPrompt(float x, float y, float w, float h)
        {
            float pw = Mathf.Min(w, 480), px = x + (w - pw) / 2, ph = 190, py = y + Mathf.Max(0f, (h - ph) / 2 - 40);
            GUI.Box(new Rect(px, py, pw, ph), "", _box);
            float ry = py + 12, rx = px + 14, rw = pw - 28;
            GUI.Label(new Rect(rx, ry, rw, 24), L.T("PASSWORD", "HASŁO"), _label); ry += 30;
            GUI.Label(new Rect(rx, ry, rw, 22), L.T("This game is password protected:", "Ta gra jest zabezpieczona hasłem:"), _small); ry += 24;
            _joinPass = Field("pass", new Rect(rx, ry, rw, 30), _joinPass, 32, out var enter, true);
            ry += 44;
            float bw = (rw - 10) / 2;
            if (GUI.Button(new Rect(rx, ry, bw, 38), L.T("Cancel", "Anuluj"), _btn)) { _pwPrompt = false; Session.LastError = ""; }
            if (GUI.Button(new Rect(rx + bw + 10, ry, bw, 38), L.T("Join", "Dołącz"), _btnOn) || enter)
            {
                _pwPrompt = false;
                Session.Join(_pwTarget, _joinPass);
            }
        }

        static void DrawConnected(float x, float y, float w, float h)
        {
            bool host = Session.IsHost;
            // connection info bar
            GUI.Box(new Rect(x, y, w, 64), "", _box);
            GUI.Label(new Rect(x + 10, y + 6, w - 20, 22), $"{(host ? L.T("You are hosting", "Hostujesz") : L.T("Connected", "Połączono"))} · {Session.Transport?.Kind}" + (host && !string.IsNullOrEmpty(Cfg.Password.Value) ? L.T(" · password protected", " · z hasłem") : ""), _label);
            float bx = x + 10, by = y + 32;
            if (Session.SteamLobby != 0)
            {
                if (GUI.Button(new Rect(bx, by, 190, 26), L.T("Copy Lobby ID", "Kopiuj Lobby ID"), _btn)) { U.CopyToClipboard(Session.SteamLobby.ToString()); Session.Notify(L.T("Lobby ID copied", "Skopiowano Lobby ID"), false); }
                bx += 196;
            }
            if (SteamNative.Ready)
            {
                ulong hostSid = host ? SteamNative.MySteamId : (Session.Players.TryGetValue(0, out var hp) ? hp.SteamId : 0);
                if (hostSid != 0 && GUI.Button(new Rect(bx, by, 190, 26), L.T("Copy host SteamID", "Kopiuj SteamID hosta"), _btn)) { U.CopyToClipboard(hostSid.ToString()); Session.Notify(L.T("SteamID copied", "Skopiowano SteamID"), false); }
                bx += 196;
                if (host && Session.SteamLobby != 0 && GUI.Button(new Rect(bx, by, 190, 26), L.T("Invite friends", "Zaproś znajomych"), _btn)) SteamNative.InviteOverlay(Session.SteamLobby);
                bx += 196;
            }
            if (Session.Transport is UdpTransport)
            {
                var info = Session.JoinInfo;
                GUI.Label(new Rect(bx, by + 4, w - (bx - x) - 140, 22), info, _small);
                if (GUI.Button(new Rect(x + w - 140, by, 130, 26), L.T("Copy IP:port", "Kopiuj IP:port"), _btn)) { U.CopyToClipboard(info.Split(',')[0].Trim()); Session.Notify(L.T("Address copied", "Skopiowano adres"), false); }
            }
            y += 74;

            // player list
            float colL = w * 0.55f;
            GUI.Box(new Rect(x, y, colL, h - 74 - 120), "", _box);
            GUI.Label(new Rect(x + 10, y + 6, colL, 22), L.T("PLAYERS", "GRACZE") + $" ({Session.Players.Count}/{Cfg.MaxPlayers.Value})", _label);
            float py = y + 34;
            foreach (var p in Session.Players.Values.OrderBy(p => p.Id))
            {
                var nm = p.Name + (p.IsHost ? "  [HOST]" : "") + (p.IsLocal ? L.T("  (you)", "  (Ty)") : "");
                float nx = x + 10;
                if (Badges.Of(p.Id, out var badge, out var badgeColor)) nx += ColoredLabel(new Rect(nx, py, 90, 22), "[" + badge + "] ", badgeColor, _label);
                GUI.Label(new Rect(nx, py, colL * 0.5f + 18 - (nx - x - 10), 22), nm, _label);
                if (!Session.HostInGame)
                {
                    var ready = p.IsHost || p.Ready;
                    var c = _small.normal.textColor; _small.normal.textColor = ready ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.6f, 0.3f);
                    GUI.Label(new Rect(x + 28 + colL * 0.5f, py + 2, 90, 22), ready ? L.T("READY", "GOTOWY") : L.T("not ready", "niegotowy"), _small);
                    _small.normal.textColor = c;
                }
                GUI.Label(new Rect(x + colL * 0.5f + 120, py + 2, 110, 22), PlaceOf(p) + (p.IsHost ? "" : $" · {p.PingMs}ms"), _small);
                if (host && !p.IsHost && GUI.Button(new Rect(x + colL - 70, py, 60, 22), L.T("Kick", "Wyrzuć"), _btn)) Session.Kick(p.Id, L.Pair("kicked by the host", "wyrzucony przez hosta"));
                py += 26;
            }

            // settings
            float rx = x + colL + 12, rw = w - colL - 12;
            GUI.Box(new Rect(rx, y, rw, h - 74 - 120), "", _box);
            float sy = y + 6; rx += 10; rw -= 20;
            GUI.Label(new Rect(rx, sy, rw, 22), L.T("GAME MODE", "TRYB GRY") + (host ? "" : L.T(" (set by the host)", " (ustawia host)")), _label); sy += 26;
            // Co-op or PvP (PvP: in the lobby only – the Granny player is chosen when the game starts); the day rules
            // (shared days / last survivor) apply to both
            bool pvp = Session.PvpOn;
            float hw = (rw - 6) / 2f;
            GUI.enabled = host && !Session.HostInGame;
            if (GUI.Button(new Rect(rx, sy, hw, 30), "Co-op", pvp ? _btn : _btnOn) && pvp) { Session.PvpOn = false; Session.PushSettings(); }
            if (GUI.Button(new Rect(rx + hw + 6, sy, hw, 30), L.T("PvP (player Granny)", "PvP (gracz-Granny)"), pvp ? _btnOn : _btn) && !pvp) { Session.PvpOn = true; Session.PushSettings(); }
            sy += 34;
            GUI.enabled = host && !Session.HostInGame; // the day rules cannot change during a game
            if (GUI.Button(new Rect(rx, sy, hw, 26), L.T("Shared days", "Wspólne dni"), Session.Mode == CoopMode.SharedDays ? _btnOn : _btn)) { Session.Mode = CoopMode.SharedDays; Session.PushSettings(); }
            if (GUI.Button(new Rect(rx + hw + 6, sy, hw, 26), L.T("Last survivor", "Ostatni żywy"), Session.Mode == CoopMode.LastSurvivor ? _btnOn : _btn)) { Session.Mode = CoopMode.LastSurvivor; Session.PushSettings(); }
            sy += 30;
            GUI.enabled = true;
            string rules = Session.Mode == CoopMode.SharedDays
                ? (pvp ? L.T("Granny catches anyone = everybody loses a day.", "Granny łapie kogokolwiek = wszyscy tracą dzień.") : L.T("Anyone caught = everybody loses a day.", "Złapanie dowolnego gracza = wszyscy tracą dzień."))
                : (pvp ? L.T("A caught survivor spectates; the day is lost when all survivors are caught.", "Złapany ocalały obserwuje innych; dzień przepada, gdy złapani są wszyscy ocalali.")
                       : L.T("A caught player spectates the others until the end of the day. The day is lost when everyone is caught.", "Złapany gracz obserwuje innych do końca dnia. Dzień przepada, gdy złapani są wszyscy."));
            if (pvp) rules = L.T($"Granny: {GrannyChoiceName()}. ", $"Granny: {GrannyChoiceName()}. ") + rules;
            GUI.Label(new Rect(rx, sy, rw, 44), rules, _small);
            sy += 46;
            if (GUI.Button(new Rect(rx, sy, rw, 30), L.T("Server configuration...", "Konfiguracja serwera..."), _btn)) _serverCfg = true;
            sy += 38;
            GUI.Label(new Rect(rx, sy, rw, 40), L.T("Difficulty and options are chosen by the host in the game menu (Play / Options) and sent to the clients.",
                "Poziom trudności i opcje wybiera host w menu gry (Graj / Opcje) – są przesyłane klientom."), _small);

            // bottom: chat + buttons
            float cy = y + h - 74 - 112;
            GUI.Box(new Rect(x, cy, colL, 108), "", _box);
            var lines = Session.ChatLog.Skip(Math.Max(0, Session.ChatLog.Count - 4)).ToList();
            float ly = cy + 4;
            foreach (var l in lines)
            {
                var c = _small.normal.textColor; _small.normal.textColor = l.c;
                GUI.Label(new Rect(x + 8, ly, colL - 16, 18), l.text, _small); _small.normal.textColor = c; ly += 17;
            }
            _chat = Field("lobbychat", new Rect(x + 8, cy + 76, colL - 96, 26), _chat, 200, out var enter);
            if (GUI.Button(new Rect(x + colL - 82, cy + 76, 74, 26), L.T("Send", "Wyślij"), _btn) || enter) { Session.SendChat(_chat); _chat = ""; }

            float bx2 = x + colL + 12, bw = w - colL - 12;
            var me = Session.Local;
            bool inGame = GameFlow.CurrentScene == Cfg.GameScene.Value || GameFlow.CurrentScene == Cfg.IntroScene.Value;
            if (!host && me != null && (inGame || Session.HostInGame))
                GUI.Label(new Rect(bx2, cy, bw, 34), L.T("GAME IN PROGRESS", "GRA TRWA"), _center);
            else if (!host && me != null)
            {
                if (GUI.Button(new Rect(bx2, cy, bw, 34), me.Ready ? L.T("READY ✓ (click to cancel)", "GOTOWY ✓ (kliknij, aby cofnąć)") : L.T("I'M READY", "ZGŁOŚ GOTOWOŚĆ"), me.Ready ? _btnOn : _btn)) Session.SetReady(!me.Ready);
            }
            else if (host)
            {
                GUI.enabled = !inGame && Session.AllReady;
                var label = inGame ? L.T("GAME IN PROGRESS", "GRA TRWA") : (Session.AllReady ? L.T("START GAME", "ROZPOCZNIJ GRĘ") : L.T("Waiting for players to be ready...", "Czekam na gotowość graczy..."));
                if (GUI.Button(new Rect(bx2, cy, bw, 34), label, _btnOn)) { GameFlow.HostStartGame(); Toggle(); }
                GUI.enabled = true;
            }
            if (GUI.Button(new Rect(bx2, cy + 40, bw, 30), inGame ? (host ? L.T("End game & close lobby (menu)", "Zakończ grę i zamknij lobby (menu)") : L.T("Leave game (to menu)", "Opuść grę (wyjście do menu)")) : (host ? L.T("Close lobby", "Zamknij lobby") : L.T("Leave lobby", "Opuść lobby")), _btn))
            { Session.Leave(); Toggle(); }
            if (!inGame) GUI.Label(new Rect(bx2, cy + 76, bw, 30), host ? L.T("Choose difficulty in Play/Options, then START GAME here.", "Wybierz trudność w Graj/Opcje, potem ROZPOCZNIJ GRĘ tutaj.") : L.T("The host starts the game.", "Grę rozpocznie host."), _small);
        }

        static Texture2D _mic;
        /// <summary>Microphone glyph (white, alpha), drawn in code.</summary>
        static Texture2D MicTex()
        {
            if (_mic != null) return _mic;
            const int w = 32, h = 48;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = x + 0.5f, v = h - (y + 0.5f); // v = 0 at the top
                    float a = 0f;
                    // capsule: x 10..22, v 2..28
                    float cx = Mathf.Clamp(u, 16f, 16f), cy = Mathf.Clamp(v, 8f, 22f);
                    float dc = Mathf.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy));
                    a = Mathf.Max(a, Mathf.Clamp01(6.5f - dc));
                    // holder arc (lower half of a ring around the capsule)
                    float dr = Mathf.Sqrt((u - 16f) * (u - 16f) + (v - 22f) * (v - 22f));
                    if (v >= 20f) a = Mathf.Max(a, Mathf.Clamp01(1.6f - Mathf.Abs(dr - 11f)));
                    // stand + base
                    if (Mathf.Abs(u - 16f) < 1.8f && v > 32f && v < 42f) a = Mathf.Max(a, 1f);
                    if (Mathf.Abs(u - 16f) < 8f && v >= 41f && v < 44.5f) a = Mathf.Max(a, 1f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply(); t.hideFlags = HideFlags.HideAndDontSave;
            return _mic = t;
        }

        /// <summary>HUD microphone: green while talking, grey when only switched on; the bar shows how loud you are.</summary>
        static void DrawMicIcon(Rect r, bool talking, float level)
        {
            GUI.Box(r, "", _box);
            var gc = GUI.color;
            var icon = new Rect(r.x + 6, r.y + 8, 32, 48);
            GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(icon.x + 1, icon.y + 1, icon.width, icon.height), MicTex());
            GUI.color = talking ? new Color(0.4f, 1f, 0.5f) : new Color(0.75f, 0.75f, 0.78f);
            GUI.DrawTexture(icon, MicTex());
            var bar = new Rect(r.xMax - 12, r.y + 8, 6, r.height - 16);
            GUI.color = new Color(0f, 0f, 0f, 0.7f); GUI.DrawTexture(bar, _white);
            float f = Mathf.Clamp01(level);
            GUI.color = f > 0.9f ? new Color(1f, 0.35f, 0.3f) : (talking ? new Color(0.4f, 1f, 0.5f) : new Color(0.75f, 0.75f, 0.78f));
            GUI.DrawTexture(new Rect(bar.x, bar.yMax - bar.height * f, bar.width, bar.height * f), _white);
            GUI.color = gc;
        }

        static string VoiceHint()
        {
            if (!Cfg.VoiceEnabled.Value) return "";
            return Cfg.VoiceModeCfg.Value switch
            {
                VoiceMode.Toggle => $" · {KeyName(Cfg.VoiceKey.Value)}: {L.T("mic on/off", "mikrofon wł./wył.")}",
                VoiceMode.Always => L.T(" · voice: always on", " · głos: zawsze włączony"),
                _ => $" · {KeyName(Cfg.VoiceKey.Value)}: {L.T("voice", "głos")}",
            };
        }

        // ---------------- settings page (gear) ----------------
        static bool _settings;
        static System.Collections.Generic.List<string> _mics = new();
        static BepInEx.Configuration.ConfigEntry<KeyCode> _capture;
        static string _drag;

        /// <summary>A key binding is being captured (the next key press is the new key).</summary>
        public static bool CapturingKey => _capture != null;

        static void OpenSettings()
        {
            _settings = true; _focusId = null; _resList = null;
            try { _mics = VoiceChat.Devices(); } catch (Exception e) { _mics = new(); Log.Warn("Microphone list: " + e.Message); }
        }

        static Vector2 _setScroll; static float _setH = 1200f; static bool _barDrag;

        /// <summary>A vertical scroll bar drawn by hand: the thumb, a click or a drag on the track.</summary>
        static void ScrollBar(Rect track, float view, ref float scroll, float content)
        {
            if (content <= view + 1f) { _barDrag = false; return; }
            float thumbH = Mathf.Max(30f, track.height * view / content), max = content - view;
            float ty = track.y + (track.height - thumbH) * Mathf.Clamp01(scroll / max);
            var gc = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.08f); GUI.DrawTexture(track, _white);
            GUI.color = new Color(0.85f, 0.85f, 0.9f, _barDrag ? 0.7f : 0.4f); GUI.DrawTexture(new Rect(track.x, ty, track.width, thumbH), _white);
            GUI.color = gc;
            var e = Event.current;
            if (e == null) return;
            var hit = new Rect(track.x - 6, track.y, track.width + 12, track.height);
            if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition)) { _barDrag = true; e.Use(); }
            else if (e.type == EventType.MouseUp) _barDrag = false;
            if (_barDrag && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
            {
                float t = Mathf.Clamp01((e.mousePosition.y - track.y - thumbH / 2f) / Mathf.Max(1f, track.height - thumbH));
                scroll = t * max;
                e.Use();
            }
        }

        static void CloseSettings() { _settings = false; _capture = null; _drag = null; VoiceChat.Testing = false; if (_characters) CloseCharacters(); }

        static Texture2D _gear;
        static Texture2D Gear()
        {
            if (_gear != null) return _gear;
            const int n = 32;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = x + 0.5f - n / 2f, v = y + 0.5f - n / 2f;
                    float r = Mathf.Sqrt(u * u + v * v), a = Mathf.Atan2(v, u);
                    float outer = Mathf.Cos(a * 8f) > 0.2f ? 14f : 10.5f;
                    float alpha = Mathf.Clamp01(outer - r + 0.5f) * Mathf.Clamp01(r - 4.5f + 0.5f);
                    t.SetPixel(x, y, new Color(0.85f, 0.85f, 0.88f, alpha));
                }
            t.Apply(); t.hideFlags = HideFlags.HideAndDontSave;
            return _gear = t;
        }

        static bool GearButton(Rect r, bool selected)
        {
            if (selected) GUI.DrawTexture(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), _accent);
            bool clicked = GUI.Button(r, "", _btn);
            GUI.DrawTexture(new Rect(r.x + (r.width - 22) / 2, r.y + (r.height - 22) / 2, 22, 22), Gear());
            return clicked;
        }

        static string KeyName(KeyCode k) => k switch
        {
            KeyCode.None => L.T("none", "brak"),
            KeyCode.Mouse2 => L.T("Middle mouse", "Środkowy myszy"),
            KeyCode.Mouse3 => L.T("Mouse 4", "Mysz 4"),
            KeyCode.Mouse4 => L.T("Mouse 5", "Mysz 5"),
            KeyCode.Mouse5 => L.T("Mouse 6", "Mysz 6"),
            KeyCode.Mouse6 => L.T("Mouse 7", "Mysz 7"),
            _ => k.ToString(),
        };

        static bool Toggle(Rect r, bool on, string label)
        {
            if (GUI.Button(r, (on ? "[x] " : "[  ] ") + label, on ? _btnOn : _btn)) on = !on;
            return on;
        }

        /// <summary>Key binding row: click, then press a key (or a middle / side mouse button). Esc cancels, Backspace clears.</summary>
        static void KeyRow(float x, ref float y, float w, string label, BepInEx.Configuration.ConfigEntry<KeyCode> entry, bool canClear = false, bool anyMouse = false)
        {
            GUI.Label(new Rect(x, y + 4, w - 170, 22), label, _small);
            bool capturing = _capture == entry;
            var r = new Rect(x + w - 164, y, 164, 26);
            if (capturing)
            {
                var e = Event.current;
                if (e != null && e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
                {
                    if (e.keyCode == KeyCode.Escape) _capture = null;
                    else if (e.keyCode == KeyCode.Backspace && canClear) { entry.Value = KeyCode.None; _capture = null; }
                    else { entry.Value = e.keyCode; _capture = null; }
                    e.Use();
                }
                else if (e != null && e.type == EventType.MouseDown && e.button >= (anyMouse ? 0 : 2) && e.button <= 6)
                {
                    entry.Value = KeyCode.Mouse0 + e.button; _capture = null; e.Use();
                }
            }
            if (GUI.Button(r, capturing ? L.T("press a key... (Esc)", "naciśnij klawisz... (Esc)") : KeyName(entry.Value), capturing ? _btnOn : _btn))
                _capture = capturing ? null : entry;
            y += 30;
        }

        /// <summary>Slider drawn by hand (click / drag on the bar).</summary>
        static float Slider(string id, Rect r, float value, float min, float max, float step)
        {
            var e = Event.current;
            if (e != null)
            {
                if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { _drag = id; e.Use(); }
                else if (e.type == EventType.MouseUp && _drag == id) { _drag = null; e.Use(); }
                if (_drag == id && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
                {
                    float t = Mathf.Clamp01((e.mousePosition.x - r.x) / r.width);
                    value = min + t * (max - min);
                    if (step > 0) value = Mathf.Round(value / step) * step;
                    value = Mathf.Clamp(value, min, max);
                    e.Use();
                }
            }
            float f = Mathf.InverseLerp(min, max, value);
            var bar = new Rect(r.x, r.y + r.height / 2 - 4, r.width, 8);
            GUI.DrawTexture(bar, _field.normal.background);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * f, bar.height), _accent);
            GUI.DrawTexture(new Rect(r.x + r.width * f - 5, r.y + 2, 10, r.height - 4), _white);
            return value;
        }

        static void SliderRow(string id, float x, ref float y, float w, string label, BepInEx.Configuration.ConfigEntry<float> entry, float min, float max, float step, string shown)
        {
            GUI.Label(new Rect(x, y + 3, 150, 22), label, _small);
            var v = Slider(id, new Rect(x + 150, y, w - 210, 24), entry.Value, min, max, step);
            if (Mathf.Abs(v - entry.Value) > 0.0001f) entry.Value = v;
            GUI.Label(new Rect(x + w - 54, y + 3, 54, 22), shown, _small);
            y += 30;
        }

        // ---------------- character choice (Settings → Change) ----------------
        static bool _characters;
        static int _charRow;

        // 4 columns x 2 rows; more characters: the mouse wheel or the arrows on the right scroll by rows
        static void DrawCharacters(float x, float y, float w, float h)
        {
            GUI.Box(new Rect(x, y, w, h - 44), "", _box);
            GUI.Label(new Rect(x + 12, y + 8, w - 24, 24), L.T("CHOOSE YOUR CHARACTER", "WYBIERZ POSTAĆ"), _label);
            var list = PlayerModels.Available;
            const int cols = 4, rows = 2;
            int totalRows = Mathf.Max(1, (list.Count + cols - 1) / cols), maxRow = Mathf.Max(0, totalRows - rows);
            const float gap = 10f, barW = 30f;
            float gx = x + 12, gy = y + 40, gw = w - 24 - barW - 8, gh = h - 44 - 40 - 76;
            try
            {
                var ev = Event.current;
                if (ev != null && ev.type == EventType.ScrollWheel && maxRow > 0)
                {
                    _charRow += ev.delta.y > 0 ? 1 : -1;
                    ev.Use();
                }
            }
            catch { }
            _charRow = Mathf.Clamp(_charRow, 0, maxRow);
            float cardW = (gw - (cols - 1) * gap) / cols, cardH = (gh - (rows - 1) * gap) / rows;
            if (list.Count == 0) GUI.Label(new Rect(gx, gy, gw, 40), L.T("No models found in the models folder.", "Brak modeli w folderze models."), _small);
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int idx = (_charRow + r) * cols + c;
                    if (idx >= list.Count) break;
                    var name = list[idx];
                    shown.Add(name);
                    bool sel = string.Equals(name, Cfg.Character.Value, StringComparison.OrdinalIgnoreCase);
                    var card = new Rect(gx + c * (cardW + gap), gy + r * (cardH + gap), cardW, cardH);
                    if (sel) GUI.DrawTexture(new Rect(card.x - 3, card.y - 3, card.width + 6, card.height + 6), _accent);
                    GUI.Box(card, "", _win);
                    var img = new Rect(card.x + 4, card.y + 4, card.width - 8, card.height - 34);
                    var tex = PlayerModels.PreviewTexture(name);
                    if (tex != null)
                    {
                        // fit the 2:3 picture into the card
                        float iw = Mathf.Min(img.width, img.height * 2f / 3f), ih = iw * 1.5f;
                        GUI.DrawTexture(new Rect(img.x + (img.width - iw) / 2f, img.y + (img.height - ih) / 2f, iw, ih), tex);
                    }
                    else GUI.Label(img, L.T("no preview", "brak podglądu"), _center);
                    GUI.Label(new Rect(card.x + 4, card.yMax - 30, card.width - 8, 24), PlayerModels.Title(name), sel ? _tag : _center);
                    // the whole card is the button
                    if (GUI.Button(card, "", _clear) && !sel)
                    {
                        Cfg.Character.Value = name;
                        if (Session.Active) Session.SendModel();
                    }
                }
            // only the previews on screen are rendered
            PlayerModels.UpdatePreviews(shown);
            // scroll bar: arrows and the position
            float bx = x + w - 12 - barW;
            GUI.enabled = _charRow > 0;
            if (GUI.Button(new Rect(bx, gy, barW, 30), "▲", _btn)) _charRow--;
            GUI.enabled = _charRow < maxRow;
            if (GUI.Button(new Rect(bx, gy + gh - 30, barW, 30), "▼", _btn)) _charRow++;
            GUI.enabled = true;
            var track = new Rect(bx + barW / 2 - 3, gy + 36, 6, gh - 72);
            { var gc0 = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.15f); GUI.DrawTexture(track, _white); GUI.color = gc0; }
            float frac = Mathf.Min(1f, rows / (float)totalRows), thumbH = Mathf.Max(18f, track.height * frac);
            float thumbY = track.y + (maxRow > 0 ? (track.height - thumbH) * _charRow / maxRow : 0f);
            GUI.DrawTexture(new Rect(track.x - 3, thumbY, 12, thumbH), _accent);
            float cy = gy, cardH2 = gh;
            GUI.Label(new Rect(x + 12, cy + cardH2 + 8, w - 32, 60), L.T(
                "Custom characters: put a .gmdl file into BepInEx\\plugins\\GrannyMP\\models (made from an FBX with a Mixamo skeleton with the Model Tool: Tools\\GrannyModelTool.exe). Other players need the same file to see it.",
                "Własne postacie: wrzuć plik .gmdl do BepInEx\\plugins\\GrannyMP\\models (zrobiony z FBX ze szkieletem Mixamo programem Tools\\GrannyModelTool.exe). Inni gracze muszą mieć ten sam plik, żeby ją widzieć."), _small);
            if (GUI.Button(new Rect(x + w - 160, y + h - 36, 160, 32), L.T("Back", "Wróć"), _btn)) CloseCharacters();
        }

        static void CloseCharacters() { _characters = false; PlayerModels.ClearPreviews(); }

        // ---- the host's debug panel (DebugMenu, F9) ----
        static void DrawDebug()
        {
            float w = 380, h = 190;
            var r = new Rect(SW - w - 20, 80, w, h);
            GUI.Box(r, "", _win);
            float x = r.x + 16, y = r.y + 12, cw = w - 32;
            GUI.Label(new Rect(x, y, cw, 26), "DEBUG (" + Cfg.DebugKey.Value + ")", _label); y += 34;
            if (GUI.Button(new Rect(x, y, cw, 32), L.T("Spawn every item here", "Zespawnuj wszystkie przedmioty tutaj"), _btn)) DebugMenu.SpawnAllItems();
            y += 40;
            if (GUI.Button(new Rect(x, y, cw, 32), Session.GodMode ? L.T("God mode for everybody: ON", "Tryb boga dla wszystkich: WŁ.") : L.T("God mode for everybody: OFF", "Tryb boga dla wszystkich: WYŁ."),
                           Session.GodMode ? _btnOn : _btn)) DebugMenu.SetGodMode(!Session.GodMode);
            y += 40;
            if (GUI.Button(new Rect(x, y, cw, 30), L.T("Close", "Zamknij"), _btn)) DebugMenu.Toggle(false);
        }

        // ---- screen: resolution and window mode, chosen with < > and applied with the button ----
        static List<(int w, int h)> _resList; static (int w, int h) _pendRes; static int _pendMode;

        static void DisplayRows(float lx, ref float ly, float lw)
        {
            if (_resList == null)
            {
                _resList = Display.Resolutions();
                _pendRes = Display.Current; _pendMode = Display.CurrentMode;
                if (Array.IndexOf(Display.Modes, _pendMode) < 0) _pendMode = Display.Borderless;
            }
            bool ok = Display.Available;
            float vw = lw - 150; // < value >, Apply below
            GUI.enabled = ok;
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Resolution:", "Rozdzielczość:"), _small);
            {
                int at = Mathf.Max(0, _resList.IndexOf(_pendRes)), dir = 0;
                if (GUI.Button(new Rect(lx + 150, ly, 28, 26), "<", _btn)) dir = -1;
                if (GUI.Button(new Rect(lx + 150 + vw - 28, ly, 28, 26), ">", _btn)) dir = 1;
                if (dir != 0 && _resList.Count > 0) _pendRes = _resList[Mathf.Clamp(at + dir, 0, _resList.Count - 1)];
                GUI.Label(new Rect(lx + 182, ly + 4, vw - 64, 22), $"{_pendRes.w} × {_pendRes.h}", _center);
            }
            ly += 32;
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Window mode:", "Tryb okna:"), _small);
            {
                int at = Mathf.Max(0, Array.IndexOf(Display.Modes, _pendMode)), dir = 0, n = Display.Modes.Length;
                if (GUI.Button(new Rect(lx + 150, ly, 28, 26), "<", _btn)) dir = -1;
                if (GUI.Button(new Rect(lx + 150 + vw - 28, ly, 28, 26), ">", _btn)) dir = 1;
                if (dir != 0) _pendMode = Display.Modes[((at + dir) % n + n) % n];
                GUI.Label(new Rect(lx + 182, ly + 4, vw - 64, 22), Display.ModeName(_pendMode), _center);
            }
            bool changed = _pendRes != Display.Current || _pendMode != Display.CurrentMode;
            ly += 32;
            GUI.enabled = ok && changed;
            if (GUI.Button(new Rect(lx + 150, ly, vw, 26), L.T("Apply", "Zastosuj"), changed ? _btnOn : _btn))
                Display.Apply(_pendRes, _pendMode, true);
            GUI.enabled = true;
            ly += 32;
        }

        static void DrawSettings(float x, float y, float w, float h)
        {
            if (_characters) { DrawCharacters(x, y, w, h); return; }
            // one panel, scrolled with the mouse wheel / the bar on the right
            GUI.Box(new Rect(x, y, w, h - 44), "", _box);
            float cw = w - 22;
            // GUI.BeginScrollView is stripped from this game: the view is clipped by hand (GUIClip, what it uses inside)
            var clip = new Rect(x + 2, y + 2, w - 18, h - 48);
            float maxScroll = Mathf.Max(0f, _setH - clip.height);
            var ev = Event.current;
            if (ev != null && ev.type == EventType.ScrollWheel && new Rect(x, y, w, h - 44).Contains(ev.mousePosition))
            { _setScroll.y += ev.delta.y * 30f; ev.Use(); }
            ScrollBar(new Rect(x + w - 14, y + 4, 10, h - 52), clip.height, ref _setScroll.y, _setH);
            _setScroll.y = Mathf.Clamp(_setScroll.y, 0f, maxScroll);
            UnityEngine.GUIClip.Internal_Push(clip, new Vector2(0f, -Mathf.Round(_setScroll.y)), Vector2.zero, false);
            try
            {
            float lw = Mathf.Min(cw - 28, 680), lx = (cw - lw) / 2f, ly = 10;
            GUI.Label(new Rect(lx, ly, lw, 24), L.T("SETTINGS", "USTAWIENIA"), _label); ly += 30;
            // your name (in the lobby and above your character)
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Your name:", "Twój nick:"), _small);
            var nn = Field("name", new Rect(lx + 150, ly, lw - 150, 28), _name, 24, out _);
            if (nn != _name) { _name = nn; Cfg.PlayerName.Value = nn; }
            if (Session.Active) GUI.Label(new Rect(lx + 150, ly + 30, lw - 150, 20), L.T("Used from the next lobby.", "Zmieni się od następnego lobby."), _small);
            ly += Session.Active ? 54 : 36;
            GUI.Label(new Rect(lx, ly + 6, 150, 22), L.T("Language:", "Język:"), _small);
            if (FlagButton(new Rect(lx + 150, ly, 48, 30), FlagUK(), !L.PL)) Cfg.Lang.Value = Language.English;
            if (FlagButton(new Rect(lx + 206, ly, 48, 30), FlagPL(), L.PL)) Cfg.Lang.Value = Language.Polish;
            ly += 38;
            // interface size: Auto (from the screen resolution) or a fixed size
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Interface size:", "Rozmiar interfejsu:"), _small);
            {
                float[] sizes = { 0f, 0.75f, 1f, 1.25f, 1.5f, 2f, 2.5f, 3f };
                int at = 0; for (int i = 1; i < sizes.Length; i++) if (Mathf.Abs(Cfg.UiScale.Value - sizes[i]) < 0.01f) at = i;
                int dir = 0;
                if (GUI.Button(new Rect(lx + 150, ly, 28, 26), "<", _btn)) dir = -1;
                if (GUI.Button(new Rect(lx + lw - 28, ly, 28, 26), ">", _btn)) dir = 1;
                if (dir != 0) Cfg.UiScale.Value = sizes[((at + dir) % sizes.Length + sizes.Length) % sizes.Length];
                string shown = at == 0 ? L.T($"Auto ({Mathf.RoundToInt(AutoScale * 100)}%)", $"Auto ({Mathf.RoundToInt(AutoScale * 100)}%)") : Mathf.RoundToInt(sizes[at] * 100) + "%";
                GUI.Label(new Rect(lx + 182, ly + 4, lw - 214, 22), shown, _center);
            }
            ly += 32;
            // field of view (the host can lock it in the server configuration)
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Field of view:", "Pole widzenia (FOV):"), _small);
            {
                float[] fovs = { 0f, 60f, 65f, 70f, 75f, 80f, 85f, 90f, 95f, 100f, 105f, 110f, 115f, 120f };
                int at = 0; for (int i = 1; i < fovs.Length; i++) if (Mathf.Abs(Cfg.Fov.Value - fovs[i]) < 0.5f) at = i;
                int dir = 0;
                if (GUI.Button(new Rect(lx + 150, ly, 28, 26), "<", _btn)) dir = -1;
                if (GUI.Button(new Rect(lx + lw - 28, ly, 28, 26), ">", _btn)) dir = 1;
                if (dir != 0) Cfg.Fov.Value = fovs[((at + dir) % fovs.Length + fovs.Length) % fovs.Length];
                string shown = at == 0 ? L.T("the game's", "z gry") : Mathf.RoundToInt(fovs[at]) + "°";
                if (Session.Active && Session.LockFov) shown += L.T(" (locked by the host)", " (zablokowane przez hosta)");
                GUI.Label(new Rect(lx + 182, ly + 4, lw - 214, 22), shown, _center);
            }
            ly += 32;
            DisplayRows(lx, ref ly, lw);
            var skip = Toggle(new Rect(lx, ly, lw, 26), Cfg.SkipSplash.Value, L.T("Skip the DVloper intro at game start", "Pomijaj intro DVloper przy starcie gry"));
            if (skip != Cfg.SkipSplash.Value) Cfg.SkipSplash.Value = skip;
            ly += 30;
            var tags = Toggle(new Rect(lx, ly, lw, 26), Cfg.ShowNametags.Value, L.T("Show names above players", "Pokazuj nicki nad graczami"));
            if (tags != Cfg.ShowNametags.Value) Cfg.ShowNametags.Value = tags;
            ly += 30;
            var dbg = Toggle(new Rect(lx, ly, lw, 26), Cfg.DebugLog.Value, L.T("Advanced logs (for bug reports)", "Zaawansowane logi (do zgłaszania błędów)"));
            if (dbg != Cfg.DebugLog.Value) Cfg.DebugLog.Value = dbg;
            ly += 30;
            // how the other players see you
            GUI.Label(new Rect(lx, ly + 4, 150, 22), L.T("Your character:", "Twoja postać:"), _small);
            GUI.Label(new Rect(lx + 150, ly + 4, lw - 270, 22), PlayerModels.Title(Cfg.Character.Value), _label);
            if (GUI.Button(new Rect(lx + lw - 110, ly, 110, 26), L.T("Change", "Zmień"), _btn)) _characters = true;
            ly += 36;
            GUI.Label(new Rect(lx, ly, lw, 24), L.T("CONTROLS", "STEROWANIE"), _label); ly += 28;
            KeyRow(lx, ref ly, lw, L.T("Multiplayer window", "Okno multiplayer"), Cfg.ToggleKey);
            KeyRow(lx, ref ly, lw, L.T("Chat", "Czat"), Cfg.ChatKey);
            KeyRow(lx, ref ly, lw, Cfg.VoiceModeCfg.Value == VoiceMode.Toggle ? L.T("Microphone on/off", "Mikrofon wł./wył.") : L.T("Push to talk", "Naciśnij, aby mówić"), Cfg.VoiceKey);
            GUI.Label(new Rect(lx, ly + 2, lw, 40), L.T("Click a key, then press the new one (middle / side mouse buttons work too). Esc cancels.",
                "Kliknij klawisz i naciśnij nowy (działają też środkowy / boczne przyciski myszy). Esc anuluje."), _small);
            ly += 44;
            GUI.Label(new Rect(lx, ly, lw, 24), L.T("PvP – PLAYING GRANNY", "PvP – GRA JAKO GRANNY"), _label); ly += 28;
            KeyRow(lx, ref ly, lw, L.T("Grab a survivor", "Złap ocalałego"), Cfg.PvpGrabKey, false, true);
            KeyRow(lx, ref ly, lw, L.T("Say a line / laugh", "Mów / śmiej się"), Cfg.PvpVoiceKey, false, true);
            KeyRow(lx, ref ly, lw, L.T("Put a bear trap down", "Postaw pułapkę"), Cfg.PvpTrapKey, false, true);
            GUI.Label(new Rect(lx, ly + 2, lw, 22), L.T("Here the left / right mouse button can be chosen too.", "Tu można też wybrać lewy / prawy przycisk myszy."), _small);
            ly += 34;

            // ---- voice chat ----
            float rx = lx, ry = ly, rw = lw;
            GUI.Label(new Rect(rx, ry, rw, 24), L.T("VOICE CHAT", "CZAT GŁOSOWY"), _label); ry += 30;
            var en = Toggle(new Rect(rx, ry, rw, 26), Cfg.VoiceEnabled.Value, L.T("Voice chat enabled", "Czat głosowy włączony"));
            if (en != Cfg.VoiceEnabled.Value) { Cfg.VoiceEnabled.Value = en; if (!en) VoiceChat.Testing = false; }
            ry += 32;
            GUI.enabled = en;
            GUI.Label(new Rect(rx, ry + 4, 90, 22), L.T("Talk:", "Mówienie:"), _small);
            float mw = (rw - 90 - 8) / 3;
            var mode = Cfg.VoiceModeCfg.Value;
            if (GUI.Button(new Rect(rx + 90, ry, mw, 26), L.T("Push to talk", "Przytrzymaj"), mode == VoiceMode.PushToTalk ? _btnOn : _btn)) Cfg.VoiceModeCfg.Value = VoiceMode.PushToTalk;
            if (GUI.Button(new Rect(rx + 94 + mw, ry, mw, 26), L.T("Toggle", "Przełącz"), mode == VoiceMode.Toggle ? _btnOn : _btn)) Cfg.VoiceModeCfg.Value = VoiceMode.Toggle;
            if (GUI.Button(new Rect(rx + 98 + 2 * mw, ry, mw, 26), L.T("Always", "Zawsze"), mode == VoiceMode.Always ? _btnOn : _btn)) Cfg.VoiceModeCfg.Value = VoiceMode.Always;
            ry += 34;
            // microphone choice
            GUI.Label(new Rect(rx, ry + 4, 90, 22), L.T("Microphone:", "Mikrofon:"), _small);
            var cur = Cfg.MicDevice.Value ?? "";
            int ci = _mics.IndexOf(cur); // -1 = Windows default (or a device that is not connected now)
            string curName = cur == "" ? L.T("Windows default", "Domyślny Windows") : cur + (ci < 0 ? L.T(" (not found)", " (nie znaleziono)") : "");
            int step = 0;
            if (GUI.Button(new Rect(rx + 90, ry, 28, 26), "<", _btn)) step = -1;
            if (GUI.Button(new Rect(rx + rw - 28, ry, 28, 26), ">", _btn)) step = 1;
            if (step != 0)
            {
                bool next = step > 0;
                int count = _mics.Count + 1; // 0 = default
                int at = cur == "" ? 0 : ci + 1;
                at = ((at + (next ? 1 : -1)) % count + count) % count;
                Cfg.MicDevice.Value = at == 0 ? "" : _mics[at - 1];
            }
            GUI.Label(new Rect(rx + 122, ry + 4, rw - 154, 22), curName, _small);
            ry += 32;
            SliderRow("micgain", rx, ref ry, rw, L.T("Microphone volume", "Głośność mikrofonu"), Cfg.MicGain, 0.1f, 4f, 0.05f, Mathf.RoundToInt(Cfg.MicGain.Value * 100) + "%");
            SliderRow("voicevol", rx, ref ry, rw, L.T("Voice chat volume", "Głośność czatu głosowego"), Cfg.VoiceVolume, 0f, 3f, 0.05f, Mathf.RoundToInt(Cfg.VoiceVolume.Value * 100) + "%");
            if (mode == VoiceMode.Always)
                SliderRow("micthr", rx, ref ry, rw, L.T("Activation level", "Próg aktywacji"), Cfg.MicThreshold, 0f, 0.25f, 0.005f, Mathf.RoundToInt(Cfg.MicThreshold.Value * 400) + "%");
            // microphone test
            ry += 4;
            if (GUI.Button(new Rect(rx, ry, 150, 28), VoiceChat.Testing ? L.T("Stop test", "Zakończ test") : L.T("Test microphone", "Test mikrofonu"), VoiceChat.Testing ? _btnOn : _btn))
                VoiceChat.Testing = !VoiceChat.Testing;
            var meter = new Rect(rx + 160, ry + 8, rw - 160, 12);
            GUI.DrawTexture(meter, _field.normal.background);
            var gc = GUI.color;
            GUI.color = VoiceChat.Level > 0.9f ? new Color(1f, 0.35f, 0.3f) : new Color(0.35f, 0.9f, 0.45f);
            GUI.DrawTexture(new Rect(meter.x, meter.y, meter.width * VoiceChat.Level, meter.height), _white);
            GUI.color = gc;
            if (mode == VoiceMode.Always)
                GUI.DrawTexture(new Rect(meter.x + meter.width * Mathf.Clamp01(Cfg.MicThreshold.Value * 4f) - 1, meter.y - 4, 2, meter.height + 8), _accent);
            ry += 34;
            string info = VoiceChat.Error ?? (VoiceChat.Testing
                ? L.T("Speak – you should hear yourself. Watch the bar: it should not turn red.", "Mów – powinieneś słyszeć siebie. Pasek nie powinien robić się czerwony.")
                : L.T("Works with Steam and Direct IP. Other voices come from the player's direction and fade with distance.", "Działa przez Steam i Direct IP. Głosy innych słychać z ich kierunku i cichną z odległością."));
            if (VoiceChat.Error != null) { var c = _small.normal.textColor; _small.normal.textColor = new Color(1f, 0.5f, 0.45f); GUI.Label(new Rect(rx, ry, rw, 54), info, _small); _small.normal.textColor = c; }
            else GUI.Label(new Rect(rx, ry, rw, 54), info, _small);
            GUI.enabled = true;
            _setH = ry + 64;
            }
            finally { UnityEngine.GUIClip.Internal_Pop(); }

            if (GUI.Button(new Rect(x + w - 160, y + h - 36, 160, 32), L.T("Back", "Wróć"), _btn)) CloseSettings();
            if (GUI.Button(new Rect(x, y + h - 36, 160, 32), L.T("Refresh devices", "Odśwież urządzenia"), _btn)) OpenSettings();
        }

        static string ModeName(CoopMode m)
        {
            string rules = m == CoopMode.SharedDays ? L.T("shared days", "wspólne dni") : L.T("last survivor", "ostatni żywy");
            if (!Session.PvpOn) return rules;
            return (Pvp.Active ? $"PvP – Granny: {Session.NameOf(Session.GrannyId)}" : "PvP") + " · " + rules;
        }
    }

    /// <summary>Team badges shown with the player's name (by SteamID): DEV in yellow, Helper in green.</summary>
    internal static class Badges
    {
        static readonly HashSet<ulong> Dev = new() { 76561198966183975UL, 76561198328319838UL };
        static readonly HashSet<ulong> Helper = new() { 76561198312981915UL };
        static readonly Color DevColor = new Color(1f, 0.85f, 0.1f), HelperColor = new Color(0.3f, 0.95f, 0.4f);

        /// <summary>This copy of the game runs on a DEV's Steam account.</summary>
        public static bool LocalIsDev => SteamNative.Ready && Dev.Contains(SteamNative.MySteamId);

        public static bool Of(byte id, out string badge, out Color color)
        {
            badge = null; color = Color.white;
            if (!Session.Players.TryGetValue(id, out var p)) return false;
            ulong sid = p.SteamId;
            if (sid == 0 && p.IsLocal && SteamNative.Ready) sid = SteamNative.MySteamId;
            if (Dev.Contains(sid)) { badge = "DEV"; color = DevColor; return true; }
            if (Helper.Contains(sid)) { badge = "Helper"; color = HelperColor; return true; }
            return false;
        }
    }
}
