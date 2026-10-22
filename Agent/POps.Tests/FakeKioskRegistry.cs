using System;
using System.Collections.Generic;
using POps.Shared;

namespace POps.Tests
{
    // Bellekteki kayıt defteri: testler gerçek HKLM/HKU politikalarına asla dokunmaz (bkz. TestEnvironment)
    public sealed class FakeKioskRegistry : IKioskRegistry
    {
        public readonly Dictionary<string, int> Values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Hives = new List<string>();
        public Func<string, bool> FailWrites = _ => false;

        public static string Path(string hive, string key, string name) => hive + "\\" + key + "\\" + name;

        public IList<string> UserHives() => new List<string>(Hives);

        public int? Get(string hive, string key, string name) => Values.TryGetValue(Path(hive, key, name), out int v) ? v : (int?)null;

        public void Set(string hive, string key, string name, int value)
        {
            if (FailWrites(hive)) throw new UnauthorizedAccessException("erişim reddedildi");
            Values[Path(hive, key, name)] = value;
        }

        public void Delete(string hive, string key, string name)
        {
            if (FailWrites(hive)) throw new UnauthorizedAccessException("erişim reddedildi");
            Values.Remove(Path(hive, key, name));
        }
    }
}
