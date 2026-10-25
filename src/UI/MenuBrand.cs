using System;
using UnityEngine;
using UnityEngine.UI;

namespace GrannyMP
{
    /// <summary>
    /// Main menu: "Multiplayer" under the game's GRANNY logo (MenuButtonsHolder/GrannyText) – the picture next to the mod
    /// (multiplayer-logo.png), otherwise a uGUI text in the menu's own font. It sits next to the logo (same parent),
    /// sized from the logo's corners on the screen.
    /// </summary>
    internal static class MenuBrand
    {
        static GameObject _label; static float _next;
        static Texture2D _logo; static bool _logoTried;

        static Texture2D Logo()
        {
            if (_logoTried) return _logo;
            _logoTried = true;
            try
            {
                _logo = Png.Asset("multiplayer-logo.png");
                if (_logo != null) { _logo.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(_logo); }
            }
            catch (Exception e) { Log.Debug("menu logo: " + e.Message); }
            return _logo;
        }

        public static void Update()
        {
            if (GameFlow.CurrentScene != Cfg.MenuScene.Value) { _label = null; return; }
            if (U.Alive(_label) || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            try
            {
                var logo = GameObject.Find("MenuButtonsHolder/GrannyText");
                if (logo == null) return;
                var lrt = logo.GetComponent<RectTransform>();
                var parent = logo.transform.parent;
                if (lrt == null || parent == null) return;
                var font = MenuFont(parent);
                if (font == null && Logo() == null) { _next = Time.unscaledTime + 5f; return; }
                // the logo's rect is 100 x 100 and scaled – its corners show its real size on the screen
                var wc = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                lrt.GetWorldCorners(wc);
                Vector3 bl = parent.InverseTransformPoint(wc[0]), tl = parent.InverseTransformPoint(wc[1]), tr = parent.InverseTransformPoint(wc[2]);
                float lw = Mathf.Abs(tr.x - tl.x), lh = Mathf.Abs(tl.y - bl.y);
                if (lw < 1f || lh < 1f) return;
                float cy = (tl.y + bl.y) * 0.5f;
                // the GRANNY picture keeps its aspect inside the square rect: only its middle band is visible
                try
                {
                    var img = logo.GetComponent<Image>();
                    var sp = img != null ? img.sprite : null;
                    if (sp != null && img.preserveAspect)
                    {
                        var r = sp.rect;
                        float aspect = r.width / Mathf.Max(1f, r.height);
                        if (lw / lh < aspect) lh = lw / aspect; else lw = lh * aspect;
                    }
                }
                catch { }
                bl = new Vector3(bl.x, cy - lh * 0.5f, 0f);

                var go = new GameObject("GrannyMP_MultiplayerLabel");
                go.layer = logo.layer;
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(parent, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(lw * 0.4f, lh * 0.14f);
                rt.localScale = Vector3.one; rt.localRotation = Quaternion.identity;
                rt.localPosition = new Vector3((tl.x + tr.x) * 0.5f, bl.y + lh * 0.08f, 0f);
                var tex = Logo();
                if (tex != null)
                {
                    float pw = lw * 0.32f, ph = pw * tex.height / Mathf.Max(1f, tex.width);
                    rt.sizeDelta = new Vector2(pw, ph);
                    rt.localPosition = new Vector3((tl.x + tr.x) * 0.5f, bl.y + lh * 0.23f, 0f);
                    var raw = go.AddComponent<RawImage>();
                    raw.texture = tex;
                    raw.raycastTarget = false;
                    Log.Debug($"menu: the Multiplayer picture under the GRANNY logo ({pw:F0}x{ph:F0}, logo {lw:F0}x{lh:F0})");
                    _label = go;
                    return;
                }
                var text = go.AddComponent<Text>();
                text.font = font;
                text.text = "Multiplayer";
                text.fontSize = 200;
                text.resizeTextForBestFit = true; text.resizeTextMinSize = 6; text.resizeTextMaxSize = 200;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
                text.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                text.raycastTarget = false;
                var sh = go.AddComponent<Shadow>();
                sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
                sh.effectDistance = new Vector2(2f, -2f);
                Log.Debug($"menu: 'Multiplayer' under the GRANNY logo (logo {lw:F0}x{lh:F0} in its parent, font {font.name})");
                _label = go;
            }
            catch (Exception e) { Log.Debug("menu label: " + e.Message); _next = Time.unscaledTime + 10f; }
        }

        /// <summary>A font the menu already uses (its buttons' texts), otherwise any text's in the scene.</summary>
        static Font MenuFont(Transform near)
        {
            var type = Il2CppInterop.Runtime.Il2CppType.Of<Text>();
            try
            {
                if (near != null)
                    foreach (var c in near.GetComponentsInChildren(type, true)) { var t = c?.TryCast<Text>(); if (t != null && t.font != null) return t.font; }
                foreach (var o in UnityEngine.Object.FindObjectsOfType(type, true)) { var t = o?.TryCast<Text>(); if (t != null && t.font != null) return t.font; }
            }
            catch { }
            return null;
        }
    }
}
