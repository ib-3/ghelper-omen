using Microsoft.Win32;
using OmenCore.Hardware;
using PawnIO;
using System.Runtime.InteropServices;

namespace GHelper.Mode
{
    internal class PowerNative
    {
        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerWriteDCValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            int AcValueIndex);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerWriteACValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            int AcValueIndex);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerReadACValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            out IntPtr AcValueIndex
            );

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerReadDCValueIndex(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
            out IntPtr AcValueIndex
            );


        [DllImport("powrprof.dll")]
        static extern uint PowerReadACValue(
            IntPtr RootPowerKey,
            Guid SchemeGuid,
            Guid SubGroupOfPowerSettingGuid,
            Guid PowerSettingGuid,
            ref int Type,
            ref IntPtr Buffer,
            ref uint BufferSize
            );


        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerSetActiveScheme(IntPtr RootPowerKey,
            [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid);

        [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
        static extern UInt32 PowerGetActiveScheme(IntPtr UserPowerKey, out IntPtr ActivePolicyGuid);

        static readonly Guid GUID_CPU = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid GUID_BOOST = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        static readonly Guid GUID_PROCTHROTTLEMAX = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        static readonly Guid GUID_SYSCOOLPOL = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");

        // Windows PERFEPP: 0 = max performance, 100 = max power saving.
        // Intel HWP EPP MSR: 0 = max performance, 255 = max power saving.
        static readonly Guid GUID_EPP_AC = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        static readonly Guid GUID_EPP_DC = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

        private static Guid GUID_SLEEP_SUBGROUP = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        private static Guid GUID_HIBERNATEIDLE = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");

        private static Guid GUID_SYSTEM_BUTTON_SUBGROUP = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        private static Guid GUID_LIDACTION = new Guid("5CA83367-6E45-459F-A27B-476B1D01C936");

        private static Guid GUID_SUB_PCIEXPRESS = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        private static Guid GUID_PCI_EXPRESS_ASPM = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

        private static Guid GUID_SUB_DISK = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
        private static Guid GUID_DISKNVMENOPPME = new Guid("fc7372b6-ab2d-43ee-8797-15e9841f2cca");

        private static Guid GUID_SUB_NONE = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        private static Guid GUID_CONNECTIVITYINSTANDBY = new Guid("f15576e8-98b7-4186-b944-eafa664402d9");

        private static Guid GUID_SCHEDPOLICY = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        private static Guid GUID_SHORTSCHEDPOLICY = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
        private static Guid GUID_CPMAXCORES = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028"); // Class 0 (P-cores)

        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetActualOverlayScheme")]
        public static extern uint PowerGetActualOverlayScheme(out Guid ActualOverlayGuid);

        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
        public static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayGuid);

        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
        public static extern uint PowerSetActiveOverlayScheme(ref Guid OverlaySchemeGuid);

        const string POWER_SILENT = "961cc777-2547-4f9d-8174-7d86181b8a7a";
        const string POWER_BALANCED = "00000000-0000-0000-0000-000000000000";
        const string POWER_TURBO = "ded574b5-45a0-4f42-8737-46345c09c238";

        const string PLAN_BALANCED = "381b4222-f694-41f0-9685-ff5bb260df2e";
        const string PLAN_HIGH_PERFORMANCE = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        static List<string> overlays = new() {
                POWER_BALANCED,
                POWER_TURBO,
                POWER_SILENT,
            };

        public static Dictionary<string, string> powerModes = new Dictionary<string, string>
            {
                { POWER_SILENT, "Best Power Efficiency" },
                { POWER_BALANCED, "Balanced" },
                { POWER_TURBO, "Best Performance" },
                { PLAN_HIGH_PERFORMANCE, "High Performance Plan"},
            };

        private static IMsrAccess? _msrAccess;
        private static bool _msrAccessAttempted;

        // EPP presets: ThrottleStop-style raw HWP EPP values (0-255).
        public static Dictionary<int, string> eppPresets = new Dictionary<int, string>
            {
                { 0,   "Performance" },
                { 32,  "High Performance" },
                { 64,  "Balanced Performance" },
                { 128, "Balanced" },
                { 192, "Balanced Power Saving" },
                { 255, "Power Saving" },
            };

        private static int ClampRawEpp(int epp)
        {
            return Math.Clamp(epp, 0, 255);
        }

        private static int RawEppToWindowsPercent(int epp)
        {
            return Math.Clamp((int)Math.Round(ClampRawEpp(epp) * 100.0 / 255.0), 0, 100);
        }

        private static int WindowsPercentToRawEpp(int epp)
        {
            return Math.Clamp((int)Math.Round(Math.Clamp(epp, 0, 100) * 255.0 / 100.0), 0, 255);
        }

        private static IMsrAccess? GetMsrAccess()
        {
            if (CpuInfo.IsAMD) return null;
            if (_msrAccess?.IsAvailable == true) return _msrAccess;
            if (_msrAccessAttempted) return null;

            _msrAccessAttempted = true;
            _msrAccess = MsrAccessFactory.Create();
            return _msrAccess?.IsAvailable == true ? _msrAccess : null;
        }

        static Guid GetActiveScheme()
        {
            IntPtr pActiveSchemeGuid;
            var hr = PowerGetActiveScheme(IntPtr.Zero, out pActiveSchemeGuid);
            Guid activeSchemeGuid = (Guid)Marshal.PtrToStructure(pActiveSchemeGuid, typeof(Guid));
            return activeSchemeGuid;
        }

        public static int GetCPUBoost()
        {
            IntPtr AcValueIndex;
            Guid activeSchemeGuid = GetActiveScheme();

            UInt32 value = PowerReadACValueIndex(IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_BOOST, out AcValueIndex);

            return AcValueIndex.ToInt32();

        }

        public static int GetEPP()
        {
            var msr = GetMsrAccess();
            if (msr != null)
            {
                try
                {
                    int epp = msr.ReadHwpEpp();
                    if (epp >= 0 && epp <= 255)
                        return epp;
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"EPP MSR read error: {ex.Message}");
                }
            }

            try
            {
                Guid activeSchemeGuid = GetActiveScheme();
                uint status = PowerReadACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_EPP_AC, out IntPtr val);
                if (status != 0)
                {
                    Logger.WriteLine($"EPP read failed: {status}");
                    return 128;
                }

                int result = val.ToInt32();
                if (result < 0 || result > 100) return 128;
                return WindowsPercentToRawEpp(result);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"EPP read error: {ex.Message}");
                return 128;
            }
        }

        public static bool SetEPP(int epp)
        {
            epp = ClampRawEpp(epp);

            bool msrOk = false;
            var msr = GetMsrAccess();
            if (msr != null)
            {
                try
                {
                    msrOk = msr.SetHwpEpp(epp);
                    Logger.WriteLine($"EPP MSR set to {epp}: {(msrOk ? "OK" : "FAIL")}");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"EPP MSR set error: {ex.Message}");
                }
            }

            int windowsEpp = RawEppToWindowsPercent(epp);

            Guid activeSchemeGuid = GetActiveScheme();

            uint hrAC = PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_EPP_AC, windowsEpp);
            uint hrDC = PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_EPP_DC, windowsEpp);
            uint hrActive = PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            PowerReadACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_EPP_AC, out IntPtr readAc);
            PowerReadDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_EPP_DC, out IntPtr readDc);

            int ac = readAc.ToInt32();
            int dc = readDc.ToInt32();
            bool windowsOk = hrAC == 0 && hrDC == 0 && hrActive == 0 && ac == windowsEpp && dc == windowsEpp;

            Logger.WriteLine($"EPP Windows PERFEPP set to {windowsEpp}% ({epp}/255): {(windowsOk ? "OK" : $"AC={hrAC}/{ac}, DC={hrDC}/{dc}, Active={hrActive}")}");
            return msrOk || windowsOk;
        }

        public static void SetCPUBoost(int boost = 0)
        {
            Guid activeSchemeGuid = GetActiveScheme();

            if (boost == GetCPUBoost()) return;

            var hrAC = PowerWriteACValueIndex(
                 IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_BOOST,
                 boost);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            var hrDC = PowerWriteDCValueIndex(
                 IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_BOOST,
                 boost);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            Logger.WriteLine("Boost " + boost);
        }

        public static int GetCPUMaxState()
        {
            IntPtr AcValueIndex;
            Guid activeSchemeGuid = GetActiveScheme();

            UInt32 value = PowerReadACValueIndex(IntPtr.Zero,
                 activeSchemeGuid,
                 GUID_CPU,
                 GUID_PROCTHROTTLEMAX, out AcValueIndex);

            int result = AcValueIndex.ToInt32();
            if (result < 5 || result > 100) return 100;
            return result;
        }

        public static void SetCPUMaxState(int percent)
        {
            if (percent < 5) percent = 5;
            if (percent > 100) percent = 100;

            Guid activeSchemeGuid = GetActiveScheme();

            if (percent == GetCPUMaxState()) return;

            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_PROCTHROTTLEMAX, percent);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_PROCTHROTTLEMAX, percent);
            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            Logger.WriteLine("CPU Max State %: " + percent);
        }

        public static string GetPowerMode()
        {
            if (GetActiveScheme().ToString() == PLAN_HIGH_PERFORMANCE) return PLAN_HIGH_PERFORMANCE;
            PowerGetEffectiveOverlayScheme(out Guid activeScheme);
            return activeScheme.ToString();
        }

        public static void SetPowerMode(string scheme)
        {

            if (scheme == PLAN_HIGH_PERFORMANCE)
            {
                SetPowerPlan(scheme);
                return;
            }
            else
            {
                // Power plan from config or defaulting to balanced
                SetPowerPlan(AppConfig.GetModeString("scheme"));
            }

            if (!overlays.Contains(scheme)) return;

            Guid guidScheme = new Guid(scheme);

            uint status = PowerGetEffectiveOverlayScheme(out Guid activeScheme);

            if (GetBatterySaverStatus())
            {
                Logger.WriteLine("Battery Saver detected");
                return;
            }

            if (status != 0 || activeScheme != guidScheme)
            {
                status = PowerSetActiveOverlayScheme(ref guidScheme);
                Logger.WriteLine("Power Mode " + activeScheme + " -> " + scheme + ":" + (status == 0 ? "OK" : status));
            }

        }

        public static void SetPowerPlan(string scheme)
        {
            // Skipping power modes
            if (overlays.Contains(scheme)) return;

            if (scheme is null) scheme = PLAN_BALANCED;
            var activeScheme = GetActiveScheme().ToString();
            if (activeScheme == scheme) return;

            uint status = PowerSetActiveScheme(IntPtr.Zero, new Guid(scheme));
            Logger.WriteLine($"Power Plan {activeScheme} -> {scheme} :" + (status == 0 ? "OK" : status));
        }

        public static string GetDefaultPowerMode(int mode)
        {
            switch (mode)
            {
                case 1: // turbo
                    return POWER_TURBO;
                case 2: //silent
                    return POWER_SILENT;
                default: // balanced
                    return POWER_BALANCED;
            }
        }

        public static void SetPowerMode(int mode)
        {
            SetPowerMode(GetDefaultPowerMode(mode));
        }

        public static int GetASPM()
        {
            Guid activeSchemeGuid = GetActiveScheme();
            IntPtr activeIndex;

            PowerReadACValueIndex(IntPtr.Zero,
                    activeSchemeGuid,
                    GUID_SUB_PCIEXPRESS,
                    GUID_PCI_EXPRESS_ASPM, out activeIndex);

            return activeIndex.ToInt32();
        }

        public static void SetASPM(int status = 0)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            var currentASPM = GetASPM();
            if (currentASPM == status) return;

            var hrAC = PowerWriteACValueIndex(
                IntPtr.Zero,
                activeSchemeGuid,
                GUID_SUB_PCIEXPRESS,
                GUID_PCI_EXPRESS_ASPM,
                status);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine($"Changed AC ASPM {currentASPM} -> {status}");
        }

        public static void SetBalancedASPM(int status = 0)
        {
            if (GetActiveScheme().ToString() != PLAN_BALANCED) return;
            SetASPM(status);
        }

        public static int GetLidAction(bool ac)
        {
            Guid activeSchemeGuid = GetActiveScheme();

            IntPtr activeIndex;
            if (ac)
                PowerReadACValueIndex(IntPtr.Zero,
                     activeSchemeGuid,
                     GUID_SYSTEM_BUTTON_SUBGROUP,
                     GUID_LIDACTION, out activeIndex);

            else
                PowerReadDCValueIndex(IntPtr.Zero,
                    activeSchemeGuid,
                    GUID_SYSTEM_BUTTON_SUBGROUP,
                    GUID_LIDACTION, out activeIndex);


            return activeIndex.ToInt32();
        }


        public static void SetLidAction(int action, bool acOnly = false)
        {
            /**
             * 1: Do nothing
             * 2: Seelp
             * 3: Hibernate
             * 4: Shutdown
             */

            Guid activeSchemeGuid = GetActiveScheme();

            var hrAC = PowerWriteACValueIndex(
                IntPtr.Zero,
                activeSchemeGuid,
                GUID_SYSTEM_BUTTON_SUBGROUP,
                GUID_LIDACTION,
                action);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            if (!acOnly)
            {
                var hrDC = PowerWriteDCValueIndex(
                  IntPtr.Zero,
                  activeSchemeGuid,
                  GUID_SYSTEM_BUTTON_SUBGROUP,
                  GUID_LIDACTION,
                  action);

                PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            }

            Logger.WriteLine("Changed Lid Action to " + action);
        }

        public static int GetHibernateAfter()
        {
            Guid activeSchemeGuid = GetActiveScheme();
            IntPtr seconds;
            PowerReadDCValueIndex(IntPtr.Zero,
                    activeSchemeGuid,
                    GUID_SLEEP_SUBGROUP,
                    GUID_HIBERNATEIDLE, out seconds);

            Logger.WriteLine("Hibernate after " + seconds);
            return (seconds.ToInt32() / 60);
        }


        public static void SetHibernateAfter(int minutes)
        {
            int seconds = minutes * 60;

            Guid activeSchemeGuid = GetActiveScheme();
            var hrAC = PowerWriteDCValueIndex(
                IntPtr.Zero,
                activeSchemeGuid,
                GUID_SLEEP_SUBGROUP,
                GUID_HIBERNATEIDLE,
                seconds);

            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);

            Logger.WriteLine("Setting Hibernate after " + seconds + ": " + (hrAC == 0 ? "OK" : hrAC));
        }

        public static void SetCoolingPolicy(int policy)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_SYSCOOLPOL, policy);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_SYSCOOLPOL, policy);
            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine("System Cooling Policy: " + (policy == 0 ? "Passive" : "Active"));
        }

        public static void SetNvmePower(int on)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_SUB_DISK, GUID_DISKNVMENOPPME, on);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_SUB_DISK, GUID_DISKNVMENOPPME, on);
            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine("NVMe NOPPME: " + (on == 1 ? "On" : "Off"));
        }

        public static void SetConnectivityInStandby(int disable)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_SUB_NONE, GUID_CONNECTIVITYINSTANDBY, disable);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_SUB_NONE, GUID_CONNECTIVITYINSTANDBY, disable);
            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine("Connectivity in Standby: " + (disable == 0 ? "Disabled" : "Enabled"));
        }

        public static void SetSchedPolicy(int policy)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_SCHEDPOLICY, policy);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_SCHEDPOLICY, policy);
            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_SHORTSCHEDPOLICY, policy);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_SHORTSCHEDPOLICY, policy);
            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine("Sched Policy: " + policy);
        }

        public static void SetPcoreParking(int maxCoresPercent)
        {
            Guid activeSchemeGuid = GetActiveScheme();
            PowerWriteACValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_CPMAXCORES, maxCoresPercent);
            PowerWriteDCValueIndex(IntPtr.Zero, activeSchemeGuid, GUID_CPU, GUID_CPMAXCORES, maxCoresPercent);
            PowerSetActiveScheme(IntPtr.Zero, activeSchemeGuid);
            Logger.WriteLine("P-Core Max Active %: " + maxCoresPercent);
        }

        [DllImport("Kernel32")]
        private static extern bool GetSystemPowerStatus(SystemPowerStatus sps);
        public enum ACLineStatus : byte
        {
            Offline = 0, Online = 1, Unknown = 255
        }

        public enum BatteryFlag : byte
        {
            High = 1,
            Low = 2,
            Critical = 4,
            Charging = 8,
            NoSystemBattery = 128,
            Unknown = 255
        }

        // Fields must mirror their unmanaged counterparts, in order
        [StructLayout(LayoutKind.Sequential)]
        public class SystemPowerStatus
        {
            public ACLineStatus ACLineStatus;
            public BatteryFlag BatteryFlag;
            public Byte BatteryLifePercent;
            public Byte SystemStatusFlag;
            public Int32 BatteryLifeTime;
            public Int32 BatteryFullLifeTime;
        }

        public static bool GetBatterySaverStatus()
        {
            try
            {
                var status = Registry.GetValue(@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Control\Power", "EnergySaverState", null);
                if (status == null)
                {
                    SystemPowerStatus sps = new SystemPowerStatus();
                    GetSystemPowerStatus(sps);
                    return (sps.SystemStatusFlag > 0);
                }
                return (int)status == 1;
            }
            catch (Exception e)
            {
                Logger.WriteLine("Can't check EnergySaverState" + e.Message);
                return false;
            }
        }


    }
}
