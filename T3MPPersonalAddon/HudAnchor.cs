using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace T3MPPersonalAddon
{
    // Caches real Timberborn UI Toolkit roots, including the version of the
    // weather/date HUD installed by ModdableWeathers. Never patches UI behavior.
    internal static class HudAnchor
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly List<object> Roots = new List<object>();

        internal static void Install(Harmony harmony)
        {
            var targets = new HashSet<MethodBase>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (t.Name != "DatePanel" && t.Name != "WeatherPanel" &&
                        t.Name != "ModdableDatePanel" && t.Name != "ModdableWeatherPanel") continue;
                    foreach (var method in t.GetMethods(All | BindingFlags.DeclaredOnly))
                    {
                        if (method.ReturnType != typeof(void) || method.GetParameters().Length != 0) continue;
                        if (method.Name != "Load" && !method.Name.EndsWith(".Load", StringComparison.Ordinal)) continue;
                        if (targets.Add(method))
                        {
                            try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(HudAnchor), nameof(AfterHudLoad))); }
                            catch (Exception e) { Console.WriteLine("[T3MPPersonalAddon] HUD observer skipped: " + e.GetBaseException().Message); }
                        }
                    }
                }
            }
        }

        // The Load postfix only captures a reference. No changed return value,
        // no replacement of ModdableWeathers or Timberborn UI initialization.
        public static void AfterHudLoad(object __instance)
        {
            var root = AccessTools.Field(__instance.GetType(), "_root")?.GetValue(__instance);
            if (root != null && !Roots.Contains(root)) Roots.Add(root);
        }

        internal static IEnumerable<object> LiveRoots()
        {
            var yielded = new HashSet<object>();
            foreach (var r in Roots.ToArray())
            {
                var parent = r;
                for (int i = 0; i < 32; i++)
                {
                    var next = Get(parent, "parent");
                    if (next == null) break;
                    parent = next;
                }
                if (Get(parent, "panel") != null && yielded.Add(parent)) yield return parent;
            }
            // Some game versions put UI into standalone UIDocuments.
            var docType = AccessTools.TypeByName("UnityEngine.UIElements.UIDocument");
            var unityObject = AccessTools.TypeByName("UnityEngine.Object");
            if (docType == null || unityObject == null) yield break;
            var find = unityObject.GetMethod("FindObjectsOfType", All, null, new[] { typeof(Type) }, null);
            if (find == null) yield break;
            Array? docs;
            try { docs = find.Invoke(null, new object[] { docType }) as Array; }
            catch { yield break; }
            if (docs == null) yield break;
            foreach (var doc in docs)
            {
                if (doc == null) continue;
                var root = Get(doc, "rootVisualElement");
                if (root != null && yielded.Add(root)) yield return root;
            }
        }

        // Find *the header*, never the expanding district-list container.
        // This remains in the same place when "Global view" opens more rows.
        internal static bool TryFindHeader(object root, out object header)
        {
            header = null!;
            foreach (var node in Descendants(root, 22000))
            {
                var text = Get(node, "text") as string;
                if (text == null || !string.Equals(text.Trim(), "Global view", StringComparison.OrdinalIgnoreCase))
                    continue;
                var element = node;
                if (!TryBounds(node, out var bounds)) continue;
                for (int i = 0; i < 4; i++)
                {
                    var parent = Get(element, "parent");
                    if (parent == null || !TryBounds(parent, out var pb)) break;
                    // A short selector/header only, not its variable-height popup.
                    if (pb.Height > 65f || pb.Width > 550f || pb.Height < 12f ||
                        pb.Width < bounds.Width || pb.Top > 200f) break;
                    element = parent;
                }
                header = element;
                return true;
            }
            return false;
        }

        internal static object? Get(object o, string property)
        {
            try { return o.GetType().GetProperty(property, All)?.GetValue(o); }
            catch { return null; }
        }

        internal static bool TryBounds(object node, out Rect bounds)
        {
            bounds = default;
            var v = Get(node, "worldBound");
            if (v == null) return false;
            try
            {
                var left = Convert.ToSingle(Get(v, "xMin"));
                var right = Convert.ToSingle(Get(v, "xMax"));
                var top = Convert.ToSingle(Get(v, "yMin"));
                var bottom = Convert.ToSingle(Get(v, "yMax"));
                if (right <= left || bottom <= top || !Finite(left) || !Finite(top) ||
                    !Finite(right) || !Finite(bottom)) return false;
                bounds = new Rect(left, top, right, bottom);
                return true;
            }
            catch { return false; }
        }

        private static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);

        private static IEnumerable<object> Descendants(object root, int maxNodes)
        {
            var stack = new Stack<object>();
            stack.Push(root);
            for (int i=0; stack.Count>0 && i<maxNodes; i++)
            {
                var n = stack.Pop();
                yield return n;
                var count = Get(n, "childCount");
                if (count == null) continue;
                var method = n.GetType().GetMethod("ElementAt", All, null, new[] {typeof(int)}, null);
                if (method == null) continue;
                int c;
                try { c = Math.Min(300, Convert.ToInt32(count)); } catch { continue; }
                for (int j = c-1; j>=0; j--)
                {
                    try { var child = method.Invoke(n, new object[] {j}); if (child!=null) stack.Push(child); }
                    catch { }
                }
            }
        }

        internal readonly struct Rect
        {
            internal readonly float Left, Top, Right, Bottom;
            internal float Width => Right - Left;
            internal float Height => Bottom - Top;
            internal Rect(float left, float top, float right, float bottom)
            { Left=left; Top=top; Right=right; Bottom=bottom; }
        }
    }
}
