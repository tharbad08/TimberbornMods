using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace T3MPPersonalAddon
{
    // Ground the overlay in the *actual* date/weather HUD, following
    // ModdableWeathers' approach: operate on the existing WeatherPanel._root.
    // No direct reference to ModdableWeathers or the current game DLL is needed.
    internal static class HudAnchor
    {
        private static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static object _weatherRoot;
        private static bool _warned;

        internal static void Install(Harmony harmony)
        {
            var count = 0;
            var seen = new HashSet<MethodBase>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null || (type.Name != "WeatherPanel" && type.Name != "ModdableWeatherPanel"))
                        continue;
                    if (type.Name == "WeatherPanel" && !(type.Namespace ?? "").StartsWith("Timberborn.", StringComparison.Ordinal))
                        continue;
                    // WeatherPanel.Load() is invoked by ModdableWeatherPanel's own
                    // ILoadableSingleton.Load; hook both when declared to be safe.
                    foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                    {
                        if (method.ReturnType != typeof(void) || method.GetParameters().Length != 0 ||
                            !(method.Name == "Load" || method.Name.EndsWith(".Load", StringComparison.Ordinal)))
                            continue;
                        if (!seen.Add(method)) continue;
                        try
                        {
                            harmony.Patch(method, postfix: new HarmonyMethod(typeof(HudAnchor), nameof(AfterWeatherLoad)));
                            count++;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("[T3MPPersonalAddon] Could not hook " + method + ": " + e.GetBaseException().Message);
                        }
                    }
                }
            }
            Console.WriteLine("[T3MPPersonalAddon] Weather HUD layout hooks installed: " + count);
        }

        public static void AfterWeatherLoad(object __instance)
        {
            var field = AccessTools.Field(__instance.GetType(), "_root");
            var root = field?.GetValue(__instance);
            if (root != null) _weatherRoot = root;
        }

        internal static bool TryLocate(out float x, out float yBottom)
        {
            x = yBottom = -1f;
            var anchor = _weatherRoot;
            if (anchor == null || !TryBounds(anchor, out var weather)) return false;

            // A weather panel is an existing sibling of the date/speed UI.
            // Use its layout siblings to find the bottom edge of the stack,
            // including widgets that other mods add there.
            float right = weather.Right, bottom = weather.Bottom;
            var parent = Get(anchor, "parent");
            if (parent != null)
            {
                CollectNeighborPanels(parent, weather, ref right, ref bottom);
                var grandparent = Get(parent, "parent");
                if (grandparent != null)
                    CollectNeighborPanels(grandparent, weather, ref right, ref bottom);
            }

            // UI Toolkit worldBound uses panel units. IMGUI OnGUI uses screen
            // pixels, which may differ with PanelSettings' scaling.
            float scale = 1f;
            var panel = Get(anchor, "panel");
            var visualTree = panel != null ? Get(panel, "visualTree") : null;
            if (visualTree != null && TryBounds(visualTree, out var panelRect) && panelRect.Width > 0f)
            {
                var screen = AccessTools.TypeByName("UnityEngine.Screen");
                var sw = screen?.GetProperty("width", All);
                if (sw != null)
                {
                    var ratio = Convert.ToSingle(sw.GetValue(null)) / panelRect.Width;
                    if (ratio >= 0.4f && ratio <= 4f) scale = ratio;
                }
            }
            x = right * scale - 230f;
            yBottom = bottom * scale;
            return true;
        }

        private static void CollectNeighborPanels(object parent, Box weather, ref float right, ref float bottom)
        {
            foreach (var child in Children(parent))
            {
                if (!TryBounds(child, out var b)) continue;
                // Exclude full-screen frames, the minimap, invisible/zero-size
                // elements, and distant UI. Keep normal small HUD panels that
                // share this right-side stack. No type names from other mods.
                if (b.Width < 30f || b.Width > 560f || b.Height < 8f || b.Height > 150f) continue;
                if (b.Top < weather.Top - 190f || b.Top > weather.Bottom + 380f) continue;
                if (b.Left > weather.Right + 110f || b.Right < weather.Left - 110f) continue;
                if (Get(child, "visible") is bool visible && !visible) continue;
                var resolved = Get(child, "resolvedStyle");
                if (resolved != null && string.Equals(Get(resolved, "display")?.ToString(), "None", StringComparison.Ordinal))
                    continue;
                right = Math.Max(right, b.Right);
                bottom = Math.Max(bottom, b.Bottom);
            }
        }

        private static IEnumerable<object> Children(object parent)
        {
            var count = Get(parent, "childCount");
            var elementAt = parent.GetType().GetMethod("ElementAt", All, null, new[] { typeof(int) }, null);
            if (count == null || elementAt == null) yield break;
            int n;
            try { n = Math.Min(80, Convert.ToInt32(count)); } catch { yield break; }
            for (var i = 0; i < n; ++i)
            {
                object child = null;
                try { child = elementAt.Invoke(parent, new object[] { i }); } catch { }
                if (child != null) yield return child;
            }
        }

        private static object Get(object o, string property)
        {
            try { return o.GetType().GetProperty(property, All)?.GetValue(o); }
            catch { return null; }
        }

        private static bool TryBounds(object node, out Box box)
        {
            box = default;
            if (Get(node, "panel") == null) return false;
            var bounds = Get(node, "worldBound");
            if (bounds == null) return false;
            try
            {
                float left = Convert.ToSingle(Get(bounds, "xMin"));
                float right = Convert.ToSingle(Get(bounds, "xMax"));
                float top = Convert.ToSingle(Get(bounds, "yMin"));
                float bottom = Convert.ToSingle(Get(bounds, "yMax"));
                if (!Valid(left) || !Valid(top) || !Valid(right) || !Valid(bottom) ||
                    right <= left || bottom <= top) return false;
                box = new Box(left, top, right, bottom);
                return true;
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Console.WriteLine("[T3MPPersonalAddon] Weather HUD bounds unavailable: " + e.GetBaseException().Message);
                }
                return false;
            }
        }

        private static bool Valid(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        private readonly struct Box
        {
            internal readonly float Left, Top, Right, Bottom;
            internal float Width => Right - Left;
            internal float Height => Bottom - Top;
            internal Box(float l, float t, float r, float b)
            { Left = l; Top = t; Right = r; Bottom = b; }
        }
    }
}
