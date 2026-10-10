using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace T3MPPersonalAddon
{
    // Deliberately no Unity or T3MP compile-time references.
    // Upstream T3MP can be updated without recompiling against its DLL.
    public sealed class ModStarter : IModStarter
    {
        private static readonly Harmony H = new Harmony("local.t3mp.personal");
        private static bool _done;
        private static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public void StartMod(IModEnvironment environment)
        {
            if (_done) return;
            var ui = AccessTools.TypeByName("T3MP.UI.SimulationRateMeterView");
            if (ui == null) throw new InvalidOperationException("[T3MPPersonalAddon] T3MP meter view not found. Requires T3MP 1.2.5 or a compatible newer version.");
            var gui = AccessTools.Method(ui, "OnGUI", Type.EmptyTypes);
            if (gui == null) throw new MissingMethodException(ui.FullName, "OnGUI");
            var field = AccessTools.Field(ui, "_text");
            if (field == null || field.FieldType != typeof(string)) throw new MissingFieldException(ui.FullName, "_text");
            Overlay.Text = field;
            Overlay.Prepare();
            // The original GUI is disabled, but the original sampling Update()
            // and simulation tick observer remain untouched.
            H.Patch(gui, prefix: new HarmonyMethod(typeof(Overlay), nameof(Overlay.DrawInstead)));
            _done = true;
            // T3MP's speed prefix may be registered later by another mod-load order.
            RemoveSpeedPolicy();
            // This patch runs after T3MP's patch is installed, if it exists.
            var policy = AccessTools.TypeByName("T3MP.Runtime.RequestedSpeedPolicy");
            if (policy != null)
            {
                var install = AccessTools.Method(policy, "Install");
                if (install != null) H.Patch(install, postfix: new HarmonyMethod(typeof(ModStarter), nameof(AfterSpeedInstall)));
            }
            Console.WriteLine("[T3MPPersonalAddon] Meter override installed; T3MP tick/load optimizations untouched.");
        }

        public static void AfterSpeedInstall() => RemoveSpeedPolicy();

        private static void RemoveSpeedPolicy()
        {
            var manager = AccessTools.TypeByName("Timberborn.TimeSystem.SpeedManager");
            if (manager == null) return;
            var target = AccessTools.Method(manager, "ChangeSpeedScale", new[] { typeof(float) });
            if (target == null) return;
            var patches = Harmony.GetPatchInfo(target);
            if (patches == null) return;
            var removed = 0;
            foreach (var p in patches.Prefixes)
            {
                // Never touch patches from other speed mods or our optimizer.
                if (p.owner != "t3mp.speed.requested") continue;
                H.Unpatch(target, p.PatchMethod);
                removed++;
            }
            if (removed > 0) Console.WriteLine("[T3MPPersonalAddon] Removed T3MP speed-policy prefix; left other Harmony patches intact.");
        }
    }

    internal static class Overlay
    {
        internal static FieldInfo Text = null!;
        private static Type _rect = null!, _gui = null!, _screen = null!, _event = null!, _eventType = null!, _document = null!, _unityObject = null!;
        private static ConstructorInfo _rectCtor = null!;
        private static MethodInfo _label = null!, _findObjects = null!;
        private static PropertyInfo _sw = null!, _sh = null!, _eventCurrent = null!, _eventKind = null!;
        private static double _nextScan;
        private static float _anchorY = -1f, _anchorX = -1f;
        private static int _height;
        private static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type Require(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);

        internal static void Prepare()
        {
            _rect = Require("UnityEngine.Rect");
            _gui = Require("UnityEngine.GUI");
            _screen = Require("UnityEngine.Screen");
            _event = Require("UnityEngine.Event");
            _eventType = Require("UnityEngine.EventType");
            _document = Require("UnityEngine.UIElements.UIDocument");
            _unityObject = Require("UnityEngine.Object");
            _rectCtor = _rect.GetConstructor(new[] {typeof(float), typeof(float), typeof(float), typeof(float)}) ?? throw new MissingMethodException("Rect(float,float,float,float)");
            _label = _gui.GetMethod("Label", new[]{_rect,typeof(string)}) ?? throw new MissingMethodException("GUI.Label(Rect,string)");
            _findObjects = _unityObject.GetMethod("FindObjectsOfType", All, null, new[]{typeof(Type)}, null) ?? throw new MissingMethodException("Object.FindObjectsOfType(Type)");
            _sw = _screen.GetProperty("width", All)!;
            _sh = _screen.GetProperty("height", All)!;
            _eventCurrent = _event.GetProperty("current", All)!;
            _eventKind = _event.GetProperty("type", All)!;
        }

        // Return false prevents T3MP's existing OnGUI (UPS, fixed placement, hide button).
        // The observer, requested speed and speed-sampling Update remain native T3MP.
        public static bool DrawInstead(object __instance)
        {
            try
            {
                var ev = _eventCurrent.GetValue(null);
                if (ev == null || !string.Equals(_eventKind.GetValue(ev)?.ToString(), "Repaint", StringComparison.Ordinal)) return false;
                var raw = (string?)Text.GetValue(__instance) ?? "";
                var line = raw.Split('\n')[0];
                if (!line.StartsWith("rSPD/iSPD", StringComparison.Ordinal)) return false;
                var width = Convert.ToInt32(_sw.GetValue(null));
                _height = Convert.ToInt32(_sh.GetValue(null));
                var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                if (now >= _nextScan)
                {
                    _nextScan = now + 2;
                    FindSpeedPanel();
                }
                var w = 230f;
                var x = _anchorX >= 0 ? Math.Max(4f, Math.Min(width - w, _anchorX)) : Math.Max(4f, width - w - 12f);
                var y = _anchorY >= 0 ? Math.Max(4f, Math.Min(_height - 30f, _anchorY + 8f)) : 155f;
                var rect = _rectCtor.Invoke(new object[]{x,y,w,26f});
                _label.Invoke(null, new object[]{rect,line});
            }
            catch (Exception e)
            {
                // UI-only failures must never break the simulation.
                if (_nextScan < 1) Console.WriteLine("[T3MPPersonalAddon] GUI failed safely: " + e.GetBaseException().Message);
            }
            return false;
        }

        // UI Toolkit speed control is not guaranteed across game versions or UI mods.
        // Prefer its measured world bounds, falling back to a harmless screen position.
        private static void FindSpeedPanel()
        {
            _anchorX = _anchorY = -1f;
            var docs = _findObjects.Invoke(null, new object[]{_document}) as Array;
            if (docs == null) return;
            foreach (var d in docs)
            {
                if (d == null) continue;
                var root = d.GetType().GetProperty("rootVisualElement", All)?.GetValue(d);
                if (root != null && FindNamed(root, 0)) return;
            }
        }
        private static bool FindNamed(object node, int depth)
        {
            if (depth > 18) return false;
            var type = node.GetType();
            var name = type.GetProperty("name", All)?.GetValue(node)?.ToString() ?? "";
            if (name.IndexOf("Speed", StringComparison.OrdinalIgnoreCase)>=0 &&
                (name.IndexOf("Time", StringComparison.OrdinalIgnoreCase)>=0 ||
                 name.IndexOf("Button", StringComparison.OrdinalIgnoreCase)>=0))
            {
                var bounds = type.GetProperty("worldBound", All)?.GetValue(node);
                if (bounds != null)
                {
                    var bt = bounds.GetType();
                    var yMax = bt.GetProperty("yMax", All)?.GetValue(bounds);
                    var xMax = bt.GetProperty("xMax", All)?.GetValue(bounds);
                    if (yMax != null && xMax != null && Convert.ToSingle(yMax) > 0)
                    {
                        _anchorY = Convert.ToSingle(yMax);
                        _anchorX = Convert.ToSingle(xMax) - 230f;
                        return true;
                    }
                }
            }
            var countProperty = type.GetProperty("childCount", All);
            var getItem = type.GetMethod("ElementAt", All, null, new[]{typeof(int)}, null);
            if (countProperty == null || getItem == null) return false;
            var count = Math.Min(100, Convert.ToInt32(countProperty.GetValue(node)));
            for (int i=0;i<count;i++)
            {
                var child = getItem.Invoke(node, new object[]{i});
                if (child != null && FindNamed(child, depth+1)) return true;
            }
            return false;
        }
    }
}
