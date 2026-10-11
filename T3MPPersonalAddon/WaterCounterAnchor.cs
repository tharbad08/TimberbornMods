using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace T3MPPersonalAddon
{
    /// <summary>
    /// Uses the verified v1.1.96 Benchmark & Optimizer water-counter locator:
    /// TopBarPanel._counters -> TopBarCounterRow._goodId/_root.
    /// This is UI only: no game speed, simulation or inventory mutations.
    /// </summary>
    internal static class WaterCounterAnchor
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const float ReadoutWidth = 205f;
        private static object? _topBar, _water, _readout, _height, _overlayRoot;
        private static bool _moved, _confirmed, _errorLogged, _notFoundLogged;
        private static int _attempts;
        private static long _nextCheck;
        private static readonly string[] SideNames = { "Top", "Right", "Bottom", "Left" };

        internal static void Install(Harmony harmony)
        {
            var t = AccessTools.TypeByName("Timberborn.TopBarSystem.TopBarPanel");
            if (t == null)
            {
                Console.WriteLine("[T3MPPersonalAddon] Water anchor: TopBarPanel type missing; existing Height position preserved.");
                return;
            }

            var count = 0;
            foreach (var method in t.GetMethods(All | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(void) || method.GetParameters().Length != 0 ||
                    (method.Name != "Load" && !method.Name.EndsWith(".Load", StringComparison.Ordinal)))
                    continue;
                try
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(WaterCounterAnchor), nameof(AfterTopBarLoad)));
                    count++;
                }
                catch (Exception e)
                {
                    Console.WriteLine("[T3MPPersonalAddon] Water anchor hook unavailable: " + e.GetBaseException().Message);
                }
            }
            Console.WriteLine("[T3MPPersonalAddon] Water anchor: TopBarPanel.Load hooks=" + count);
        }

        public static void AfterTopBarLoad(object __instance)
        {
            _topBar = __instance;
            _water = null;
            _attempts = 0;
            _confirmed = false;
            Console.WriteLine("[T3MPPersonalAddon] TopBarPanel loaded; will resolve vanilla Water resource counter.");
        }

        internal static void Attach(object panel, object heightRoot)
        {
            _readout = panel;
            _height = heightRoot;
            _overlayRoot = null;
            _water = null;
            _moved = _confirmed = _errorLogged = _notFoundLogged = false;
            _attempts = 0;
            _nextCheck = 0;
        }

        // RatePanel.OnGUI gives us a safe Unity main-thread update point.
        // Reposition twice per second, not once per frame; no repeated scans
        // after a counter has been located. The root is never left in flex.
        internal static void Refresh()
        {
            if (_readout == null || _height == null) return;
            var now = Stopwatch.GetTimestamp();
            if (now < _nextCheck) return;
            _nextCheck = now + Stopwatch.Frequency / 2;
            try
            {
                if (_water == null && _attempts < 80)
                    ResolveWater();
                if (_water == null) return; // Keep the already-working Height slot until resolved.

                var parent = _water;
                while (Get(parent, "parent") is object next) parent = next;

                if (!TryBounds(_water, out var waterX, out var waterY, out var waterW, out var waterH) ||
                    !TryBounds(parent, out var docX, out var docY, out var docW, out _))
                    return;
                if (waterW < 10 || waterH < 10 || docW < ReadoutWidth) return;

                // Exactly the optimizer's requested placement: left-aligned to
                // Water, with the top flush with the counter's bottom.
                float x = Math.Max(2f, waterX - docX);
                float y = waterY + waterH - docY;
                if (x + ReadoutWidth > docW) x = Math.Max(2f, docW - ReadoutWidth - 2f);

                if (!_moved || !ReferenceEquals(_overlayRoot, parent))
                {
                    SetStyle(_readout, "position", "UnityEngine.UIElements.Position", "Absolute");
                    SetPixel(_readout, "width", ReadoutWidth);
                    SetPixel(_readout, "height", 26);
                    var ve = AccessTools.TypeByName("UnityEngine.UIElements.VisualElement")
                        ?? throw new TypeLoadException("UnityEngine.UIElements.VisualElement");
                    var add = parent.GetType().GetMethod("Add", All, null, new[] { ve }, null)
                        ?? throw new MissingMethodException("VisualElement.Add");
                    // Reparents out of UILayout.AddTopRight; no blank flex slot,
                    // independent of weather/global-view rows.
                    add.Invoke(parent, new[] { _readout });
                    ApplyDetachedStyle();
                    _moved = true;
                    _overlayRoot = parent;
                    Console.WriteLine("[T3MPPersonalAddon] Water overlay mounted; zero reserved top-right layout space.");
                }

                SetPixel(_readout, "left", x);
                SetPixel(_readout, "top", y);
                if (!_confirmed)
                {
                    _confirmed = true;
                    Console.WriteLine("[T3MPPersonalAddon] rSPD/iSPD below Water x=" +
                        x.ToString("F0") + " y=" + y.ToString("F0") +
                        "; waterSize=" + waterW.ToString("F0") + "x" + waterH.ToString("F0") +
                        "; no gap, may extend below Badwater.");
                }
            }
            catch (Exception e)
            {
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    Console.WriteLine("[T3MPPersonalAddon] Water anchor positioning failed; readout kept: " +
                        e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message);
                }
            }
        }

        private static void ResolveWater()
        {
            ++_attempts;
            if (_topBar == null) return;
            var list = AccessTools.Field(_topBar.GetType(), "_counters")?.GetValue(_topBar) as IEnumerable;
            if (list == null) return;

            var observed = new List<string>();
            foreach (var counter in list)
            {
                if (counter == null) continue;
                if (Inspect(counter, observed)) return;
                var rows = AccessTools.Field(counter.GetType(), "_counterRows")?.GetValue(counter) as IEnumerable;
                if (rows == null) continue;
                foreach (var row in rows)
                    if (row != null && Inspect(row, observed)) return;
            }
            if (_attempts == 80 && !_notFoundLogged)
            {
                _notFoundLogged = true;
                Console.WriteLine("[T3MPPersonalAddon] Water counter unresolved; found good IDs=" +
                    string.Join(",", observed.Take(20)) + "; retaining Height fallback.");
            }
        }

        private static bool Inspect(object counter, List<string> observed)
        {
            var t = counter.GetType();
            var id = AccessTools.Field(t, "_goodId")?.GetValue(counter)?.ToString();
            if (string.IsNullOrEmpty(id)) return false;
            if (observed.Count < 20 && !observed.Contains(id)) observed.Add(id);
            if (!id.Equals("Water", StringComparison.OrdinalIgnoreCase) &&
                !id.EndsWith(".Water", StringComparison.OrdinalIgnoreCase))
                return false; // Badwater is intentionally excluded.
            var root = AccessTools.Field(t, "_root")?.GetValue(counter);
            if (root == null || !TryBounds(root, out _, out _, out var w, out var h) || w < 10 || h < 10)
                return false;
            _water = root;
            Console.WriteLine("[T3MPPersonalAddon] Located vanilla Water: " + t.FullName +
                " goodId=" + id + " (More TopBars groups ignored).");
            return true;
        }

        private static bool TryBounds(object node, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0;
            var rect = Get(node, "worldBound");
            if (rect == null) return false;
            try
            {
                float Read(string name)
                {
                    var t = rect.GetType();
                    var v = t.GetProperty(name, All)?.GetValue(rect) ??
                        t.GetField(name, All)?.GetValue(rect);
                    return Convert.ToSingle(v);
                }
                x = Read("xMin"); y = Read("yMin"); w = Read("width"); h = Read("height");
                return w > 0 && h > 0 && !float.IsNaN(x) && !float.IsNaN(y);
            }
            catch { return false; }
        }

        private static object? Get(object node, string property)
        {
            try { return node.GetType().GetProperty(property, All)?.GetValue(node); }
            catch { return null; }
        }

        private static void SetPixel(object node, string name, float value)
        {
            var lengthType = AccessTools.TypeByName("UnityEngine.UIElements.Length")
                ?? throw new TypeLoadException("UIElements.Length");
            var unitType = AccessTools.TypeByName("UnityEngine.UIElements.LengthUnit")
                ?? throw new TypeLoadException("UIElements.LengthUnit");
            var length = Activator.CreateInstance(lengthType, new object[] {
                value, Enum.Parse(unitType,"Pixel") })!;
            WriteStyle(node, name, length);
        }

        private static void SetStyle(object node, string name, string enumType, string enumValue)
        {
            var t = AccessTools.TypeByName(enumType) ?? throw new TypeLoadException(enumType);
            WriteStyle(node, name, Enum.Parse(t, enumValue));
        }

        private static void WriteStyle(object node, string name, object value)
        {
            var style = Get(node, "style") ?? throw new MissingMemberException("VisualElement.style");
            // Unity 6 implements IStyle explicitly: use its interface's setter.
            var iface = style.GetType().GetInterfaces()
                .FirstOrDefault(t => t.FullName == "UnityEngine.UIElements.IStyle")
                ?? throw new TypeLoadException("UnityEngine.UIElements.IStyle");
            var prop = iface.GetProperty(name, All) ?? throw new MissingMemberException("IStyle."+name);
            var boxed = prop.PropertyType.IsInstanceOfType(value) ? value :
                Activator.CreateInstance(prop.PropertyType, new[] { value })!;
            prop.SetValue(style, boxed);
        }

        private static void ApplyDetachedStyle()
        {
            if (_readout == null) return;
            // Outside the HUD flex parent, parent-specific USS selectors may
            // no longer produce the green/gold frame. This is the approach
            // tested in the optimizer's historical v1.1.99 skin fallback.
            var colorType = AccessTools.TypeByName("UnityEngine.Color");
            var styleColor = AccessTools.TypeByName("UnityEngine.UIElements.StyleColor");
            if (colorType == null || styleColor == null) return;
            object Color(float r, float g, float b, float a)
            {
                var c = Activator.CreateInstance(colorType, new object[] {r,g,b,a})!;
                return Activator.CreateInstance(styleColor, new[] {c})!;
            }
            int applied = 0;
            void SafeSet(string name, object val)
            {
                try { WriteStyle(_readout, name, val); applied++; }
                catch { /* Retain game stylesheet if fallback property absent */ }
            }
            SafeSet("backgroundColor", Color(.055f,.16f,.13f,.96f));
            foreach (var side in SideNames)
            {
                SafeSet("border"+side+"Color", Color(.70f,.60f,.37f,1f));
                try
                {
                    var st = AccessTools.TypeByName("UnityEngine.UIElements.StyleFloat");
                    if (st != null) SafeSet("border"+side+"Width", Activator.CreateInstance(st,new object[]{2f})!);
                }
                catch { }
            }
            Console.WriteLine("[T3MPPersonalAddon] Detached green/gold water-panel skin props=" + applied);
        }
    }
}
