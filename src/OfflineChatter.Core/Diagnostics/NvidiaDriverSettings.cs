using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OfflineChatter.Core.Diagnostics;

/// <summary>Политика «CUDA - Sysmem Fallback Policy» драйвера NVIDIA (536.40+).</summary>
public enum SysmemFallbackPolicy
{
    /// <summary>Не удалось прочитать (нет NVIDIA, нет nvapi, ошибка).</summary>
    Unknown,
    /// <summary>Драйвер старше 536.40: настройки нет, CUDA при нехватке памяти просто сообщает об ошибке.</summary>
    NotSupported,
    /// <summary>Не задано: поведение драйвера по умолчанию — перенос в ОЗУ разрешён.</summary>
    DriverDefault,
    /// <summary>«Prefer No Sysmem Fallback»: при нехватке видеопамяти — ошибка, без переноса в ОЗУ.</summary>
    PreferNoFallback,
    /// <summary>«Prefer Sysmem Fallback»: перенос в ОЗУ разрешён явно.</summary>
    PreferFallback,
}

/// <param name="ForApp">true — значение из профиля llama-server.exe, false — из глобального профиля.</param>
public sealed record SysmemFallbackInfo(SysmemFallbackPolicy Policy, bool ForApp, uint? RawValue, string? Detail)
{
    /// <summary>Драйвер может молча переносить данные CUDA из видеопамяти в ОЗУ.</summary>
    public bool FallbackAllowed => Policy is SysmemFallbackPolicy.DriverDefault or SysmemFallbackPolicy.PreferFallback;
}

/// <summary>
/// Чтение настроек драйвера NVIDIA через NvAPI DRS (только чтение, без прав администратора).
/// nvapi64.dll ставится с драйвером, на Win7 тоже; функции берутся через nvapi_QueryInterface
/// (идентификаторы — из nvapi_interface.h, github.com/NVIDIA/nvapi).
/// </summary>
public static class NvidiaDriverSettings
{
    /// <summary>
    /// Идентификатор скрытой настройки «CUDA - Sysmem Fallback Policy»: её нет ни в публичном NvApiDriverSettings.h,
    /// NVIDIA Profile Inspector (github.com/Orbmu2k/nvidiaProfileInspector). 0x10ECDB82 рядом — это «Antialiasing - Behavior Flags».
    /// </summary>
    public const uint SysmemFallbackSettingId = 0x10ECECC9;

    /// <summary>Значения настройки (CustomSettingNames.xml NVIDIA Profile Inspector).</summary>
    public static SysmemFallbackPolicy PolicyFromValue(uint value) => value switch
    {
        0 => SysmemFallbackPolicy.DriverDefault,
        1 => SysmemFallbackPolicy.PreferNoFallback,
        2 => SysmemFallbackPolicy.PreferFallback,
        _ => SysmemFallbackPolicy.Unknown,
    };

