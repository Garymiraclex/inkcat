using System;
using System.Globalization;
using Microsoft.Win32;

namespace InkCat
{
    /// <summary>设置存在注册表 HKCU\Software\InkCat，除此之外不写任何东西。</summary>
    static class Prefs
    {
        const string path = @"HKEY_CURRENT_USER\Software\InkCat";

        public static T Get<T>(string key, T fallback)
        {
            try { return Registry.GetValue(path, key, null) is string s ? (T)Convert.ChangeType(s, typeof(T), CultureInfo.InvariantCulture) : fallback; }
            catch (FormatException) { return fallback; }
        }

        public static void Set<T>(string key, T value) => Registry.SetValue(path, key, Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}
