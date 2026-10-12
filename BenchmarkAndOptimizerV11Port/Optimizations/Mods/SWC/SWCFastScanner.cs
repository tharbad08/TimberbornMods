using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Optional fast inner loops based on SWC 1.1.36.1 source. All game and SWC
/// members are bound from the *installed* assemblies. Any mismatched member
/// disables this optimization at startup; native SWC scanning remains usable.
/// Per-instance buffers are weak-keyed, and only committed after a full scan.
/// </summary>
internal static class SWCFastScanner
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static;
    private static Type TypeNamed(string name) => AccessTools.TypeByName(name)
        ?? throw new TypeLoadException("Missing " + name);

    private static FieldInfo Field(Type type, string name)
    {
        var field = AccessTools.Field(type, name);
        return field ?? throw new MissingFieldException(type.FullName, name);
    }

    private static MethodInfo Method(Type type, string name, int arguments, bool generic = false)
    {
        var matches = type.GetMethods(Members).Where(m => m.Name == name &&
            m.GetParameters().Length == arguments && m.IsGenericMethodDefinition == generic).ToArray();
        return matches.Length == 1 ? matches[0] :
            throw new MissingMethodException(type.FullName, name + "/" + arguments);
    }

    private static MemberInfo Member(Type type, string name)
    {
        return (MemberInfo?)AccessTools.Field(type, name) ??
            AccessTools.Property(type, name) ??
            throw new MissingMemberException(type.FullName, name);
    }

    private static Expression Read(Expression owner, MemberInfo member) =>
        member is FieldInfo f ? Expression.Field(Expression.Convert(owner, f.DeclaringType!), f) :
        member is PropertyInfo p ? Expression.Property(Expression.Convert(owner, p.DeclaringType!), p) :
        throw new MissingMemberException(member.Name);

    private static Func<object, object?> Getter(MemberInfo member)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(Read(owner, member), typeof(object)), owner).Compile();
    }

    private static Func<object, bool> Bool(MemberInfo member)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        return Expression.Lambda<Func<object, bool>>(Read(owner, member), owner).Compile();
    }

    private static Func<object, string?> Text(MemberInfo member)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        return Expression.Lambda<Func<object, string?>>(
            Expression.Convert(Read(owner, member), typeof(string)), owner).Compile();
    }

    private static Func<object, int> Integer(MemberInfo member)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        return Expression.Lambda<Func<object, int>>(Read(owner, member), owner).Compile();
    }

    private static Func<object, object?> Call0(MethodInfo method)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        var call = Expression.Call(Expression.Convert(owner, method.DeclaringType!), method);
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(call, typeof(object)), owner).Compile();
    }

    private static Func<object, object?, object?> Call1(MethodInfo method)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        var value = Expression.Parameter(typeof(object), "arg");
        var args = method.GetParameters();
        var call = Expression.Call(method.IsStatic ? null :
            Expression.Convert(owner, method.DeclaringType!), method,
            Expression.Convert(value, args[0].ParameterType));
        return Expression.Lambda<Func<object, object?, object?>>(
            Expression.Convert(call, typeof(object)), owner, value).Compile();
    }

    private static Func<object, object?, object?, object?> Call2(MethodInfo method)
    {
        var owner = Expression.Parameter(typeof(object), "owner");
        var first = Expression.Parameter(typeof(object), "arg1");
        var second = Expression.Parameter(typeof(object), "arg2");
        var args = method.GetParameters();
        var call = Expression.Call(method.IsStatic ? null :
            Expression.Convert(owner, method.DeclaringType!), method,
            Expression.Convert(first, args[0].ParameterType),
            Expression.Convert(second, args[1].ParameterType));
        return Expression.Lambda<Func<object, object?, object?, object?>>(
            Expression.Convert(call, typeof(object)), owner, first, second).Compile();
    }

    private static Action<object, bool> CallBoolean(MethodInfo method)
    {
        if (method.ReturnType != typeof(void) ||
            method.GetParameters().Length != 1 ||
            method.GetParameters()[0].ParameterType != typeof(bool))
            throw new MissingMethodException(method.Name + "(bool)");
        var owner = Expression.Parameter(typeof(object), "owner");
        var value = Expression.Parameter(typeof(bool), "value");
        return Expression.Lambda<Action<object, bool>>(
            Expression.Call(Expression.Convert(owner, method.DeclaringType!), method, value),
            owner, value).Compile();
    }

    private static MethodInfo Generic(Type type, string name, Type typeArgument)
    {
        var method = Method(type, name, 1, true);
        return method.MakeGenericMethod(typeArgument);
    }

    private static MethodInfo GenericZero(Type type, string name, Type typeArgument)
    {
        var method = Method(type, name, 0, true);
        return method.MakeGenericMethod(typeArgument);
    }

    private static IEnumerable<object?> Items(object? source)
    {
        if (source is not IEnumerable sequence) return Array.Empty<object?>();
        return Enumerate(sequence);
    }

    private static IEnumerable<object?> Enumerate(IEnumerable sequence)
    {
        foreach (var item in sequence) yield return item;
    }

    private sealed class HarvestBuffer
    {
        public readonly HashSet<string> Goods = new();
    }
    private sealed class ForesterCache
    {
        public object? Forester;
        public bool Loaded;
    }

    internal sealed class Harvest
    {
        private readonly Func<object, object?> _yielders, _terrain, _cutting, _block, _goods,
            _yieldersList, _getRange, _yield, _cutYielder, _specYielder, _stock,
            _inventory, _cutStock;
        private readonly Action<object, bool> _updateYielders;
        private readonly Func<object, object?, object?> _validYielder, _inArea, _anyObject,
            _grownAt, _cuttableAt, _treeSpec, _cuttable, _cutSpec;
        private readonly Func<object, bool> _isYielding, _isGrown, _removed;
        private readonly Func<object, string?> _goodId, _cutGoodId, _stockGoodId;
        private readonly Func<object, int> _stockAmount;
        private readonly ConditionalWeakTable<object, HarvestBuffer> _buffers = new();

        internal Harvest(Type component)
        {
            _goods = Getter(Field(component, "goods"));
            _yielders = Getter(Field(component, "inRangeYielders"));
            _terrain = Getter(Field(component, "buildingTerrainRange"));
            _cutting = Getter(Field(component, "treeCuttingArea"));
            _block = Getter(Field(component, "blockService"));

            var inRange = Field(component, "inRangeYielders").FieldType;
            var yielder = TypeNamed("Timberborn.Yielding.Yielder");
            _updateYielders = CallBoolean(Method(inRange, "UpdateYielders", 1));
            _yieldersList = Getter(Field(inRange, "_yieldersInRange"));
            var valid = Method(inRange, "IsValidYielder", 2);
            if (!valid.IsStatic || valid.ReturnType != typeof(bool)) throw new MissingMethodException("IsValidYielder");
            _validYielder = Call2(valid);
            _isYielding = Bool(Member(yielder, "IsYielding"));
            var yieldMember = Member(yielder, "Yield");
            _yield = Getter(yieldMember);
            var yieldType = yieldMember is PropertyInfo yp ? yp.PropertyType : ((FieldInfo)yieldMember).FieldType;
            _goodId = Text(Member(yieldType, "GoodId"));

            var terrain = Field(component, "buildingTerrainRange").FieldType;
            var block = Field(component, "blockService").FieldType;
            var cutting = Field(component, "treeCuttingArea").FieldType;
            _getRange = Call0(Method(terrain, "GetRange", 0));
            _inArea = Call1(Method(cutting, "IsInCuttingArea", 1));
            _anyObject = Call1(Method(block, "AnyObjectAt", 1));
            var growable = TypeNamed("Timberborn.Growing.Growable");
            var cuttable = TypeNamed("Timberborn.Cutting.Cuttable");
            var treeSpec = TypeNamed("Timberborn.Cutting.TreeComponentSpec");
            var cuttableSpec = TypeNamed("Timberborn.Cutting.CuttableSpec");
            _grownAt = Call1(Generic(block, "GetFirstObjectWithComponentAt", growable));
            _cuttableAt = Call1(Generic(block, "GetFirstObjectWithComponentAt", cuttable));
            _isGrown = Bool(Member(growable, "IsGrown"));
            _treeSpec = Call0(GenericZero(growable, "GetComponent", treeSpec));
            _cuttable = Call0(GenericZero(growable, "GetComponent", cuttable));
            _cutSpec = Call0(GenericZero(growable, "GetComponent", cuttableSpec));
            _cutYielder = Getter(Member(cuttable, "Yielder"));
            _removed = Bool(Member(yielder, "IsYieldRemoved"));
            var specYielderMember = Member(cuttableSpec, "Yielder");
            _specYielder = Getter(specYielderMember);
            var specYielderType = specYielderMember is FieldInfo sf ? sf.FieldType :
                ((PropertyInfo)specYielderMember).PropertyType;
            var cutYieldMember = Member(specYielderType, "Yield");
            _cutGoodId = Text(Member(cutYieldMember is FieldInfo yf ? yf.FieldType :
                ((PropertyInfo)cutYieldMember).PropertyType, "Id"));

            var stockMember = Field(cuttable, "_goodStack");
            _cutStock = Getter(stockMember);
            var invMember = Member(stockMember.FieldType, "Inventory");
            _inventory = Getter(invMember);
            var invType = invMember is FieldInfo inf ? inf.FieldType : ((PropertyInfo)invMember).PropertyType;
            var inventoryStockMember = Member(invType, "Stock");
            _stock = Getter(inventoryStockMember);
            var stockType = inventoryStockMember is FieldInfo f ? f.FieldType :
                ((PropertyInfo)inventoryStockMember).PropertyType;
            var itemType = stockType.IsArray ? stockType.GetElementType()! :
                stockType.GetInterfaces().Concat(new[] { stockType }).Where(t => t.IsGenericType &&
                    t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    .Select(t => t.GetGenericArguments()[0]).First();
            _stockAmount = Integer(Member(itemType, "Amount"));
            _stockGoodId = Text(Member(itemType, "GoodId"));
        }

        internal bool Scan(object component)
        {
            var current = _goods(component) as HashSet<string>
                ?? throw new InvalidOperationException("SWC harvest goods changed type");
            var scratch = _buffers.GetOrCreateValue(component).Goods;
            scratch.Clear();
            var inRange = _yielders(component);
            if (inRange is not null)
            {
                _updateYielders(inRange, true);
                foreach (var item in Items(_yieldersList(inRange)))
                {
                    if (item is null || !((bool)_validYielder(null!, null, item)! || false) ||
                        !_isYielding(item)) continue;
                    var itemYield = _yield(item);
                    if (itemYield is not null && _goodId(itemYield) is { } goodId) scratch.Add(goodId);
                }
            }
            else if (_terrain(component) is { } terrain)
            {
                var cutting = _cutting(component);
                var block = _block(component);
                foreach (var pos in Items(_getRange(terrain)))
                {
                    if (pos is null || cutting is null || block is null ||
                        !((bool)_inArea(cutting, pos)!) || !((bool)_anyObject(block, pos)!)) continue;
                    var grown = _grownAt(block, pos);
                    if (grown is not null && _isGrown(grown) && _treeSpec(grown) is not null)
                    {
                        var cut = _cuttable(grown);
                        if (cut is not null && _cutYielder(cut) is { } yield && !_removed(yield))
                        {
                            var spec = _cutSpec(grown);
                            var y = spec is null ? null : _specYielder(spec);
                            var source = y is null ? null : _yield(y);
                            if (source is not null && _cutGoodId(source) is { } id) scratch.Add(id);
                        }
                    }
                    var removedCut = _cuttableAt(block, pos);
                    if (removedCut is null || _cutYielder(removedCut) is not { } cutYielder ||
                        !_removed(cutYielder) || _cutStock(removedCut) is not { } stack ||
                        _inventory(stack) is not { } inv) continue;
                    foreach (var item in Items(_stock(inv)))
                        if (item is not null && _stockAmount(item) > 0 &&
                            _stockGoodId(item) is { } goodId) scratch.Add(goodId);
                }
            }
            var changed = !current.SetEquals(scratch);
            if (changed)
            {
                current.Clear();
                current.UnionWith(scratch);
            }
            return changed;
        }
    }

    internal sealed class Plant
    {
        private readonly Func<object, object?> _coordinates, _plantingService, _soil, _blocks,
            _treeCutting, _getCoords, _forester, _livingDead, _treeAt, _livingAt;
        private readonly Func<object, object?, object?> _resource, _inCuttingArea,
            _nonOverridable, _validate;
        private readonly Func<object, object?, object?, object?> _plantingValid;
        private readonly Func<object, bool> _replant, _dead;
        private readonly object _bottom;
        private readonly ConditionalWeakTable<object, ForesterCache> _foresters = new();

        internal Plant(Type component)
        {
            var coordinateField = Field(component, "inRangePlantingCoordinates");
            var coordType = coordinateField.FieldType;
            _coordinates = Getter(coordinateField);
            _getCoords = Call0(Method(coordType, "GetCoordinates", 0));
            _plantingService = Getter(Field(coordType, "_plantingService"));
            var service = Field(coordType, "_plantingService").FieldType;
            _resource = Call1(Method(service, "GetResourceAt", 1));
            _soil = Getter(Field(component, "plantingSoilValidator"));
            _blocks = Getter(Field(component, "blockService"));
            _treeCutting = Getter(Field(component, "treeCuttingArea"));
            var soilType = Field(component, "plantingSoilValidator").FieldType;
            var validateMethod = Method(soilType, "Validate", 1);
            if (validateMethod.ReturnType != typeof(bool)) throw new MissingMethodException("PlantingSoilValidator.Validate");
            var param = validateMethod.GetParameters()[0].ParameterType;
            var constructor = param.GetConstructors(Members).Single(c =>
                c.GetParameters().Length == 3 && c.GetParameters()[1].ParameterType == typeof(string));
            var ctorArgs = constructor.GetParameters();
            var owner = Expression.Parameter(typeof(object), "validator");
            var pos = Expression.Parameter(typeof(object), "pos");
            var resource = Expression.Parameter(typeof(object), "resource");
            var context = Expression.New(constructor,
                Expression.Convert(pos, ctorArgs[0].ParameterType),
                Expression.Convert(resource, typeof(string)),
                Expression.Default(ctorArgs[2].ParameterType));
            _plantingValid = Expression.Lambda<Func<object, object?, object?, object?>>(
                Expression.Convert(Expression.Call(Expression.Convert(owner, soilType),
                    validateMethod, context), typeof(object)), owner, pos, resource).Compile();
            _validate = Call1(validateMethod); // for preflight; not used in the hot loop

            var block = Field(component, "blockService").FieldType;
            var nonOverridable = Method(block, "AnyNonOverridableObjectsAt", 2);
            _nonOverridable = Call2(nonOverridable) is { } any
                ? (obj, position) => any(obj, position, _bottom)
                : throw new MissingMethodException("AnyNonOverridableObjectsAt");
            _bottom = Enum.Parse(nonOverridable.GetParameters()[1].ParameterType, "Bottom");
            var forester = TypeNamed("Timberborn.Forestry.Forester");
            _forester = Call0(GenericZero(component, "GetComponent", forester));
            _replant = Bool(Member(forester, "ReplantDeadTrees"));
            _inCuttingArea = Call1(Method(Field(component, "treeCuttingArea").FieldType, "IsInCuttingArea", 1));
            var tree = TypeNamed("Timberborn.Forestry.TreeComponent");
            var living = TypeNamed("Timberborn.NaturalResourcesLifecycle.LivingNaturalResource");
            _treeAt = Call1(Generic(block, "GetFirstObjectWithComponentAt", tree));
            _livingAt = Call1(Generic(block, "GetFirstObjectWithComponentAt", living));
            _dead = Bool(Member(living, "IsDead"));
            _livingDead = _livingAt;
        }

        internal bool Scan(object component)
        {
            var range = _coordinates(component);
            if (range is null) return false;
            var planting = _plantingService(range);
            var soil = _soil(component);
            var block = _blocks(component);
            var cutArea = _treeCutting(component);
            if (planting is null || soil is null || block is null || cutArea is null)
                throw new InvalidOperationException("SWC planting services not initialized");
            var cache = _foresters.GetOrCreateValue(component);
            if (!cache.Loaded) { cache.Forester = _forester(component); cache.Loaded = true; }
            var replant = cache.Forester is { } f && _replant(f);
            foreach (var pos in Items(_getCoords(range)))
            {
                if (pos is null) continue;
                var good = _resource(planting, pos);
                if (!((bool)_plantingValid(soil, pos, good)!)) continue;
                if (!((bool)_nonOverridable(block, pos)!)) return true;
                if (replant && _treeAt(block, pos) is not null &&
                    !((bool)_inCuttingArea(cutArea, pos)!) &&
                    _livingAt(block, pos) is { } living && _dead(living))
                    return true;
            }
            return false;
        }
    }

    internal static Harvest? CreateHarvest(Type component)
    {
        try { return new Harvest(component); }
        catch (Exception ex)
        {
            System.Console.WriteLine("[TB Optimizer] SWC fast harvest scanner unavailable; native scan kept: " + ex.Message);
            return null;
        }
    }

    internal static Plant? CreatePlant(Type component)
    {
        try { return new Plant(component); }
        catch (Exception ex)
        {
            System.Console.WriteLine("[TB Optimizer] SWC fast planting scanner unavailable; native scan kept: " + ex.Message);
            return null;
        }
    }
}
