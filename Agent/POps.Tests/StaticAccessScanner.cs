using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace POps.Tests
{
    // Bir test sınıfının, kendi kodundan ve çağırdığı kodlardan (POpsAgent, POps.Shared, MSI custom action'ları ve test
    // projesinin yardımcıları) hangi değişebilir statiklere ve makine kaynaklarına ulaştığını IL'den bulur.
    //
    // Değişebilir statik: bu derlemelerde statik kurucu dışında bir yerde yazılan statik alan (otomatik özelliklerin
    // alanları dahil; set erişimcisini kimsenin çağırmadığı bir ayar sabit gibidir) ya da bir yerde içeriği değiştirilen
    // statik koleksiyon (Dictionary, HashSet, List ...; readonly olsa da). Okumak da yazmak da sayılır. Süreç genelindeki
    // durumu değiştiren çağrılar (ortam değişkeni, çalışma klasörü) yazma, makine kaynakları (süreç başlatmak, adlandırılmış
    // boru, dinleyen soket, Thread.Sleep) ayrıca sayılır. Çağrılar, lambda'lar (ldftn), async/yineleyici durum makineleri
    // ve iç içe türler izlenir.
    //
    // Sınırlar (sezgisel bir denetim): arayüz ve sanal çağrılarda gerçekleştirmeye inilmez; reflection ve dynamic ile
    // yapılan çağrılar görünmez; bir yolun gerçekten çalışıp çalışmadığı bilinmez (ulaşılabilen her şey sayılır, test
    // kendi sahtesini verse de varsayılan temsilci sayılır); statik kurucular (tür bir kez başlatılır), [ThreadStatic]
    // alanlar ve derleyicinin ürettiği önbellekler (lambda önbelleği, dynamic çağrı noktaları) sayılmaz; koleksiyonun
    // değiştiği, bir ekleme/silme çağrısının alıcısı olmasından anlaşılır (yığın yalnızca bunun için izlenir).
    internal static class StaticAccessScanner
    {
        public enum Kind { Read, Write, Machine }

        public sealed class Finding
        {
            public string Member { get; set; }
            public Kind Kind { get; set; }
            // Erişimin olduğu yöntem ("Tür.Yöntem") ve test sınıfından oraya giden en kısa yol
            public string At { get; set; }
            public string Path { get; set; }
            public override string ToString() => $"{Kind} {Member} ({Path})";
        }

        private sealed class Summary
        {
            public readonly List<(FieldInfo Field, bool Write)> Fields = new List<(FieldInfo, bool)>();
            public readonly List<(string Member, Kind Kind)> Calls = new List<(string, Kind)>();
            public readonly List<MethodBase> Callees = new List<MethodBase>();
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null))
            .ToDictionary(op => op.Value);

        // İçeriği değişebilen koleksiyonlar ve içeriği değiştiren yöntemleri
        private static readonly string[] MutableCollections =
        {
            "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.HashSet`1", "System.Collections.Generic.List`1",
            "System.Collections.Generic.Queue`1", "System.Collections.Generic.Stack`1", "System.Collections.Generic.SortedDictionary`2",
            "System.Collections.Generic.SortedSet`1", "System.Collections.Generic.LinkedList`1",
            "System.Collections.Concurrent.ConcurrentDictionary`2", "System.Collections.Concurrent.ConcurrentQueue`1",
            "System.Collections.Concurrent.ConcurrentBag`1", "System.Collections.Concurrent.ConcurrentStack`1",
        };

        private static readonly string[] MutatingMethods =
        {
            "set_Item", "Add", "AddRange", "Insert", "InsertRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "RemoveWhere",
            "Clear", "Sort", "Reverse", "TryAdd", "TryRemove", "TryUpdate", "AddOrUpdate", "GetOrAdd", "Enqueue", "Dequeue",
            "TryDequeue", "Push", "Pop", "TryPop", "TryTake", "UnionWith", "ExceptWith", "IntersectWith", "SymmetricExceptWith",
            "AddFirst", "AddLast", "RemoveFirst", "RemoveLast", "TrimExcess",
        };

        // Süreç genelindeki durumu değiştiren BCL çağrıları (tür, yöntem)
        private static readonly (string Type, string Method)[] ProcessWideWrites =
        {
            ("System.Environment", "SetEnvironmentVariable"),
            ("System.Environment", "set_CurrentDirectory"),
            ("System.IO.Directory", "SetCurrentDirectory"),
        };

        // Makine kaynakları (tür, yöntem; ".ctor" kurucu)
        private static readonly (string Type, string Method)[] MachineCalls =
        {
            ("System.Diagnostics.Process", "Start"),
            ("System.IO.Pipes.NamedPipeServerStream", ".ctor"),
            ("System.IO.Pipes.NamedPipeClientStream", ".ctor"),
            ("System.IO.Pipes.NamedPipeServerStreamAcl", "Create"),
            ("System.Net.Sockets.TcpListener", ".ctor"),
            ("System.Net.Sockets.UdpClient", ".ctor"),
            ("System.Net.Sockets.Socket", "Bind"),
            ("System.Net.HttpListener", ".ctor"),
            ("System.Threading.Thread", "Sleep"),
        };

        private static readonly object Gate = new object();
        private static readonly Dictionary<(Guid, int), Summary> Summaries = new Dictionary<(Guid, int), Summary>();
        private static HashSet<(Guid, int)> _mutable;

        // Bu derlemelerin içine inilir; geri kalan (BCL, xUnit, NuGet paketleri) yalnızca çağrı olarak görülür
        public static bool IsScanned(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return assembly == typeof(StaticAccessScanner).Assembly
                || name == "POpsAgent" || name == "POps.Shared" || name == "PopsInstallerActions";
        }

        public static IEnumerable<Assembly> ScannedAssemblies()
        {
            Assembly tests = typeof(StaticAccessScanner).Assembly;
            yield return tests;
            foreach (AssemblyName name in tests.GetReferencedAssemblies())
                if (name.Name == "POpsAgent" || name.Name == "POps.Shared" || name.Name == "PopsInstallerActions")
                    yield return Assembly.Load(name);
        }

        // Test sınıfının ve iç içe türlerinin (lambda kapanışları, durum makineleri, yardımcı sınıflar) bütün yöntemlerinden
        // başlayarak ulaşılan değişebilir statikler ve makine kaynakları; her (üye, tür, erişen yöntem) için en kısa yol.
        // skip: içine inilmeyen yöntemler (ör. SharedStateTestBase'in kurucusu: EnsureIsolated sınıfın kendi erişimi değildir)
        public static List<Finding> Scan(Type testClass, Func<MethodBase, bool> skip = null)
        {
            HashSet<(Guid, int)> mutable = MutableFields();
            var parent = new Dictionary<(Guid, int), MethodBase>();
            var queue = new Queue<MethodBase>();
            foreach (Type type in WithNested(testClass))
                foreach (MethodBase root in DeclaredMethods(type))
                    if (!parent.ContainsKey(Key(root)))
                    {
                        parent.Add(Key(root), null);
                        queue.Enqueue(root);
                    }

            var findings = new Dictionary<(string, Kind, string), Finding>();
            void Add(string member, Kind kind, MethodBase at)
            {
                string where = Describe(at);
                if (!findings.ContainsKey((member, kind, where)))
                    findings[(member, kind, where)] = new Finding { Member = member, Kind = kind, At = where, Path = PathTo(at, parent) };
            }

            while (queue.Count > 0)
            {
                MethodBase method = queue.Dequeue();
                Summary summary = Summarize(method);
                foreach ((FieldInfo field, bool write) in summary.Fields)
                    if (mutable.Contains(Key(field))) Add(MemberName(field), write ? Kind.Write : Kind.Read, method);
                foreach ((string member, Kind kind) in summary.Calls) Add(member, kind, method);
                foreach (MethodBase callee in summary.Callees)
                    if (!parent.ContainsKey(Key(callee)) && (skip == null || !skip(callee)))
                    {
                        parent.Add(Key(callee), method);
                        queue.Enqueue(callee);
                    }
            }
            return findings.Values.OrderBy(f => f.Kind).ThenBy(f => f.Member, StringComparer.Ordinal).ThenBy(f => f.At, StringComparer.Ordinal).ToList();
        }

        // Taranan derlemelerin bütün yöntemlerinde statik kurucu dışında yazılan alanlar ve içeriği değiştirilen koleksiyonlar.
        // Otomatik özelliğin set erişimcisi alanı yazar ama özellik ancak biri o erişimciyi çağırırsa değişir: hiç
        // atanmayan bir ayar sabit gibidir.
        private static HashSet<(Guid, int)> MutableFields()
        {
            lock (Gate)
            {
                if (_mutable != null) return _mutable;
                var methods = ScannedAssemblies().SelectMany(Types).SelectMany(DeclaredMethods).ToList();
                var called = new HashSet<(Guid, int)>(methods.SelectMany(m => SummarizeLocked(m).Callees).Select(Key));
                var mutable = new HashSet<(Guid, int)>();
                foreach (MethodBase method in methods)
                {
                    bool autoSetter = method.Name.StartsWith("set_", StringComparison.Ordinal) && method.IsDefined(typeof(CompilerGeneratedAttribute), false);
                    if (autoSetter && !called.Contains(Key(method))) continue;
                    foreach ((FieldInfo field, bool write) in SummarizeLocked(method).Fields)
                        if (write) mutable.Add(Key(field));
                }
                _mutable = mutable;
                return mutable;
            }
        }

        private static IEnumerable<Type> Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        private static string PathTo(MethodBase method, Dictionary<(Guid, int), MethodBase> parent)
        {
            var chain = new List<string>();
            for (MethodBase m = method; m != null && chain.Count < 12; m = parent[Key(m)]) chain.Add(Describe(m));
            chain.Reverse();
            return string.Join(" -> ", chain);
        }

        // "Tür.Yöntem"; lambda ve durum makinesi yöntemleri onları yazan türün adıyla
        public static string Describe(MethodBase method)
        {
            Type type = method.DeclaringType;
            while (type != null && IsCompilerGenerated(type) && type.DeclaringType != null) type = type.DeclaringType;
            return (type?.Name ?? "?") + "." + method.Name;
        }

        private static IEnumerable<Type> WithNested(Type type)
        {
            yield return type;
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (Type t in WithNested(nested)) yield return t;
        }

        private static IEnumerable<MethodBase> DeclaredMethods(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            return type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
        }

        private static (Guid, int) Key(MemberInfo member) => (member.Module.ModuleVersionId, member.MetadataToken);

        private static bool IsCompilerGenerated(Type type)
        {
            for (Type t = type; t != null; t = t.DeclaringType)
                if (t.Name.StartsWith("<", StringComparison.Ordinal) || t.IsDefined(typeof(CompilerGeneratedAttribute), false)) return true;
            return false;
        }

        private static Summary Summarize(MethodBase method)
        {
            lock (Gate) return SummarizeLocked(method);
        }

        private static Summary SummarizeLocked(MethodBase method)
        {
            if (Summaries.TryGetValue(Key(method), out Summary cached)) return cached;
            var summary = new Summary();
            Summaries[Key(method)] = summary;
            Fill(method, summary);
            return summary;
        }

        private static void Fill(MethodBase method, Summary summary)
        {
            // async ve yineleyici yöntemlerin gövdesi durum makinesindedir; çağıran yalnızca makineyi başlatır
            StateMachineAttribute stateMachine = method.GetCustomAttribute<StateMachineAttribute>();
            if (stateMachine != null) summary.Callees.AddRange(DeclaredMethods(stateMachine.StateMachineType));

            MethodBody body;
            byte[] il;
            try
            {
                body = method.GetMethodBody();
                il = body?.GetILAsByteArray();
            }
            catch (InvalidOperationException) { return; }
            if (il == null) return;
            // Statik kurucu (MethodBase.IsConstructor statik kurucuda false döner)
            bool typeInitializer = method is ConstructorInfo && method.IsStatic;
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            Module module = method.Module;

            // Değerlendirme yığını yalnızca "bu değer şu readonly statik koleksiyondur" bilgisi için izlenir: koleksiyon,
            // bir ekleme/silme çağrısının alıcısı olduğunda değişmiş sayılır. Dallanma hedeflerinde yığın derinliği
            // dallanmadan alınır (C# derleyicisinin ürettiği IL için yeterli).
            var stack = new List<FieldInfo>();
            var depthAt = new Dictionary<int, int>();
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                bool catches = clause.Flags == ExceptionHandlingClauseOptions.Clause || clause.Flags == ExceptionHandlingClauseOptions.Filter;
                depthAt[clause.HandlerOffset] = catches ? 1 : 0;
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter) depthAt[clause.FilterOffset] = 1;
            }
            bool unreachable = false;

            int pos = 0;
            while (pos < il.Length)
            {
                if (depthAt.TryGetValue(pos, out int depth)) SetDepth(stack, depth);
                else if (unreachable) stack.Clear();
                unreachable = false;

                OpCode op;
                byte first = il[pos++];
                if (first == 0xFE) op = OpCodesByValue[unchecked((short)(0xFE00 | il[pos++]))];
                else op = OpCodesByValue[first];
                int operand = pos;
                pos += OperandSize(op.OperandType, il, pos);

                FieldInfo pushed = null;
                int pops, pushes;
                if (op.OperandType == OperandType.InlineMethod && op != OpCodes.Ldftn && op != OpCodes.Ldvirtftn)
                {
                    MethodBase callee = Resolve(() => module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArgs, methodArgs));
                    if (callee == null)
                    {
                        stack.Clear();
                        continue;
                    }
                    bool hasThis = !callee.IsStatic && op != OpCodes.Newobj;
                    pops = callee.GetParameters().Length + (hasThis ? 1 : 0);
                    pushes = op == OpCodes.Newobj || (callee is MethodInfo info && info.ReturnType != typeof(void)) ? 1 : 0;
                    if (hasThis && pops <= stack.Count && stack[stack.Count - pops] is FieldInfo receiver
                        && IsMutableCollection(callee.DeclaringType) && MutatingMethods.Contains(callee.Name))
                        summary.Fields.Add((receiver, true));
                    Inspect(callee, summary);
                }
                else
                {
                    if (op.OperandType == OperandType.InlineMethod)   // ldftn, ldvirtftn: lambda ve yöntem grupları
                    {
                        MethodBase target = Resolve(() => module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArgs, methodArgs));
                        if (target != null) Inspect(target, summary);
                    }
                    else if (op.OperandType == OperandType.InlineField && (op == OpCodes.Ldsfld || op == OpCodes.Stsfld || op == OpCodes.Ldsflda) && !typeInitializer)
                    {
                        FieldInfo field = StaticState(Resolve(() => module.ResolveField(BitConverter.ToInt32(il, operand), typeArgs, methodArgs)));
                        if (field != null)
                        {
                            summary.Fields.Add((field, op != OpCodes.Ldsfld && !field.IsInitOnly));
                            if (op == OpCodes.Ldsfld && IsMutableCollection(field.FieldType)) pushed = field;
                        }
                    }
                    else if (op == OpCodes.Calli)
                    {
                        stack.Clear();
                        continue;
                    }
                    pops = op == OpCodes.Ret ? (method is MethodInfo m && m.ReturnType != typeof(void) ? 1 : 0) : Pops(op.StackBehaviourPop);
                    pushes = Pushes(op.StackBehaviourPush);
                    if (op == OpCodes.Dup)   // üstteki değerin bir kopyası daha
                    {
                        pops = 0;
                        pushes = 1;
                        pushed = stack.Count > 0 ? stack[stack.Count - 1] : null;
                    }
                }

                if (pops > stack.Count) stack.Clear();
                else stack.RemoveRange(stack.Count - pops, pops);
                for (int i = 0; i < pushes; i++) stack.Add(i == pushes - 1 ? pushed : null);

                if (op.OperandType == OperandType.InlineBrTarget || op.OperandType == OperandType.ShortInlineBrTarget)
                {
                    int offset = op.OperandType == OperandType.InlineBrTarget ? BitConverter.ToInt32(il, operand) : (sbyte)il[operand];
                    depthAt[pos + offset] = op == OpCodes.Leave || op == OpCodes.Leave_S ? 0 : stack.Count;
                }
                else if (op.OperandType == OperandType.InlineSwitch)
                {
                    int count = BitConverter.ToInt32(il, operand);
                    for (int i = 0; i < count; i++) depthAt[pos + BitConverter.ToInt32(il, operand + 4 + 4 * i)] = stack.Count;
                }
                if (op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Return || op.FlowControl == FlowControl.Throw
                    || op == OpCodes.Endfinally || op == OpCodes.Jmp)
                    unreachable = true;
            }
        }

        // Çağrılan ya da temsilcisi alınan yöntem: süreç geneli ve makine çağrıları kaydedilir, taranan derlemelerdeyse içine inilir
        private static void Inspect(MethodBase callee, Summary summary)
        {
            Type declaring = callee.DeclaringType;
            string typeName = declaring?.FullName;
            if (typeName == null) return;
            string shortName = declaring.Name + "." + callee.Name;
            if (ProcessWideWrites.Any(w => w.Type == typeName && w.Method == callee.Name)) summary.Calls.Add((shortName, Kind.Write));
            if (MachineCalls.Any(m => m.Type == typeName && m.Method == callee.Name)) summary.Calls.Add((shortName, Kind.Machine));
            if (IsScanned(callee.Module.Assembly))
            {
                // Genel (generic) türlerin ve yöntemlerin tanımına inilir: gövde ve anahtar aynıdır
                MethodBase definition = Resolve(() => callee.Module.ResolveMethod(callee.MetadataToken));
                if (definition != null) summary.Callees.Add(definition);
            }
        }

        // Durum sayılabilecek statik alan (tanımı), ya da null: taranmayan derlemeler, derleyicinin önbellekleri, sabitler,
        // [ThreadStatic] alanlar, içeriği değişmeyen readonly alanlar ve bu tarayıcının kendi önbellekleri
        private static FieldInfo StaticState(FieldInfo field)
        {
            if (field == null || !IsScanned(field.Module.Assembly) || IsCompilerGenerated(field.DeclaringType)) return null;
            if (field.DeclaringType == typeof(StaticAccessScanner)) return null;
            if (field.IsLiteral || field.IsDefined(typeof(ThreadStaticAttribute), false)) return null;
            if (field.IsInitOnly && !IsMutableCollection(field.FieldType)) return null;
            return Resolve(() => field.Module.ResolveField(field.MetadataToken)) ?? field;
        }

        private static void SetDepth(List<FieldInfo> stack, int depth)
        {
            if (stack.Count > depth) stack.RemoveRange(depth, stack.Count - depth);
            while (stack.Count < depth) stack.Add(null);
        }

        private static int Pops(StackBehaviour behaviour)
        {
            switch (behaviour)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: return 2;
                default: return 3;   // Popi_popi_popi, Popref_popi_pop1, Popref_popi_popi, ... (Varpop yalnızca çağrılarda ve ret'te)
            }
        }

        private static int Pushes(StackBehaviour behaviour) =>
            behaviour == StackBehaviour.Push0 ? 0 : behaviour == StackBehaviour.Push1_push1 ? 2 : 1;

        private static T Resolve<T>(Func<T> resolve) where T : class
        {
            try { return resolve(); }
            catch (ArgumentException) { return null; }
            catch (BadImageFormatException) { return null; }
            catch (TypeLoadException) { return null; }
            catch (MissingMemberException) { return null; }
            catch (System.IO.FileNotFoundException) { return null; }
        }

        private static bool IsMutableCollection(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.IsGenericType && MutableCollections.Contains(t.GetGenericTypeDefinition().FullName)) return true;
            return false;
        }

        // Otomatik özelliğin alanı özelliğin adıyla yazılır: "<Dir>k__BackingField" -> "SecureStore.Dir"
        private static string MemberName(FieldInfo field)
        {
            string name = field.Name;
            if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                name = name.Substring(1, name.Length - 1 - ">k__BackingField".Length);
            return field.DeclaringType.Name + "." + name;
        }

        private static int OperandSize(OperandType type, byte[] il, int pos)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return 4;   // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString, InlineTok, InlineType, ShortInlineR
            }
        }
    }
}
