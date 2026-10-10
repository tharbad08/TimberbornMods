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
            HudAnchor.Install(H);
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

    // UI Toolkit label is an earlier sibling in the existing root. The
    // expanding Global view panel paints over it instead of pushing it down.
    internal static class Overlay
    {
        internal static FieldInfo Text = null!;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static object? _root, _label, _header;
        private static double _nextScan;
        private static string _lastText = "";
        private static bool _warned;
        private static Type _labelType = null!, _lengthType = null!, _lengthUnitType = null!, _positionType = null!;

        internal static void Prepare()
        {
            _labelType = AccessTools.TypeByName("UnityEngine.UIElements.Label") ?? throw new TypeLoadException("UIElements.Label");
            _lengthType = AccessTools.TypeByName("UnityEngine.UIElements.Length") ?? throw new TypeLoadException("UIElements.Length");
            _lengthUnitType = AccessTools.TypeByName("UnityEngine.UIElements.LengthUnit") ?? throw new TypeLoadException("UIElements.LengthUnit");
            _positionType = AccessTools.TypeByName("UnityEngine.UIElements.Position") ?? throw new TypeLoadException("UIElements.Position");
        }

        public static bool DrawInstead(object __instance)
        {
            try
            {
                var raw = (string?)Text.GetValue(__instance) ?? "";
                var line = raw.Split('\n')[0];
                if (!line.StartsWith("rSPD/iSPD", StringComparison.Ordinal)) return false;
                var now = (double)DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                if (now >= _nextScan || _label == null || HudAnchor.Get(_label, "panel") == null)
                {
                    _nextScan = now + 2;
                    Locate();
                }
                if (_label != null && _header != null && _root != null &&
                    HudAnchor.TryBounds(_root, out var rb) && HudAnchor.TryBounds(_header, out var hb))
                {
                    // Recalculate from *header* geometry, never from the dropdown
                    // container's variable height.
                    Position(_label, hb.Left - rb.Left, hb.Bottom - rb.Top + 6f);
                    if (line != _lastText)
                    {
                        _labelType.GetProperty("text", All)!.SetValue(_label, line);
                        _lastText = line;
                    }
                }
            }
            catch (Exception e)
            {
                if (!_warned)
                {
                    _warned = true;
                    Console.WriteLine("[T3MPPersonalAddon] Readout unavailable; game unaffected: " + e.GetBaseException().Message);
                }
            }
            // Always suppress original floating T3MP OnGUI. Keep its native
            // meter sampling, tick observers and all optimizations.
            return false;
        }

        private static void Locate()
        {
            foreach (var root in HudAnchor.LiveRoots())
            {
                if (!HudAnchor.TryFindHeader(root, out var header)) continue;
                if (!ReferenceEquals(_root, root) || _label == null || HudAnchor.Get(_label, "panel") == null)
                {
                    _root = root;
                    _header = header;
                    _label = Activator.CreateInstance(_labelType, new object[] { "" })!;
                    // Put label behind the other HUD elements, including the
                    // Global view expandable rows. Does not intercept clicks.
                    var insert = root.GetType().GetMethod("Insert", All, null, new[] {typeof(int), AccessTools.TypeByName("UnityEngine.UIElements.VisualElement")!}, null);
                    if (insert == null) throw new MissingMethodException("VisualElement.Insert");
                    insert.Invoke(root, new object[] {0, _label});
                    var style = HudAnchor.Get(_label, "style")!;
                    SetEnumStyle(style, "position", _positionType, "Absolute");
                    SetEnumStyle(style, "pickingMode", AccessTools.TypeByName("UnityEngine.UIElements.PickingMode")!, "Ignore");
                    SetLength(style, "width", 245);
                    SetLength(style, "height", 25);
                    // The readout has a transparent background. Expanded
                    // Global view contents remain on top.
                    _lastText = "";
                }
                _header = header;
                return;
            }
            _header = null;
        }

        private static void Position(object label, float x, float y)
        {
            var style = HudAnchor.Get(label, "style")!;
            SetLength(style, "left", Math.Max(0f, x));
            SetLength(style, "top", Math.Max(0f, y));
        }

        private static void SetLength(object style, string property, float value)
        {
            var unit = Enum.Parse(_lengthUnitType, "Pixel");
            var length = Activator.CreateInstance(_lengthType, new object[] {value, unit})!;
            SetStyle(style, property, length);
        }

        private static void SetEnumStyle(object style, string property, Type enumeration, string name)
        {
            // pickingMode is on VisualElement, not IStyle; no-op if absent.
            if (property == "pickingMode")
            {
                var v = Enum.Parse(enumeration, name);
                _label?.GetType().GetProperty(property, All)?.SetValue(_label, v);
                return;
            }
            SetStyle(style, property, Enum.Parse(enumeration, name));
        }

        private static void SetStyle(object style, string property, object value)
        {
            // Unity exposes these properties through IStyle. On the shipped
            // game build InlineStyleAccess implements IStyle explicitly, so
            // reflection on the concrete type cannot find "position"/"left".
            var p = style.GetType().GetProperty(property, All);
            if (p == null)
                foreach (var iface in style.GetType().GetInterfaces())
                {
                    p = iface.GetProperty(property, All);
                    if (p != null && p.CanWrite) break;
                    p = null;
                }
            if (p == null || !p.CanWrite)
                throw new MissingMemberException(style.GetType().FullName, "IStyle." + property);
            // Each IStyle setter takes StyleLength or StyleEnum<T>.
            var wrapper = Activator.CreateInstance(p.PropertyType, new[] { value });
            p.SetValue(style, wrapper);
        }
    }
}
