using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace T3MPPersonalAddon
{
    // Independent of T3MP and HeightShower binary versions. Only the T3MP
    // speed-owner patch and the display are changed; optimizations remain on.
    public sealed class ModStarter : IModStarter
    {
        private static readonly Harmony Patcher = new Harmony("local.t3mp.personal");
        private static bool _started;

        public void StartMod(IModEnvironment environment)
        {
            if (_started) return;
            var view = AccessTools.TypeByName("T3MP.UI.SimulationRateMeterView")
                ?? throw new TypeLoadException("T3MP.UI.SimulationRateMeterView");
            var onGui = AccessTools.Method(view, "OnGUI", Type.EmptyTypes)
                ?? throw new MissingMethodException(view.FullName, "OnGUI");
            RatePanel.Source = AccessTools.Field(view, "_text")
                ?? throw new MissingFieldException(view.FullName, "_text");

            Patcher.Patch(onGui, prefix: new HarmonyMethod(typeof(RatePanel), nameof(RatePanel.DrawInstead)));
            HeightShowerAdapter.Install(Patcher);
            _started = true;
            RemoveT3MPSpeedPatch();
            var policy = AccessTools.TypeByName("T3MP.Runtime.RequestedSpeedPolicy");
            var install = policy == null ? null : AccessTools.Method(policy, "Install");
            if (install != null)
                Patcher.Patch(install, postfix: new HarmonyMethod(typeof(ModStarter), nameof(AfterSpeedInstall)));
            Console.WriteLine("[T3MPPersonalAddon] Height-styled readout addon v0.2.0 started; all T3MP optimizations retained");
        }

        public static void AfterSpeedInstall() => RemoveT3MPSpeedPatch();

        private static void RemoveT3MPSpeedPatch()
        {
            var speedManager = AccessTools.TypeByName("Timberborn.TimeSystem.SpeedManager");
            var method = speedManager == null ? null :
                AccessTools.Method(speedManager, "ChangeSpeedScale", new[] {typeof(float)});
            if (method == null) return;
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return;
            foreach (var prefix in info.Prefixes)
            {
                // Only this exact T3MP owner: never unpatch the configurable
                // speed mod, our optimizer, or any other mod's Harmony hooks.
                if (prefix.owner != "t3mp.speed.requested") continue;
                Patcher.Unpatch(method, prefix.PatchMethod);
                Console.WriteLine("[T3MPPersonalAddon] Removed T3MP requested-speed prefix; all other speed patches remain");
            }
        }
    }

    internal static class RatePanel
    {
        internal static FieldInfo Source = null!;
        private static object? _label;
        private static string _last = "";
        private static bool _error;

        internal static void Attach(object label)
        {
            _label = label;
            _last = "";
            Console.WriteLine("[T3MPPersonalAddon] Display attached after HeightShower (top-right order 9)");
        }

        public static bool DrawInstead(object __instance)
        {
            // Returning false suppresses ONLY original T3MP OnGUI. T3MP's
            // Update and tick observer still calculate values normally.
            if (_label == null) return false;
            try
            {
                var raw = (string?)Source.GetValue(__instance) ?? "";
                var first = raw.Split('\n')[0];
                if (first.StartsWith("rSPD/iSPD", StringComparison.Ordinal) && first != _last)
                {
                    var text = _label.GetType().GetProperty("text", HeightShowerAdapter.All);
                    if (text == null) throw new MissingMemberException("Label.text");
                    text.SetValue(_label, first);
                    _last = first;
                }
            }
            catch (Exception e)
            {
                if (!_error)
                {
                    _error = true;
                    Console.WriteLine("[T3MPPersonalAddon] Display text update failed: " + e.GetBaseException().Message);
                }
            }
            return false;
        }
    }

    internal static class HeightShowerAdapter
    {
        internal const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static bool _warned;

        internal static void Install(Harmony harmony)
        {
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).Cast<Type>(); }
                catch { return Enumerable.Empty<Type>(); }
            }).Where(t => t.Name == "HeightShowerPanel" && (t.Namespace ?? "").Contains("HeightShower")).ToArray();
            if (types.Length == 0)
            {
                Console.WriteLine("[T3MPPersonalAddon] HeightShowerPanel not found; readout cannot be created");
                return;
            }
            foreach (var t in types)
            {
                var load = AccessTools.Method(t, "Load", Type.EmptyTypes);
                if (load != null) harmony.Patch(load,
                    postfix: new HarmonyMethod(typeof(HeightShowerAdapter), nameof(AfterHeightLoad)));
            }
            Console.WriteLine("[T3MPPersonalAddon] HeightShower Load hook installed");
        }

        public static void AfterHeightLoad(object __instance)
        {
            try
            {
                var type = __instance.GetType();
                var oldPanel = AccessTools.Field(type, "_root")?.GetValue(__instance)
                    ?? throw new MissingFieldException(type.FullName, "_root");
                var gameLayout = AccessTools.Field(type, "_gameLayout")?.GetValue(__instance)
                    ?? throw new MissingFieldException(type.FullName, "_gameLayout");
                var heightLabel = AccessTools.Field(type, "HeightLabel")?.GetValue(__instance);

                var ve = AccessTools.TypeByName("UnityEngine.UIElements.VisualElement")
                    ?? throw new TypeLoadException("UnityEngine.UIElements.VisualElement");
                var labelType = AccessTools.TypeByName("UnityEngine.UIElements.Label")
                    ?? throw new TypeLoadException("UnityEngine.UIElements.Label");
                // Timberborn's square-large--green panel uses NineSlice geometry,
                // not a stock VisualElement. HeightShower's VisualElementBuilder
                // actually creates a NineSliceVisualElement (TimberAPI source).
                var nineSlice = AccessTools.TypeByName("Timberborn.CoreUI.NineSliceVisualElement")
                    ?? throw new TypeLoadException("Timberborn.CoreUI.NineSliceVisualElement");
                var panel = Activator.CreateInstance(nineSlice)!;
                var label = Activator.CreateInstance(labelType)!;
                ve.GetProperty("name", All)!.SetValue(panel, "T3MPPersonalRatePanel");
                ve.GetProperty("name", All)!.SetValue(label, "T3MPPersonalRateLabel");

                if (!CopyClasses(oldPanel, panel))
                    foreach (var c in new[] {"top-right-item","square-large--green"}) AddClass(panel,c);
                if (heightLabel == null || !CopyClasses(heightLabel, label))
                    foreach (var c in new[] {"text--centered","text--yellow","date-panel__text","game-text--normal"}) AddClass(label,c);

                CopyInline(oldPanel, panel, "flexDirection");
                CopyInline(oldPanel, panel, "flexWrap");
                CopyInline(oldPanel, panel, "justifyContent");

                // The readout must never intercept any game UI clicks.
                var picking = AccessTools.TypeByName("UnityEngine.UIElements.PickingMode");
                var pickProperty = ve.GetProperty("pickingMode", All);
                if (picking != null && pickProperty != null)
                {
                    var ignore = Enum.Parse(picking, "Ignore");
                    pickProperty.SetValue(panel, ignore);
                    pickProperty.SetValue(label, ignore);
                }

                labelType.GetProperty("text",All)!.SetValue(label, "rSPD/iSPD -- / --");
                var add = ve.GetMethod("Add", All, null, new[] {ve}, null)
                    ?? throw new MissingMethodException("VisualElement.Add");
                add.Invoke(panel, new object[]{label});
                // HeightShower calls UILayout.AddTopRight(...,8). Place after it
                // at 9, with *the same* existing green and gold panel styling.
                var append = gameLayout.GetType().GetMethods(All).FirstOrDefault(m =>
                    m.Name == "AddTopRight" && m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(ve) &&
                    m.GetParameters()[1].ParameterType == typeof(int))
                    ?? throw new MissingMethodException("UILayout.AddTopRight(VisualElement,int)");
                append.Invoke(gameLayout, new object[]{panel,9});
                Console.WriteLine("[T3MPPersonalAddon] Styled readout element: " + panel.GetType().FullName);
                RatePanel.Attach(label);
            }
            catch(Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Console.WriteLine("[T3MPPersonalAddon] HeightShower panel creation failed: "+ex.GetBaseException().Message);
                }
            }
        }

        private static bool CopyClasses(object source, object target)
        {
            var get = source.GetType().GetMethod("GetClasses", All, null, Type.EmptyTypes, null);
            if (get?.Invoke(source, null) is not IEnumerable classes) return false;
            bool copied = false;
            foreach(var c in classes)
                if(c is string s) { AddClass(target,s); copied = true; }
            return copied;
        }

        private static void AddClass(object element, string className)
        {
            var add = element.GetType().GetMethod("AddToClassList", All, null, new[]{typeof(string)},null)
                ?? throw new MissingMethodException("VisualElement.AddToClassList");
            add.Invoke(element,new object[]{className});
        }

        private static void CopyInline(object source, object target, string prop)
        {
            var from = source.GetType().GetProperty("style", All)?.GetValue(source);
            var to = target.GetType().GetProperty("style", All)?.GetValue(target);
            if(from == null || to == null) return;
            // Unity 6 exposes IStyle members through explicit interface
            // implementations, not always as properties of InlineStyleAccess.
            var iface = from.GetType().GetInterfaces()
                .FirstOrDefault(i => i.Name == "IStyle" && i.GetProperty(prop) != null);
            var property = iface?.GetProperty(prop);
            if(property != null) property.SetValue(to, property.GetValue(from));
        }
    }
}
