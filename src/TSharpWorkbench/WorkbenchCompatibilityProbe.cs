using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

// A conservative static probe of whether the Workbench Proxy files (the consumers) can bind to an
// application's own framework build (the providers) when that build has the same version but other content (FW003).
// Equal version metadata proves nothing, and neither does a member count; the probe checks what the consumers actually
// reference, in the metadata only (nothing is loaded or started):
//  * every assembly reference: name, version not lower, public key token;
//  * every type reference: defined (or forwarded to a probed assembly), accessible;
//  * every member reference (methods, fields, properties' accessors, attribute constructors): same name and signature,
//    found on the type or its base chain inside the provider, accessible (internal members need InternalsVisibleTo);
//  * every consumer type deriving from or implementing a provider type: base not sealed, kind unchanged, no abstract or
//    interface member left unimplemented;
//  * against the build the consumers were compiled against (the payload's framework): constants and enum values of the
//    referenced types unchanged (they are compiled into the consumer), and the field layout of referenced value types.
// A finding that cannot be checked counts as a hard incompatibility ("cannot be established"), never as compatible. The
// probe first runs against the reference build itself; if that does not come out clean, its result is inconclusive.
internal sealed record ProbeResult(bool Safe, string[] Hard, string[] Checked, int Types, int Members)
{
    internal bool Inconclusive { get; init; }
    internal string Summary => Inconclusive ? "Static probe inconclusive: " + (Hard.FirstOrDefault() ?? "the reference build could not be validated.")
        : Safe ? $"Static probe: no hard incompatibility ({Types} types and {Members} members the Proxy uses were found, with the same signatures and access)."
        : $"Static probe: {Hard.Length} hard incompatibilit{(Hard.Length == 1 ? "y" : "ies")}; first: {Hard[0]}";
}

internal static class CompatibilityProbe
{
    // What a clean probe still does not prove. Shown before the risk-accepted fix and in the report.
    internal static readonly string[] RemainingRisks =
    [
        "Behaviour: method bodies may do something else with the same signature (for example a driver handshake or event timing).",
        "Virtual dispatch and overrides the Proxy relies on at runtime may differ.",
        "Static initialisation, configuration and resource lookups of the application's framework build are not checked.",
        "Native SDL libraries the application ships are used as they are; their behaviour is not checked.",
        "Only the headless handshake is verified afterwards (Verify target); scenario behaviour is proven only by running scenarios."
    ];

    internal static ProbeResult Run(IReadOnlyList<string> consumers, IReadOnlyDictionary<string, string> providers, IReadOnlyDictionary<string, string> reference)
    {
        // Self-check: the same probe against the builds the consumers were compiled against must be clean.
        var baseline = Probe(consumers, reference, null);
        if (baseline.Hard.Length > 0)
            return new(false, ["the probe does not validate the payload's own framework build: " + baseline.Hard[0]], baseline.Checked, 0, 0) { Inconclusive = true };
        return Probe(consumers, providers, reference);
    }

