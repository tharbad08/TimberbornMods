using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.ModManagerScene;

namespace WorkerWorkplaceGuard
{
    public sealed class ModStarter : IModStarter
    {
        public void StartMod(IModEnvironment modEnvironment)
        {
            GuardRuntime.Install();
        }
    }

    internal static class GuardRuntime
    {
        private const string HarmonyId = "shay.worker-workplace-guard";
        private const string Version = "0.1.1";

        private static readonly ConditionalWeakTable<object, WorkerHistory> Histories =
            new ConditionalWeakTable<object, WorkerHistory>();

        private static Type _workerType;
        private static Type _workerRootBehaviorType;
        private static Type _harvestStarterType;
        private static PropertyInfo _workerWorkplace;
        private static FieldInfo _rootWorker;
        private static FieldInfo _harvestWorker;
        private static MethodInfo _unemploy;
        private static MethodInfo _employAt;
        private static MethodInfo _workAtWorkplace;
        private static MethodInfo _rootDecide;
        private static MethodInfo _rootDecideAsWorker;
        private static MethodInfo _harvestFindYielder;
        private static MethodInfo _decisionReleaseNow;
        private static int _sequence;
        private static bool _installed;

        internal static void Install()
        {
            if (_installed) return;
            try
            {
                _workerType = RequireType("Timberborn.WorkSystem.Worker", "Timberborn.WorkSystem");
                _workerRootBehaviorType = RequireType("Timberborn.WorkSystem.WorkerRootBehavior", "Timberborn.WorkSystem");
                _harvestStarterType = RequireType("Timberborn.Fields.HarvestStarter", "Timberborn.Fields");

                _workerWorkplace = _workerType.GetProperty("Workplace", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(_workerType.FullName, "Workplace");
                _rootWorker = _workerRootBehaviorType.GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(_workerRootBehaviorType.FullName, "_worker");
                _harvestWorker = _harvestStarterType.GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(_harvestStarterType.FullName, "_worker");

                _unemploy = RequireMethod(_workerType, "Unemploy", 0);
                _employAt = RequireMethod(_workerType, "EmployAt", 1);
                _workAtWorkplace = RequireMethod(_workerRootBehaviorType, "WorkAtWorkplace", 0);
                _rootDecide = _workerRootBehaviorType.GetMethods(All).FirstOrDefault(m => m.Name == "Decide" && m.GetParameters().Length == 1);
                _rootDecideAsWorker = RequireMethod(_workerRootBehaviorType, "DecideAsWorker", 0);
                _harvestFindYielder = _harvestStarterType.GetMethods(All).FirstOrDefault(
                    m => m.Name == "FindYielder" && m.GetParameters().Length == 3)
                    ?? throw new MissingMethodException(_harvestStarterType.FullName, "FindYielder");

                Type decisionType = _workAtWorkplace.ReturnType;
                _decisionReleaseNow = decisionType.GetMethod("ReleaseNow", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException(decisionType.FullName, "ReleaseNow");

                Type searchResultType = _harvestFindYielder.ReturnType;

                Type harmonyType = RequireType("HarmonyLib.Harmony", "0Harmony");
                Type harmonyMethodType = RequireType("HarmonyLib.HarmonyMethod", "0Harmony");
                object harmony = Activator.CreateInstance(harmonyType, new object[] { HarmonyId });
                ConstructorInfo harmonyMethodCtor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) })
                    ?? throw new MissingMethodException(harmonyMethodType.FullName, ".ctor(MethodInfo)");
                MethodInfo patch = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "Patch" && m.GetParameters().Length == 5)
                    ?? throw new MissingMethodException(harmonyType.FullName, "Patch");

                Patch(harmony, harmonyMethodCtor, patch, _unemploy,
                    prefix: typeof(GuardRuntime).GetMethod(nameof(UnemployPrefix), All));

                Patch(harmony, harmonyMethodCtor, patch, _employAt,
                    postfix: typeof(GuardRuntime).GetMethod(nameof(EmployAtPostfix), All));

                MethodInfo harvestPrefix = BuildHarvestPrefix(searchResultType);
                Patch(harmony, harmonyMethodCtor, patch, _harvestFindYielder, prefix: harvestPrefix);

