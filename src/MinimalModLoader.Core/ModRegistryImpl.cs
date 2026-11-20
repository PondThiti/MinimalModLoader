using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using MinimalModLoader.Abstractions;

namespace MinimalModLoader.Core;

/// <summary>
/// Tracks loaded mods and provides cross-ALC duck-typed Inter-Mod APIs using a collectible
/// Reflection.Emit proxy (compatible with interfaces defined inside collectible mod ALCs).
/// </summary>
public sealed class ModRegistryImpl : IModRegistry
{
    private readonly Dictionary<string, ModManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<object?>> _apiProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _cachedApis = new(StringComparer.OrdinalIgnoreCase);

    internal void RegisterMod(ModManifest manifest, Func<object?>? apiFactory = null)
    {
        _manifests[manifest.UniqueID] = manifest;
        if (apiFactory is not null)
            _apiProviders[manifest.UniqueID] = apiFactory;
    }

    internal void Clear()
    {
        _manifests.Clear();
        _apiProviders.Clear();
        _cachedApis.Clear();
    }

    public bool IsLoaded(string uniqueId) => _manifests.ContainsKey(uniqueId);

    public ModManifest? GetManifest(string uniqueId)
        => _manifests.TryGetValue(uniqueId, out var manifest) ? manifest : null;

    public IReadOnlyCollection<ModManifest> GetAllManifests() => _manifests.Values.ToArray();

    public TInterface? GetApi<TInterface>(string uniqueId) where TInterface : class
    {
        if (!typeof(TInterface).IsInterface)
            throw new ArgumentException($"API contract '{typeof(TInterface).Name}' must be an interface.", nameof(TInterface));

        if (!_cachedApis.TryGetValue(uniqueId, out var rawApi))
        {
            if (!_apiProviders.TryGetValue(uniqueId, out var factory))
                return null;

            rawApi = factory();
            _cachedApis[uniqueId] = rawApi;
        }

        if (rawApi is null)
            return null;

        // Fast path when both mods share the exact same interface assembly
        if (rawApi is TInterface direct)
            return direct;

        // Collectible Reflection.Emit proxy works even when TInterface lives in a collectible ModAssemblyLoadContext
        return CollectibleApiProxyGenerator.CreateProxy<TInterface>(rawApi, uniqueId);
    }

    /// <summary>
    /// Runtime dispatcher invoked by generated duck-typed API proxy classes.
    /// Must be public so collectible dynamic assemblies can call it.
    /// </summary>
    public sealed class ApiProxyInvoker
    {
        private readonly object _target;
        private readonly string _providerId;

        public ApiProxyInvoker(object target, string providerId)
        {
            _target = target;
            _providerId = providerId;
        }

        public object? Invoke(string methodName, Type[] paramTypes, object?[] args)
        {
            var implMethod = _target.GetType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: paramTypes,
                modifiers: null);

            if (implMethod is null)
            {
                throw new MissingMethodException(
                    $"API exposed by mod '{_providerId}' ({_target.GetType().FullName}) does not implement " +
                    $"'{methodName}({string.Join(", ", paramTypes.Select(t => t.Name))})'.");
            }

            try
            {
                return implMethod.Invoke(_target, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }
    }

    private static class CollectibleApiProxyGenerator
    {
        private static readonly MethodInfo InvokerInvokeMethod =
            typeof(ApiProxyInvoker).GetMethod(nameof(ApiProxyInvoker.Invoke))!;

        private static readonly ConstructorInfo ObjectCtor =
            typeof(object).GetConstructor(Type.EmptyTypes)!;

        public static TInterface CreateProxy<TInterface>(object target, string providerId)
            where TInterface : class
        {
            var interfaceType = typeof(TInterface);
            var methods = GetAllInterfaceMethods(interfaceType);
            var paramTypesTable = methods
                .Select(m => m.GetParameters().Select(p => p.ParameterType).ToArray())
                .ToArray();

            var asmName = new AssemblyName($"ModApiProxy_{Guid.NewGuid():N}");
            var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.RunAndCollect);
            var modBuilder = asmBuilder.DefineDynamicModule("Main");