    private static ProbeResult Probe(IReadOnlyList<string> consumers, IReadOnlyDictionary<string, string> providers, IReadOnlyDictionary<string, string>? reference)
    {
        var hard = new List<string>(); var checkedLines = new List<string>();
        var loaded = new Dictionary<string, Asm?>(StringComparer.OrdinalIgnoreCase);
        var referenceLoaded = new Dictionary<string, Asm?>(StringComparer.OrdinalIgnoreCase);
        int types = 0, members = 0;
        try
        {
            Asm? Provider(string name)
            {
                if (loaded.TryGetValue(name, out var known)) return known;
                return loaded[name] = providers.TryGetValue(name, out string? path) ? Asm.Open(path) : null;
            }
            Asm? Reference(string name)
            {
                if (reference is null) return null;
                if (referenceLoaded.TryGetValue(name, out var known)) return known;
                return referenceLoaded[name] = reference.TryGetValue(name, out string? path) ? Asm.Open(path) : null;
            }
            foreach (string consumerPath in consumers)
            {
                using var consumer = Asm.Open(consumerPath);
                var md = consumer.Md;
                var names = new Names(md);
                int beforeTypes = types, beforeMembers = members;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Assembly references to probed providers.
                foreach (var h in md.AssemblyReferences)
                {
                    var r = md.GetAssemblyReference(h);
                    string name = md.GetString(r.Name);
                    if (Provider(name) is not { } a) continue;
                    used.Add(name);
                    if (a.Version < r.Version) hard.Add($"{consumer.Name} references {name} {r.Version}; the application's build is {a.Version}.");
                    var token = md.GetBlobBytes(r.PublicKeyOrToken);
                    if (token.Length > 0 && !token.AsSpan().SequenceEqual(a.Token)) hard.Add($"{consumer.Name} references {name} with another public key token.");
                }
                // Type references.
                foreach (var h in md.TypeReferences)
                {
                    if (Scope(md, h) is not { } scope || Provider(scope) is not { } a) continue;
                    string full = names.Reference(h);
                    types++;
                    if (Resolve(a, full, Provider) is not { } found) { hard.Add($"{full} ({scope}) is missing in the application's build."); continue; }
                    if (!found.Asm.TypeAccessible(found.Handle, consumer.Name)) hard.Add($"{full} ({scope}) is not accessible to {consumer.Name}.");
                    if (Reference(scope) is { } refAsm && refAsm.Types.TryGetValue(full, out var refType)) Compare(refAsm, refType, found.Asm, found.Handle, full, hard);
                }
                // Member references.
                foreach (var h in md.MemberReferences)
                {
                    var m = md.GetMemberReference(h);
                    if (ParentType(md, m.Parent) is not { } parent || Scope(md, parent) is not { } scope || Provider(scope) is not { } a) continue;
                    string full = names.Reference(parent), name = md.GetString(m.Name);
                    bool field = m.GetKind() == MemberReferenceKind.Field;
                    string signature = field ? m.DecodeFieldSignature(names, null) : Method(m.DecodeMethodSignature(names, null));
                    members++;
                    if (Resolve(a, full, Provider) is not { } owner) continue; // reported with the type references
                    var member = FindMember(owner.Asm, owner.Handle, name, signature, field, Provider);
                    if (member is null) hard.Add($"{full}.{name} {signature} ({scope}) is missing or has another signature in the application's build.");
                    else if (!Accessible(member.Value.Access, member.Value.Asm, consumer.Name)) hard.Add($"{full}.{name} ({scope}) is not accessible to {consumer.Name}.");
                }
                // Consumer types deriving from or implementing provider types.
                foreach (var h in md.TypeDefinitions)
                {
                    var t = md.GetTypeDefinition(h);
                    var own = OwnMethods(md, names, t);
                    var inherited = new HashSet<string>(StringComparer.Ordinal);
                    if (!t.BaseType.IsNil && t.BaseType.Kind == HandleKind.TypeReference && Scope(md, (TypeReferenceHandle)t.BaseType) is { } bs && Provider(bs) is { } ba
                        && Resolve(ba, names.Reference((TypeReferenceHandle)t.BaseType), Provider) is { } b)
                    {
                        var bd = b.Asm.Md.GetTypeDefinition(b.Handle);
                        string baseName = names.Reference((TypeReferenceHandle)t.BaseType);
                        if (bd.Attributes.HasFlag(TypeAttributes.Sealed)) hard.Add($"{names.Definition(h)} derives from {baseName}, which is sealed in the application's build.");
                        if (bd.Attributes.HasFlag(TypeAttributes.Interface)) hard.Add($"{names.Definition(h)} derives from {baseName}, which is an interface in the application's build.");
                        foreach (string missing in Abstracts(b.Asm, b.Handle, inherited, Provider).Where(r => !own.Contains(r)))
                            hard.Add($"{names.Definition(h)} does not implement {baseName} member {missing} (abstract in the application's build).");
                    }
                    foreach (var ih in t.GetInterfaceImplementations())
                    {
                        var iface = md.GetInterfaceImplementation(ih).Interface;
                        if (iface.Kind != HandleKind.TypeReference || Scope(md, (TypeReferenceHandle)iface) is not { } s || Provider(s) is not { } ia) continue;
                        string ifaceName = names.Reference((TypeReferenceHandle)iface);
                        if (Resolve(ia, ifaceName, Provider) is not { } i) continue;
                        var id = i.Asm.Md.GetTypeDefinition(i.Handle);
                        if (!id.Attributes.HasFlag(TypeAttributes.Interface)) { hard.Add($"{names.Definition(h)} implements {ifaceName}, which is not an interface in the application's build."); continue; }
                        foreach (string required in InterfaceMembers(i.Asm, i.Handle, Provider))
                            if (!own.Contains(required) && !inherited.Contains(required) && !own.Contains(ifaceName + "." + required))
                                hard.Add($"{names.Definition(h)} does not implement {ifaceName} member {required} (required in the application's build).");
                    }
                }
                if (used.Count > 0)
                    checkedLines.Add($"{consumer.Name}: {types - beforeTypes} type and {members - beforeMembers} member references checked against "
                        + string.Join(", ", used.Order(StringComparer.Ordinal).Select(name => $"{name} {Provider(name)!.Version} (SHA-256 {Preparation.FileHash(providers[name])[..12]})")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            hard.Add("the metadata cannot be read: " + ex.Message);
        }
        finally
        {
            foreach (var a in loaded.Values) a?.Dispose();
            foreach (var a in referenceLoaded.Values) a?.Dispose();
        }
        var distinct = hard.Distinct().ToArray();
        return new(distinct.Length == 0, distinct, checkedLines.ToArray(), types, members);
    }

    // ── metadata helpers ─────────────────────────────────────────────────────────────

    // The assembly a type reference (or its outermost declaring reference) resolves to; null for module-local scopes.
    private static string? Scope(MetadataReader md, TypeReferenceHandle h)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            var r = md.GetTypeReference(h);
            if (r.ResolutionScope.Kind == HandleKind.TypeReference) { h = (TypeReferenceHandle)r.ResolutionScope; continue; }
            return r.ResolutionScope.Kind == HandleKind.AssemblyReference ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)r.ResolutionScope).Name) : null;
        }
        return null;
    }

    // A member reference's declaring type: a type reference, or the generic type of an instantiated type specification.
    private static TypeReferenceHandle? ParentType(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference) return (TypeReferenceHandle)parent;
        if (parent.Kind != HandleKind.TypeSpecification) return null;
        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        blob.ReadSignatureTypeCode();
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)generic : null;
    }

    private static string Method(MethodSignature<string> s) =>
        (s.Header.IsInstance ? "instance " : "") + (s.GenericParameterCount > 0 ? $"<{s.GenericParameterCount}>" : "") + s.ReturnType + "(" + string.Join(",", s.ParameterTypes) + ")";

    private sealed record Found(Asm Asm, TypeDefinitionHandle Handle);
    // A type by full name in a provider, following type forwarders into other probed providers.
    private static Found? Resolve(Asm a, string full, Func<string, Asm?> provider, int depth = 0)
    {
        if (a.Types.TryGetValue(full, out var h)) return new(a, h);
        if (depth < 4 && a.Forwarded.TryGetValue(full, out string? target) && provider(target) is { } next) return Resolve(next, full, provider, depth + 1);
        return null;
    }

    // A method or field on the type or its base chain (within probed providers): its assembly and access.
    private static (Asm Asm, int Access)? FindMember(Asm a, TypeDefinitionHandle h, string name, string signature, bool field, Func<string, Asm?> provider)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            var md = a.Md; var names = a.Names; var t = md.GetTypeDefinition(h);
            if (field)
            {
                foreach (var fh in t.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    if (md.StringComparer.Equals(f.Name, name) && f.DecodeSignature(names, null) == signature) return (a, (int)(f.Attributes & FieldAttributes.FieldAccessMask));
                }
            }
            else
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (md.StringComparer.Equals(m.Name, name) && Method(m.DecodeSignature(names, null)) == signature) return (a, (int)(m.Attributes & MethodAttributes.MemberAccessMask));
                }
            if (Base(a, t, provider) is not { } b) return null;
            (a, h) = (b.Asm, b.Handle);
        }
        return null;
    }
    private static Found? Base(Asm a, TypeDefinition t, Func<string, Asm?> provider)
    {
        if (t.BaseType.IsNil) return null;
        if (t.BaseType.Kind == HandleKind.TypeDefinition) return new(a, (TypeDefinitionHandle)t.BaseType);
        if (t.BaseType.Kind == HandleKind.TypeReference && Scope(a.Md, (TypeReferenceHandle)t.BaseType) is { } s && provider(s) is { } next)
            return Resolve(next, a.Names.Reference((TypeReferenceHandle)t.BaseType), provider);
        return null;
    }

    // Public, protected (the consumer derives) or protected internal: accessible. internal: only with InternalsVisibleTo.
    private static bool Accessible(int access, Asm owner, string consumer) => access switch
    {
        (int)MethodAttributes.Public or (int)MethodAttributes.Family or (int)MethodAttributes.FamORAssem => true,
        (int)MethodAttributes.Assembly or (int)MethodAttributes.FamANDAssem => owner.Friends.Contains(consumer),
        _ => false
    };

    // Methods a consumer type declares (name + signature), and the interface methods it implements explicitly.
    private static HashSet<string> OwnMethods(MetadataReader md, Names names, TypeDefinition t)
    {
        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            own.Add(md.GetString(m.Name) + " " + Method(m.DecodeSignature(names, null)));
        }
        foreach (var ih in t.GetMethodImplementations())
        {
            var decl = md.GetMethodImplementation(ih).MethodDeclaration;
            if (decl.Kind != HandleKind.MemberReference) continue;
            var r = md.GetMemberReference((MemberReferenceHandle)decl);
            if (ParentType(md, r.Parent) is { } p) own.Add(names.Reference(p) + "." + md.GetString(r.Name) + " " + Method(r.DecodeMethodSignature(names, null)));
        }
        return own;
    }

    // Abstract instance methods of a provider type's chain that no class of the chain implements; inherited collects the
    // implemented ones (they also satisfy interfaces).
    private static IEnumerable<string> Abstracts(Asm a, TypeDefinitionHandle h, HashSet<string> inherited, Func<string, Asm?> provider)
    {
        var required = new List<string>();
        for (int depth = 0; depth < 32; depth++)
        {
            var md = a.Md; var t = md.GetTypeDefinition(h);
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.Attributes.HasFlag(MethodAttributes.Static)) continue;
                string key = md.GetString(m.Name) + " " + Method(m.DecodeSignature(a.Names, null));
                if (m.Attributes.HasFlag(MethodAttributes.Abstract)) { if (!inherited.Contains(key)) required.Add(key); }
                else inherited.Add(key);
            }
            if (Base(a, t, provider) is not { } b) break;
            (a, h) = (b.Asm, b.Handle);
        }
        return required.Distinct();
    }

    // Abstract members of an interface and of the interfaces it extends (default implementations are not required).
    private static IEnumerable<string> InterfaceMembers(Asm a, TypeDefinitionHandle h, Func<string, Asm?> provider, int depth = 0)
    {
        var md = a.Md; var t = md.GetTypeDefinition(h);
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (m.Attributes.HasFlag(MethodAttributes.Abstract) && !m.Attributes.HasFlag(MethodAttributes.Static))
                yield return md.GetString(m.Name) + " " + Method(m.DecodeSignature(a.Names, null));
        }
        if (depth > 8) yield break;
        foreach (var ih in t.GetInterfaceImplementations())
        {
            var i = md.GetInterfaceImplementation(ih).Interface;
            Found? f = i.Kind == HandleKind.TypeDefinition ? new(a, (TypeDefinitionHandle)i)
                : i.Kind == HandleKind.TypeReference && Scope(md, (TypeReferenceHandle)i) is { } s && provider(s) is { } next ? Resolve(next, a.Names.Reference((TypeReferenceHandle)i), provider) : null;
            if (f is not null) foreach (string m in InterfaceMembers(f.Asm, f.Handle, provider, depth + 1)) yield return m;
        }
    }

    // Against the build the consumer was compiled against: constant values (compiled into the consumer) and the layout of
    // value types (the consumer allocates them).
    private static void Compare(Asm reference, TypeDefinitionHandle rh, Asm app, TypeDefinitionHandle ah, string full, List<string> hard)
    {
        var rt = reference.Md.GetTypeDefinition(rh); var at = app.Md.GetTypeDefinition(ah);
        var appFields = at.GetFields().Select(app.Md.GetFieldDefinition).ToArray();
        foreach (var f in rt.GetFields().Select(reference.Md.GetFieldDefinition).Where(f => f.Attributes.HasFlag(FieldAttributes.Literal)))
        {
            string name = reference.Md.GetString(f.Name);
            var other = appFields.FirstOrDefault(x => app.Md.StringComparer.Equals(x.Name, name) && x.Attributes.HasFlag(FieldAttributes.Literal));
            if (other.Name.IsNil) continue;
            if (!Constant(reference.Md, f).SequenceEqual(Constant(app.Md, other))) hard.Add($"the constant {full}.{name} has another value in the application's build (constants are compiled into the Proxy).");
        }
        bool valueType = Base(reference, rt) is "System.ValueType" or "System.Enum";
        if (!valueType) return;
        string Layout(Asm a, TypeDefinition t) => string.Join(";", t.GetFields().Select(a.Md.GetFieldDefinition).Where(f => !f.Attributes.HasFlag(FieldAttributes.Static))
            .Select(f => a.Md.GetString(f.Name) + ":" + f.DecodeSignature(a.Names, null))) + "|" + t.GetLayout().Size + "/" + t.GetLayout().PackingSize + "/" + (int)(t.Attributes & TypeAttributes.LayoutMask);
        if (Layout(reference, rt) != Layout(app, at)) hard.Add($"the value type {full} has another field layout in the application's build.");
    }
    private static string? Base(Asm a, TypeDefinition t) => t.BaseType.Kind == HandleKind.TypeReference ? a.Names.Reference((TypeReferenceHandle)t.BaseType) : null;
    private static byte[] Constant(MetadataReader md, FieldDefinition f)
    {
        var c = md.GetConstant(f.GetDefaultValue());
        return [(byte)c.TypeCode, .. md.GetBlobBytes(c.Value)];
    }

    // ── one opened assembly ──────────────────────────────────────────────────────────

    private sealed class Asm : IDisposable
    {
        private readonly FileStream stream;
        private readonly PEReader pe;
        internal MetadataReader Md { get; }
        internal Names Names { get; }
        internal string Name { get; }
        internal Version Version { get; }
        internal byte[] Token { get; }
        internal Dictionary<string, TypeDefinitionHandle> Types { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Forwarded { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Friends { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Asm(string path)
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new BadImageFormatException(Path.GetFileName(path) + " has no .NET metadata.");
            Md = pe.GetMetadataReader();
            Names = new Names(Md);
            var def = Md.GetAssemblyDefinition();
            Name = Md.GetString(def.Name); Version = def.Version;
            var key = Md.GetBlobBytes(def.PublicKey);
            Token = key.Length == 0 ? [] : SHA1.HashData(key)[^8..].Reverse().ToArray();
            foreach (var h in Md.TypeDefinitions) Types[Names.Definition(h)] = h;
            foreach (var h in Md.ExportedTypes)
            {
                var e = Md.GetExportedType(h);
                if (e.Implementation.Kind == HandleKind.AssemblyReference)
                    Forwarded[(e.Namespace.IsNil ? "" : Md.GetString(e.Namespace) + ".") + Md.GetString(e.Name)] = Md.GetString(Md.GetAssemblyReference((AssemblyReferenceHandle)e.Implementation).Name);
            }
            foreach (var h in Md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var a = Md.GetCustomAttribute(h);
                if (AttributeType(a) != "System.Runtime.CompilerServices.InternalsVisibleToAttribute") continue;
                var blob = Md.GetBlobReader(a.Value);
                if (blob.Length < 3 || blob.ReadUInt16() != 1) continue;
                if (blob.ReadSerializedString() is { } friend) Friends.Add(friend.Split(',')[0].Trim());
            }
        }
        internal static Asm Open(string path) => new(path);
        private string? AttributeType(CustomAttribute a) => a.Constructor.Kind switch
        {
            HandleKind.MemberReference when Md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent is { Kind: HandleKind.TypeReference } p => Names.Reference((TypeReferenceHandle)p),
            HandleKind.MethodDefinition => Names.Definition(Md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType()),
            _ => null
        };
        // Public (or nested public/protected) types are accessible; internal ones only with InternalsVisibleTo.
        internal bool TypeAccessible(TypeDefinitionHandle h, string consumer)
        {
            for (int depth = 0; depth < 32 && !h.IsNil; depth++)
            {
                var t = Md.GetTypeDefinition(h);
                switch (t.Attributes & TypeAttributes.VisibilityMask)
                {
                    case TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem: break;
                    case TypeAttributes.NotPublic or TypeAttributes.NestedAssembly or TypeAttributes.NestedFamANDAssem: if (!Friends.Contains(consumer)) return false; break;
                    default: return false;
                }
                h = t.GetDeclaringType();
            }
            return true;
        }
        public void Dispose() { pe.Dispose(); stream.Dispose(); }
    }

    // Type names as signatures spell them, identical for definitions and references ("Namespace.Outer/Inner`1").
    private sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        internal string Definition(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            var declaring = t.GetDeclaringType();
            string name = md.GetString(t.Name);
            return !declaring.IsNil ? Definition(declaring) + "/" + name : t.Namespace.IsNil || md.GetString(t.Namespace).Length == 0 ? name : md.GetString(t.Namespace) + "." + name;
        }
        internal string Reference(TypeReferenceHandle h)
        {
            var r = md.GetTypeReference(h);
            string name = md.GetString(r.Name);
            return r.ResolutionScope.Kind == HandleKind.TypeReference ? Reference((TypeReferenceHandle)r.ResolutionScope) + "/" + name
                : r.Namespace.IsNil || md.GetString(r.Namespace).Length == 0 ? name : md.GetString(r.Namespace) + "." + name;
        }
        private static string Kind(byte raw) => raw == (byte)SignatureTypeKind.ValueType ? "valuetype " : "";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Kind(rawTypeKind) + Definition(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Kind(rawTypeKind) + Reference(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + shape.Rank + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + Method(signature);
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => isRequired ? unmodifiedType + " modreq(" + modifier + ")" : unmodifiedType;
    }
}
