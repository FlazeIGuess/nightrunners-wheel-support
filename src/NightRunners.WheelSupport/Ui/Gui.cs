using System;
using UnityEngine;

namespace NightRunners.WheelSupport.Ui
{
    // Thin IMGUI wrapper. Centralizes the IL2CPP detail that every GUILayout call needs an
    // explicit (empty) GUILayoutOption[] array, and adds a few small conveniences so the
    // wizard/menu code stays readable.
    internal static class Gui
    {
        private static readonly GUILayoutOption[] X = new GUILayoutOption[0];

        public static void Label(string s) { GUILayout.Label(s, X); }
        public static bool Button(string s) { return GUILayout.Button(s, X); }
        public static string TextField(string s) { return GUILayout.TextField(s ?? "", X); }
        public static bool Toggle(bool v, string s) { return GUILayout.Toggle(v, s, X); }
        public static float Slider(float v, float a, float b) { return GUILayout.HorizontalSlider(v, a, b, X); }
        public static void BeginH() { GUILayout.BeginHorizontal(X); }
        public static void EndH() { GUILayout.EndHorizontal(); }
        public static void BeginV() { GUILayout.BeginVertical(X); }
        public static void EndV() { GUILayout.EndVertical(); }
        public static void Space(float p) { GUILayout.Space(p); }
        public static void Flex() { GUILayout.FlexibleSpace(); }

        public static void BeginArea(Rect r) { GUILayout.BeginArea(r); }
        public static void EndArea() { GUILayout.EndArea(); }

        public static Vector2 BeginScroll(Vector2 pos) { return GUILayout.BeginScrollView(pos, X); }
        public static void EndScroll() { GUILayout.EndScrollView(); }

        // ASCII progress bar for a 0..1 value (readable without custom styles).
        public static string Bar(float n01, int width = 18)
        {
            if (n01 < 0f) n01 = 0f; else if (n01 > 1f) n01 = 1f;
            int filled = (int)(n01 * width + 0.5f);
            var sb = new System.Text.StringBuilder(width + 2);
            sb.Append('[');
            for (int i = 0; i < width; i++) sb.Append(i < filled ? '#' : '.');
            sb.Append(']');
            return sb.ToString();
        }

        // A centered panel rect.
        public static Rect CenteredPanel(float w, float h)
        {
            float sw = 1920f, sh = 1080f;
            try { sw = Screen.width; sh = Screen.height; } catch { }
            if (sw < 100f) sw = 1920f;
            if (sh < 100f) sh = 1080f;
            return new Rect((sw - w) * 0.5f, (sh - h) * 0.5f, w, h);
        }
    }
}