                MethodInfo workPrefix = BuildWorkPrefix(decisionType);
                MethodInfo workFinalizer = BuildWorkFinalizer(decisionType);
                Patch(harmony, harmonyMethodCtor, patch, _workAtWorkplace,
                    prefix: workPrefix, finalizer: workFinalizer);

                _installed = true;
                GuardLog.Info("[WorkplaceGuard] v" + Version + " loaded. Tracks Worker.Unemploy and recovers stale-null workplace continuations instead of crashing.");
                DumpHarmonyMap(harmonyType);
            }
            catch (Exception ex)
            {
                GuardLog.Error("[WorkplaceGuard] FAILED TO INSTALL: " + ex);
            }
        }

        private static readonly BindingFlags All =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type RequireType(string fullName, string assemblyName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);
            if (type != null) return type;

            try
            {
                Assembly assembly = Assembly.Load(assemblyName);
                type = assembly.GetType(fullName, false);
            }
            catch { }

            return type ?? throw new TypeLoadException(fullName);
        }

        private static MethodInfo RequireMethod(Type type, string name, int parameterCount)
        {
            return type.GetMethods(All).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount)
                ?? throw new MissingMethodException(type.FullName, name);
        }

        private static void Patch(object harmony, ConstructorInfo harmonyMethodCtor, MethodInfo patch,
            MethodBase original, MethodInfo prefix = null, MethodInfo postfix = null, MethodInfo transpiler = null, MethodInfo finalizer = null)
        {
            object Wrap(MethodInfo method) => method == null ? null : harmonyMethodCtor.Invoke(new object[] { method });
            patch.Invoke(harmony, new[]
            {
                (object)original,
                Wrap(prefix),
                Wrap(postfix),
                Wrap(transpiler),
                Wrap(finalizer)
            });
        }

        private static void UnemployPrefix(object __instance)
        {
            try
            {
                object workplace = GetWorkplace(__instance);
                if (workplace == null) return;

                int seq = Interlocked.Increment(ref _sequence);
                string stack = Environment.StackTrace;
                var history = Histories.GetOrCreateValue(__instance);
                history.Sequence = seq;
                history.Worker = SafeName(__instance);
                history.Workplace = SafeName(workplace);
                history.Utc = DateTime.UtcNow;
                history.Stack = stack;

                if (IsInsideWorkplaceDecision(stack))
                {
                    GuardLog.Warn(
                        "[WorkplaceGuard] SUSPICIOUS Unemploy #" + seq +
                        "\nworker=" + history.Worker +
                        "\npreviousWorkplace=" + history.Workplace +
                        "\nutc=" + history.Utc.ToString("O") +
                        "\ncaller stack:\n" + stack);
                }
            }
            catch (Exception ex)
            {
                GuardLog.Warn("[WorkplaceGuard] Unemploy diagnostic failed: " + ex.GetBaseException().Message);
            }
        }

        private static void EmployAtPostfix(object __instance)
        {
            try
            {
                object workplace = GetWorkplace(__instance);
                if (workplace == null) return;
                var history = Histories.GetOrCreateValue(__instance);
                history.LastKnownWorkplace = SafeName(workplace);
            }
            catch { }
        }

        private static bool HarvestCanContinue(object harvestStarter)
        {
            try
            {
                object worker = _harvestWorker.GetValue(harvestStarter);
                if (worker != null && GetWorkplace(worker) != null) return true;

                ReportIncident(worker, "HarvestStarter.FindYielder", null,
                    "returning empty yielder result; vanilla harvest decision will release cleanly");
                return false;
            }
            catch (Exception ex)
            {
                GuardLog.Warn("[WorkplaceGuard] Harvest guard diagnostic failed; allowing vanilla code: " + ex.GetBaseException().Message);
                return true;
            }
        }

        private static bool WorkCanContinue(object workerRootBehavior)
        {
            try
            {
                object worker = _rootWorker.GetValue(workerRootBehavior);
                if (worker != null && GetWorkplace(worker) != null) return true;

                ReportIncident(worker, "WorkerRootBehavior.WorkAtWorkplace entry", null,
                    "returning Decision.ReleaseNow()");
                return false;
            }
            catch (Exception ex)
            {
                GuardLog.Warn("[WorkplaceGuard] WorkAtWorkplace entry guard diagnostic failed; allowing vanilla code: " + ex.GetBaseException().Message);
                return true;
            }
        }

        private static bool ShouldSuppressWorkplaceException(object workerRootBehavior, Exception exception)
        {
            try
            {
                if (!(exception is NullReferenceException)) return false;
                object worker = _rootWorker.GetValue(workerRootBehavior);
                if (worker != null && GetWorkplace(worker) != null) return false;

                ReportIncident(worker, "WorkerRootBehavior.WorkAtWorkplace finalizer", exception,
                    "suppressed stale-workplace NullReferenceException and returned Decision.ReleaseNow()");
                return true;
            }
            catch (Exception ex)
            {
                GuardLog.Warn("[WorkplaceGuard] Finalizer diagnostic failed; preserving original exception: " + ex.GetBaseException().Message);
                return false;
            }
        }

        private static object GetWorkplace(object worker)
        {
            return worker == null ? null : _workerWorkplace.GetValue(worker, null);
        }

        private static void ReportIncident(object worker, string location, Exception exception, string recovery)
        {
            int seq = Interlocked.Increment(ref _sequence);
            string workerName = SafeName(worker);
            WorkerHistory history = null;
            if (worker != null) Histories.TryGetValue(worker, out history);

            string message =
                "[WorkplaceGuard] INCIDENT #" + seq +
                "\nlocation=" + location +
                "\nworker=" + workerName +
                "\ncurrentWorkplace=<null>" +
                "\nrecovery=" + recovery;

            if (history != null)
            {
                message +=
                    "\nlastRecordedUnemploy=#" + history.Sequence +
                    "\nlastWorkplace=" + (history.Workplace ?? history.LastKnownWorkplace ?? "<unknown>") +
                    "\nunemployUtc=" + history.Utc.ToString("O") +
                    "\nlast Unemploy caller stack:\n" + (history.Stack ?? "<not recorded>");
            }
            else
            {
                message += "\nlast Unemploy caller stack=<not recorded>";
            }

            if (exception != null)
                message += "\noriginal exception:\n" + exception;

            message += "\nincident stack:\n" + Environment.StackTrace;
            GuardLog.Error(message);
        }

        private static bool IsInsideWorkplaceDecision(string stack)
        {
            return stack != null &&
                   (stack.IndexOf("WorkerRootBehavior.WorkAtWorkplace", StringComparison.Ordinal) >= 0 ||
                    stack.IndexOf("FarmHouseWorkplaceBehavior", StringComparison.Ordinal) >= 0 ||
                    stack.IndexOf("WorkplaceBehavior", StringComparison.Ordinal) >= 0);
        }

        private static string SafeName(object value)
        {
            if (value == null) return "<null>";
            try
            {
                PropertyInfo name = value.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object result = name?.GetValue(value, null);
                if (result != null && !string.IsNullOrWhiteSpace(result.ToString())) return result.ToString();
            }
            catch { }
            return value.GetType().FullName ?? value.GetType().Name;
        }

        private static MethodInfo BuildHarvestPrefix(Type resultType)
        {
            var dm = new DynamicMethod(
                "WorkplaceGuard_HarvestPrefix",
                typeof(bool),
                new[] { typeof(object), resultType.MakeByRefType() },
                typeof(GuardRuntime).Module,
                true);
            dm.DefineParameter(1, ParameterAttributes.None, "__instance");
            dm.DefineParameter(2, ParameterAttributes.Out, "__result");

            ILGenerator il = dm.GetILGenerator();
            Label continueOriginal = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(GuardRuntime).GetMethod(nameof(HarvestCanContinue), All));
            il.Emit(OpCodes.Brtrue_S, continueOriginal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Initobj, resultType);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(continueOriginal);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            return dm;
        }

        private static MethodInfo BuildWorkPrefix(Type decisionType)
        {
            var dm = new DynamicMethod(
                "WorkplaceGuard_WorkPrefix",
                typeof(bool),
                new[] { typeof(object), decisionType.MakeByRefType() },
                typeof(GuardRuntime).Module,
                true);
            dm.DefineParameter(1, ParameterAttributes.None, "__instance");
            dm.DefineParameter(2, ParameterAttributes.Out, "__result");

            ILGenerator il = dm.GetILGenerator();
            Label continueOriginal = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(GuardRuntime).GetMethod(nameof(WorkCanContinue), All));
            il.Emit(OpCodes.Brtrue_S, continueOriginal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, _decisionReleaseNow);
            il.Emit(OpCodes.Stobj, decisionType);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(continueOriginal);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            return dm;
        }

        private static MethodInfo BuildWorkFinalizer(Type decisionType)
        {
            var dm = new DynamicMethod(
                "WorkplaceGuard_WorkFinalizer",
                typeof(Exception),
                new[] { typeof(object), typeof(Exception), decisionType.MakeByRefType() },
                typeof(GuardRuntime).Module,
                true);
            dm.DefineParameter(1, ParameterAttributes.None, "__instance");
            dm.DefineParameter(2, ParameterAttributes.None, "__exception");
            dm.DefineParameter(3, ParameterAttributes.Out, "__result");

            ILGenerator il = dm.GetILGenerator();
            Label returnOriginal = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Brfalse_S, returnOriginal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(GuardRuntime).GetMethod(nameof(ShouldSuppressWorkplaceException), All));
            il.Emit(OpCodes.Brfalse_S, returnOriginal);

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, _decisionReleaseNow);
            il.Emit(OpCodes.Stobj, decisionType);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(returnOriginal);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ret);
            return dm;
        }

        private static void DumpHarmonyMap(Type harmonyType)
        {
            try
            {
                MethodInfo getPatchInfo = harmonyType.GetMethod(
                    "GetPatchInfo", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(MethodBase) }, null);
                if (getPatchInfo == null) return;

                GuardLog.Info("[WorkplaceGuard] Harmony patch map:");
                foreach (MethodBase method in new MethodBase[]
                {
                    _unemploy, _employAt, _rootDecide, _rootDecideAsWorker, _workAtWorkplace, _harvestFindYielder
                }.Where(m => m != null))
                {
                    object info = getPatchInfo.Invoke(null, new object[] { method });
                    string owners = PatchOwners(info);
                    GuardLog.Info("[WorkplaceGuard]   " +
                        method.DeclaringType?.FullName + "." + method.Name + ": " + owners);
                }
            }
            catch (Exception ex)
            {
                GuardLog.Warn("[WorkplaceGuard] Harmony map unavailable: " + ex.GetBaseException().Message);
            }
        }

        private static string PatchOwners(object patchInfo)
        {
            if (patchInfo == null) return "none";
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (string group in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
            {
                PropertyInfo property = patchInfo.GetType().GetProperty(group, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(property?.GetValue(patchInfo, null) is IEnumerable patches)) continue;
                foreach (object patch in patches)
                {
                    if (patch == null) continue;
                    object owner =
                        patch.GetType().GetProperty("owner", All)?.GetValue(patch, null) ??
                        patch.GetType().GetField("owner", All)?.GetValue(patch);
                    if (owner != null) owners.Add(owner.ToString());
                }
            }
            return owners.Count == 0 ? "none" : string.Join(", ", owners.OrderBy(x => x));
        }

        private sealed class WorkerHistory
        {
            internal int Sequence;
            internal string Worker;
            internal string Workplace;
            internal string LastKnownWorkplace;
            internal DateTime Utc;
            internal string Stack;
        }
    }

    internal static class GuardLog
    {
        private static readonly Type DebugType = FindDebugType();
        private static readonly MethodInfo LogMethod = FindMethod("Log");
        private static readonly MethodInfo WarningMethod = FindMethod("LogWarning");
        private static readonly MethodInfo ErrorMethod = FindMethod("LogError");

        internal static void Info(string message) => Write(LogMethod, message);
        internal static void Warn(string message) => Write(WarningMethod ?? LogMethod, message);
        internal static void Error(string message) => Write(ErrorMethod ?? WarningMethod ?? LogMethod, message);

        private static Type FindDebugType()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.Debug", false))
                .FirstOrDefault(t => t != null);
        }

        private static MethodInfo FindMethod(string name)
        {
            if (DebugType == null) return null;
            return DebugType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == name && p.Length == 1 && p[0].ParameterType == typeof(object);
                });
        }

        private static void Write(MethodInfo method, string message)
        {
            try
            {
                if (method != null)
                {
                    method.Invoke(null, new object[] { message });
                    return;
                }
            }
            catch { }
            Console.WriteLine(message);
        }
    }
}