    /// <summary>Есть ли настройка в драйвере такой версии ("566.36" → да, "474.44" → нет).</summary>
    public static bool? DriverHasPolicy(string? driverVersion)
    {
        if (string.IsNullOrWhiteSpace(driverVersion)) return null;
        var parts = driverVersion!.Trim().Split('.');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)) return null;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return major > 536 || major == 536 && minor >= 40;
    }

    /// <param name="exeName">Имя программы для поиска её профиля (llama-server.exe).</param>
    /// <param name="driverVersion">Версия драйвера из NVML (для случая, когда настройка нигде не задана).</param>
    public static SysmemFallbackInfo ReadSysmemFallback(string exeName, string? driverVersion)
    {
        if (DriverHasPolicy(driverVersion) == false)
            return new SysmemFallbackInfo(SysmemFallbackPolicy.NotSupported, false, null, "driver " + driverVersion);
        try
        {
            return ReadCore(exeName, driverVersion);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                       or AccessViolationException or SEHException or MarshalDirectiveException)
        {
            return new SysmemFallbackInfo(SysmemFallbackPolicy.Unknown, false, null, ex.GetType().Name);
        }
    }

    private static SysmemFallbackInfo ReadCore(string exeName, string? driverVersion)
    {
        var api = Api.Load();
        if (api == null) return new SysmemFallbackInfo(SysmemFallbackPolicy.Unknown, false, null, "nvapi not available");
        if (api.Initialize() != 0) return new SysmemFallbackInfo(SysmemFallbackPolicy.Unknown, false, null, "NvAPI_Initialize failed");
        if (api.CreateSession(out var session) != 0) return new SysmemFallbackInfo(SysmemFallbackPolicy.Unknown, false, null, "DRS session failed");
        var name = Marshal.AllocHGlobal(UnicodeStringBytes);
        var app = Marshal.AllocHGlobal(ApplicationV4Bytes);
        var setting = Marshal.AllocHGlobal(SettingV1Bytes);
        try
        {
            if (api.LoadSettings(session) != 0) return new SysmemFallbackInfo(SysmemFallbackPolicy.Unknown, false, null, "DRS load failed");

            // 1) свой профиль у llama-server.exe (Программные настройки)
            WriteUnicode(name, exeName);
            if (FindApplication(api, session, name, app, out var appProfile)
                && GetDword(api, session, appProfile, setting, out var appValue, out var location) && location == 0)
                return new SysmemFallbackInfo(PolicyFromValue(appValue), true, appValue, null);

            // 2) глобальный профиль (Глобальные параметры)
            if (api.GetCurrentGlobalProfile(session, out var global) == 0 && GetDword(api, session, global, setting, out var globalValue, out _))
                return new SysmemFallbackInfo(PolicyFromValue(globalValue), false, globalValue, null);

            // Нигде не задано: по умолчанию драйвера перенос разрешён
            if (DriverHasPolicy(driverVersion) == true)
                return new SysmemFallbackInfo(SysmemFallbackPolicy.DriverDefault, false, null, null);
            return new SysmemFallbackInfo(SysmemFallbackPolicy.Unknown, false, null, "setting not found");
        }
        finally
        {
            api.DestroySession(session);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(app);
            Marshal.FreeHGlobal(setting);
        }
    }

    private static bool FindApplication(Api api, IntPtr session, IntPtr name, IntPtr app, out IntPtr profile)
    {
        profile = IntPtr.Zero;
        // Новые драйверы понимают все версии структуры, старые — только до своей: пробуем от новой к старой
        foreach (var (size, ver) in new[] { (ApplicationV4Bytes, 4), (16396, 3), (16392, 2), (12296, 1) })
        {
            Zero(app, ApplicationV4Bytes);
            Marshal.WriteInt32(app, size | (ver << 16));
            var r = api.FindApplicationByName(session, name, out profile, app);
            if (r == 0) return true;
            if (r != IncompatibleStructVersion) return false;
        }
        return false;
    }

    /// <param name="location">0 — значение задано в самом профиле, иначе унаследовано (NVDRS_SETTING_LOCATION).</param>
    private static bool GetDword(Api api, IntPtr session, IntPtr profile, IntPtr setting, out uint value, out int location)
    {
        value = 0;
        location = -1;
        Zero(setting, SettingV1Bytes);
        Marshal.WriteInt32(setting, SettingV1Bytes | (1 << 16));
        if (api.GetSetting(session, profile, SysmemFallbackSettingId, setting) != 0) return false;
        if (Marshal.ReadInt32(setting, SettingTypeOffset) != 0) return false; // не DWORD
        location = Marshal.ReadInt32(setting, SettingLocationOffset);
        value = unchecked((uint)Marshal.ReadInt32(setting, CurrentValueOffset));
        return true;
    }

    // ----- раскладка структур (nvapi.h, #pragma pack 4) -----
    private const int UnicodeStringBytes = 2048 * 2;                    // NvAPI_UnicodeString = NvU16[2048]
    private const int UnionBytes = 4 + 4096;                            // NVDRS_BINARY_SETTING — самый большой член
    private const int SettingTypeOffset = 4 + UnicodeStringBytes + 4;   // version, settingName, settingId
    private const int SettingLocationOffset = SettingTypeOffset + 4;
    private const int CurrentValueOffset = SettingLocationOffset + 4 + 4 + 4 + UnionBytes; // + isCurrentPredefined, isPredefinedValid, predefined
    private const int SettingV1Bytes = CurrentValueOffset + UnionBytes; // 12320 = 0x3020
    private const int ApplicationV4Bytes = 8 + UnicodeStringBytes * 4 + 4 + UnicodeStringBytes; // 20492
    private const int IncompatibleStructVersion = -9;

    private static void Zero(IntPtr p, int bytes)
    {
        for (var i = 0; i < bytes; i += 4) Marshal.WriteInt32(p, i, 0);
    }

    private static void WriteUnicode(IntPtr p, string s)
    {
        Zero(p, UnicodeStringBytes);
        var bytes = Encoding.Unicode.GetBytes(s.Length > 2047 ? s.Substring(0, 2047) : s);
        Marshal.Copy(bytes, 0, p, bytes.Length);
    }

    /// <summary>Функции NvAPI. Минимальная ОС — Windows 7 (драйвер NVIDIA с nvapi).</summary>
    private sealed class Api
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int CreateSessionFn(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SessionFn(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetProfileFn(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int FindApplicationFn(IntPtr session, IntPtr appName, out IntPtr profile, IntPtr application);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);

        public InitializeFn Initialize = null!;
        public CreateSessionFn CreateSession = null!;
        public SessionFn DestroySession = null!;
        public SessionFn LoadSettings = null!;
        public GetProfileFn GetCurrentGlobalProfile = null!;
        public FindApplicationFn FindApplicationByName = null!;
        public GetSettingFn GetSetting = null!;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceFn(uint id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static Api? Load()
        {
            var module = LoadLibrary(Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll");
            if (module == IntPtr.Zero) return null;
            var qiPtr = GetProcAddress(module, "nvapi_QueryInterface");
            if (qiPtr == IntPtr.Zero) return null;
            var qi = (QueryInterfaceFn)Marshal.GetDelegateForFunctionPointer(qiPtr, typeof(QueryInterfaceFn));

            T? Fn<T>(uint id) where T : class
            {
                var p = qi(id);
                return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
            }

            var api = new Api
            {
                Initialize = Fn<InitializeFn>(0x0150E828)!,
                CreateSession = Fn<CreateSessionFn>(0x0694D52E)!,
                DestroySession = Fn<SessionFn>(0xDAD9CFF8)!,
                LoadSettings = Fn<SessionFn>(0x375DBD6B)!,
                GetCurrentGlobalProfile = Fn<GetProfileFn>(0x617BFF9F)!,
                FindApplicationByName = Fn<FindApplicationFn>(0xEEE566B2)!,
                GetSetting = Fn<GetSettingFn>(0x73BF8338)!,
            };
            return api.Initialize == null || api.CreateSession == null || api.DestroySession == null || api.LoadSettings == null
                   || api.GetCurrentGlobalProfile == null || api.FindApplicationByName == null || api.GetSetting == null
                ? null
                : api;
        }
    }
}