            var typeBuilder = modBuilder.DefineType(
                $"Proxy_{interfaceType.Name}_{Guid.NewGuid():N}",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(object),
                [interfaceType]);

            var invokerField = typeBuilder.DefineField("_invoker", typeof(ApiProxyInvoker), FieldAttributes.Private | FieldAttributes.InitOnly);
            var paramTableField = typeBuilder.DefineField("_paramTypes", typeof(Type[][]), FieldAttributes.Private | FieldAttributes.InitOnly);

            // public Proxy(ApiProxyInvoker invoker, Type[][] paramTypes)
            var ctor = typeBuilder.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                [typeof(ApiProxyInvoker), typeof(Type[][])]);

            var ctorIl = ctor.GetILGenerator();
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, ObjectCtor);
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg_1);
            ctorIl.Emit(OpCodes.Stfld, invokerField);
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg_2);
            ctorIl.Emit(OpCodes.Stfld, paramTableField);
            ctorIl.Emit(OpCodes.Ret);

            for (var methodIdx = 0; methodIdx < methods.Length; methodIdx++)
            {
                var ifaceMethod = methods[methodIdx];
                var parameters = ifaceMethod.GetParameters();
                var paramTypes = paramTypesTable[methodIdx];

                if (parameters.Any(p => p.ParameterType.IsByRef))
                    throw new NotSupportedException($"By-ref parameters in '{interfaceType.Name}.{ifaceMethod.Name}' are not supported by duck-typed API proxies.");

                var methodBuilder = typeBuilder.DefineMethod(
                    ifaceMethod.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    ifaceMethod.ReturnType,
                    paramTypes);

                var il = methodBuilder.GetILGenerator();

                // Load this._invoker
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, invokerField);

                // Arg 1: method name
                il.Emit(OpCodes.Ldstr, ifaceMethod.Name);

                // Arg 2: this._paramTypes[methodIdx]
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, paramTableField);
                il.Emit(OpCodes.Ldc_I4, methodIdx);
                il.Emit(OpCodes.Ldelem_Ref);

                // Arg 3: new object[parameters.Length]
                il.Emit(OpCodes.Ldc_I4, parameters.Length);
                il.Emit(OpCodes.Newarr, typeof(object));

                for (var pIdx = 0; pIdx < parameters.Length; pIdx++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, pIdx);
                    il.Emit(OpCodes.Ldarg, pIdx + 1);
                    if (paramTypes[pIdx].IsValueType)
                        il.Emit(OpCodes.Box, paramTypes[pIdx]);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                // Call _invoker.Invoke(methodName, paramTypes, args)
                il.Emit(OpCodes.Callvirt, InvokerInvokeMethod);

                // Handle return value
                if (ifaceMethod.ReturnType == typeof(void))
                {
                    il.Emit(OpCodes.Pop);
                }
                else if (ifaceMethod.ReturnType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, ifaceMethod.ReturnType);
                }
                else if (ifaceMethod.ReturnType != typeof(object))
                {
                    il.Emit(OpCodes.Castclass, ifaceMethod.ReturnType);
                }

                il.Emit(OpCodes.Ret);
                typeBuilder.DefineMethodOverride(methodBuilder, ifaceMethod);
            }

            var proxyType = typeBuilder.CreateType()!;
            var invoker = new ApiProxyInvoker(target, providerId);
            return (TInterface)Activator.CreateInstance(proxyType, invoker, paramTypesTable)!;
        }

        private static MethodInfo[] GetAllInterfaceMethods(Type interfaceType)
        {
            var set = new HashSet<MethodInfo>();
            foreach (var m in interfaceType.GetMethods())
                set.Add(m);
            foreach (var parent in interfaceType.GetInterfaces())
            {
                foreach (var m in parent.GetMethods())
                    set.Add(m);
            }
            return set.ToArray();
        }
    }
}
